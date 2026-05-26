using System.Collections.Generic;
using System.Globalization;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Handles "container_scan" tasks. Wraps ContainerScanner.FindNearbyContainers.
    ///     Returns the count of containers found in the result data.
    ///     Priority: Low (1).
    /// </summary>
    [RegisterTaskHandler]
    public class ContainerScanHandler : ITaskHandlerWithLog
    {
        public string TaskName => "container_scan";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (!TaskAttributeParser.TryParsePosition(task.Attributes, "center", out var center))
                return TaskResult.Fail("Missing or invalid center position attributes");

            if (!task.Attributes.TryGetValue("radius", out var radiusStr) ||
                !float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius))
                radius = WorkSettings.ChestScanRadius;

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
                { "container_count", containers.Count.ToString() },
            });
        }
    }
}