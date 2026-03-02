using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Serialization and restoration of the HNA region graph for ZDO persistence.
    /// Extracted from HnaRegionGraph to separate persistence concerns from runtime queries.
    /// </summary>
    internal static class HnaGraphPersistence
    {
        /// <summary>
        /// Serialize the graph to a compact string for ZDO persistence.
        /// Format: "originX,originZ,cellSize;cellId1:height1;cellId2:height2;..."
        /// Links are omitted (boundary detection only needs regions + heights).
        /// </summary>
        internal static string Serialize()
        {
            if (!HnaRegionGraph.IsAvailable) return "";

            if (!HnaRegionGraph.GetOrigin(out float originX, out float originZ))
                return "";

            var sb = new System.Text.StringBuilder();
            sb.Append(originX.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(originZ.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(HnaRegionGraph.CellSize.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            foreach (string id in HnaRegionGraph.GetRegionIds())
            {
                sb.Append(';');
                sb.Append(id);
                sb.Append(':');
                if (HnaRegionGraph.TryGetCellHeight(id, out float h))
                    sb.Append(h.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append('?');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Restore graph from a serialized string (from ZDO).
        /// Returns true if restoration was successful (graph is now available).
        /// </summary>
        internal static bool Restore(string data)
        {
            if (string.IsNullOrEmpty(data)) return false;

            var segments = data.Split(';');
            if (segments.Length < 2) return false;

            var header = segments[0].Split(',');
            if (header.Length < 2) return false;
            if (!float.TryParse(header[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float ox)) return false;
            if (!float.TryParse(header[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float oz)) return false;

            // Reject graphs serialized with a different cell size
            if (header.Length >= 3)
            {
                if (float.TryParse(header[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float storedCellSize))
                {
                    if (Mathf.Abs(storedCellSize - HnaRegionGraph.CellSize) > 0.01f)
                    {
                        Plugin.Log?.LogInfo(
                            $"[HNA] ZDO graph cell size mismatch ({storedCellSize}m vs current {HnaRegionGraph.CellSize}m), " +
                            $"forcing regeneration");
                        return false;
                    }
                }
            }
            else
            {
                Plugin.Log?.LogInfo("[HNA] ZDO graph missing cell size header, forcing regeneration");
                return false;
            }

            var regionIds = new HashSet<string>();
            var cellHeights = new Dictionary<string, float>();

            for (int i = 1; i < segments.Length; i++)
            {
                var seg = segments[i];
                int colonIdx = seg.LastIndexOf(':');
                if (colonIdx <= 0) continue;

                string cellId = seg.Substring(0, colonIdx);
                string heightStr = seg.Substring(colonIdx + 1);

                regionIds.Add(cellId);
                if (heightStr != "?" && float.TryParse(heightStr,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float h))
                    cellHeights[cellId] = h;
            }

            if (regionIds.Count == 0) return false;

            HnaRegionGraph.SetGraph(ox, oz, regionIds, new List<HnaLink>(), cellHeights);
            Plugin.Log?.LogInfo($"[HNA] Restored graph from ZDO: {regionIds.Count} regions");
            return true;
        }
    }
}
