﻿using Celeste.Mod.ConsistencyTracker.Models;
using Celeste.Mod.ConsistencyTracker.Stats;
using Celeste.Mod.ConsistencyTracker.Entities.Summary.Tables;
using Monocle;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using Celeste.Mod.ConsistencyTracker.Entities.Summary.Charts;

namespace Celeste.Mod.ConsistencyTracker.Entities.Summary {
    public class PageCurrentSession : SummaryHudPage {

        //public string TextLastRuns { get; set; }
        //public string TextBestRuns { get; set; }
        public string TextAttemptCount { get; set; }
        
        public List<string> GoldenDeathsTree { get; set; }

        private readonly int countBestRuns = 5;
        private List<ProgressBar> BestRunsProgressBars { get; set; }
        private List<Tuple<string, int>> BestRunsData { get; set; } = new List<Tuple<string, int>>();
        private int ChapterRoomCount { get; set; }

        private Table LastRunsTable { get; set; }
        private readonly int LastRunCount = 10;

        private LineChart SessionChart { get; set; }

        private double AverageRunDistance { get; set; }
        private double AverageRunDistanceSession { get; set; }
        private Table AverageRunTable { get; set; }

        private float BarWith = 400;
        private float BarHeight = 13;
        private float ChartWidth = 610;
        private float ChartHeight = 420;

        public PageCurrentSession(string name) : base(name) {
            BestRunsProgressBars = new List<ProgressBar>();
            for (int i = 0; i < countBestRuns; i++) {
                BestRunsProgressBars.Add(new ProgressBar(0, 100) { BarWidth = BarWith, BarHeight = BarHeight });
            }

            LastRunsTable = new Table() {
                Settings = new TableSettings() {
                    Title = "Last Runs",
                    FontMultAll = 0.5f,
                },
            };

            SessionChart = new LineChart(new ChartSettings() {
                ChartWidth = ChartWidth,
                ChartHeight = ChartHeight,
                YMin = 1,
                YAxisLabelFontMult = 0.5f,
                TitleFontMult = 0.5f,
                LegendFontMult = 0.5f,
                Title = "Run Distances (+Average)",
            });

            AverageRunTable = new Table() {
                Settings = new TableSettings() {
                    ShowHeader = false,
                    FontMultAll = 0.7f,
                },
            };
        }

