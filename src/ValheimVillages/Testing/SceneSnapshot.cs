using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages;

namespace ValheimVillages.Testing
{
    /// <summary>
    /// Captures a snapshot of the current game state for regression testing.
    /// Includes villager positions, behavior states, and village area data.
    /// </summary>
    public class SceneSnapshot
    {
        public string Timestamp;
        public List<VillagerSnapshot> Villagers = new List<VillagerSnapshot>();
        public List<VillageAreaSnapshot> VillageAreas = new List<VillageAreaSnapshot>();

        /// <summary>
        /// Capture current game state as a snapshot.
        /// </summary>
        public static SceneSnapshot Capture()
        {
            var snapshot = new SceneSnapshot
            {
                Timestamp = DateTime.UtcNow.ToString("O")
            };

            // Capture villager data from Villager.AI manager
            foreach (var kvp in VillagerAIManager.ActiveVillagers)
            {
                var ai = kvp.Value;
                var pos = ai.Position;

                snapshot.Villagers.Add(new VillagerSnapshot
                {
                    UniqueId = ai.UniqueId ?? "unknown",
                    NpcType = ai.VillagerType ?? "unknown",
                    BehaviorState = ai.CurrentState.ToString(),
                    Position = new float[] { pos.x, pos.y, pos.z },
                    KnownLocationCount = 0
                });
            }

            // Village area count (detailed area data not exposed publicly)
            Plugin.Log?.LogDebug(
                $"[SceneSnapshot] Captured {snapshot.Villagers.Count} villagers, " +
                $"{VillageAreaManager.AreaCount} village areas");

            return snapshot;
        }

        /// <summary>Save snapshot to a JSON file.</summary>
        public void SaveToFile(string path)
        {
            var json = ToJson();
            File.WriteAllText(path, json);
            Plugin.Log?.LogInfo($"[SceneSnapshot] Saved to {path}");
        }

        /// <summary>Load snapshot from a JSON file.</summary>
        public static SceneSnapshot LoadFromFile(string path)
        {
            if (!File.Exists(path))
            {
                Plugin.Log?.LogWarning($"[SceneSnapshot] File not found: {path}");
                return null;
            }

            var json = File.ReadAllText(path);
            return FromJson(json);
        }

        /// <summary>
        /// Compare current state against a saved snapshot and report diffs.
        /// </summary>
        public static string CompareWithCurrent(SceneSnapshot saved)
        {
            var current = Capture();
            var sb = new StringBuilder();

            sb.AppendLine($"Snapshot comparison (saved: {saved.Timestamp}, current: {current.Timestamp})");
            sb.AppendLine($"  Villagers: saved={saved.Villagers.Count}, current={current.Villagers.Count}");

            // Compare villager counts by type
            var savedTypes = saved.Villagers.GroupBy(v => v.NpcType ?? "unknown")
                .ToDictionary(g => g.Key, g => g.Count());
            var currentTypes = current.Villagers.GroupBy(v => v.NpcType ?? "unknown")
                .ToDictionary(g => g.Key, g => g.Count());

            var allTypes = savedTypes.Keys.Union(currentTypes.Keys).OrderBy(t => t);
            foreach (var type in allTypes)
            {
                int s = savedTypes.TryGetValue(type, out var sv) ? sv : 0;
                int c = currentTypes.TryGetValue(type, out var cv) ? cv : 0;
                if (s != c)
                    sb.AppendLine($"  DIFF {type}: saved={s}, current={c}");
            }

            return sb.ToString();
        }

        #region JSON serialization (manual, no library)

        private string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"timestamp\": \"{Timestamp}\",");

            sb.AppendLine("  \"villagers\": [");
            for (int i = 0; i < Villagers.Count; i++)
            {
                var v = Villagers[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"uniqueId\": \"{Esc(v.UniqueId)}\",");
                sb.AppendLine($"      \"npcType\": \"{Esc(v.NpcType)}\",");
                sb.AppendLine($"      \"behaviorState\": \"{Esc(v.BehaviorState)}\",");
                sb.AppendLine($"      \"position\": [{v.Position[0]:F2}, {v.Position[1]:F2}, {v.Position[2]:F2}],");
                sb.AppendLine($"      \"knownLocationCount\": {v.KnownLocationCount}");
                sb.Append("    }");
                if (i < Villagers.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            sb.AppendLine("  \"villageAreas\": [");
            for (int i = 0; i < VillageAreas.Count; i++)
            {
                var a = VillageAreas[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"center\": [{a.Center[0]:F2}, {a.Center[1]:F2}, {a.Center[2]:F2}],");
                sb.AppendLine($"      \"radius\": {a.Radius:F2},");
                sb.AppendLine($"      \"villagerCount\": {a.VillagerCount}");
                sb.Append("    }");
                if (i < VillageAreas.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static SceneSnapshot FromJson(string json)
        {
            var snapshot = new SceneSnapshot();
            var tsMatch = System.Text.RegularExpressions.Regex.Match(
                json, "\"timestamp\":\\s*\"([^\"]+)\"");
            if (tsMatch.Success) snapshot.Timestamp = tsMatch.Groups[1].Value;
            return snapshot;
        }

        private static string Esc(string s) => s?.Replace("\"", "\\\"") ?? "";

        #endregion

        #region Console commands

        [DevCommand("Capture scene snapshot to vv_snapshot.json")]
        public static void CaptureCommand()
        {
            var path = Path.Combine(Paths.ConfigPath, "vv_snapshot.json");
            var snapshot = Capture();
            snapshot.SaveToFile(path);
        }

        [DevCommand("Verify current state against saved snapshot")]
        public static void VerifyCommand()
        {
            var path = Path.Combine(Paths.ConfigPath, "vv_snapshot.json");
            var saved = LoadFromFile(path);
            if (saved == null)
            {
                Plugin.Log?.LogWarning("[SceneSnapshot] No saved snapshot found. Run vv_snapshot first.");
                return;
            }

            var diff = CompareWithCurrent(saved);
            Plugin.Log?.LogInfo($"[SceneSnapshot] Comparison:\n{diff}");
        }

        #endregion
    }

    /// <summary>Villager state at a point in time.</summary>
    public class VillagerSnapshot
    {
        public string UniqueId;
        public string NpcType;
        public string BehaviorState;
        public float[] Position = new float[3];
        public int KnownLocationCount;
    }

    /// <summary>Village area state at a point in time.</summary>
    public class VillageAreaSnapshot
    {
        public float[] Center = new float[3];
        public float Radius;
        public int VillagerCount;
    }
}
