using ValheimVillages.Scheduling;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     A work behavior the scheduler can drive directly (PrimaryMode): instead of
    ///     self-discovering a target, it executes a specific scheduler-assigned task. Every
    ///     directed behavior is also an <see cref="IBehavior" /> — the scheduler assigns the
    ///     task, the normal dispatch loop executes it.
    /// </summary>
    public interface IDirectedBehavior
    {
        /// <summary>Can this behavior carry out the given task kind?</summary>
        bool CanExecute(TaskKind kind);

        /// <summary>
        ///     Begin executing a scheduler-assigned task. Returns false if it can't start
        ///     (no reachable approach, nothing actionable at the target), in which case the
        ///     dispatcher releases the claim.
        /// </summary>
        bool BeginAssignment(CandidateTask task);

        /// <summary>True while an assignment is in progress (travel + action).</summary>
        bool AssignmentActive { get; }
    }
}
