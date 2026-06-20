using System.Collections.Generic;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Spawn-settle gate for a villager, built on the SAME deferred precondition /
    ///     backoff machinery as the <see cref="RequireAgentHandler" /> /
    ///     <see cref="RequireObjectDBHandler" /> tasks — only per-villager instead of per
    ///     static method. One task is enqueued per villager (SourceId = RecordId) when it
    ///     registers; this handler defers it via <see cref="ITaskPrecondition" /> until the
    ///     villager is standing on its village graph with a ready agent, then flips the
    ///     villager's settled flag (<see cref="VillagerAI.MarkSettled" />). Until then
    ///     <see cref="VillagerAI.UpdateAI" /> holds the villager in place, so a first-tick
    ///     <c>FleeBehavior</c> can't path the fresh villager off-graph before it settles.
    ///     <para>
    ///         There is no per-frame race: the queue polls <see cref="IsReady" /> with
    ///         backoff and the villager simply waits until its preconditions are ready.
    ///     </para>
    /// </summary>
    [RegisterTaskHandler]
    public class VillagerSettleHandler : ITaskHandlerWithLog, ITaskPrecondition
    {
        public const string TaskNameConst = "villager_settle";

        // Effectively no timeout: keep retrying until the village graph is ready. A server
        // that can't yet bake the village navmesh may take a long time, and a hot-reload's
        // navmesh self-heals after ~10s — holding a villager is the correct, safe wait in
        // both cases (far better than letting it act off-graph). The task can't leak: once
        // the villager is gone, IsReady returns true so Handle completes it and the queue
        // clears its pending key; world unload clears the queue entirely.
        private const float NoTimeout = float.MaxValue;

        public string TaskName => TaskNameConst;

        /// <summary>Enqueue the one-per-villager settle gate. Deduped by (name, recordId).</summary>
        public static void Enqueue(string recordId)
        {
            if (string.IsNullOrEmpty(recordId)) return;
            GlobalTaskQueue.Enqueue(new VillagerTask
            {
                Name = TaskNameConst,
                SourceId = recordId,
                Priority = TaskPriority.High,
                TimeoutSeconds = NoTimeout,
                Attributes = new Dictionary<string, string>(),
            });
        }

        /// <summary>
        ///     Ready once the villager is on its village graph with a ready agent — OR once
        ///     it is gone (despawned/evicted), so the task completes instead of spinning.
        ///     Side-effect free (the agent is created in VillagerAI.UpdateAI's hold path, not
        ///     here) and cheap, as the precondition contract requires.
        /// </summary>
        public bool IsReady(VillagerTask task)
        {
            if (!VillagerAIManager.ActiveVillagers.TryGetValue(task.SourceId, out var ai) || ai == null)
                return true; // villager gone — let Handle no-op and finish

            if (!NavMeshBakeManager.AgentReady) return false; // no slot-31 bake installed yet
            return ai.PreconditionsSettled();
        }

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (VillagerAIManager.ActiveVillagers.TryGetValue(task.SourceId, out var ai) && ai != null)
                ai.MarkSettled("on village graph, agent ready");
            return TaskResult.Ok();
        }
    }
}
