using System.Collections.Generic;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Handles "village_index" tasks. Runs once after world load to rebuild the
    ///     live village cache from the world's <c>vv_village</c> ZDOs and hydrate each
    ///     village's HNA region graph from its persisted blob — the load-time
    ///     counterpart to the partition's save. Priority: High.
    /// </summary>
    [RegisterTaskHandler]
    public class VillageIndexHandler : ITaskHandlerWithLog
    {
        public string TaskName => "village_index";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            var (villages, withGraph) = VillageRegistry.HydrateAll();

            Plugin.Log?.LogInfo($"[village_index] villages={villages} with_graph={withGraph}");
            activityLog.Record(task.SourceId, TaskName, "index",
                $"villages={villages} with_graph={withGraph}");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "villages", villages.ToString() },
                { "with_graph", withGraph.ToString() },
            });
        }
    }
}
