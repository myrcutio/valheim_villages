using System.Collections.Generic;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Dev command to force an immediate hna_partition rebuild. Useful for
    /// verifying partition-time behavior (e.g., polygon clip activation,
    /// door blocker counts) without waiting for the patrol behavior to
    /// request one.
    /// </summary>
    internal static class RepartitionCommand
    {
        [DevCommand("Force enqueue an immediate hna_partition rebuild", Name = "vv_repartition")]
        public static void Repartition()
        {
            var task = new VillagerTask
            {
                Name = "hna_partition",
                SourceId = "user",
                Priority = TaskPriority.High,
                TimeoutSeconds = 60f,
                Attributes = new Dictionary<string, string>(),
            };
            GlobalTaskQueue.Enqueue(task);
            string msg = "[vv_repartition] Enqueued hna_partition (high priority, no anchor)";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
