using System;
using System.IO;
using UnityEngine;

namespace ValheimVillages
{
    /// <summary>
    /// Writes path-comparison telemetry to NDJSON for debugging (player path vs farmer pathfinding).
    /// </summary>
    public static class PathTelemetry
    {
        private const string LogPath = "/home/benny/Projects/valheim_villages/.cursor/debug.log";
        private static readonly object Lock = new object();

        /// <summary>Log a path point (player or NPC position).</summary>
        public static void LogPathPoint(string role, string name, Vector3 position, string extra = null)
        {
            var data = new PathPointData
            {
                role = role,
                name = name ?? "",
                x = (float)Math.Round(position.x, 2),
                y = (float)Math.Round(position.y, 2),
                z = (float)Math.Round(position.z, 2),
                extra = extra ?? ""
            };
            Write("path_point", JsonUtility.ToJson(data), role);
        }

        /// <summary>Log farmer pathing state (position, target, distances, subState).</summary>
        public static void LogFarmerPathing(string farmerName, Vector3 position, Vector3 target, float dist3D, float distXZ, string subState)
        {
            var data = new FarmerPathingData
            {
                role = "farmer",
                name = farmerName ?? "",
                nx = (float)Math.Round(position.x, 2),
                ny = (float)Math.Round(position.y, 2),
                nz = (float)Math.Round(position.z, 2),
                tx = (float)Math.Round(target.x, 2),
                ty = (float)Math.Round(target.y, 2),
                tz = (float)Math.Round(target.z, 2),
                dist3D = (float)Math.Round(dist3D, 2),
                distXZ = (float)Math.Round(distXZ, 2),
                subState = subState ?? ""
            };
            Write("farmer_pathing", JsonUtility.ToJson(data), "farmer");
        }

        /// <summary>Log pathing event (stuck detected or give up). Strategy cycle removed with pathing strategy system.</summary>
        public static void LogStrategyEvent(string eventType, string npcName, string fromStrategy, string toStrategy, float dist3D, float threshold, bool wrapped)
        {
            var data = new StrategyEventData
            {
                eventType = eventType ?? "",
                npcName = npcName ?? "",
                fromStrategy = fromStrategy ?? "",
                toStrategy = toStrategy ?? "",
                dist3D = (float)Math.Round(dist3D, 2),
                threshold = (float)Math.Round(threshold, 2),
                wrapped = wrapped
            };
            Write("strategy_" + eventType, JsonUtility.ToJson(data), "strategy_cycle");
        }

        /// <summary>Log HNA graph for analysis: region count, link count, bounds, region centers (id,x,y,z;...), links (fromId,toId,type;...).</summary>
        public static void LogHnaGraph(int regionCount, int linkCount, float minX, float minZ, float maxX, float maxZ, string regionCenters, string linksSummary)
        {
            var data = new HnaGraphData
            {
                regionCount = regionCount,
                linkCount = linkCount,
                minX = (float)Math.Round(minX, 2),
                minZ = (float)Math.Round(minZ, 2),
                maxX = (float)Math.Round(maxX, 2),
                maxZ = (float)Math.Round(maxZ, 2),
                regionCenters = regionCenters ?? "",
                linksSummary = linksSummary ?? ""
            };
            Write("hna_graph", JsonUtility.ToJson(data), "hna");
        }

