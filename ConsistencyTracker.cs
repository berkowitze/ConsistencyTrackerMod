﻿using Celeste.Mod.ConsistencyTracker.Models;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Celeste.Mod.ConsistencyTracker.ThirdParty;
using Celeste.Mod.ConsistencyTracker.Stats;
using Celeste.Mod.ConsistencyTracker.EverestInterop;
using Celeste.Mod.ConsistencyTracker.Properties;
using Celeste.Mod.ConsistencyTracker.Util;
using Newtonsoft.Json;
using System.Diagnostics;
using Celeste.Mod.ConsistencyTracker.Entities;
using Monocle;

namespace Celeste.Mod.ConsistencyTracker {
    public class ConsistencyTrackerModule : EverestModule {

        public static ConsistencyTrackerModule Instance;
        private static readonly int LOG_FILE_COUNT = 10;

        public static readonly string ModVersion = "2.1.1";
        public static readonly string OverlayVersion = "2.0.0";

        public override Type SettingsType => typeof(ConsistencyTrackerSettings);
        public ConsistencyTrackerSettings ModSettings => (ConsistencyTrackerSettings)this._Settings;

        public static readonly string BaseFolderPath = "./ConsistencyTracker/";
        public static readonly string ExternalToolsFolder = "external-tools";
        public static readonly string LogsFolder = "logs";
        public static readonly string PathsFolder = "paths";
        public static readonly string StatsFolder = "stats";
        public static readonly string SummariesFolder = "summaries";


        private bool DidRestart { get; set; } = false;
        private HashSet<string> ChaptersThisSession { get; set; } = new HashSet<string>();

        #region Path Recording Variables

        public bool DoRecordPath {
            get => _DoRecordPath;
            set {
                if (value) {
                    if (DisabledInRoomName != CurrentRoomName) {
                        PathRec = new PathRecorder();
                        InsertCheckpointIntoPath(null, LastRoomWithCheckpoint);
                        PathRec.AddRoom(CurrentRoomName);
                    }
                } else {
                    SaveRecordedRoomPath();
                }

                _DoRecordPath = value;
            }
        }
        private bool _DoRecordPath = false;
        private PathRecorder PathRec;
        private string DisabledInRoomName;

        #endregion

        #region State Variables

        //Used to cache and prevent unnecessary operations via DebugRC
        public long CurrentUpdateFrame;

        public PathInfo CurrentChapterPath;
        public ChapterStats CurrentChapterStats;

        public string CurrentChapterDebugName;
        public string PreviousRoomName;
        public string CurrentRoomName;
        public string SpeedrunToolSaveStateRoomName;

        private string LastRoomWithCheckpoint = null;

        private bool _CurrentRoomCompleted = false;
        private bool _CurrentRoomCompletedResetOnDeath = false;
        public bool PlayerIsHoldingGolden {
            get => _PlayerIsHoldingGolden; 
            set { 
                _PlayerIsHoldingGolden = value;
                if (IngameOverlay != null) {
                    IngameOverlay.SetGoldenState(value);
                }
            } 
        }
        private bool _PlayerIsHoldingGolden = false;

        #endregion

        public StatManager StatsManager;
        public TextOverlay IngameOverlay;


        public ConsistencyTrackerModule() {
            Instance = this;
        }

        #region Load/Unload Stuff

        public override void Load() {
            CheckFolderExists(BaseFolderPath);
            
            CheckFolderExists(GetPathToFolder(PathsFolder));
            CheckPrepackagedPaths();
            
            CheckFolderExists(GetPathToFolder(StatsFolder));
            CheckFolderExists(GetPathToFolder(LogsFolder));
            CheckFolderExists(GetPathToFolder(SummariesFolder));

            bool toolsFolderExisted = CheckFolderExists(GetPathToFolder(ExternalToolsFolder));
            if (!toolsFolderExisted) {
                CreateExternalTools();
            }

            LogInit();

            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Log($"~~~==== CCT STARTED ({time}) ====~~~");
            Log($"Mod Settings -> \n{JsonConvert.SerializeObject(ModSettings, Formatting.Indented)}");
            Log($"~~~==============================~~~");

            HookStuff();

            StatsManager = new StatManager();

            DebugRcPage.Load();

            //https://github.com/EverestAPI/CelesteTAS-EverestInterop/blob/master/CelesteTAS-EverestInterop/Source/Communication/StudioCommunicationClient.cs
            //idk how to use this class to get GameBananaId
            //ModUpdateInfo updateInfo = new ModUpdateInfo();
        }

        public override void Unload() {
            UnHookStuff();
            DebugRcPage.Unload();
            LogCleanup();
        }

        private void HookStuff() {
            //Track where the player is
            On.Celeste.Level.Begin += Level_Begin;
            Everest.Events.Level.OnExit += Level_OnExit;
            Everest.Events.Level.OnComplete += Level_OnComplete;
            Everest.Events.Level.OnTransitionTo += Level_OnTransitionTo;
            Everest.Events.Level.OnLoadLevel += Level_OnLoadLevel;
            On.Celeste.Level.TeleportTo += Level_TeleportTo;
            //Track deaths
            Everest.Events.Player.OnDie += Player_OnDie;
            //Track checkpoints
            On.Celeste.Checkpoint.TurnOn += Checkpoint_TurnOn;

            //Track in-room events, to determine when exiting back into a previous room counts as success
            //E.g. Power Source rooms where you collect a key but exit back into the HUB room should be marked as success

            //Picking up a kye
            On.Celeste.Key.OnPlayer += Key_OnPlayer; //works

            //Activating Resort clutter switches
            On.Celeste.ClutterSwitch.OnDashed += ClutterSwitch_OnDashed; //works

            //Picking up a strawberry
            On.Celeste.Strawberry.OnCollect += Strawberry_OnCollect; //doesnt work :(
            On.Celeste.Strawberry.OnPlayer += Strawberry_OnPlayer; //sorta works, but triggers very often for a single berry

            //Changing lava/ice in Core
            On.Celeste.CoreModeToggle.OnChangeMode += CoreModeToggle_OnChangeMode; //works

            //Picking up a Cassette tape
            On.Celeste.Cassette.OnPlayer += Cassette_OnPlayer; //works

            //Open up key doors?
            //On.Celeste.Door.Open += Door_Open; //Wrong door (those are the resort doors)
            On.Celeste.LockBlock.TryOpen += LockBlock_TryOpen; //works

            //On.Celeste.Player.Update += LogPhysicsUpdate;
            On.Monocle.Engine.Update += Engine_Update;
        }

