using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ValheimVillages.NPCs.AI;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villages;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Low-priority task handler that rebakes NavMesh in village bounds using Unity's public APIs.
    /// Bounds can be supplied via task attributes or computed from VillageAreaManager + villager beds.
    /// </summary>
    public class NavMeshRebakeHandler : ITaskHandler
    {
        // #region agent log
        private const string DebugLogPath = "/home/benny/Projects/valheim_villages/.cursor/debug.log";
        private static void DebugLog(string hypothesisId, string location, string message, string data)
        {
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = $"{{\"hypothesisId\":\"{hypothesisId}\",\"location\":\"{location}\",\"message\":\"{message}\",\"data\":{data},\"timestamp\":{ts}}}\n";
                File.AppendAllText(DebugLogPath, line);
            }
            catch { }
        }
        // #endregion

        /// <summary>Margin in meters when computing bounds from beds/areas.</summary>
        private const float BoundsMargin = 30f;

        public string TaskName => NavMeshRebakeTaskContract.TaskName;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            // #region agent log
            DebugLog("H5", "NavMeshRebakeHandler.cs:Handle:entry",
                "Handler invoked",
                $"{{\"sourceId\":\"{task.SourceId}\",\"hasAttrs\":{(task.Attributes != null).ToString().ToLower()}}}");
            // #endregion
            if (!TryGetBounds(task, out float minX, out float minZ, out float maxX, out float maxZ, out string boundsError))
            {
                Plugin.Log?.LogWarning($"[NavMeshRebake] {boundsError}");
                return TaskResult.Fail(boundsError);
            }

            float agentRadius = ParseFloat(task, NavMeshRebakeTaskContract.AttrAgentRadius, VillageNavMeshBake.DefaultAgentRadius);
            float agentHeight = ParseFloat(task, NavMeshRebakeTaskContract.AttrAgentHeight, VillageNavMeshBake.DefaultAgentHeight);
            float agentClimb = ParseFloat(task, NavMeshRebakeTaskContract.AttrAgentClimb, VillageNavMeshBake.DefaultAgentClimb);

            bool ok = VillageNavMeshBake.Bake(minX, minZ, maxX, maxZ, agentRadius, agentHeight, agentClimb, out string error);

            if (ok)
            {
                // Place NavMeshLinks between floor islands if HNA graph is available
                if (HnaRegionGraph.IsAvailable)
                    NavMeshLinkPlacer.PlaceLinks();

                Plugin.Log?.LogInfo(
                    $"[NavMeshRebake] Complete (bounds {minX:F0},{minZ:F0} to {maxX:F0},{maxZ:F0})");
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "minX", minX.ToString("F1") },
                    { "minZ", minZ.ToString("F1") },
                    { "maxX", maxX.ToString("F1") },
                    { "maxZ", maxZ.ToString("F1") }
                });
            }

            Plugin.Log?.LogWarning($"[NavMeshRebake] Failed: {error}");
            return TaskResult.Fail(error ?? "Bake failed");
        }

        private static bool TryGetBounds(
            VillagerTask task,
            out float minX, out float minZ, out float maxX, out float maxZ,
            out string error)
        {
            minX = minZ = maxX = maxZ = 0f;
            error = null;

            if (task.Attributes != null &&
                TryParseFloat(task.Attributes, NavMeshRebakeTaskContract.AttrMinX, out float ax) &&
                TryParseFloat(task.Attributes, NavMeshRebakeTaskContract.AttrMinZ, out float az) &&
                TryParseFloat(task.Attributes, NavMeshRebakeTaskContract.AttrMaxX, out float bx) &&
                TryParseFloat(task.Attributes, NavMeshRebakeTaskContract.AttrMaxZ, out float bz))
            {
                minX = Mathf.Min(ax, bx);
                maxX = Mathf.Max(ax, bx);
                minZ = Mathf.Min(az, bz);
                maxZ = Mathf.Max(az, bz);
                return true;
            }

            return ComputeBoundsFromVillage(out minX, out minZ, out maxX, out maxZ, out error);
        }

        private static bool ComputeBoundsFromVillage(
            out float minX, out float minZ, out float maxX, out float maxZ,
            out string error)
        {
            minX = minZ = maxX = maxZ = 0f;
            error = null;

            var beds = VillagerAIManager.GetAllBedPositions();
            bool hasGuardBounds = VillageAreaManager.TryGetCombinedBounds(
                out float guardMinX, out float guardMinZ, out float guardMaxX, out float guardMaxZ);

            if (hasGuardBounds && beds != null && beds.Count > 0)
            {
                minX = guardMinX;
                minZ = guardMinZ;
                maxX = guardMaxX;
                maxZ = guardMaxZ;
                foreach (var bed in beds)
                {
                    if (bed.x - BoundsMargin < minX) minX = bed.x - BoundsMargin;
                    if (bed.z - BoundsMargin < minZ) minZ = bed.z - BoundsMargin;
                    if (bed.x + BoundsMargin > maxX) maxX = bed.x + BoundsMargin;
                    if (bed.z + BoundsMargin > maxZ) maxZ = bed.z + BoundsMargin;
                }
                return true;
            }

            if (hasGuardBounds)
            {
                minX = guardMinX;
                minZ = guardMinZ;
                maxX = guardMaxX;
                maxZ = guardMaxZ;
                return true;
            }

            if (beds != null && beds.Count > 0)
            {
                minX = maxX = beds[0].x;
                minZ = maxZ = beds[0].z;
                foreach (var bed in beds)
                {
                    if (bed.x - BoundsMargin < minX) minX = bed.x - BoundsMargin;
                    if (bed.z - BoundsMargin < minZ) minZ = bed.z - BoundsMargin;
                    if (bed.x + BoundsMargin > maxX) maxX = bed.x + BoundsMargin;
                    if (bed.z + BoundsMargin > maxZ) maxZ = bed.z + BoundsMargin;
                }
                return true;
            }

            error = "No village areas and no villager beds; cannot compute bounds";
            return false;
        }

        private static float ParseFloat(VillagerTask task, string key, float defaultValue)
        {
            if (task.Attributes == null || !task.Attributes.TryGetValue(key, out string s))
                return defaultValue;
            return TryParseFloat(s, out float v) ? v : defaultValue;
        }

        private static bool TryParseFloat(Dictionary<string, string> attrs, string key, out float value)
        {
            value = 0f;
            return attrs != null && attrs.TryGetValue(key, out string s) && TryParseFloat(s, out value);
        }

        private static bool TryParseFloat(string s, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