        /// <summary>Log HNA attributes for a world position (e.g. player): region id, validity, solid height, cell bounds, vertical sample heights.</summary>
        public static void LogHnaPlayerDebug(Vector3 position)
        {
            float px = (float)Math.Round(position.x, 2);
            float py = (float)Math.Round(position.y, 2);
            float pz = (float)Math.Round(position.z, 2);
            string regionId = Villager.AI.Navigation.HnaRegionGraph.PointToRegionId(position);
            bool graphAvailable = Villager.AI.Navigation.HnaRegionGraph.GetOrigin(out _, out _);
            bool regionValid = !string.IsNullOrEmpty(regionId) && Villager.AI.Navigation.HnaRegionGraph.IsValidRegion(regionId);
            float solidHeightAtPosition = 0f;
            if (ZoneSystem.instance != null)
                ZoneSystem.instance.GetSolidHeight(new Vector3(position.x, 0f, position.z), out solidHeightAtPosition, 500);
            float cellMinX = 0f, cellMaxX = 0f, cellMinZ = 0f, cellMaxZ = 0f;
            float centerY = 0f, minY = 0f, maxY = 0f;
            float mx = 0f, mx2 = 0f, mz = 0f, mz2 = 0f;
            if (regionValid && Villager.AI.Navigation.HnaRegionGraph.GetRegionBounds(regionId, out mx, out mx2, out mz, out mz2))
            {
                cellMinX = (float)Math.Round(mx, 2);
                cellMaxX = (float)Math.Round(mx2, 2);
                cellMinZ = (float)Math.Round(mz, 2);
                cellMaxZ = (float)Math.Round(mz2, 2);
            }
            float cy = 0f, mnY = 0f, mxY = 0f;
            if (regionValid && Villager.AI.Navigation.HnaRegionGraph.GetRegionSampleHeights(regionId, out cy, out mnY, out mxY))
            {
                centerY = (float)Math.Round(cy, 2);
                minY = (float)Math.Round(mnY, 2);
                maxY = (float)Math.Round(mxY, 2);
            }
            var data = new HnaPlayerDebugData
            {
                px = px,
                py = py,
                pz = pz,
                regionId = regionId ?? "",
                graphAvailable = graphAvailable,
                regionValid = regionValid,
                solidHeightAtPosition = (float)Math.Round(solidHeightAtPosition, 2),
                cellMinX = cellMinX,
                cellMaxX = cellMaxX,
                cellMinZ = cellMinZ,
                cellMaxZ = cellMaxZ,
                centerY = centerY,
                minY = minY,
                maxY = maxY,
                verticalSpread = (float)Math.Round(maxY - minY, 2)
            };
            Write("hna_player_debug", JsonUtility.ToJson(data), "hna_debug");
        }

        private static void Write(string message, string dataJson, string runId)
        {
            try
            {
                long ts = (long)(Time.time * 1000);
                string id = "pt_" + ts;
                string line = "{\"id\":\"" + id + "\",\"timestamp\":" + ts + ",\"location\":\"PathTelemetry\",\"message\":\"" +
                    Escape(message) + "\",\"data\":" + dataJson + ",\"runId\":\"" + Escape(runId) + "\",\"hypothesisId\":\"path_compare\"}\n";
                lock (Lock)
                {
                    File.AppendAllText(LogPath, line);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PathTelemetry] Write failed: {ex.Message}");
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        [Serializable]
        private class PathPointData
        {
            public string role;
            public string name;
            public float x, y, z;
            public string extra;
        }

        [Serializable]
        private class FarmerPathingData
        {
            public string role;
            public string name;
            public string subState;
            public float nx, ny, nz;
            public float tx, ty, tz;
            public float dist3D;
            public float distXZ;
        }

        [Serializable]
        private class StrategyEventData
        {
            public string eventType;
            public string npcName;
            public string fromStrategy;
            public string toStrategy;
            public float dist3D;
            public float threshold;
            public bool wrapped;
        }

        [Serializable]
        private class HnaGraphData
        {
            public int regionCount;
            public int linkCount;
            public float minX, minZ, maxX, maxZ;
            public string regionCenters;
            public string linksSummary;
        }

        [Serializable]
        private class HnaPlayerDebugData
        {
            public float px, py, pz;
            public string regionId;
            public bool graphAvailable;
            public bool regionValid;
            public float solidHeightAtPosition;
            public float cellMinX, cellMaxX, cellMinZ, cellMaxZ;
            public float centerY, minY, maxY, verticalSpread;
        }
    }
}
