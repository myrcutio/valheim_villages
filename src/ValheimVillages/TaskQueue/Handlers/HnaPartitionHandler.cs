using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villages;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Low-priority task that builds the HNA region graph for village pathfinding.
    /// Partitions only non-spawnable areas: patrol polygons and, at minimum,
    /// any space within 15m of a villager's bed. Adds doors and stairs as links.
    /// Grid sampling, flood-fill, and link detection are delegated to <see cref="HnaGridBuilder"/>.
    /// </summary>
    [RegisterTaskHandler]
    public class HnaPartitionHandler : ITaskHandlerWithLog
    {
        public const string HnaPartitionTaskName = "hna_partition";

        /// <summary>Radius in meters around each bed to treat as village for scanning.</summary>
        public const float BedVillageRadius = 15f;
        /// <summary>Max distance (m) from any bed that the flood-fill may reach.</summary>
        internal const float FloodFillRadius = 45f;
        /// <summary>When building regions, use this radius so link endpoints (door ± DoorSideOffset) still land in a region.</summary>
        private const float RegionBuildRadius = 30f;

        public string TaskName => HnaPartitionTaskName;

        /// <summary>Max distance from the anchor bed for another bed to be considered
        /// part of the same village.</summary>
        private const float VillageClusterRadius = 50f;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            var allBeds = ValheimVillages.Villager.AI.VillagerAIManager.GetAllBedPositions();
            var beds = FilterBedsByAnchor(allBeds, task);
            bool hasPatrolBounds = VillageAreaManager.TryGetCombinedBounds(
                out float patrolMinX, out float patrolMinZ, out float patrolMaxX, out float patrolMaxZ);

            float minX, minZ, maxX, maxZ;
            if (hasPatrolBounds && beds != null && beds.Count > 0)
            {
                minX = patrolMinX;
                minZ = patrolMinZ;
                maxX = patrolMaxX;
                maxZ = patrolMaxZ;
                foreach (var bed in beds)
                {
                    if (bed.x - RegionBuildRadius < minX) minX = bed.x - RegionBuildRadius;
                    if (bed.z - RegionBuildRadius < minZ) minZ = bed.z - RegionBuildRadius;
                    if (bed.x + RegionBuildRadius > maxX) maxX = bed.x + RegionBuildRadius;
                    if (bed.z + RegionBuildRadius > maxZ) maxZ = bed.z + RegionBuildRadius;
                }
            }
            else if (hasPatrolBounds)
            {
                minX = patrolMinX;
                minZ = patrolMinZ;
                maxX = patrolMaxX;
                maxZ = patrolMaxZ;
            }
            else if (beds != null && beds.Count > 0)
            {
                minX = maxX = beds[0].x;
                minZ = maxZ = beds[0].z;
                foreach (var bed in beds)
                {
                    if (bed.x - RegionBuildRadius < minX) minX = bed.x - RegionBuildRadius;
                    if (bed.z - RegionBuildRadius < minZ) minZ = bed.z - RegionBuildRadius;
                    if (bed.x + RegionBuildRadius > maxX) maxX = bed.x + RegionBuildRadius;
                    if (bed.z + RegionBuildRadius > maxZ) maxZ = bed.z + RegionBuildRadius;
                }
            }
            else
            {
                HnaRegionGraph.Clear();
                HnaDebugVisualization.ClearMarkers();
                Plugin.Log?.LogInfo("[HNA] Partition skipped: no village areas and no villager beds.");
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "regions", "0" },
                    { "links", "0" },
                    { "reason", "no_beds_or_areas" }
                });
            }

            float originX = minX;
            float originZ = minZ;
            int cellCountX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / HnaRegionGraph.CellSize));
            int cellCountZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / HnaRegionGraph.CellSize));

            var (regionIds, cellHeights) = HnaGridBuilder.FloodFillFromBeds(originX, originZ, cellCountX, cellCountZ, beds);

            var links = new List<HnaLink>();
            HnaGridBuilder.CollectDoorLinks(originX, originZ, minX, minZ, maxX, maxZ, regionIds, cellHeights, links);
            HnaGridBuilder.CollectSlopeLinks(originX, originZ, regionIds, cellHeights, links);
            HnaGridBuilder.CollectVerticalLinksInCell(originX, originZ, regionIds, cellHeights, links);

            HnaRegionGraph.SetGraph(originX, originZ, regionIds, links, cellHeights);

            string regionCentersStr = BuildRegionCentersString(originX, originZ, regionIds, cellHeights);
            string linksStr = BuildLinksSummaryString(links);
            ValheimVillages.PathTelemetry.LogHnaGraph(regionIds.Count, links.Count, minX, minZ, maxX, maxZ, regionCentersStr, linksStr);

            // Only refresh markers if they were already toggled on
            if (HnaDebugVisualization.MarkersEnabled)
                HnaDebugVisualization.SpawnTorchesAtRegions();

            Plugin.Log?.LogInfo(
                $"[HNA] Partition complete: {regionIds.Count} regions, {links.Count} links " +
                $"(bounds {minX:F0},{minZ:F0} to {maxX:F0},{maxZ:F0})");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "regions", regionIds.Count.ToString() },
                { "links", links.Count.ToString() }
            });
        }

        private static string BuildRegionCentersString(float originX, float originZ, HashSet<string> regionIds,
            Dictionary<string, float> cellHeights)
        {
            var sb = new StringBuilder();
            foreach (string id in regionIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!HnaGridBuilder.TryParseRegionId(id, out int ix, out int iz)) continue;
                float wx = originX + (ix + 0.5f) * HnaRegionGraph.CellSize;
                float wz = originZ + (iz + 0.5f) * HnaRegionGraph.CellSize;
                float wy = 0f;
                if (cellHeights != null && cellHeights.TryGetValue(id, out float bfsY))
                    wy = bfsY;
                else if (HnaRegionGraph.GetSolidHeightAt(wx, wz, out float h))
                    wy = h;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(id).Append(',').Append(wx.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(',').Append(wy.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(',').Append(wz.ToString("F1", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string BuildLinksSummaryString(List<HnaLink> links)
        {
            var sb = new StringBuilder();
            foreach (var link in links)
            {
                if (sb.Length > 0) sb.Append(';');
                string typeStr = link.LinkType == HnaLinkType.Door ? "door" : link.LinkType == HnaLinkType.Slope ? "slope" : "stair";
                sb.Append(link.FromRegionId).Append(',').Append(link.ToRegionId).Append(',').Append(typeStr);
            }
            return sb.ToString();
        }

        /// <summary>
        /// If the task includes an anchor position (from the requesting patroller's bed),
        /// filter beds to only those within VillageClusterRadius.
        /// </summary>
        private static List<Vector3> FilterBedsByAnchor(List<Vector3> allBeds, VillagerTask task)
        {
            if (allBeds == null || allBeds.Count == 0) return allBeds;

            if (task?.Attributes == null ||
                !task.Attributes.TryGetValue("anchor_x", out string axStr) ||
                !task.Attributes.TryGetValue("anchor_z", out string azStr))
                return allBeds;

            if (!float.TryParse(axStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float anchorX) ||
                !float.TryParse(azStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float anchorZ))
                return allBeds;

            float r2 = VillageClusterRadius * VillageClusterRadius;
            var filtered = new List<Vector3>();
            foreach (var bed in allBeds)
            {
                float dx = bed.x - anchorX;
                float dz = bed.z - anchorZ;
                if (dx * dx + dz * dz <= r2)
                    filtered.Add(bed);
            }

            Plugin.Log?.LogInfo(
                $"[HNA] Filtered beds by anchor ({anchorX:F0},{anchorZ:F0}): " +
                $"{filtered.Count}/{allBeds.Count} within {VillageClusterRadius}m");

            return filtered.Count > 0 ? filtered : allBeds;
        }
    }
}