        public override void Update() {
            base.Update();

            PathInfo path = Stats.LastPassPathInfo;
            ChapterStats stats = Stats.LastPassChapterStats;

            if (path == null) {
                MissingPath = true;
                return;
            }

            string format = "Golden Deaths This Session: {chapter:goldenDeathsSession}";
            TextAttemptCount = Stats.FormatVariableFormat(format);

            //Update progress bars for best runs
            ChapterRoomCount = path.GameplayRoomCount;
            for (int i = 0; i < countBestRuns; i++) {
                string[] split = Stats.FormatVariableFormat($"{{pb:bestSession#{i + 1}}};{{pb:bestRoomNumberSession#{i + 1}}}").Split(';');
                string bestRoom = split[0];
                string bestRoomNumberStr = split[1];

                if (!int.TryParse(bestRoomNumberStr, out int bestRoomNumber)) {
                    bestRoomNumber = 0;
                }

                BestRunsData.Add(Tuple.Create(bestRoom, bestRoomNumber));
            }


            //Last Runs
            DataColumn lastRunNumber = new DataColumn("", typeof(string));
            DataColumn lastRunRoomName = new DataColumn("Room", typeof(string));
            DataColumn lastRunDistance = new DataColumn("Distance", typeof(string));

            DataTable lastRunsData = new DataTable();
            lastRunsData.Columns.Add(lastRunNumber);
            lastRunsData.Columns.Add(lastRunRoomName);
            lastRunsData.Columns.Add(lastRunDistance);

            LastRunsTable.ColSettings.Add(lastRunNumber, new ColumnSettings() { Alignment = ColumnSettings.TextAlign.Center, NoHeader = true });
            LastRunsTable.ColSettings.Add(lastRunRoomName, new ColumnSettings() { Alignment = ColumnSettings.TextAlign.Center });
            LastRunsTable.ColSettings.Add(lastRunDistance, new ColumnSettings() { Alignment = ColumnSettings.TextAlign.Center });

            for (int i = 0; i < LastRunCount; i++){
                string number = i == 0 ? $"Last Run" : $"#{i+1}";
                string distance = "-";
                string roomName = "-";

                int index = stats.LastGoldenRuns.Count - 1 - i;
                if (index >= 0) {
                    RoomInfo rInfo = path.GetRoom(stats.LastGoldenRuns[index]);
                    distance = $"{rInfo.RoomNumberInChapter}/{ChapterRoomCount}";
                    roomName = rInfo.GetFormattedRoomName(StatManager.RoomNameType);
                }

                lastRunsData.Rows.Add(number, roomName, distance);
            }

            LastRunsTable.Data = lastRunsData;
            LastRunsTable.Update();


            //format = "Last runs\n1 -> ({chapter:lastRunDistance#1}/{chapter:roomCount})" +
            //    "\n2 -> ({chapter:lastRunDistance#2}/{chapter:roomCount})" +
            //    "\n3 -> ({chapter:lastRunDistance#3}/{chapter:roomCount})" +
            //    "\n4 -> ({chapter:lastRunDistance#4}/{chapter:roomCount})" +
            //    "\n5 -> ({chapter:lastRunDistance#5}/{chapter:roomCount})" +
            //    "\n6 -> ({chapter:lastRunDistance#6}/{chapter:roomCount})" +
            //    "\n7 -> ({chapter:lastRunDistance#7}/{chapter:roomCount})" +
            //    "\n8 -> ({chapter:lastRunDistance#8}/{chapter:roomCount})" +
            //    "\n9 -> ({chapter:lastRunDistance#9}/{chapter:roomCount})" +
            //    "\n10 -> ({chapter:lastRunDistance#10}/{chapter:roomCount})";
            //TextLastRuns = Stats.FormatVariableFormat(format);



            GoldenDeathsTree = new List<string>() { };
            Dictionary<RoomInfo, Tuple<int, float, int, float>> roomGoldenSuccessRateData = ChokeRateStat.GetRoomData(path, stats);
            //Walk path
            foreach (CheckpointInfo cpInfo in path.Checkpoints) {
                GoldenDeathsTree.Add($"{cpInfo.Name} (Deaths: {cpInfo.Stats.GoldenBerryDeathsSession})");
                foreach (RoomInfo rInfo in cpInfo.Rooms) {
                    RoomStats rStats = stats.GetRoom(rInfo);
                    Tuple<int, float, int, float> data = roomGoldenSuccessRateData[rInfo];
                    string successRate = float.IsNaN(data.Item4) ? "-%" : $"{StatManager.FormatPercentage(data.Item4)}";
                    GoldenDeathsTree.Add($"    {rInfo.GetFormattedRoomName(StatManager.RoomNameType)}: {successRate} ({data.Item3 - rStats.GoldenBerryDeathsSession}/{data.Item3})");
                }
            }

            UpdateSessionChart(path, stats);

            //Update average run distance
            Tuple<double, double> avgRunDistances = AverageLastRunsStat.GetAverageRunDistance(path, stats);
            AverageRunDistance = avgRunDistances.Item1;
            AverageRunDistanceSession = avgRunDistances.Item2;

            DataTable avgData = new DataTable();
            avgData.Columns.Add(new DataColumn("Stat", typeof(string)));
            avgData.Columns.Add(new DataColumn("Value", typeof(string)));

            foreach (DataColumn col in avgData.Columns) {
                AverageRunTable.ColSettings.Add(col, new ColumnSettings() { 
                    Alignment = ColumnSettings.TextAlign.Center,
                });
            }

            avgData.Rows.Add("Average Run Distance", $"{StatManager.FormatDouble(AverageRunDistance)}");
            avgData.Rows.Add("Average Run Distance\n(Session)", $"{StatManager.FormatDouble(AverageRunDistanceSession)}");

            AverageRunTable.Data = avgData;
            AverageRunTable.Update();
        }

