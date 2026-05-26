using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Testing;

namespace ValheimVillages.TaskQueue.Handlers
{
    [RegisterTaskHandler]
    public class IntegrationTestHandler : ITaskHandlerWithLog
    {
        public const string TaskNameConst = "integration_tests";

        private const float DeferralSeconds = 2f;
        private const float DeadlineSeconds = 30f;
        private const string AttrScheduledAt = "scheduled_at";

        public string TaskName => TaskNameConst;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            var scheduledAt = GetScheduledAt(task);
            var elapsed = Time.time - scheduledAt;

            // Run after a short deferral; do not wait for NavMesh (baking disabled for built-in MoveTo/FindPath experiments).
            if (elapsed < DeferralSeconds)
            {
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = TaskNameConst,
                    SourceId = "system",
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = DeadlineSeconds + 5f,
                    NotBefore = Time.time + DeferralSeconds,
                    Attributes = new Dictionary<string, string>
                    {
                        { AttrScheduledAt, scheduledAt.ToString("R") },
                    },
                });
                return TaskResult.Ok();
            }

            Plugin.Log?.LogInfo(
                $"[IntegrationTests] Running tests ({elapsed:F1}s after schedule)");
            ModTestRunner.RunAll();
            return TaskResult.Ok();
        }

        private static float GetScheduledAt(VillagerTask task)
        {
            if (task.Attributes != null &&
                task.Attributes.TryGetValue(AttrScheduledAt, out var s) &&
                float.TryParse(s, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v))
                return v;

            return task.CreatedAt;
        }
    }
}