        private void UnHookStuff() {
            On.Celeste.Level.Begin -= Level_Begin;
            Everest.Events.Level.OnExit -= Level_OnExit;
            Everest.Events.Level.OnComplete -= Level_OnComplete;
            Everest.Events.Level.OnTransitionTo -= Level_OnTransitionTo;
            Everest.Events.Level.OnLoadLevel -= Level_OnLoadLevel;
            On.Celeste.Level.TeleportTo -= Level_TeleportTo;

            //Track deaths
            Everest.Events.Player.OnDie -= Player_OnDie;

            //Track checkpoints
            On.Celeste.Checkpoint.TurnOn -= Checkpoint_TurnOn;

            //Picking up a kye
            On.Celeste.Key.OnPlayer -= Key_OnPlayer;

            //Activating Resort clutter switches
            On.Celeste.ClutterSwitch.OnDashed -= ClutterSwitch_OnDashed;

            //Picking up a strawberry
            On.Celeste.Strawberry.OnPlayer -= Strawberry_OnPlayer;

            //Changing lava/ice in Core
            On.Celeste.CoreModeToggle.OnChangeMode -= CoreModeToggle_OnChangeMode;

            //Picking up a Cassette tape
            On.Celeste.Cassette.OnPlayer -= Cassette_OnPlayer;

            //Open up key doors
            On.Celeste.LockBlock.TryOpen -= LockBlock_TryOpen;

            //On.Celeste.Player.Update -= LogPhysicsUpdate;
            On.Monocle.Engine.Update -= Engine_Update;
        }

        public override void Initialize()
        {
            base.Initialize();

            // load SpeedrunTool if it exists
            if (Everest.Modules.Any(m => m.Metadata.Name == "SpeedrunTool")) {
                SpeedrunToolSupport.Load();
            }
        }

        #endregion

        #region Hooks

        private void LockBlock_TryOpen(On.Celeste.LockBlock.orig_TryOpen orig, LockBlock self, Player player, Follower fol) {
            orig(self, player, fol);
            LogVerbose($"Opened a door");
            SetRoomCompleted(resetOnDeath: false);
        }

        private DashCollisionResults ClutterSwitch_OnDashed(On.Celeste.ClutterSwitch.orig_OnDashed orig, ClutterSwitch self, Player player, Vector2 direction) {
            LogVerbose($"Activated a clutter switch");
            SetRoomCompleted(resetOnDeath: false);
            return orig(self, player, direction);
        }

        private void Key_OnPlayer(On.Celeste.Key.orig_OnPlayer orig, Key self, Player player) {
            LogVerbose($"Picked up a key");
            orig(self, player);
            SetRoomCompleted(resetOnDeath: false);
        }

        private void Cassette_OnPlayer(On.Celeste.Cassette.orig_OnPlayer orig, Cassette self, Player player) {
            LogVerbose($"Collected a cassette tape");
            orig(self, player);
            SetRoomCompleted(resetOnDeath: false);
        }

        private readonly List<EntityID> TouchedBerries = new List<EntityID>();
        // All touched berries need to be reset on death, since they either:
        // - already collected
        // - disappeared on death
        private void Strawberry_OnPlayer(On.Celeste.Strawberry.orig_OnPlayer orig, Strawberry self, Player player) {
            orig(self, player);

            if (TouchedBerries.Contains(self.ID)) return; //to not spam the log
            TouchedBerries.Add(self.ID);

            LogVerbose($"Strawberry on player");
            SetRoomCompleted(resetOnDeath: true);
        }

        private void Strawberry_OnCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
            LogVerbose($"Collected a strawberry");
            orig(self);
            SetRoomCompleted(resetOnDeath: false);
        }

        private void CoreModeToggle_OnChangeMode(On.Celeste.CoreModeToggle.orig_OnChangeMode orig, CoreModeToggle self, Session.CoreModes mode) {
            LogVerbose($"Changed core mode to '{mode}'");
            orig(self, mode);
            SetRoomCompleted(resetOnDeath:true);
        }

        private void Checkpoint_TurnOn(On.Celeste.Checkpoint.orig_TurnOn orig, Checkpoint cp, bool animate) {
            orig(cp, animate);
            Log($"cp.Position={cp.Position}, LastRoomWithCheckpoint={LastRoomWithCheckpoint}");
            if (ModSettings.Enabled && DoRecordPath) {
                InsertCheckpointIntoPath(cp, LastRoomWithCheckpoint);
            }
        }

        //Not triggered when teleporting via debug map
        private void Level_TeleportTo(On.Celeste.Level.orig_TeleportTo orig, Level level, Player player, string nextLevel, Player.IntroTypes introType, Vector2? nearestSpawn) {
            orig(level, player, nextLevel, introType, nearestSpawn);
            Log($"level.Session.LevelData.Name={SanitizeRoomName(level.Session.LevelData.Name)}");
        }

        private void Level_OnLoadLevel(Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
            string newCurrentRoom = SanitizeRoomName(level.Session.LevelData.Name);
            bool holdingGolden = PlayerIsHoldingGoldenBerry(level.Tracker.GetEntity<Player>());

            Log($"level.Session.LevelData.Name={newCurrentRoom}, playerIntro={playerIntro} | CurrentRoomName: '{CurrentRoomName}', PreviousRoomName: '{PreviousRoomName}'");
            if (playerIntro == Player.IntroTypes.Respawn) { //Changing room via golden berry death or debug map teleport
                if (CurrentRoomName != null && newCurrentRoom != CurrentRoomName) {
                    SetNewRoom(newCurrentRoom, false, holdingGolden);
                }
            }

            if (DidRestart) {
                Log($"\tRequested reset of PreviousRoomName to null", true);
                DidRestart = false;
                SetNewRoom(newCurrentRoom, false, holdingGolden);
                PreviousRoomName = null;
            }

            if (isFromLoader) {
                Log("Adding overlay!");
                IngameOverlay = new TextOverlay();
                level.Add(IngameOverlay);
            }
        }

        private void Level_OnExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
            Log($"mode={mode}, snow={snow}");
            if (mode == LevelExit.Mode.Restart) {
                DidRestart = true;
            } else if (mode == LevelExit.Mode.GoldenBerryRestart) {
                DidRestart = true;

                if (ModSettings.Enabled && !ModSettings.PauseDeathTracking) { //Only count golden berry deaths when enabled
                    CurrentChapterStats?.AddGoldenBerryDeath();
                    if (ModSettings.OnlyTrackWithGoldenBerry) {
                        CurrentChapterStats.AddAttempt(false);
                    }
                }
            }