        private void UpdateSessionChart(PathInfo path, ChapterStats stats) {
            SessionChart.Settings.YMax = path.GameplayRoomCount;

            List<LineDataPoint> dataRollingAvg1 = new List<LineDataPoint>();
            List<LineDataPoint> dataRollingAvg3 = new List<LineDataPoint>();
            List<LineDataPoint> dataRollingAvg10 = new List<LineDataPoint>();

            List<double> rollingAvg1 = AverageLastRunsStat.GetRollingAverages(path, stats, 1, stats.LastGoldenRuns);
            List<double> rollingAvg3 = AverageLastRunsStat.GetRollingAverages(path, stats, 3, stats.LastGoldenRuns);
            List<double> rollingAvg10 = AverageLastRunsStat.GetRollingAverages(path, stats, 10, stats.LastGoldenRuns);

            for (int i = 0; i < rollingAvg1.Count; i++) {
                dataRollingAvg1.Add(new LineDataPoint() {
                    XAxisLabel = $"{i + 1}",
                    Y = (float)rollingAvg1[i],
                    Label = rollingAvg1[i].ToString(),
                });

                float val3 = float.NaN;
                if (i > 0 && i < rollingAvg1.Count - 1 && rollingAvg1.Count >= 3) {
                    val3 = (float)rollingAvg3[i - 1];
                }
                dataRollingAvg3.Add(new LineDataPoint() {
                    XAxisLabel = $"{i + 1}",
                    Y = val3,
                });


                float val10 = float.NaN;
                if (i > 3 && i < rollingAvg1.Count - 5 && rollingAvg1.Count >= 10) {
                    val10 = (float)rollingAvg10[i - 4];
                }
                dataRollingAvg10.Add(new LineDataPoint() {
                    XAxisLabel = $"{i + 1}",
                    Y = val10,
                });
            }

            int pointCount = dataRollingAvg1.Count;

            LineSeries data1 = new LineSeries() { Data = dataRollingAvg1, LineColor = Color.LightBlue, Depth = 1, Name = "Run Distances", ShowLabels = true, LabelPosition = LabelPosition.Middle, LabelFontMult = 0.75f };
            LineSeries data3 = new LineSeries() { Data = dataRollingAvg3, LineColor = new Color(255, 165, 0, 100), Depth = 2, Name = "Avg. over 3", LabelPosition = LabelPosition.Top, LabelFontMult = 0.75f };
            LineSeries data10 = new LineSeries() { Data = dataRollingAvg10, LineColor = new Color(255, 165, 0, 100), Depth = 2, Name = "Avg. over 10" };

            List<LineSeries> series = new List<LineSeries>() { data1 };

            if (pointCount <= 13) {
                series.Add(data3);
            } else {
                series.Add(data10);
            }
            

            SessionChart.Settings.XAxisLabelFontMult = 1f;

            if (pointCount > 70) {
                series[0].ShowLabels = false;
                SessionChart.Settings.XAxisLabelFontMult = 0.3f;
            } else if (pointCount > 40) {
                series[0].ShowLabels = false;
                series[1].ShowLabels = true;
                SessionChart.Settings.XAxisLabelFontMult = 0.5f;
            } else if (pointCount > 25) {
                SessionChart.Settings.XAxisLabelFontMult = 0.65f;
            }

            SessionChart.SetSeries(series);
        }

        public override void Render() {
            base.Render();

            if (MissingPath) return;

            Vector2 pointer = MoveCopy(Position, 0, 0);
            Vector2 pointerCol2 = MoveCopy(pointer, PageWidth / 2 - 100, 0);

            //Left Column
            Vector2 measure = DrawText(TextAttemptCount, pointer, FontMultMediumSmall, Color.White);

            Move(ref pointer, 0, measure.Y + BasicMargin * 2);
            measure = DrawText("Best Runs", pointer, FontMultSmall, Color.White);

            Move(ref pointer, 0, measure.Y + BasicMargin * 3);
            float maxLabelHeight = 0;
            float maxWidth = 0;
            //Determine highest label width
            for (int i = 0; i < countBestRuns; i++) {
                string bestRoom = BestRunsData[i].Item1;
                int bestRoomNumber = BestRunsData[i].Item2;
                
                measure = ActiveFont.Measure($"{bestRoom} ({bestRoomNumber}/{ChapterRoomCount})") * FontMultSmall;
                if (measure.Y > maxLabelHeight) maxLabelHeight = measure.Y;

                measure = ActiveFont.Measure($"#{i+1}") * FontMultSmall;
                if (measure.X > maxWidth) maxWidth = measure.X;
            }
            for (int i = 0; i < countBestRuns; i++) {
                string bestRoom = BestRunsData[i].Item1;
                int bestRoomNumber = BestRunsData[i].Item2;

                measure = DrawText($"#{i+1}", pointer, FontMultSmall, Color.White);

                ProgressBar bar = BestRunsProgressBars[i];
                bar.FontMult = FontMultSmall;
                bar.Value = bestRoomNumber;
                bar.MaxValue = ChapterRoomCount;
                bar.RightLabel = $"{ChapterRoomCount}";
                bar.ValueLabel = bestRoomNumber == 0 ? "" : $"{bestRoom} ({bestRoomNumber}/{ChapterRoomCount})";
                bar.Color = new Color(242, 182, 0);
                bar.Position = MoveCopy(pointer, maxWidth + 10, measure.Y / 2 - bar.BarHeight / 2);
                bar.Render();

                Move(ref pointer, 0, Math.Max(bar.BarHeight, measure.Y) + maxLabelHeight + BasicMargin);
            }


            Move(ref pointer, 0, measure.Y - BasicMargin * 2);

            //measure = DrawText(TextLastRuns, pointer, FontMultSmall, Color.White);
            //Move(ref pointer, 0, measure.Y + BasicMargin);

            LastRunsTable.Position = MoveCopy(pointer, 0, 0);
            LastRunsTable.Render();

            //Right Column: Chart
            Vector2 separator = MoveCopy(pointerCol2, -50, 0);
            Draw.Line(separator, MoveCopy(separator, 0, PageHeight), Color.Gray, 2);

            SessionChart.Position = pointerCol2;
            SessionChart.Render();

            Vector2 pointerTable = MoveCopy(pointerCol2, 0, ChartHeight + 60 + BasicMargin * 2);
            AverageRunTable.Position = pointerTable;
            AverageRunTable.Render();
        }
    }
}
