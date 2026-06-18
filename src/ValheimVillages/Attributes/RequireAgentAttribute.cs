using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks a static parameterless method that must not run until the villager
    ///     NavMesh agent infrastructure is ready — i.e. the slot-31 agent type is
    ///     registered with Valheim's Pathfinding AND a slot-31 NavMesh bake has
    ///     successfully completed and is installed.
    ///     <para>
    ///     <see cref="AttributeScanner.EnqueueAgentDependentTasks" /> enqueues a
    ///     high-priority <c>require_agent</c> task for each annotated method when the
    ///     scene comes up. The task's precondition (RequireAgentHandler.IsReady) polls
    ///     <see cref="ValheimVillages.Villager.AI.Navigation.NavMeshBakeManager.AgentReady" />
    ///     and the queue defers the task with a ~1.5s backoff until it is ready, then
    ///     invokes the method.
    ///     </para>
    ///     <para>
    ///     Mirrors <see cref="RequireObjectDBAttribute" />, but the dependency lands much
    ///     later in a world load (the bake is itself deferred until the village's zones
    ///     are loaded and piece colliders instantiated), so these tasks carry a longer
    ///     timeout. Use this instead of creating a NavMeshAgent inside the movement tick
    ///     and hoping the bake already ran: that silently produces a null or off-mesh
    ///     agent during the race (the symptom: a villager stuck reporting "Patrolling"
    ///     with moveDir zero and no agent).
    ///     </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RequireAgentAttribute : Attribute
    {
    }
}
