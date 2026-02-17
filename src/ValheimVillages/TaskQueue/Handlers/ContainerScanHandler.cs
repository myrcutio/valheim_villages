using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.NPCs.AI;
using ValheimVillages.NPCs.AI.Work;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Handles "container_scan" tasks. Wraps ContainerScanner.FindNearbyContainers.
    /// Returns the count of containers found in the result data.
    /// Priority: Low (1).
    /// </summary>
    public class ContainerScanHandler : ITaskHandler
    {
        public string TaskName => "container_scan";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (!TaskAttributeParser.TryParsePosition(task.Attributes, "center", out var center))
                return TaskResult.Fail("Missing or invalid center position attributes");

            if (!task.Attributes.TryGetValue("radius", out var radiusStr) ||
                !float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
            {
                radius = WorkSettings.ChestScanRadius;
            }

            var containers = ContainerScanner.FindNearbyContainers(center, radius);

            Plugin.Log?.LogDebug(
                $"[ContainerScanHandler] Found {containers.Count} containers " +
                $"near ({center.x:F0},{center.y:F0},{center.z:F0}), radius={radius:F0}m");

            activityLog.Record(
                task.SourceId,
                TaskName,
                "scan_containers",
                $"found {containers.Count} containers near ({center.x:F0},{center.y:F0},{center.z:F0})");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "container_count", containers.Count.ToString() }
            });
        }
    }
}
