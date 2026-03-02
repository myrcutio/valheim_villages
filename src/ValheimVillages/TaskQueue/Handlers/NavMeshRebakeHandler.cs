using System.Collections.Generic;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// No-op handler: NavMesh baking and link placement are disabled.
    /// Experiments use Valheim's built-in MoveTo/FindPath only.
    /// </summary>
    [RegisterTaskHandler]
    public class NavMeshRebakeHandler : ITaskHandlerWithLog
    {
        public string TaskName => NavMeshRebakeTaskContract.TaskName;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            return TaskResult.Ok(new Dictionary<string, string> { { "skipped", "baking_disabled" } });
        }
    }
}
