using System.Collections.Generic;
using ValheimVillages.Attributes;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Handles "recipe_discovery_refresh" tasks. Re-runs cultivator and cooking
    ///     discovery after world load so mods that registered items later are picked up.
    ///     Priority: Low (1).
    /// </summary>
    [RegisterTaskHandler]
    public class RecipeDiscoveryRefreshHandler : ITaskHandlerWithLog
    {
        public string TaskName => "recipe_discovery_refresh";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (ObjectDB.instance == null)
                return TaskResult.Fail("ObjectDB not ready");

            var added = VirtualRecipeLoader.RecheckDiscoveredRecipes(ObjectDB.instance);

            activityLog.Record(
                task.SourceId,
                TaskName,
                "recheck",
                $"added {added} discovered recipes");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "recipes_added", added.ToString() },
            });
        }
    }
}