            if (DoRecordPath) {
                DoRecordPath = false;
                ModSettings.RecordPath = false;
            }
        }

        private void Level_OnComplete(Level level) {
            Log($"Incrementing {CurrentChapterStats?.CurrentRoom.DebugRoomName}");
            if(ModSettings.Enabled && !ModSettings.PauseDeathTracking && (!ModSettings.OnlyTrackWithGoldenBerry || PlayerIsHoldingGolden))
                CurrentChapterStats?.AddAttempt(true);
            CurrentChapterStats.ModState.ChapterCompleted = true;
            SaveChapterStats();
        }

        private void Level_Begin(On.Celeste.Level.orig_Begin orig, Level level) {
            Log($"Calling ChangeChapter with 'level.Session'");
            ChangeChapter(level.Session);
            orig(level);
        }

        private void Level_OnTransitionTo(Level level, LevelData levelDataNext, Vector2 direction) {
            if (levelDataNext.HasCheckpoint) {
                LastRoomWithCheckpoint = levelDataNext.Name;
            }
            
            string roomName = SanitizeRoomName(levelDataNext.Name);
            Log($"levelData.Name->{roomName}, level.Completed->{level.Completed}, level.NewLevel->{level.NewLevel}, levelDataNext.Bounds->{levelDataNext.Bounds}");
            bool holdingGolden = PlayerIsHoldingGoldenBerry(level.Tracker.GetEntity<Player>());
            SetNewRoom(roomName, true, holdingGolden);
        }

        private void Player_OnDie(Player player) {
            TouchedBerries.Clear();
            bool holdingGolden = PlayerIsHoldingGoldenBerry(player);

            Log($"Player died. (holdingGolden: {holdingGolden})");
            if (_CurrentRoomCompletedResetOnDeath) {
                _CurrentRoomCompleted = false;
            }

            if (ModSettings.Enabled) {
                if (!ModSettings.PauseDeathTracking && (!ModSettings.OnlyTrackWithGoldenBerry || holdingGolden))
                    CurrentChapterStats?.AddAttempt(false);

                if(CurrentChapterStats != null)
                    CurrentChapterStats.CurrentRoom.DeathsInCurrentRun++;

                SaveChapterStats();
            }
        }

        #endregion

        #region State Management

        private string SanitizeRoomName(string name) {
            name = name.Replace(";", "");
            return name;
        }

        private void ChangeChapter(Session session) {
            Log($"Called chapter change");
            AreaData area = AreaData.Areas[session.Area.ID];
            string chapName = area.Name;
            string chapNameClean = chapName.DialogCleanOrNull() ?? chapName.SpacedPascalCase();
            string campaignName = DialogExt.CleanLevelSet(area.GetLevelSet());

            Log($"Level->{session.Level}, session.Area.GetSID()->{session.Area.GetSID()}, session.Area.Mode->{session.Area.Mode}, chapterNameClean->{chapNameClean}, campaignName->{campaignName}");

            CurrentChapterDebugName = ($"{session.MapData.Data.SID}_{session.Area.Mode}").Replace("/", "_");

            //string test2 = Dialog.Get($"luma_farewellbb_FarewellBB_b_intro");
            //Log($"Dialog Test 2: {test2}");

            PreviousRoomName = null;
            CurrentRoomName = session.Level;

            CurrentChapterStats = GetCurrentChapterStats();
            CurrentChapterStats.ChapterDebugName = CurrentChapterDebugName;
            CurrentChapterStats.CampaignName = campaignName;
            CurrentChapterStats.ChapterName = chapNameClean;
            CurrentChapterStats.ChapterSID = session.MapData.Data.SID;
            CurrentChapterStats.ChapterSIDDialogSanitized = SanitizeSIDForDialog(session.MapData.Data.SID);
            CurrentChapterStats.SideName = session.Area.Mode.ToReadableString();

            SetCurrentChapterPath(GetPathInputInfo());

            //fix for SpeedrunTool savestate inconsistency
            TouchedBerries.Clear();

            SetNewRoom(CurrentRoomName, false, false);
            if (session.LevelData.HasCheckpoint) {
                LastRoomWithCheckpoint = CurrentRoomName;
            } else {
                LastRoomWithCheckpoint = null;
            }

            if (!DoRecordPath && ModSettings.RecordPath) { // TODO figure out why i did this
                DoRecordPath = true;
            }
        }

        public void SetCurrentChapterPath(PathInfo path) {
            CurrentChapterPath = path;
            if (CurrentChapterPath != null) {
                CurrentChapterPath.SetCheckpointRefs();

                if (CurrentChapterPath.ChapterName == null && CurrentChapterStats != null) {
                    CurrentChapterPath.CampaignName = CurrentChapterStats.CampaignName;
                    CurrentChapterPath.ChapterName = CurrentChapterStats.ChapterName;
                    CurrentChapterPath.ChapterSID = CurrentChapterStats.ChapterSID;
                    CurrentChapterPath.SideName = CurrentChapterStats.SideName;
                    SavePathToFile();
                }
            }
        }

        public void SetNewRoom(string newRoomName, bool countDeath=true, bool holdingGolden=false) {
            PlayerIsHoldingGolden = holdingGolden;
            CurrentChapterStats.ModState.ChapterCompleted = false;

            if (PreviousRoomName == newRoomName && !_CurrentRoomCompleted) { //Don't complete if entering previous room and current room was not completed
                Log($"Entered previous room '{PreviousRoomName}'");
                PreviousRoomName = CurrentRoomName;
                CurrentRoomName = newRoomName;
                CurrentChapterStats?.SetCurrentRoom(newRoomName);
                SaveChapterStats();
                return;
            }


            Log($"Entered new room '{newRoomName}' | Holding golden: '{holdingGolden}'");

            PreviousRoomName = CurrentRoomName;
            CurrentRoomName = newRoomName;
            _CurrentRoomCompleted = false;

            if (DoRecordPath) {
                PathRec.AddRoom(newRoomName);
            }

            if (ModSettings.Enabled && CurrentChapterStats != null) {
                if (countDeath && !ModSettings.PauseDeathTracking && (!ModSettings.OnlyTrackWithGoldenBerry || holdingGolden)) {
                    CurrentChapterStats.AddAttempt(true);
                }
                CurrentChapterStats.SetCurrentRoom(newRoomName);
                SaveChapterStats();
            }
        }

        private void SetRoomCompleted(bool resetOnDeath=false) {
            _CurrentRoomCompleted = true;
            _CurrentRoomCompletedResetOnDeath = resetOnDeath;
        }

        private bool PlayerIsHoldingGoldenBerry(Player player) {
            if (player == null || player.Leader == null || player.Leader.Followers == null)
                return false;

            return player.Leader.Followers.Any((f) => {
                if (!(f.Entity is Strawberry))
                    return false;

                Strawberry berry = (Strawberry)f.Entity;

                if (!berry.Golden || berry.Winged)
                    return false;

                return true;
            });
        }

        #region Speedrun Tool Save States

        public void SpeedrunToolSaveState(Dictionary<Type, Dictionary<string, object>> savedvalues, Level level) {
            Type type = GetType();
            if (!savedvalues.ContainsKey(type)) {
                savedvalues.Add(type, new Dictionary<string, object>());
                savedvalues[type].Add(nameof(PreviousRoomName), PreviousRoomName);
                savedvalues[type].Add(nameof(CurrentRoomName), CurrentRoomName);
                savedvalues[type].Add(nameof(_CurrentRoomCompleted), _CurrentRoomCompleted);
                savedvalues[type].Add(nameof(_CurrentRoomCompletedResetOnDeath), _CurrentRoomCompletedResetOnDeath);
            } else {
                savedvalues[type][nameof(PreviousRoomName)] = PreviousRoomName;
                savedvalues[type][nameof(CurrentRoomName)] = CurrentRoomName;
                savedvalues[type][nameof(_CurrentRoomCompleted)] = _CurrentRoomCompleted;
                savedvalues[type][nameof(_CurrentRoomCompletedResetOnDeath)] = _CurrentRoomCompletedResetOnDeath;
            }

            SpeedrunToolSaveStateRoomName = CurrentRoomName;
            SaveChapterStats();
        }

        public void SpeedrunToolLoadState(Dictionary<Type, Dictionary<string, object>> savedvalues, Level level) {
            Type type = GetType();
            if (!savedvalues.ContainsKey(type)) {
                Log("Trying to load state without prior saving a state...");
                return;
            }

            TextOverlay textOverlayEntity = level.Tracker.GetEntity<TextOverlay>();
            IngameOverlay = textOverlayEntity;

            PreviousRoomName = (string) savedvalues[type][nameof(PreviousRoomName)];
            CurrentRoomName = (string) savedvalues[type][nameof(CurrentRoomName)];
            _CurrentRoomCompleted = (bool) savedvalues[type][nameof(_CurrentRoomCompleted)];
            _CurrentRoomCompletedResetOnDeath = (bool) savedvalues[type][nameof(_CurrentRoomCompletedResetOnDeath)];

            CurrentChapterStats.SetCurrentRoom(CurrentRoomName);
            SaveChapterStats();
        }

        public void SpeedrunToolClearState() {
            SpeedrunToolSaveStateRoomName = null;
            if (CurrentChapterPath != null) {
                CurrentChapterPath.SpeedrunToolSaveStateRoom = null;
            }
            SaveChapterStats();
        }

        #endregion

        #endregion

        #region Data Import/Export

        public static string GetPathToFile(string file) {
            return BaseFolderPath + file;
        }
        public static string GetPathToFolder(string folder) {
            return BaseFolderPath + folder + "/";
        }
        /// <summary>Checks the folder exists.</summary>
        /// <param name="folderPath">The folder path.</param>
        /// <returns>true when the folder already existed, false when a new folder has been created.</returns>
        public static bool CheckFolderExists(string folderPath) {
            if (!Directory.Exists(folderPath)) {
                Directory.CreateDirectory(folderPath);
                return false;
            }

            return true;
        }


        public bool PathInfoExists() {
            string path = GetPathToFile($"{PathsFolder}/{CurrentChapterDebugName}.txt");
            return File.Exists(path);
        }
        public PathInfo GetPathInputInfo(string pathName = null) {
            if(pathName == null) {
                pathName = CurrentChapterDebugName;
            }
            Log($"Fetching path info for chapter '{pathName}'");

            string path = GetPathToFile($"{PathsFolder}/{pathName}.txt");
            Log($"\tSearching for path '{path}'", true);

            if (File.Exists(path)) { //Parse File
                Log($"\tFound file, parsing...", true);
                string content = File.ReadAllText(path);

                //[Try 1] New file format: JSON
                try {
                    return JsonConvert.DeserializeObject<PathInfo>(content);
                } catch (Exception) {
                    Log($"\tCouldn't read path info as JSON, trying old path format...", true);
                }

                //[Try 2] Old file format: selfmade text format
                try {
                    PathInfo parsedOldFormat = PathInfo.ParseString(content);
                    Log($"\tSaving path for map '{pathName}' in new format!", true);
                    SavePathToFile(parsedOldFormat, pathName); //Save in new format
                    return parsedOldFormat;
                } catch (Exception) {
                    Log($"\tCouldn't read old path info. Old path info content:\n{content}", true);
                    return null;
                }

            } else { //Create new
                Log($"\tDidn't find file at '{path}', returned null.", true);
                return null;
            }
        }

        public ChapterStats GetCurrentChapterStats() {
            string path = GetPathToFile($"{StatsFolder}/{CurrentChapterDebugName}.txt");

            bool hasEnteredThisSession = ChaptersThisSession.Contains(CurrentChapterDebugName);
            ChaptersThisSession.Add(CurrentChapterDebugName);
            Log($"CurrentChapterName: '{CurrentChapterDebugName}', hasEnteredThisSession: '{hasEnteredThisSession}', ChaptersThisSession: '{string.Join(", ", ChaptersThisSession)}'");

            ChapterStats toRet = null;

            if (File.Exists(path)) { //Parse File
                string content = File.ReadAllText(path);

                //[Try 1] New file format: JSON
                try {
                    toRet = JsonConvert.DeserializeObject<ChapterStats>(content);
                } catch (Exception) {
                    Log($"\tCouldn't read chapter stats as JSON, trying old stats format...", true);
                }

                if (toRet == null) {
                    //[Try 2] Old file format: selfmade text format
                    try {
                        toRet = ChapterStats.ParseString(content);
                        Log($"\tSaving chapter stats for map '{CurrentChapterDebugName}' in new format!", true);
                    } catch (Exception) {
                        Log($"\tCouldn't read old chapter stats, created new ChapterStats. Old chapter stats content:\n{content}", true);
                        toRet = new ChapterStats();
                        toRet.SetCurrentRoom(CurrentRoomName);
                    }
                }
                
            } else { //Create new
                toRet = new ChapterStats();
                toRet.SetCurrentRoom(CurrentRoomName);
            }

            if (!hasEnteredThisSession) {
                toRet.ResetCurrentSession();
            }
            toRet.ResetCurrentRun();

            return toRet;
        }

        public void SaveChapterStats() {
            if (CurrentChapterStats == null) {
                Log($"Aborting saving chapter stats as '{nameof(CurrentChapterStats)}' is null");
                return;
            }
            if (!ModSettings.Enabled) {
                return;
            }

            CurrentUpdateFrame++;

            CurrentChapterStats.ModState.PlayerIsHoldingGolden = PlayerIsHoldingGolden;
            CurrentChapterStats.ModState.GoldenDone = PlayerIsHoldingGolden && CurrentChapterStats.ModState.ChapterCompleted;

            CurrentChapterStats.ModState.DeathTrackingPaused = ModSettings.PauseDeathTracking;
            CurrentChapterStats.ModState.RecordingPath = ModSettings.RecordPath;
            CurrentChapterStats.ModState.OverlayVersion = OverlayVersion;
            CurrentChapterStats.ModState.ModVersion = ModVersion;
            CurrentChapterStats.ModState.ChapterHasPath = CurrentChapterPath != null;


            string path = GetPathToFile($"{StatsFolder}/{CurrentChapterDebugName}.txt");
            File.WriteAllText(path, JsonConvert.SerializeObject(CurrentChapterStats, Formatting.Indented));

            string modStatePath = GetPathToFile($"{StatsFolder}/modState.txt");

            string content = $"{CurrentChapterStats.CurrentRoom}\n{CurrentChapterStats.ChapterDebugName};{CurrentChapterStats.ModState}\n";
            File.WriteAllText(modStatePath, content);

            StatsManager.OutputFormats(CurrentChapterPath, CurrentChapterStats);
        }

        public void CreateChapterSummary(int attemptCount) {
            Log($"Attempting to create tracker summary, attemptCount = '{attemptCount}'");

            bool hasPathInfo = PathInfoExists();

            string relativeOutPath = $"{SummariesFolder}/{CurrentChapterDebugName}.txt";
            string outPath = GetPathToFile(relativeOutPath);

            if (!hasPathInfo) {
                Log($"Called CreateChapterSummary without chapter path info. Please create a path before using this feature");
                File.WriteAllText(outPath, "No path info was found for the current chapter.\nPlease create a path before using the summary feature");
                return;
            }

            CurrentChapterStats?.OutputSummary(outPath, CurrentChapterPath, attemptCount);
        }

        #endregion

        #region Default Path Creation

        public void CheckPrepackagedPaths() {
            CheckDefaultPathFile(nameof(Resources.Celeste_1_ForsakenCity_Normal), Resources.Celeste_1_ForsakenCity_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_1_ForsakenCity_BSide), Resources.Celeste_1_ForsakenCity_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_1_ForsakenCity_CSide), Resources.Celeste_1_ForsakenCity_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_2_OldSite_Normal), Resources.Celeste_2_OldSite_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_2_OldSite_BSide), Resources.Celeste_2_OldSite_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_2_OldSite_CSide), Resources.Celeste_2_OldSite_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_3_CelestialResort_Normal), Resources.Celeste_3_CelestialResort_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_3_CelestialResort_BSide), Resources.Celeste_3_CelestialResort_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_3_CelestialResort_CSide), Resources.Celeste_3_CelestialResort_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_4_GoldenRidge_Normal), Resources.Celeste_4_GoldenRidge_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_4_GoldenRidge_BSide), Resources.Celeste_4_GoldenRidge_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_4_GoldenRidge_CSide), Resources.Celeste_4_GoldenRidge_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_5_MirrorTemple_Normal), Resources.Celeste_5_MirrorTemple_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_5_MirrorTemple_BSide), Resources.Celeste_5_MirrorTemple_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_5_MirrorTemple_CSide), Resources.Celeste_5_MirrorTemple_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_6_Reflection_Normal), Resources.Celeste_6_Reflection_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_6_Reflection_BSide), Resources.Celeste_6_Reflection_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_6_Reflection_CSide), Resources.Celeste_6_Reflection_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_7_Summit_Normal), Resources.Celeste_7_Summit_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_7_Summit_BSide), Resources.Celeste_7_Summit_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_7_Summit_CSide), Resources.Celeste_7_Summit_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_9_Core_Normal), Resources.Celeste_9_Core_Normal);
            CheckDefaultPathFile(nameof(Resources.Celeste_9_Core_BSide), Resources.Celeste_9_Core_BSide);
            CheckDefaultPathFile(nameof(Resources.Celeste_9_Core_CSide), Resources.Celeste_9_Core_CSide);

            CheckDefaultPathFile(nameof(Resources.Celeste_LostLevels_Normal), Resources.Celeste_LostLevels_Normal);

        }
        private void CheckDefaultPathFile(string name, string content) {
            if (name != "Celeste_LostLevels_Normal") {
                string[] split = name.Split('_');
                string celeste = split[0];
                string chapterNum = split[1];
                string chapterName = split[2];
                string side = split[3];

                name = $"{celeste}_{chapterNum}-{chapterName}_{side}";
            }

            string path = GetPathToFile($"{PathsFolder}/{name}.txt");

            if (!File.Exists(path)) { 
                File.WriteAllText(path, content);
            }
        }

        public void CreateExternalTools() {
            Log($"Creating external tools files");
            
            //Overlay files
            CreateExternalToolFile("common.js", Resources.CCT_common_JS);
            CreateExternalToolFile("CCTOverlay.html", Resources.CCTOverlay_HTML);
            CreateExternalToolFile("CCTOverlay.js", Resources.CCTOverlay_JS);
            CreateExternalToolFile("CCTOverlay.css", Resources.CCTOverlay_CSS);
            CheckFolderExists(GetPathToFolder($"{ExternalToolsFolder}/img"));
            Resources.goldberry_GIF.Save(GetPathToFile($"{ExternalToolsFolder}/img/goldberry.gif"));

            //Path Edit Tool files

            //Format Edit Tool files
            CreateExternalToolFile("LiveDataEditTool.html", Resources.LiveDataEditTool_HTML);
            CreateExternalToolFile("LiveDataEditTool.js", Resources.LiveDataEditTool_JS);
            CreateExternalToolFile("LiveDataEditTool.css", Resources.LiveDataEditTool_CSS);
        }
        private void CreateExternalToolFile(string name, string content) {
            string path = GetPathToFile($"{ExternalToolsFolder}/{name}");
            File.WriteAllText(path, content);
        }

        #endregion

        #region Stats Data Control

        public void WipeChapterData() {
            if (CurrentChapterStats == null) {
                Log($"Aborting wiping chapter data as '{nameof(CurrentChapterStats)}' is null");
                return;
            }

            Log($"Wiping death data for chapter '{CurrentChapterDebugName}'");

            RoomStats currentRoom = CurrentChapterStats.CurrentRoom;
            List<string> toRemove = new List<string>();

            foreach (string debugName in CurrentChapterStats.Rooms.Keys) {
                if (debugName == currentRoom.DebugRoomName) continue;
                toRemove.Add(debugName);
            }

            foreach (string debugName in toRemove) {
                CurrentChapterStats.Rooms.Remove(debugName);
            }

            WipeRoomData();
        }

        public void RemoveRoomGoldenBerryDeaths() {
            if (CurrentChapterStats == null) {
                Log($"Aborting wiping room golden berry deaths as '{nameof(CurrentChapterStats)}' is null");
                return;
            }

            Log($"Wiping golden berry death data for room '{CurrentChapterStats.CurrentRoom.DebugRoomName}'");

            CurrentChapterStats.CurrentRoom.GoldenBerryDeaths = 0;
            CurrentChapterStats.CurrentRoom.GoldenBerryDeathsSession = 0;

            SaveChapterStats();
        }
        public void WipeChapterGoldenBerryDeaths() {
            if (CurrentChapterStats == null) {
                Log($"Aborting wiping chapter golden berry deaths as '{nameof(CurrentChapterStats)}' is null");
                return;
            }

            Log($"Wiping golden berry death data for chapter '{CurrentChapterDebugName}'");

            foreach (string debugName in CurrentChapterStats.Rooms.Keys) {
                CurrentChapterStats.Rooms[debugName].GoldenBerryDeaths = 0;
                CurrentChapterStats.Rooms[debugName].GoldenBerryDeathsSession = 0;
            }

            SaveChapterStats();
        }

        

        public void WipeRoomData() {
            if (CurrentChapterStats == null) {
                Log($"Aborting wiping room data as '{nameof(CurrentChapterStats)}' is null");
                return;
            }
            Log($"Wiping room data for room '{CurrentChapterStats.CurrentRoom.DebugRoomName}'");

            CurrentChapterStats.CurrentRoom.PreviousAttempts.Clear();
            SaveChapterStats();
        }

        public void RemoveLastDeathStreak() {
            if (CurrentChapterStats == null) {
                Log($"Aborting removing death streak as '{nameof(CurrentChapterStats)}' is null");
                return;
            }
            Log($"Removing death streak for room '{CurrentChapterStats.CurrentRoom.DebugRoomName}'");

            while (CurrentChapterStats.CurrentRoom.PreviousAttempts.Count > 0 && CurrentChapterStats.CurrentRoom.LastAttempt == false) {
                CurrentChapterStats.CurrentRoom.RemoveLastAttempt();
            }

            SaveChapterStats();
        }

        public void RemoveLastAttempt() {
            if (CurrentChapterStats == null) {
                Log($"Aborting removing death streak as '{nameof(CurrentChapterStats)}' is null");
                return;
            }
            Log($"Removing last attempt for room '{CurrentChapterStats.CurrentRoom.DebugRoomName}'");

            CurrentChapterStats.CurrentRoom.RemoveLastAttempt();
            SaveChapterStats();
        }
        public void AddRoomAttempt(bool success) {
            if (CurrentChapterStats == null) {
                Log($"Aborting adding room attempt ({success}) as '{nameof(CurrentChapterStats)}' is null");
                return;
            }

            Log($"Adding room attempt ({success}) to '{CurrentChapterStats.CurrentRoom.DebugRoomName}'");

            CurrentChapterStats.AddAttempt(success);

            SaveChapterStats();
        }

        #endregion

        #region Path Management

        public void SaveRecordedRoomPath() {
            Log($"Saving recorded path...");
            if (PathRec.TotalRecordedRooms <= 1) {
                Log($"Path is too short to save. ({PathRec.TotalRecordedRooms} rooms)");
                return;
            }
            
            DisabledInRoomName = CurrentRoomName;
            SetCurrentChapterPath(PathRec.ToPathInfo());
            Log($"Recorded path:\n{JsonConvert.SerializeObject(CurrentChapterPath)}", true);
            SavePathToFile();
        }
        public void SavePathToFile(PathInfo path = null, string pathName = null) {
            if (path == null) {
                path = CurrentChapterPath;
            }
            if (pathName == null) {
                pathName = CurrentChapterDebugName;
            }

            string relativeOutPath = $"{PathsFolder}/{pathName}.txt";
            string outPath = GetPathToFile(relativeOutPath);
            File.WriteAllText(outPath, JsonConvert.SerializeObject(path, Formatting.Indented));
            Log($"Wrote path data to '{relativeOutPath}'");
        }

        public void RemoveRoomFromChapter() {
            if (CurrentChapterPath == null) {
                Log($"CurrentChapterPath was null");
                return;
            }

            bool foundRoom = false;
            foreach (CheckpointInfo cpInfo in CurrentChapterPath.Checkpoints) {
                foreach (RoomInfo rInfo in cpInfo.Rooms) {
                    if (rInfo.DebugRoomName != CurrentRoomName) continue;

                    cpInfo.Rooms.Remove(rInfo);
                    foundRoom = true;
                    break;
                }

                if (foundRoom) break;
            }

            if (foundRoom) {
                SavePathToFile();
            }
        }

        #endregion

        #region Logging
        private bool LogInitialized = false;
        private StreamWriter LogFileWriter = null;
        public void LogInit() {
            string logFileMax = GetPathToFile($"{LogsFolder}/log_old{LOG_FILE_COUNT}.txt");
            if (File.Exists(logFileMax)) {
                File.Delete(logFileMax);
            }

            for (int i = LOG_FILE_COUNT - 1; i >= 1; i--) {
                string logFilePath = GetPathToFile($"{LogsFolder}/log_old{i}.txt");
                if (File.Exists(logFilePath)) {
                    string logFileNewPath = GetPathToFile($"{LogsFolder}/log_old{i+1}.txt");
                    File.Move(logFilePath, logFileNewPath);
                }
            }

            string lastFile = GetPathToFile($"{LogsFolder}/log.txt");
            if (File.Exists(lastFile)) {
                string logFileNewPath = GetPathToFile($"{LogsFolder}/log_old{1}.txt");
                File.Move(lastFile, logFileNewPath);
            }

            string path = GetPathToFile($"{LogsFolder}/log.txt");
            LogFileWriter = new StreamWriter(path) {
                AutoFlush = true
            };
            LogInitialized = true;
        }
        public void Log(string log, bool isFollowup = false, bool isComingFromVerbose = false) {
            if (!LogInitialized) {
                return;
            }

            if (!isFollowup) {
                int frameBack = 1;
                if (isComingFromVerbose) {
                    frameBack = 2;
                }

                StackFrame frame = new StackTrace().GetFrame(frameBack);
                string methodName = frame.GetMethod().Name;
                string typeName = frame.GetMethod().DeclaringType.Name;

                string time = DateTime.Now.ToString("HH:mm:ss.ffff");

                LogFileWriter.WriteLine($"[{time}]\t[{typeName}.{methodName}]\t{log}");
            } else {
                LogFileWriter.WriteLine($"\t\t{log}");
            }
        }

        public void LogVerbose(string message, bool isFollowup = false) {
            if (ModSettings.VerboseLogging) { 
                Log(message, isFollowup, true);
            }
        }

        public void LogCleanup() {
            LogFileWriter?.Close();
            LogFileWriter?.Dispose();
        }
        #endregion

        #region Physics Logging

        private Vector2 PhysicsLogLastExactPos = Vector2.Zero;
        private bool PhysicsLogLastFrameEnabled = false;
        private StreamWriter PhysicsLogWriter = null;
        private long PhysicsLogFrame = -1;
        private bool PhysicsLogPosition, PhysicsLogSpeed, PhysicsLogVelocity, PhysicsLogLiftBoost, PhysicsLogFlags, PhysicsLogInputs;
        private bool PhysicsLogIsFrozen = false;
        private Player PhysicsLogLastPlayer = null;

        private int PhysicsLogTasFrameCount = 0;
        private string PhysicsLogTasInputs = null;
        private string PhysicsLogTasFileContent = null;
        
        private void Engine_Update(On.Monocle.Engine.orig_Update orig, Engine self, GameTime gameTime) {
            PhysicsLogIsFrozen = Engine.FreezeTimer > 0;

            orig(self, gameTime);

            if (Engine.Scene is Level level) {
                Player player = level.Tracker.GetEntity<Player>();
                LogPhysicsUpdate(player);
            }
        }

        private void LogPhysicsUpdate(Player player) {
            if (player == null) {
                if (PhysicsLogLastPlayer == null) return;
                player = PhysicsLogLastPlayer;
            }
            PhysicsLogLastPlayer = player;


            bool logPhysics = ModSettings.LogPhysics;
            if (logPhysics && !PhysicsLogLastFrameEnabled) {
                //should log now, but didnt previously
                PhysicsLogPosition = ModSettings.LogPosition;
                PhysicsLogSpeed = ModSettings.LogSpeed;
                PhysicsLogVelocity = ModSettings.LogVelocity;
                PhysicsLogLiftBoost = ModSettings.LogLiftBoost;
                PhysicsLogFlags = ModSettings.LogFlags;
                PhysicsLogInputs = ModSettings.LogInputs;

                PhysicsLogWriter = new StreamWriter(GetPathToFile($"{LogsFolder}/position_log.txt"));
                PhysicsLogWriter.WriteLine(GetPhysicsLogHeader(PhysicsLogPosition, PhysicsLogSpeed, PhysicsLogVelocity, PhysicsLogLiftBoost, PhysicsLogFlags, PhysicsLogInputs));
                PhysicsLogFrame = 0;
                PhysicsLogLastFrameEnabled = logPhysics;

                PhysicsLogTasFileContent = "#Generated by CCT\n";
                PhysicsLogFrame = 0;
                PhysicsLogTasInputs = GetInputsTASFormatted();

            } else if (!logPhysics && PhysicsLogLastFrameEnabled) {
                //previously logged, but shouldnt now
                //close log file writer
                PhysicsLogWriter.Close();
                PhysicsLogWriter.Dispose();
                PhysicsLogWriter = null;
                PhysicsLogLastFrameEnabled = logPhysics;

                if (ModSettings.LogPhysicsInputsToTasFile) { 
                    TextInput.SetClipboardText(PhysicsLogTasFileContent);
                    PhysicsLogTasFileContent = "";
                }
                
                return;

            } else if (logPhysics && PhysicsLogLastFrameEnabled) {
                //should log now, and did previously
                //do nothing
            } else {
                //shouldnt log now, and didnt previously
                return;
            }

            Vector2 pos = player.ExactPosition;
            Vector2 speed = player.Speed;

            Vector2 velocity = Vector2.Zero;
            if (PhysicsLogLastExactPos != Vector2.Zero) {
                velocity = pos - PhysicsLogLastExactPos;
            }

            PhysicsLogLastExactPos = pos;
            Vector2 liftboost = player.LiftSpeed;
            PhysicsLogFrame++;

            int flipYFactor = ModSettings.LogPhysicsFlipY ? -1 : 1;

            string toWrite = $"{PhysicsLogFrame}";
            if (PhysicsLogPosition) {
                toWrite += $",{pos.X},{pos.Y * flipYFactor}";
            }
            if (PhysicsLogSpeed) {
                toWrite += $",{speed.X},{speed.Y * flipYFactor}";
            }
            if (PhysicsLogVelocity) {
                toWrite += $",{velocity.X},{velocity.Y * flipYFactor}";
            }
            if (PhysicsLogLiftBoost) {
                toWrite += $",{liftboost.X},{liftboost.Y * flipYFactor}";
            }
            if (PhysicsLogFlags) {
                toWrite += $",{GetPlayerFlagsFormatted(player)}";
            }


            PhysicsLogUpdateJumpState();
            
            if (PhysicsLogInputs) {
                toWrite += $",{GetInputsFormatted()}";
            }

            if (ModSettings.LogPhysicsInputsToTasFile) { 
                string tasInputs = GetInputsTASFormatted();
                if (tasInputs != PhysicsLogTasInputs) {
                    //new input combination, write old one to file
                    PhysicsLogTasFileContent += $"{PhysicsLogTasFrameCount},{PhysicsLogTasInputs}\n";
                    PhysicsLogTasInputs = tasInputs;
                    PhysicsLogTasFrameCount = 0;
                }
                PhysicsLogTasFrameCount++;
            }


            PhysicsLogWriter.WriteLine(toWrite);
        }

        public string GetPhysicsLogHeader(bool position, bool speed, bool velocity, bool liftBoost, bool flags, bool inputs) {
            string header = "Frame";
            if (position) {
                header += ",Position X,Position Y";
            }
            if (speed) {
                header += ",Speed X,Speed Y";
            }
            if (velocity) {
                header += ",Velocity X,Velocity Y";
            }
            if (liftBoost) {
                header += ",LiftBoost X,LiftBoost Y";
            }
            if (flags) {
                header += ",Flags";
            }
            if (inputs) {
                header += ",Inputs";
            }
            return header;
        }

        private Dictionary<int, string> PhysicsLogStatesToCheck = new Dictionary<int, string>() {
            [Player.StAttract] = nameof(Player.StAttract),
            [Player.StBoost] = nameof(Player.StBoost),
            [Player.StCassetteFly] = nameof(Player.StCassetteFly),
            [Player.StClimb] = nameof(Player.StClimb),
            [Player.StDash] = nameof(Player.StDash),
            [Player.StDreamDash] = nameof(Player.StDreamDash),
            [Player.StDummy] = nameof(Player.StDummy),
            [Player.StFlingBird] = nameof(Player.StFlingBird),
            [Player.StFrozen] = nameof(Player.StFrozen),
            [Player.StHitSquash] = nameof(Player.StHitSquash),
            [Player.StLaunch] = nameof(Player.StLaunch),
            [Player.StNormal] = nameof(Player.StNormal),
            [Player.StPickup] = nameof(Player.StPickup),
            [Player.StRedDash] = nameof(Player.StRedDash),
            [Player.StReflectionFall] = nameof(Player.StReflectionFall),
            [Player.StStarFly] = nameof(Player.StStarFly),
            [Player.StSummitLaunch] = nameof(Player.StSummitLaunch),
            [Player.StSwim] = nameof(Player.StSwim),
            [Player.StTempleFall] = nameof(Player.StTempleFall),
        };
        public string GetPlayerFlagsFormatted(Player player) {
            string flags = "";
            if (player.Dead) {
                flags += "Dead ";
            }
            if (PhysicsLogIsFrozen) {
                flags += "Frozen ";
            }
            if (PhysicsLogStatesToCheck.ContainsKey(player.StateMachine.State)) {
                flags += $"{PhysicsLogStatesToCheck[player.StateMachine.State]} ";
            } else {
                flags += $"StOther ";
            }

            return flags.TrimEnd(' ');
        }

        private bool PhysicsLogHeldJumpLastFrame = false;
        private bool PhysicsLogHoldingSecondJump = false;
        private void PhysicsLogUpdateJumpState() {
            //Log($"Pre frame: Jump.Check -> {Input.Jump.Check}, Jump.Pressed -> {Input.Jump.Pressed}, Held Jump Last Frame -> {PhysicsLogHeldJumpLastFrame}, Holding Second Jump -> {PhysicsLogHoldingSecondJump}");
            if (Input.Jump.Check) {
                if (PhysicsLogHeldJumpLastFrame && Input.Jump.Pressed) {
                    PhysicsLogHoldingSecondJump = !PhysicsLogHoldingSecondJump;
                }
                PhysicsLogHeldJumpLastFrame = true;
            } else {
                PhysicsLogHeldJumpLastFrame = false;
                PhysicsLogHoldingSecondJump = false;
            }
        }
        
        public string GetInputsFormatted(char separator = ' ') {
            string inputs = "";

            if (Input.MoveX.Value != 0) {
                string rightleft = Input.MoveX.Value > 0 ? "R" : "L";
                inputs += $"{rightleft}{separator}";
            }
            if (Input.MoveY.Value != 0) {
                string updown = Input.MoveY.Value > 0 ? "D" : "U";
                inputs += $"{updown}{separator}";
            }

            IngameOverlay.SetText(4, $"Jump.Check: {Input.Jump.Check}\nJump.Pressed: {Input.Jump.Pressed}\nJump.Released: {Input.Jump.Released}\nCheck Last Frame: {PhysicsLogHeldJumpLastFrame}\nHolding Second Jump: {PhysicsLogHoldingSecondJump}");

            if (Input.Jump.Check) {
                if (PhysicsLogHoldingSecondJump) {
                    inputs += $"K{separator}";
                } else {
                    inputs += $"J{separator}";
                }
            }
            
            if (Input.Dash.Check) {
                inputs += $"X{separator}";
            }
            if (Input.CrouchDash.Check) {
                inputs += $"Z{separator}";
            }
            if (Input.Grab.Check) {
                inputs += $"G{separator}";
            }

            return inputs.TrimEnd(separator);
        }
        public string GetInputsTASFormatted() {
            return GetInputsFormatted(',');
        }
        #endregion

        #region Util
        public static string SanitizeSIDForDialog(string sid) {
            string bsSuffix = "_pqeijpvqie";
            string dialogCleaned = Dialog.Get($"{sid}{bsSuffix}");
            return dialogCleaned.Substring(1, sid.Length);
        }

        public void InsertCheckpointIntoPath(Checkpoint cp, string roomName) {
            if (roomName == null) {
                PathRec.AddCheckpoint(cp, PathRecorder.DefaultCheckpointName);
                return;
            }

            string cpDialogName = $"{CurrentChapterStats.ChapterSIDDialogSanitized}_{roomName}";
            Log($"cpDialogName: {cpDialogName}");
            string cpName = Dialog.Get(cpDialogName);
            Log($"Dialog.Get says: {cpName}");

            //if (cpName.Length+1 >= cpDialogName.Length && cpName.Substring(1, cpDialogName.Length) == cpDialogName) cpName = null;
            if (cpName.StartsWith("[") && cpName.EndsWith("]")) cpName = null;

            PathRec.AddCheckpoint(cp, cpName);
        }
        #endregion
    }
}
