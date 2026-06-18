using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Runs a method annotated with <see cref="RequireAgentAttribute" /> once the
    ///     villager NavMesh agent infrastructure is ready. One task is enqueued per
    ///     annotated method (SourceId = the method key); this handler defers each via
    ///     <see cref="ITaskPrecondition" /> until both the slot-31 agent type is
    ///     registered and a slot-31 bake is installed, then invokes it through
    ///     <see cref="AttributeScanner" />.
    /// </summary>
    [RegisterTaskHandler]
    public class RequireAgentHandler : ITaskHandlerWithLog, ITaskPrecondition
    {
        public const string TaskNameConst = "require_agent";

        public string TaskName => TaskNameConst;

        /// <summary>
        ///     Ready once the villager agent type is registered with Pathfinding AND a
        ///     slot-31 NavMesh bake has successfully completed and is installed
        ///     (<see cref="NavMeshBakeManager.AgentReady" />). This is the assertion the
        ///     [RequireAgent] gate provides: no agent setup before the bake exists.
        /// </summary>
        public bool IsReady(VillagerTask task)
        {
            return NavMeshBakeManager.AgentReady;
        }

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            return AttributeScanner.InvokeRequireAgent(task.SourceId)
                ? TaskResult.Ok()
                : TaskResult.Fail($"[RequireAgent] no method registered for key '{task.SourceId}'");
        }
    }
}
