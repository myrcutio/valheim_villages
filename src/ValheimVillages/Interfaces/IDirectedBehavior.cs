using ValheimVillages.Scheduling;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     A work behavior the scheduler drives directly: instead of self-discovering a
    ///     target, it executes a specific scheduler-assigned task. Every directed behavior is
    ///     also an <see cref="IBehavior" /> — the scheduler assigns the task, the normal
    ///     dispatch loop executes it.
    ///
    ///     <para>The scheduler is the ONLY selector of work, so a directed behavior must
    ///     never self-discover: its <c>WantsControl</c> is required to return exactly
    ///     <see cref="AssignmentActive" />. If the two could disagree the villager would read
    ///     as busy to the dispatcher (which holds the task claim while AssignmentActive) and
    ///     as idle to the behavior selector, and would never be reassigned — a permanent
    ///     stall with the villager falling through to the routine tier forever.</para>
    /// </summary>
    public interface IDirectedBehavior
    {
        /// <summary>Can this behavior carry out the given task kind?</summary>
        bool CanExecute(TaskKind kind);

        /// <summary>
        ///     Begin executing a scheduler-assigned task. On anything other than
        ///     <see cref="AssignmentResult.Accepted" /> the dispatcher releases the claim;
        ///     the distinction between the two failures decides whether the row is retried
        ///     next tick or blocked until the region graph changes, so a behavior must not
        ///     report <see cref="AssignmentResult.Unreachable" /> for a merely transient
        ///     "nothing to do here right now".
        /// </summary>
        AssignmentResult BeginAssignment(CandidateTask task);

        /// <summary>True while an assignment is in progress (travel + action).</summary>
        bool AssignmentActive { get; }

        /// <summary>
        ///     Drop the current assignment outright and return to Idle, so
        ///     <see cref="AssignmentActive" /> reads false afterwards. Called by the dispatcher
        ///     when an assignment outlives <see cref="SchedulerSettings.AssignmentCeiling" /> —
        ///     the backstop for any path that leaves a behavior "busy" with nothing to finish it.
        /// </summary>
        void AbandonAssignment(string reason);
    }
}
