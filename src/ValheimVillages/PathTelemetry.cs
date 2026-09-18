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

    }
}