using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.Behaviors.Alarm;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Handles "breach_check" tasks. Wraps BreachDetection.CheckForBreach.
    /// Returns breach status and outward direction in result data.
    /// Priority: High (3).
    /// </summary>
    public class BreachCheckHandler : ITaskHandler
    {
        public string TaskName => "breach_check";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (!TaskAttributeParser.TryParsePosition(task.Attributes, "waypoint", out var waypoint))
                return TaskResult.Fail("Missing or invalid waypoint position attributes");

            if (!TaskAttributeParser.TryParsePosition(task.Attributes, "bed", out var bedCenter))
                return TaskResult.Fail("Missing or invalid bed position attributes");

            bool breached = BreachDetection.CheckForBreach(waypoint, bedCenter);

            var data = new Dictionary<string, string>
            {
                { "breached", breached.ToString() }
            };

            if (breached)
            {
                // Compute breach position (5m outward from waypoint)
                var outward = waypoint - bedCenter;
                outward.y = 0f;
                outward.Normalize();
                var breachPos = waypoint + outward * 5f;

                data["breach_x"] = breachPos.x.ToString("F2", CultureInfo.InvariantCulture);
                data["breach_y"] = breachPos.y.ToString("F2", CultureInfo.InvariantCulture);
                data["breach_z"] = breachPos.z.ToString("F2", CultureInfo.InvariantCulture);

                activityLog.Record(
                    task.SourceId,
                    TaskName,
                    "breach_detected",
                    $"breach detected at waypoint ({waypoint.x:F0},{waypoint.y:F0},{waypoint.z:F0})");

                Plugin.Log?.LogWarning(
                    $"[BreachCheckHandler] BREACH at ({waypoint.x:F0},{waypoint.z:F0}) " +
                    $"for {task.SourceId}");
            }
            else
            {
                activityLog.Record(
                    task.SourceId,
                    TaskName,
                    "no_breach",
                    $"no breach at waypoint ({waypoint.x:F0},{waypoint.y:F0},{waypoint.z:F0})");
            }

            return TaskResult.Ok(data);
        }
    }
}
