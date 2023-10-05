﻿using Celeste.Editor;
using Celeste.Mod.ConsistencyTracker.Entities;
using Celeste.Mod.ConsistencyTracker.Entities.Summary;
using Celeste.Mod.ConsistencyTracker.Enums;
using Celeste.Mod.ConsistencyTracker.EverestInterop;
using Celeste.Mod.ConsistencyTracker.Models;
using Celeste.Mod.ConsistencyTracker.PhysicsLog;
using Celeste.Mod.ConsistencyTracker.Stats;
using Celeste.Mod.ConsistencyTracker.ThirdParty;
using Celeste.Mod.ConsistencyTracker.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using MonoMod.ModInterop;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Celeste.Mod.ConsistencyTracker {
    public class ConsistencyTrackerModule : EverestModule {

        public static ConsistencyTrackerModule Instance;
        private static readonly int LOG_FILE_COUNT = 10;

        #region Versions
        public class VersionsNewest {
            public static string Mod => "2.4.0";
            public static string Overlay => "2.0.0";
            public static string LiveDataEditor => "1.0.0";
            public static string PhysicsInspector => "1.2.0";
        }
        public class VersionsCurrent {
            public static string Overlay {
                get => Instance.ModSettings.OverlayVersion;
                set => Instance.ModSettings.OverlayVersion = value;
            }
            public static string LiveDataEditor {
                get => Instance.ModSettings.LiveDataEditorVersion;
                set => Instance.ModSettings.LiveDataEditorVersion = value;
            }
            public static string PhysicsInspector {
                get => Instance.ModSettings.PhysicsInspectorVersion;
                set => Instance.ModSettings.PhysicsInspectorVersion = value;
            }
        }
        #endregion

        public override Type SettingsType => typeof(ConsistencyTrackerSettings);
        public ConsistencyTrackerSettings ModSettings => (ConsistencyTrackerSettings)this._Settings;

        public static string BaseFolderPath = "./ConsistencyTracker/";
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
                Log($"DoRecordPath is now '{_DoRecordPath}'");
            }
        }
        private bool _DoRecordPath = false;
        public PathRecorder PathRec;
        private string DisabledInRoomName;
        public bool AbortPathRecording = false;

        #endregion

        #region State Variables

        //Used to cache and prevent unnecessary operations via DebugRC
        public long CurrentUpdateFrame;

        public PathSegmentList CurrentChapterPathSegmentList { get; set; }
        public PathInfo CurrentChapterPath {
            get => CurrentChapterPathSegmentList?.CurrentPath;
            set {
                if (CurrentChapterPathSegmentList != null) {
                    CurrentChapterPathSegmentList.CurrentPath = value;
                }
            }
        }
        public int SelectedPathSegmentIndex {
            get => CurrentChapterPathSegmentList?.SelectedIndex ?? 0;
        }

        public ChapterStatsList CurrentChapterStatsList { get; set; }
        public ChapterStats CurrentChapterStats { 
            get => CurrentChapterStatsList?.GetStats(SelectedPathSegmentIndex);
            set => CurrentChapterStatsList?.SetStats(SelectedPathSegmentIndex, value);
        }

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
        public SummaryHud SummaryOverlay;
        public PhysicsLogger PhysicsLog;
        public PacePingManager PacePingManager;


        public ConsistencyTrackerModule() {
            Instance = this;
        }

        #region Load/Unload Stuff

        public override void Load() {
            CheckRootFolder();
            CheckFolderExists(GetPathToFile(LogsFolder));
            LogInit();
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Log($"~~~==== CCT STARTED ({time}) ====~~~");
            
            CheckFolderExists(GetPathToFile(PathsFolder));
            CheckPrepackagedPaths(reset:false);
            
            CheckFolderExists(GetPathToFile(StatsFolder));
            CheckFolderExists(GetPathToFile(SummariesFolder));


            CheckFolderExists(GetPathToFile(ExternalToolsFolder));
            UpdateExternalTools();


            Log($"Mod Settings -> \n{JsonConvert.SerializeObject(ModSettings, Formatting.Indented)}");
            Log($"~~~==============================~~~");

            PhysicsLog = new PhysicsLogger();

            HookStuff();

            PacePingManager = new PacePingManager();

            //Interop
            DebugRcPage.Load();
            typeof(ConsistencyTrackerAPI).ModInterop();
        }

        public override void Unload() {
            Log($"Called Unload");
            UnHookStuff();
            
            if (PhysicsLogger.Settings.IsRecording) {
                PhysicsLog.StopRecording();
            }

            DebugRcPage.Unload();
            LogCleanup();
        }

        private void HookStuff() {
            //Create stats manager
            Everest.Events.MainMenu.OnCreateButtons += MainMenu_OnCreateButtons;
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
            On.Celeste.Strawberry.OnPlayer += Strawberry_OnPlayer; //sorta works, but triggers very often for a single berry
            On.Celeste.Strawberry.OnCollect += On_Strawberry_OnCollect;

            //Changing lava/ice in Core
            On.Celeste.CoreModeToggle.OnChangeMode += CoreModeToggle_OnChangeMode; //works

            //Picking up a Cassette tape
            On.Celeste.Cassette.OnPlayer += Cassette_OnPlayer; //works

            //Open up key doors?
            //On.Celeste.Door.Open += Door_Open; //Wrong door (those are the resort doors)
            On.Celeste.LockBlock.TryOpen += LockBlock_TryOpen; //works

            On.Monocle.Engine.Update += PhysicsLog.Engine_Update;
            //On.Monocle.Engine.Update += Engine_Update;

            On.Celeste.Editor.MapEditor.Render += MapEditor_Render;
        }

        private void UnHookStuff() {
            Everest.Events.MainMenu.OnCreateButtons -= MainMenu_OnCreateButtons;
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
            On.Celeste.Strawberry.OnCollect -= On_Strawberry_OnCollect;

            //Changing lava/ice in Core
            On.Celeste.CoreModeToggle.OnChangeMode -= CoreModeToggle_OnChangeMode;

            //Picking up a Cassette tape
            On.Celeste.Cassette.OnPlayer -= Cassette_OnPlayer;

            //Open up key doors
            On.Celeste.LockBlock.TryOpen -= LockBlock_TryOpen;

            On.Monocle.Engine.Update -= PhysicsLog.Engine_Update;
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
        private void MainMenu_OnCreateButtons(OuiMainMenu menu, System.Collections.Generic.List<MenuButton> buttons) {
            StatsManager = new StatManager();
        }

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

            GoldenType goldenType = GoldenType.Golden;
            switch (self.GetType().Name) {
                case "Strawberry":
                    goldenType = GoldenType.Golden;
                    break;
                case "SilverBerry":
                    goldenType = GoldenType.Silver;
                    break;
                case "PlatinumBerry":
                    goldenType = GoldenType.Platinum;
                    break;
            }

            if (self.Golden) {
                PlayerIsHoldingGolden = true;
                CurrentChapterStats.GoldenBerryType = goldenType;
                SaveChapterStats();
            }
        }

        private void On_Strawberry_OnCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
            orig(self);

            Log($"Berry collected! Type Name: '{self.GetType().Name}', Golden: '{self.Golden}', Winged: '{self.Winged}'");
            
            GoldenType goldenType = GoldenType.Golden;
            switch (self.GetType().Name) {
                case "Strawberry":
                    goldenType = GoldenType.Golden;
                    break;
                case "SilverBerry":
                    goldenType = GoldenType.Silver;
                    break;
                case "PlatinumBerry":
                    goldenType = GoldenType.Platinum;
                    break;
            }

            if (self.Golden) {
                CollectedGoldenBerry(goldenType);
            }
        }

        private void CoreModeToggle_OnChangeMode(On.Celeste.CoreModeToggle.orig_OnChangeMode orig, CoreModeToggle self, Session.CoreModes mode) {
            LogVerbose($"Changed core mode to '{mode}'");
            orig(self, mode);
            SetRoomCompleted(resetOnDeath:true);
        }

        private void Checkpoint_TurnOn(On.Celeste.Checkpoint.orig_TurnOn orig, Checkpoint cp, bool animate) {
            orig(cp, animate);

            if (Engine.Scene is Level level) {
                LogVerbose($"Checkpoint in room '{level.Session.LevelData.Name}'");
            } else {
                LogVerbose($"Engine.Scene is not Level...");
                return;
            }

            if (level.Session == null) {
                LogVerbose($"level.Session is null");
                return;
            }
            if (level.Session.LevelData == null) {
                LogVerbose($"level.Session.LevelData is null");
                return;
            }

            string roomName = SanitizeRoomName(level.Session.LevelData.Name);

            Log($"cp.Position={cp.Position}, Room Name='{roomName}'");
            if (DoRecordPath) {
                InsertCheckpointIntoPath(cp, roomName);
            }

            LastRoomWithCheckpoint = roomName;
        }

        //Not triggered when teleporting via debug map
        private void Level_TeleportTo(On.Celeste.Level.orig_TeleportTo orig, Level level, Player player, string nextLevel, Player.IntroTypes introType, Vector2? nearestSpawn) {
            orig(level, player, nextLevel, introType, nearestSpawn);

            string roomName = SanitizeRoomName(level.Session.LevelData.Name);
            Log($"level.Session.LevelData.Name={roomName}");

            //if (ModSettings.CountTeleportsForRoomTransitions && CurrentRoomName != null && roomName != CurrentRoomName) {
            //    bool holdingGolden = PlayerIsHoldingGoldenBerry(level.Tracker.GetEntity<Player>());
            //    SetNewRoom(roomName, true, holdingGolden);
            //}
        }

        private void Level_OnLoadLevel(Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
            string newCurrentRoom = SanitizeRoomName(level.Session.LevelData.Name);
            PlayerIsHoldingGolden = PlayerIsHoldingGoldenBerry(level.Tracker.GetEntity<Player>());

            Log($"level.Session.LevelData.Name={newCurrentRoom}, playerIntro={playerIntro} | CurrentRoomName: '{CurrentRoomName}', PreviousRoomName: '{PreviousRoomName}', holdingGolden: '{PlayerIsHoldingGolden}'");

            //Changing room via golden berry death or debug map teleport
            if (playerIntro == Player.IntroTypes.Respawn && CurrentRoomName != null && newCurrentRoom != CurrentRoomName) {
                if (level.Session.LevelData.HasCheckpoint) {
                    LastRoomWithCheckpoint = newCurrentRoom;
                }
                SetNewRoom(newCurrentRoom, false, PlayerIsHoldingGolden);
            }
            //Teleporters?
            if (playerIntro == Player.IntroTypes.Transition && CurrentRoomName != null && newCurrentRoom != CurrentRoomName) {
                if (level.Session.LevelData.HasCheckpoint) {
                    LastRoomWithCheckpoint = newCurrentRoom;
                }
                SetNewRoom(newCurrentRoom, true, PlayerIsHoldingGolden);
            }

            if (DidRestart) {
                if (level.Session.LevelData.HasCheckpoint) {
                    LastRoomWithCheckpoint = newCurrentRoom;
                }
                Log($"\tRequested reset of PreviousRoomName to null", true);
                DidRestart = false;
                SetNewRoom(newCurrentRoom, false, PlayerIsHoldingGolden);
                PreviousRoomName = null;
            }

            if (isFromLoader) {
                Log("Adding overlay!");
                IngameOverlay = new TextOverlay();
                level.Add(IngameOverlay);

                SummaryOverlay = new SummaryHud();
                level.Add(SummaryOverlay);
            }
        }

        private void Level_OnExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
            Log($"mode={mode}, snow={snow}");
            if (mode == LevelExit.Mode.Restart) {
                DidRestart = true;
                if (PlayerIsHoldingGolden 
                    && ModSettings.TrackingRestartChapterCountsForGoldenDeath
                    && (!ModSettings.PauseDeathTracking || ModSettings.TrackingAlwaysGoldenDeaths)) {
                    CurrentChapterStats?.AddGoldenBerryDeath();
                    PacePingManager.DiedWithGolden(CurrentChapterPath, CurrentChapterStats);
                }

            } else if (mode == LevelExit.Mode.GoldenBerryRestart) {
                DidRestart = true;

                if (!ModSettings.PauseDeathTracking || ModSettings.TrackingAlwaysGoldenDeaths) { //Only count golden berry deaths when enabled
                    CurrentChapterStats?.AddGoldenBerryDeath();
                    if (ModSettings.TrackingOnlyWithGoldenBerry) {
                        CurrentChapterStats.AddAttempt(false);
                    }
                    PacePingManager.DiedWithGolden(CurrentChapterPath, CurrentChapterStats);
                }
            }

            if (DoRecordPath) { //Abort path recording when exiting level
                AbortPathRecording = true;
                DoRecordPath = false;
                ModSettings.RecordPath = false;
            }

            if (PhysicsLogger.Settings.IsRecording) {
                PhysicsLog.StopRecording();
                PhysicsLog.IsInMap = false;
            }
        }

        private void Level_OnComplete(Level level) {
            Log($"Incrementing {CurrentChapterStats?.CurrentRoom.DebugRoomName}");
            if(!ModSettings.PauseDeathTracking && (!ModSettings.TrackingOnlyWithGoldenBerry || PlayerIsHoldingGolden))
                CurrentChapterStats?.AddAttempt(true);
            CurrentChapterStats.ModState.ChapterCompleted = true;
            SaveChapterStats();

            if (DoRecordPath) { //Auto disable path recording when completing level
                DoRecordPath = false;
                ModSettings.RecordPath = false;
            }
        }

        private void Level_Begin(On.Celeste.Level.orig_Begin orig, Level level) {
            Log($"Calling ChangeChapter with 'level.Session'");
            ChangeChapter(level.Session);
            orig(level);
        }

        //**** USE THIS WHEN MORE NON-FUNCTIONING ROOM TRANSITIONS ARE FOUND ****//
        //private bool engineDelayHadSetTimer = false;
        //private int engineDelayTimer = 0;
        //private int engineDelayTime = 3;
        //public void Engine_Update(On.Monocle.Engine.orig_Update orig, Engine self, GameTime gameTime) {
        //    orig(self, gameTime);


        //    if (!(Engine.Scene is Level)) return;
        //    Level level = Engine.Scene as Level;
        //    Player player = level.Tracker.GetEntity<Player>();
        //    string roomNameNoSani = level.Session.LevelData.Name;

        //    if (level.Session.LevelData.HasCheckpoint) {
        //        LastRoomWithCheckpoint = roomNameNoSani;
        //    }

        //    string roomName = SanitizeRoomName(roomNameNoSani);

        //    if (CurrentRoomName != null && roomName != CurrentRoomName && ModSettings.CountTeleportsForRoomTransitions) {
        //        if (!engineDelayHadSetTimer) {
        //            engineDelayHadSetTimer = true;
        //            engineDelayTimer = engineDelayTime;
        //        }

        //        if (engineDelayTimer > 0) {
        //            engineDelayTimer--;
        //            return;
        //        }
        //        Log($"Engine.Update found a special room transition!");

        //        bool holdingGolden = PlayerIsHoldingGoldenBerry(player);
        //        SetNewRoom(roomName, true, holdingGolden);
        //    }
        //}
        
        private void Level_OnTransitionTo(Level level, LevelData levelDataNext, Vector2 direction) {
            if (levelDataNext.HasCheckpoint) {
                LastRoomWithCheckpoint = levelDataNext.Name;
            }

            string roomName = SanitizeRoomName(levelDataNext.Name);
            Log($"levelData.Name->{roomName}");

            if (CurrentRoomName != null && roomName != CurrentRoomName) {
                bool holdingGolden = PlayerIsHoldingGoldenBerry(level.Tracker.GetEntity<Player>());
                SetNewRoom(roomName, true, holdingGolden);
            }
        }

        private void Player_OnDie(Player player) {
            TouchedBerries.Clear();
            PlayerIsHoldingGolden = PlayerIsHoldingGoldenBerry(player);

            Log($"Player died. (holdingGolden: {PlayerIsHoldingGolden})");
            if (_CurrentRoomCompletedResetOnDeath) {
                _CurrentRoomCompleted = false;
            }

            if (!ModSettings.PauseDeathTracking && (!ModSettings.TrackingOnlyWithGoldenBerry || PlayerIsHoldingGolden))
                CurrentChapterStats?.AddAttempt(false);

            if(CurrentChapterStats != null)
                CurrentChapterStats.CurrentRoom.DeathsInCurrentRun++;

            SaveChapterStats();
        }

        #endregion

        #region State Management

        private string SanitizeRoomName(string name) {
            name = name.Replace(";", "");
            return name;
        }

        private void ChangeChapter(Session session) {
            Log($"Called chapter change");
            ChapterMetaInfo chapterInfo = new ChapterMetaInfo(session);

            Log($"Level->{session.Level}, chapterInfo->'{chapterInfo}'");

            CurrentChapterDebugName = chapterInfo.ChapterDebugName;

            //string test2 = Dialog.Get($"luma_farewellbb_FarewellBB_b_intro");
            //Log($"Dialog Test 2: {test2}");

            PreviousRoomName = null;
            CurrentRoomName = session.Level;

            CurrentChapterStatsList = GetCurrentChapterStats();
            SetCurrentChapterPathList(GetPathInfo(), chapterInfo);

            //Set the meta info after the path has been read, to ensure the selected segment (which might not be 0) has correct meta info
            SetChapterMetaInfo(chapterInfo);

            //fix for SpeedrunTool savestate inconsistency
            TouchedBerries.Clear();
            
            //Cause initial stats calculation
            SetNewRoom(CurrentRoomName, false, false);

            //Reset session
            bool hasEnteredThisSession = ChaptersThisSession.Contains(CurrentChapterDebugName);
            ChaptersThisSession.Add(CurrentChapterDebugName);
            if (!hasEnteredThisSession) {
                CurrentChapterStats.ResetCurrentSession(CurrentChapterPath);
            }
            CurrentChapterStats.ResetCurrentRun();
            //Another stats calculation, accounting for reset session
            SaveChapterStats();

            if (session.LevelData.HasCheckpoint) {
                LastRoomWithCheckpoint = CurrentRoomName;
            } else {
                LastRoomWithCheckpoint = null;
            }

            if (PhysicsLogger.Settings.IsRecording && PhysicsLog.IsInMap) {
                PhysicsLog.SegmentLog(true);
            }

            PhysicsLog.IsInMap = true;
            PacePingManager.ResetRun();
        }

        public void SetChapterMetaInfo(ChapterMetaInfo chapterInfo, ChapterStats stats = null) {
            if (stats == null) stats = CurrentChapterStats;
            if (stats == null) return;

            CurrentChapterStats.ChapterDebugName = CurrentChapterDebugName;
            CurrentChapterStats.CampaignName = chapterInfo.CampaignName;
            CurrentChapterStats.ChapterName = chapterInfo.ChapterName;
            CurrentChapterStats.ChapterSID = chapterInfo.ChapterSID;
            CurrentChapterStats.ChapterSIDDialogSanitized = chapterInfo.ChapterSIDDialogSanitized;
            CurrentChapterStats.SideName = chapterInfo.SideName;
        }

        public void SetCurrentChapterPathList(PathSegmentList pathList, ChapterMetaInfo chapterInfo = null) {
            CurrentChapterPathSegmentList = pathList;
            if (CurrentChapterPath != null) {
                CurrentChapterPath.SegmentList = CurrentChapterPathSegmentList;
                
                CurrentChapterPath.SetCheckpointRefs();

                if (CurrentChapterPath.ChapterName == null && chapterInfo != null) {
                    CurrentChapterPath.CampaignName = chapterInfo.CampaignName;
                    CurrentChapterPath.ChapterName = chapterInfo.ChapterName;
                    CurrentChapterPath.ChapterSID = chapterInfo.ChapterSID;
                    CurrentChapterPath.SideName = chapterInfo.SideName;
                    SavePathToFile();
                }
            }
        }
        public void SetCurrentChapterPath(PathInfo path, ChapterMetaInfo chapterInfo = null) {
            if (CurrentChapterPathSegmentList == null) {
                CurrentChapterPathSegmentList = PathSegmentList.Create();
            }

            CurrentChapterPath = path;
            SetCurrentChapterPathList(CurrentChapterPathSegmentList, chapterInfo);
        }

        public void SetCurrentChapterPathSegment(int segmentIndex) {
            if (CurrentChapterPathSegmentList == null) return;
            CurrentChapterPathSegmentList.SelectedIndex = segmentIndex;

            ChapterMetaInfo chapterInfo = null;
            if (Engine.Scene is Level level) {
                chapterInfo = new ChapterMetaInfo(level.Session);
            }
            SetCurrentChapterPath(CurrentChapterPath, chapterInfo);//To set checkpoint refs and stuff
            
            SavePathToFile();
            if (CurrentChapterPath != null) {
                StatsManager.AggregateStatsPassOnce(CurrentChapterPath);
            }
            SetNewRoom(CurrentRoomName, false);
        }
        public bool SetCurrentChapterPathSegmentName(string name) {
            if (string.IsNullOrEmpty(name) || CurrentChapterPathSegmentList == null) return false;

            CurrentChapterPathSegmentList.CurrentSegment.Name = name;
            SavePathToFile();
            SaveChapterStats();
            return true;
        }
        public PathSegment AddCurrentChapterPathSegment() {
            if (CurrentChapterPathSegmentList == null) return null;

            PathSegment segment = new PathSegment() {
                Name = $"Segment {CurrentChapterPathSegmentList.Segments.Count + 1}",
                Path = null,
            };
            CurrentChapterPathSegmentList.Segments.Add(segment);
            SavePathToFile();
            return segment;
        }
        public bool DeleteCurrentChapterPathSegment() {
            if (CurrentChapterPathSegmentList == null) return false;
            int segmentIndex = CurrentChapterPathSegmentList.SelectedIndex;
            if (segmentIndex >= CurrentChapterPathSegmentList.Segments.Count || CurrentChapterPathSegmentList.Segments.Count <= 1) return false;
            
            CurrentChapterPathSegmentList.RemoveSegment(segmentIndex);
            CurrentChapterStatsList.RemoveSegment(segmentIndex);
            
            SavePathToFile();
            if (CurrentChapterPath != null) {
                StatsManager.AggregateStatsPassOnce(CurrentChapterPath);
            }
            SetNewRoom(CurrentRoomName, false);
            return true;
        }

        public string ResolveGroupedRoomName(string roomName) {
            if (CurrentChapterPath == null) {
                return roomName;
            }

            //Loop through path and see if any room on the path has the roomName as grouped room
            //If yes, return that room
            //If no, return roomName
            foreach (CheckpointInfo cpInfo in CurrentChapterPath.Checkpoints) {
                foreach (RoomInfo rInfo in cpInfo.Rooms) {
                    if (rInfo.GroupedRooms != null && rInfo.GroupedRooms.Contains(roomName)) {
                        return rInfo.DebugRoomName;
                    }
                }
            }

            return roomName;
        }

        public void SetNewRoom(string newRoomName, bool countSuccess=true, bool? holdingGolden=null) {
            if (holdingGolden != null) { 
                PlayerIsHoldingGolden = holdingGolden.Value;
            }
            CurrentChapterStats.ModState.ChapterCompleted = false;

            //Resolve grouped room name
            string nameBefore = newRoomName;
            newRoomName = ResolveGroupedRoomName(newRoomName);
            if (nameBefore != newRoomName) {
                Log($"Resolved grouped room name '{nameBefore}' to '{newRoomName}'");
            } else {
                Log($"Room name '{nameBefore}' was not grouped, returned it raw");
            }

            //If the room is to be ignored, don't update anything
            if (CurrentChapterPath != null && CurrentChapterPath.IgnoredRooms.Contains(newRoomName)) {
                Log($"Entered ignored room '{newRoomName}', not updating state!");
                return;
            }

            //Don't complete if entering previous room and current room was not completed
            if (PreviousRoomName == newRoomName && !_CurrentRoomCompleted) {
                Log($"Entered previous room '{PreviousRoomName}'");
                PreviousRoomName = CurrentRoomName;
                CurrentRoomName = newRoomName;
                CurrentChapterStats?.SetCurrentRoom(newRoomName);
                SaveChapterStats();
                return;
            }

            //Transitioned to own room, probably due to teleport or entering a grouped room
            if (CurrentRoomName == newRoomName) {
                Log($"Entered own room '{newRoomName}', not updating state!");
                CurrentChapterStats.SetCurrentRoom(newRoomName);
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

            if (CurrentChapterStats != null) {
                if (countSuccess && !ModSettings.PauseDeathTracking && (!ModSettings.TrackingOnlyWithGoldenBerry || holdingGolden.Value)) {
                    CurrentChapterStats.AddAttempt(true);
                }
                CurrentChapterStats.SetCurrentRoom(newRoomName);
                SaveChapterStats();
            }

            //engineDelayTimer = 0;
            //engineDelayHadSetTimer = false;
            PacePingManager.CheckPacePing(CurrentChapterPath, CurrentChapterStats);
        }

        private void SetRoomCompleted(bool resetOnDeath=false) {
            _CurrentRoomCompleted = true;
            _CurrentRoomCompletedResetOnDeath = resetOnDeath;
        }

        private bool PlayerIsHoldingGoldenBerry(Player player) {
            if (player == null || player.Leader == null || player.Leader.Followers == null || player.Leader.Followers.Count == 0) {
                //Log($"player '{player}', player.Leader '{player?.Leader}', player.Leader.Followers '{player?.Leader?.Followers}', follower count '{player?.Leader?.Followers?.Count}'");
                return false;
            }
            
            return player.Leader.Followers.Any((f) => {
                //Log($"Follower class: '{f.Entity.GetType().Name}'");
                if (f.Entity.GetType().Name == "PlatinumBerry") {
                    return true;
                }
                
                if (!(f.Entity is Strawberry)) {
                    return false;
                }

                Strawberry berry = (Strawberry)f.Entity;

                if (!berry.Golden || berry.Winged) {
                    //Log($"Follower wasn't either a Golden '{berry.Golden}' or a Winged '{berry.Winged}' berry");
                    return false;
                }

                //Log($"Follower was a Golden!");
                return true;
            });
        }

        public void CollectedGoldenBerry(GoldenType goldenType) {
            try { //we DONT want to crash when winning
                Log($"Golden collected! GG catpog");
                CurrentChapterStats.CollectedGolden(goldenType);
                SaveChapterStats();
                
                PacePingManager.CollectedGolden(CurrentChapterPath, CurrentChapterStats);
            } catch (Exception ex) {
                Log($"An exception occurred on CollectedGoldenBerry: {ex}", isFollowup: true);
            }
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

            //Register the death if setting is enabled
            if (ModSettings.TrackingSaveStateCountsForGoldenDeath && CurrentChapterStats != null) {
                if (PlayerIsHoldingGolden && (!ModSettings.PauseDeathTracking || ModSettings.TrackingAlwaysGoldenDeaths)) {
                    CurrentChapterStats.AddGoldenBerryDeath();
                    PacePingManager.DiedWithGolden(CurrentChapterPath, CurrentChapterStats);
                }
            }

            IngameOverlay = level.Tracker.GetEntity<TextOverlay>();
            SummaryOverlay = level.Tracker.GetEntity<SummaryHud>();

            PreviousRoomName = (string) savedvalues[type][nameof(PreviousRoomName)];
            CurrentRoomName = (string) savedvalues[type][nameof(CurrentRoomName)];
            _CurrentRoomCompleted = (bool) savedvalues[type][nameof(_CurrentRoomCompleted)];
            _CurrentRoomCompletedResetOnDeath = (bool) savedvalues[type][nameof(_CurrentRoomCompletedResetOnDeath)];

            CurrentChapterStats.SetCurrentRoom(CurrentRoomName);
            SaveChapterStats();

            if (PhysicsLogger.Settings.SegmentOnLoadState) {
                PhysicsLog.SegmentLog(true);
            }
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
            return Path.Combine(BaseFolderPath, file);
        }
        public static string GetPathToFile(string folder, string file) {
            return Path.Combine(BaseFolderPath, Path.Combine(folder, file));
        }
        public static string GetPathToFile(string folder, string subfolder, string file) {
            return Path.Combine(BaseFolderPath, Path.Combine(folder, Path.Combine(subfolder, file)));
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
        public void CheckRootFolder() {
            string dataRootFolder = ModSettings.DataRootFolderLocation;
            if (dataRootFolder == null) {
                dataRootFolder = BaseFolderPath;
            } else {
                dataRootFolder = Path.Combine(dataRootFolder, "ConsistencyTracker");
            }

            if (!Directory.Exists(dataRootFolder)) {
                try { 
                    Directory.CreateDirectory(dataRootFolder);
                } catch (Exception) {
                    CheckFolderExists(BaseFolderPath);
                    dataRootFolder = BaseFolderPath;
                }
            }

            BaseFolderPath = dataRootFolder;
        }

        
        public PathSegmentList GetPathInfo(string pathName = null) {
            if(pathName == null) {
                pathName = CurrentChapterDebugName;
            }
            Log($"Fetching path info for chapter '{pathName}'");

            string pathTXT = GetPathToFile(PathsFolder, $"{pathName}.txt");
            string pathJSON = GetPathToFile(PathsFolder, $"{pathName}.json");
            Log($"\tSearching for path '{pathJSON}' or .txt equiv", true);

            if (!File.Exists(pathJSON) && !File.Exists(pathTXT)) {
                Log($"\tDidn't find file at '{pathJSON}', returned null.", true);
                return null;
            }
            
            
            string content = null;
            bool readAsTXT = false;
            if (File.Exists(pathJSON)) {
                content = File.ReadAllText(pathJSON);
            } else if (File.Exists(pathTXT)) {
                content = File.ReadAllText(pathTXT);
                readAsTXT = true;
            }

            string logExt = readAsTXT ? "txt" : "json";
            Log($"\tFound '.{logExt}' version of file, parsing...", true);

            try {
                Log($"Trying to parse PathSegmentList...");
                PathSegmentList psl = JsonConvert.DeserializeObject<PathSegmentList>(content);
                Log($"Successfully parsed PathSegmentList: Segments.Count -> {psl.Segments.Count}", isFollowup: true);
                return psl;
            } catch (Exception ex) {
                Log($"Couldn't parse PathSegmentList. Exception: {ex.Message}", isFollowup: true);
            }

            
            PathInfo oldPathFormat = null;
            //[Try 1] New file format: JSON
            try {
                PathInfo pathInfo = JsonConvert.DeserializeObject<PathInfo>(content);
                if (readAsTXT) { //Move file to json format
                    File.Move(pathTXT, pathJSON);
                }
                oldPathFormat = pathInfo;
            } catch (Exception) {
                Log($"\tCouldn't read path info as JSON, trying old path format...", true);
            }

            if (oldPathFormat == null) {
                //[Try 2] Old file format: selfmade text format
                try {
                    PathInfo parsedOldFormat = PathInfo.ParseString(content);
                    Log($"\tSaving path for map '{pathName}' in new format!", true);
                    oldPathFormat = parsedOldFormat;
                } catch (Exception) {
                    Log($"\tCouldn't read old path info. Old path info content:\n{content}", true);
                }
            }

            if (oldPathFormat == null) return null;
            PathSegmentList pathList = PathSegmentList.Create();
            pathList.Segments[0].Path = oldPathFormat;

            SavePathToFile(pathList, pathName); //Save in new format (json)
            return pathList;
        }

        public ChapterStatsList GetCurrentChapterStats() {
            string pathTXT = GetPathToFile(StatsFolder, $"{CurrentChapterDebugName}.txt");
            string pathJSON = GetPathToFile(StatsFolder, $"{CurrentChapterDebugName}.json");

            Log($"CurrentChapterName: '{CurrentChapterDebugName}', ChaptersThisSession: '{string.Join(", ", ChaptersThisSession)}'");


            ChapterStatsList toRet = null;
            if (!File.Exists(pathTXT) && !File.Exists(pathJSON)) { //Create new
                toRet = new ChapterStatsList();
                toRet.GetStats(0).SetCurrentRoom(CurrentRoomName);
                return toRet;
            }

            string content = null;
            bool readAsTXT = false;
            if (File.Exists(pathJSON)) {
                content = File.ReadAllText(pathJSON);
            } else if (File.Exists(pathTXT)) {
                content = File.ReadAllText(pathTXT);
                readAsTXT = true;
            }

            try {
                toRet = JsonConvert.DeserializeObject<ChapterStatsList>(content);
                return toRet;
            } catch (Exception) {
                Log($"\tCouldn't read chapter stats list, trying older stats formats...", true);
            }

            ChapterStats chapterStats = null;
            //[Try 1] New file format: JSON
            try {
                chapterStats = JsonConvert.DeserializeObject<ChapterStats>(content);
            } catch (Exception) {
                Log($"\tCouldn't read chapter stats as JSON, trying old stats format...", true);
            }

            if (chapterStats == null) {
                //[Try 2] Old file format: selfmade text format
                try {
                    chapterStats = ChapterStats.ParseString(content);
                    Log($"\tSaving chapter stats for map '{CurrentChapterDebugName}' in new format!", true);
                } catch (Exception) {
                    Log($"\tCouldn't read old chapter stats, created new ChapterStats. Old chapter stats content:\n{content}", true);
                    chapterStats = new ChapterStats();
                    chapterStats.SetCurrentRoom(CurrentRoomName);
                }
            }

            if (readAsTXT) { //Try to move file from TXT to JSON path 
                File.Move(pathTXT, pathJSON);
            }

            //When a stats file was parsed from an old format, the path segment is always 0 (default)
            toRet = new ChapterStatsList();
            toRet.SetStats(0, chapterStats);

            return toRet;
        }

        public void SaveChapterStats() {
            if (CurrentChapterStatsList == null) {
                Log($"Aborting saving chapter stats as '{nameof(CurrentChapterStatsList)}' is null");
                return;
            }
            if (CurrentChapterStats == null) {
                Log($"Aborting saving chapter stats as '{nameof(CurrentChapterStats)}' is null");
                return;
            }

            if (CurrentChapterStats.ChapterName == null) {
                if (Engine.Scene is Level level && level.Session != null) {
                    ChapterMetaInfo chapterInfo = new ChapterMetaInfo(level.Session);
                    SetChapterMetaInfo(chapterInfo);
                    Log($"Fixed missing meta info on '{nameof(CurrentChapterStats)}'");
                } else {
                    Log($"Aborting saving chapter stats as '{nameof(CurrentChapterStats)}' doesn't have meta info set.");
                    return;
                }
            }

            CurrentUpdateFrame++;

            CurrentChapterStats.ModState.PlayerIsHoldingGolden = PlayerIsHoldingGolden;
            CurrentChapterStats.ModState.GoldenDone = PlayerIsHoldingGolden && CurrentChapterStats.ModState.ChapterCompleted;

            CurrentChapterStats.ModState.DeathTrackingPaused = ModSettings.PauseDeathTracking;
            CurrentChapterStats.ModState.RecordingPath = ModSettings.RecordPath;
            CurrentChapterStats.ModState.OverlayVersion = VersionsCurrent.Overlay;
            CurrentChapterStats.ModState.ModVersion = VersionsNewest.Mod;
            CurrentChapterStats.ModState.ChapterHasPath = CurrentChapterPath != null;


            string path = GetPathToFile(StatsFolder, $"{CurrentChapterDebugName}.json");
            string backupPath = GetPathToFile(StatsFolder, $"{CurrentChapterDebugName}_backup.json");
            File.WriteAllText(backupPath, JsonConvert.SerializeObject(CurrentChapterStatsList, Formatting.Indented));

            //Delete actual file
            if (File.Exists(path)) {
                File.Delete(path);
            }
            //Move backup to actual file
            File.Move(backupPath, path);

            string modStatePath = GetPathToFile(StatsFolder, $"modState.txt");

            string content = $"{CurrentChapterStats.CurrentRoom}\n{CurrentChapterStats.ChapterDebugName};{CurrentChapterStats.ModState}\n";
            File.WriteAllText(modStatePath, content);

            StatsManager.OutputFormats(CurrentChapterPath, CurrentChapterStats);
        }

        public void CreateChapterSummary(int attemptCount) {
            Log($"Attempting to create tracker summary, attemptCount = '{attemptCount}'");
            string outPath = GetPathToFile(SummariesFolder, $"{CurrentChapterDebugName}.txt");
            CurrentChapterStats?.OutputSummary(outPath, CurrentChapterPath, attemptCount);
        }

        #endregion

        #region Default Path Creation

        public void CheckPrepackagedPaths(bool reset=false) {
            string assetPath = "Assets/DefaultPaths";
            List<string> sideNames = new List<string>() { "Normal", "BSide", "CSide" };
            List<string> levelNames = new List<string>() {
                "1-ForsakenCity",
                "2-OldSite",
                "3-CelestialResort",
                "4-GoldenRidge",
                "5-MirrorTemple",
                "6-Reflection",
                "7-Summit",
                "9-Core",
            };
            string farewellLevelName = "Celeste_LostLevels_Normal";

            foreach (string level in levelNames) {
                foreach (string side in sideNames) {
                    string levelName = $"Celeste_{level}_{side}";
                    LogVerbose($"Checking path file '{levelName}'...");
                    CheckDefaultPathFile(levelName, $"{assetPath}/{levelName}.json", reset);
                }
            }

            CheckDefaultPathFile(farewellLevelName, $"{assetPath}/{farewellLevelName}.json", reset);
        }
        private void CheckDefaultPathFile(string levelName, string assetPath, bool reset=false) {
            string nameTXT = $"{levelName}.txt";
            string nameJSON = $"{levelName}.json";
            string pathTXT = GetPathToFile(PathsFolder, nameTXT);
            string pathJSON = GetPathToFile(PathsFolder, nameJSON);

            if (File.Exists(pathTXT) && !File.Exists(pathJSON)) {
                File.Move(pathTXT, pathJSON);
            }
            if (File.Exists(pathTXT) && File.Exists(pathJSON)) {
                File.Delete(pathTXT);
            }

            if (!File.Exists(pathJSON) || reset) {
                CreatePathFileFromStream(nameJSON, assetPath);
            } else {
                LogVerbose($"Path file '{nameJSON}' already exists, skipping");
            }
        }

        public void UpdateExternalTools() {
            Log($"Checking for external tool updates...");

            string basePath = "Assets";
            
            List<string> externalOverlayFiles = new List<string>() {
                    "common.js",
                    "CCTOverlay.html",
                    "CCTOverlay.js",
                    "CCTOverlay.css",
                    "img/goldberry.gif"
            };
            string externalOverlayPath = $"{basePath}/ExternalOverlay";


            List<string> livedataEditorFiles = new List<string>() {
                    "LiveDataEditTool.html",
                    "LiveDataEditTool.js",
                    "LiveDataEditTool.css",
            };
            string livedataEditorPath = $"{basePath}/LiveDataEditor";


            List<string> physicsInspectorFiles = new List<string>() {
                    "PhysicsInspector.html",
                    "PhysicsInspector.js",
                    "PhysicsInspector.css",
            };
            string physicsInspectorPath = $"{basePath}/PhysicsInspector";


            //Overlay files
            string alreadyGeneratedPath = GetPathToFile(ExternalToolsFolder, "common.js");
            if (Util.IsUpdateAvailable(VersionsCurrent.Overlay, VersionsNewest.Overlay) || !File.Exists(alreadyGeneratedPath)) {
                Log($"Updating External Overlay from version {VersionsCurrent.Overlay ?? "null"} to version {VersionsNewest.Overlay}");
                VersionsCurrent.Overlay = VersionsNewest.Overlay;

                CheckFolderExists(GetPathToFile(ExternalToolsFolder, "img"));

                foreach (string file in externalOverlayFiles) {
                    CreateExternalToolFileFromStream(file, $"{externalOverlayPath}/{file}");
                }
            } else {
                Log($"External Overlay is up to date at version {VersionsCurrent.Overlay}");
            }

            //Path Edit Tool files

            //Format Edit Tool files
            alreadyGeneratedPath = GetPathToFile(ExternalToolsFolder, "LiveDataEditTool.html");
            if (Util.IsUpdateAvailable(VersionsCurrent.LiveDataEditor, VersionsNewest.LiveDataEditor) || !File.Exists(alreadyGeneratedPath)) {
                Log($"Updating LiveData Editor from version {VersionsCurrent.LiveDataEditor ?? "null"} to version {VersionsNewest.LiveDataEditor}");
                VersionsCurrent.LiveDataEditor = VersionsNewest.LiveDataEditor;

                foreach (string file in livedataEditorFiles) {
                    CreateExternalToolFileFromStream(file, $"{livedataEditorPath}/{file}");
                }
            } else {
                Log($"LiveData Editor is up to date at version {VersionsCurrent.LiveDataEditor}");
            }

            //Physics Inspector Tool files
            alreadyGeneratedPath = GetPathToFile(ExternalToolsFolder, "PhysicsInspector.html");
            if (Util.IsUpdateAvailable(VersionsCurrent.PhysicsInspector, VersionsNewest.PhysicsInspector) || !File.Exists(alreadyGeneratedPath)) {
                Log($"Updating Physics Inspector from version {VersionsCurrent.PhysicsInspector ?? "null"} to version {VersionsNewest.PhysicsInspector}");
                VersionsCurrent.PhysicsInspector = VersionsNewest.PhysicsInspector;

                foreach (string file in physicsInspectorFiles) {
                    CreateExternalToolFileFromStream(file, $"{physicsInspectorPath}/{file}");
                }
            } else {
                Log($"Physics Inspector is up to date at version {VersionsCurrent.PhysicsInspector}");
            }
        }

        private void CreateExternalToolFileFromStream(string fileName, string assetPath) {
            CreateFileFromStream(ExternalToolsFolder, fileName, assetPath);
        }
        private void CreatePathFileFromStream(string fileName, string assetPath) {
            CreateFileFromStream(PathsFolder, fileName, assetPath);
        }
        private void CreateFileFromStream(string folder, string fileName, string assetPath) {
            string path = GetPathToFile(folder, fileName);

            LogVerbose($"Trying to access asset at '{assetPath}'");
            if (Everest.Content.TryGet(assetPath, out ModAsset value, true)) {
                using (var fileStream = File.Create(path)) {
                    value.Stream.Seek(0, SeekOrigin.Begin);
                    value.Stream.CopyTo(fileStream);
                    LogVerbose($"Wrote file '{fileName}' to path '{path}'");
                }
            } else {
                Log($"No asset found with content path '{assetPath}'");
            }
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
        public void WipeChapterGoldenBerryCollects() {
            if (CurrentChapterStats == null) {
                Log($"Aborting wiping chapter golden berry collections as '{nameof(CurrentChapterStats)}' is null");
                return;
            }

            Log($"Wiping golden berry collection data for chapter '{CurrentChapterDebugName}'");

            foreach (string debugName in CurrentChapterStats.Rooms.Keys) {
                CurrentChapterStats.GoldenCollectedCount = 0;
                CurrentChapterStats.GoldenCollectedCountSession = 0;
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

        private void MapEditor_Render(On.Celeste.Editor.MapEditor.orig_Render orig, MapEditor self) {
            orig(self);

            if (CurrentChapterPath == null) return;
            
            List<LevelTemplate> levels = Util.GetPrivateProperty<List<LevelTemplate>>(self, "levels");
            Camera camera = Util.GetPrivateStaticProperty<Camera>(self, "Camera");

            if (ModSettings.ShowCCTRoomNamesOnDebugMap) {
                Draw.SpriteBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.AlphaBlend,
                        SamplerState.LinearClamp,
                        DepthStencilState.None,
                        RasterizerState.CullNone,
                        null,
                        Engine.ScreenMatrix);

                foreach (LevelTemplate template in levels) {
                    string name = template.Name;
                    RoomInfo rInfo = CurrentChapterPath.GetRoom(name);
                    if (rInfo == null) {
                        string resolvedName = ResolveGroupedRoomName(name);
                        rInfo = CurrentChapterPath.GetRoom(resolvedName);

                        if (rInfo == null) {
                            continue;
                        }
                    }

                    string formattedName;
                    int scaleDivider = 6;

                    if (ModSettings.LiveDataCustomNameBehavior == CustomNameBehavior.Override || ModSettings.LiveDataCustomNameBehavior == CustomNameBehavior.Ignore) {
                        formattedName = rInfo.GetFormattedRoomName(ModSettings.LiveDataRoomNameDisplayType);
                    } else { 
                        formattedName = rInfo.GetFormattedRoomName(ModSettings.LiveDataRoomNameDisplayType, CustomNameBehavior.Override);
                        if (!string.IsNullOrEmpty(rInfo.CustomRoomName)) {
                            formattedName += $"\n{rInfo.GetFormattedRoomName(ModSettings.LiveDataRoomNameDisplayType, CustomNameBehavior.Ignore)}";
                            scaleDivider += 2;
                        }
                    }


                    PacePingManager.PaceTiming paceTiming = PacePingManager.GetPaceTiming(CurrentChapterPath.ChapterSID, rInfo.DebugRoomName, dontLog:true);
                    if (paceTiming != null) { 
                        formattedName = $"{formattedName}\n>Ping<";
                        scaleDivider += 2;
                    }

                    int x = template.X;
                    int y = template.Y;

                    Vector2 pos = new Vector2(x + template.Width / 2, y);
                    pos -= camera.Position;
                    pos = new Vector2((float)Math.Ceiling(pos.X), (float)Math.Ceiling(pos.Y));
                    pos *= camera.Zoom;
                    pos += new Vector2(960f, 540f);

                    ActiveFont.DrawOutline(
                        formattedName,
                        pos,
                        new Vector2(0.5f, 0),
                        Vector2.One * camera.Zoom / scaleDivider,
                        Color.White * 0.9f,
                        2f * camera.Zoom / 6,
                        Color.Black * 0.7f);
                }

                Draw.SpriteBatch.End();
            }

            if (ModSettings.ShowSuccessRateBordersOnDebugMap) { //Insert Mod Option setting here
                Draw.SpriteBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.AlphaBlend,
                        SamplerState.LinearClamp,
                        DepthStencilState.None,
                        RasterizerState.CullNone,
                        null,
                        Engine.ScreenMatrix);

                foreach (LevelTemplate template in levels) {
                    string name = template.Name;
                    RoomInfo rInfo = CurrentChapterPath.GetRoom(name);
                    if (rInfo == null) {
                        string resolvedName = ResolveGroupedRoomName(name);
                        rInfo = CurrentChapterPath.GetRoom(resolvedName);

                        if (rInfo == null) {
                            continue;
                        }
                    }

                    RoomStats rStats = CurrentChapterStats.GetRoom(rInfo);
                    Color color = Color.Gray;
                    float successRate = rStats.AverageSuccessOverSelectedN() * 100;
                    if (successRate > ModSettings.LiveDataChapterBarLightGreenPercent) {
                        color = new Color(0, 230, 0);
                    } else if (successRate > ModSettings.LiveDataChapterBarGreenPercent) {
                        color = Color.Green;
                    } else if (successRate > ModSettings.LiveDataChapterBarYellowPercent) {
                        color = new Color(194, 194, 41);
                    } else if (!float.IsNaN(successRate)) {
                        color = new Color(231, 45, 45);
                    }

                    int x = template.X;
                    int y = template.Y;

                    int width = template.Width;
                    int height = template.Height;

                    Vector2 pos = new Vector2(x, y);
                    pos -= camera.Position;
                    pos = new Vector2((float)Math.Ceiling(pos.X), (float)Math.Ceiling(pos.Y));
                    pos *= camera.Zoom;
                    pos += new Vector2(960f, 540f);

                    //Draw.Rect(pos, width * camera.Zoom, height * camera.Zoom, new Color(color.R, color.G, color.B, 0));
                    Vector2 topRight = pos + new Vector2(width * camera.Zoom, 0);
                    Vector2 bottomLeft = pos + new Vector2(0, height * camera.Zoom);
                    Vector2 bottomRight = pos + new Vector2(width * camera.Zoom, height * camera.Zoom);

                    float thickness = 1.5f * camera.Zoom;
                    Draw.Line(pos, topRight, color, thickness);
                    Draw.Line(pos, bottomLeft, color, thickness);
                    Draw.Line(topRight, bottomRight, color, thickness);
                    Draw.Line(bottomLeft, bottomRight, color, thickness);
                }

                Draw.SpriteBatch.End();
            }
        }

        public void SaveRecordedRoomPath() {
            Log($"Saving recorded path...");
            if (PathRec.TotalRecordedRooms <= 1) {
                Log($"Path is too short to save. ({PathRec.TotalRecordedRooms} rooms)", isFollowup: true);
                return;
            }
            if (AbortPathRecording) {
                AbortPathRecording = false;
                Log($"AbortPathRecording is set, not saving path.", isFollowup:true);
                return;
            }

            PathInfo path = PathRec.ToPathInfo();

            ChapterMetaInfo chapterInfo = null;
            if (Engine.Scene is Level level && level.Session != null) {
                chapterInfo = new ChapterMetaInfo(level.Session);
            }

            DisabledInRoomName = CurrentRoomName;
            SetCurrentChapterPath(path, chapterInfo);
            Log($"Recorded path:\n{JsonConvert.SerializeObject(CurrentChapterPath)}", isFollowup: true);
            SavePathToFile();
            
            SaveChapterStats();//Output stats with updated path
        }
        public void SavePathToFile(PathSegmentList pathList = null, string pathName = null) {
            if (pathList == null) {
                pathList = CurrentChapterPathSegmentList;
            }
            if (pathName == null) {
                pathName = CurrentChapterDebugName;
            }

            string outPath = GetPathToFile(PathsFolder, $"{pathName}.json");
            File.WriteAllText(outPath, JsonConvert.SerializeObject(pathList, Formatting.Indented));
            Log($"Wrote path data to '{outPath}'");
        }

        public void RemoveRoomFromChapterPath() {
            Log($"Removing room '{CurrentRoomName}' from path");
            
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
                SaveChapterStats();
            } else {
                Log($"Could not find room '{CurrentRoomName}' in path");
            }
        }

        public void GroupRoomsOnChapterPath() {
            Log($"Grouping room '{CurrentRoomName}' with previous room on path");
            
            if (CurrentChapterPath == null) {
                Log($"CurrentChapterPath was null");
                return;
            }

            CheckpointInfo previousRoomCp = null;
            RoomInfo previousRoomR = null;

            CheckpointInfo currentRoomCp = null;
            RoomInfo currentRoomR = null;

            foreach (CheckpointInfo cpInfo in CurrentChapterPath.Checkpoints) {
                foreach (RoomInfo rInfo in cpInfo.Rooms) {
                    if (rInfo.DebugRoomName == CurrentRoomName) {
                        currentRoomCp = cpInfo;
                        currentRoomR = rInfo;
                        break;
                    } else { 
                        previousRoomR = rInfo;
                        previousRoomCp = cpInfo;
                    }
                }

                if (currentRoomR != null) break;
            }

            if (currentRoomR == null) {
                Log($"Could not find room '{CurrentRoomName}' in path");
                return;
            }
            if (previousRoomR == null) {
                Log($"Room '{CurrentRoomName}' doesn't have a previous room on the path");
                return;
            }

            currentRoomCp.Rooms.Remove(currentRoomR);
            if (previousRoomR.GroupedRooms == null) previousRoomR.GroupedRooms = new List<string>();
            previousRoomR.GroupedRooms.Add(currentRoomR.DebugRoomName);

            CurrentChapterPath.Stats = null; //Call a new aggregate stats pass to fix room numbering
            SetNewRoom(previousRoomR.DebugRoomName, false);
            
            SavePathToFile();
            SaveChapterStats();
        }

        public void UngroupRoomsOnChapterPath() {
            if (CurrentChapterPath == null) {
                Log($"CurrentChapterPath was null");
                return;
            }
            
            Level level = Engine.Scene as Level;
            string actualRoomName = level.Session.Level;
            Log($"Ungrouping room '{actualRoomName}' from room '{CurrentRoomName}' on path");
            
            if (actualRoomName == CurrentRoomName) {
                Log($"Room '{CurrentRoomName}' isn't marked as a grouped room!");
                return;
            }
            if (CurrentChapterPath.IgnoredRooms.Contains(actualRoomName)) {
                Log($"Room '{actualRoomName}' is marked as ignored room and can't be grouped/ungrouped");
                return;
            }

            CheckpointInfo currentRoomCp = null;
            RoomInfo currentRoomR = null;

            foreach (CheckpointInfo cpInfo in CurrentChapterPath.Checkpoints) {
                foreach (RoomInfo rInfo in cpInfo.Rooms) {
                    if (rInfo.DebugRoomName == CurrentRoomName) {
                        currentRoomCp = cpInfo;
                        currentRoomR = rInfo;
                        break;
                    }
                }

                if (currentRoomR != null) break;
            }

            if (currentRoomR == null) {
                Log($"Could not find room '{CurrentRoomName}' in path");
                return;
            }

            currentRoomR.GroupedRooms.Remove(actualRoomName);

            RoomInfo newRoomInfo = new RoomInfo() { 
                DebugRoomName = actualRoomName,
                Checkpoint = currentRoomCp,
            };
            int indexOfCurrent = currentRoomCp.Rooms.IndexOf(currentRoomR);
            currentRoomCp.Rooms.Insert(indexOfCurrent+1, newRoomInfo);

            CurrentChapterPath.Stats = null; //Call a new aggregate stats pass to fix room numbering
            SetNewRoom(actualRoomName, false);

            SavePathToFile();
            SaveChapterStats();
        }

        public void SetCustomRoomName(string name, RoomInfo room = null) {
            if (CurrentChapterPath == null || name == null) return;
            
            if (room == null) {
                room = CurrentChapterPath.CurrentRoom;
            }
            if (room == null) return;
            
            name = name.Trim();
            if (string.IsNullOrEmpty(name)) name = null;
            
            string currentRoomName = room.DebugRoomName;
            int count = 0;
            if (ModSettings.CustomRoomNameAllSegments) { //Find all other rooms with same name in other segments 
                foreach (PathSegment segment in CurrentChapterPathSegmentList.Segments) {
                    foreach (CheckpointInfo cpInfo in segment.Path.Checkpoints) {
                        foreach (RoomInfo rInfo in cpInfo.Rooms) {
                            if (rInfo.DebugRoomName == currentRoomName) {
                                rInfo.CustomRoomName = name;
                                count++;
                            }
                        }
                    }
                }
            } else {
                CurrentChapterPath.CurrentRoom.CustomRoomName = name;
                count++;
            }

            Log($"Set custom room name of room '{CurrentChapterPath.CurrentRoom.DebugRoomName}' to '{name}' (Count: {count})");
            SavePathToFile();
            SaveChapterStats();//Recalc stats
        }

        #endregion

        #region Logging
        private bool LogInitialized = false;
        private StreamWriter LogFileWriter = null;
        public void LogInit() {
            string logFileMax = GetPathToFile(LogsFolder, $"log_old{LOG_FILE_COUNT}.txt");
            if (File.Exists(logFileMax)) {
                File.Delete(logFileMax);
            }

            for (int i = LOG_FILE_COUNT - 1; i >= 1; i--) {
                string logFilePath = GetPathToFile(LogsFolder, $"log_old{i}.txt");
                if (File.Exists(logFilePath)) {
                    string logFileNewPath = GetPathToFile(LogsFolder, $"log_old{i+1}.txt");
                    File.Move(logFilePath, logFileNewPath);
                }
            }

            string lastFile = GetPathToFile(LogsFolder, $"log.txt");
            if (File.Exists(lastFile)) {
                string logFileNewPath = GetPathToFile(LogsFolder, $"log_old{1}.txt");
                File.Move(lastFile, logFileNewPath);
            }

            string path = GetPathToFile(LogsFolder, $"log.txt");
            LogFileWriter = new StreamWriter(path) {
                AutoFlush = true
            };
            LogInitialized = true;
        }
        public void Log(string log, bool isFollowup = false, bool isComingFromOtherLogMethod = false) {
            if (!LogInitialized) {
                return;
            }

            if (!isFollowup) {
                int frameBack = 1;
                if (isComingFromOtherLogMethod) {
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

        private readonly HashSet<string> _LogOnceKeys = new HashSet<string>();
        public void LogOnce(string key, string message) {
            if (_LogOnceKeys.Contains(key)) return;
            _LogOnceKeys.Add(key);

            Log(message, false, true);
        }
        public void ResetLogOnce() {
            _LogOnceKeys.Clear();
        }

        private int _LogCounter = 0;
        public void LogCount(string message="Log Counter -> ") {
            _LogCounter++;
            Log($"{message} {_LogCounter}", false, true);
        }

        public void LogCleanup() {
            LogFileWriter?.Close();
            LogFileWriter?.Dispose();
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
