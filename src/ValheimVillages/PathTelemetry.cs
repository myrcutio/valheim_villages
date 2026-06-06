using System;
using System.IO;
using BepInEx;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages
{
    /// <summary>
    ///     Writes HNA telemetry to NDJSON for debugging.
    /// </summary>
    public static class PathTelemetry
    {
        private static readonly string LogPath = Path.Combine(
            Paths.ConfigPath, "vv_dumps", "path_telemetry.ndjson");

        private static readonly object Lock = new();

        /// <summary>
        ///     Log HNA graph for analysis: region count, link count, bounds, region centers (id,x,y,z;...), links
        ///     (fromId,toId,type;...).
        /// </summary>
        public static void LogRegionGraph(int regionCount, int linkCount, float minX, float minZ, float maxX,
            float maxZ, string regionCenters, string linksSummary)
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
                linksSummary = linksSummary ?? "",
            };
            Write("hna_graph", JsonUtility.ToJson(data), "hna");
        }

        /// <summary>
        ///     Log HNA attributes for a world position (e.g. player): region id, validity, solid height, cell bounds,
        ///     vertical sample heights.
        /// </summary>
        public static void LogHnaPlayerDebug(Vector3 position)
        {
            var px = (float)Math.Round(position.x, 2);
            var py = (float)Math.Round(position.y, 2);
            var pz = (float)Math.Round(position.z, 2);
            var graph = Villages.Entity.VillageRegistry.GraphAt(position);
            var regionId = graph?.PointToRegionId(position);
            var graphAvailable = graph != null && graph.GetOrigin(out _, out _);
            var regionValid = graph != null && !string.IsNullOrEmpty(regionId) && graph.IsValidRegion(regionId);
            var solidHeightAtPosition = 0f;
            if (ZoneSystem.instance != null)
                ZoneSystem.instance.GetSolidHeight(new Vector3(position.x, 0f, position.z), out solidHeightAtPosition,
                    500);
            float cellMinX = 0f, cellMaxX = 0f, cellMinZ = 0f, cellMaxZ = 0f;
            float centerY = 0f, minY = 0f, maxY = 0f;
            float mx = 0f, mx2 = 0f, mz = 0f, mz2 = 0f;
            if (regionValid && graph.GetRegionBounds(regionId, out mx, out mx2, out mz, out mz2))
            {
                cellMinX = (float)Math.Round(mx, 2);
                cellMaxX = (float)Math.Round(mx2, 2);
                cellMinZ = (float)Math.Round(mz, 2);
                cellMaxZ = (float)Math.Round(mz2, 2);
            }

            float cy = 0f, mnY = 0f, mxY = 0f;
            if (regionValid && graph.GetRegionSampleHeights(regionId, out cy, out mnY, out mxY))
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
                verticalSpread = (float)Math.Round(maxY - minY, 2),
            };
            Write("hna_player_debug", JsonUtility.ToJson(data), "hna_debug");
        }

        private static void Write(string message, string dataJson, string runId)
        {
            try
            {
                var ts = (long)(Time.time * 1000);
                var id = "pt_" + ts;
                var line = "{\"id\":\"" + id + "\",\"timestamp\":" + ts +
                           ",\"location\":\"PathTelemetry\",\"message\":\"" +
                           Escape(message) + "\",\"data\":" + dataJson + ",\"runId\":\"" + Escape(runId) +
                           "\",\"hypothesisId\":\"path_compare\"}\n";
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