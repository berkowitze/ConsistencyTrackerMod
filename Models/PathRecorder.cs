﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Celeste.Mod.ConsistencyTracker.Models {
    public class PathRecorder {

        //Remember all previously visited rooms. Rooms only get added to the first checkpoint they appear in.
        public HashSet<string> VisitedRooms { get; set; } = new HashSet<string>();

        public List<List<string>> Checkpoints { get; set; } = new List<List<string>>() {
            new List<string>(),
        };

        public void AddRoom(string name) {
            if (VisitedRooms.Contains(name)) return;

            VisitedRooms.Add(name);
            Checkpoints.Last().Add(name);
        }

        public void AddCheckpoint() {
            string lastRoom = Checkpoints.Last().Last();
            Checkpoints.Last().Remove(lastRoom);

            Checkpoints.Add(new List<string>() { lastRoom });
        }

        public override string ToString() {
            List<string> lines = new List<string>();

            int checkpointIndex = 0;
            foreach (List<string> checkpoint in Checkpoints) {
                if (checkpointIndex == 0) {
                    lines.Add($"Start;ST;{checkpoint.Count};" + string.Join(",", checkpoint));
                } else {
                    lines.Add($"CP{checkpointIndex + 1};CP{checkpointIndex + 1};{checkpoint.Count};" + string.Join(",", checkpoint));
                }
                checkpointIndex++;
            }

            return string.Join("\n", lines)+"\n";
        }
    }
}