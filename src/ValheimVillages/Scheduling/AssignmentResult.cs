namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Why a directed behavior did or didn't take a scheduler-assigned task.
    ///
    ///     <para>
    ///     This was a bare bool, which collapsed two failures the dispatcher has to treat
    ///     very differently. "Nothing actionable at the target" is transient — a chest
    ///     deposit, or a piece taking damage, makes the same row actionable again with
    ///     nothing else in the world changing — so it must stay on the board and be retried.
    ///     "No walkable approach" is not transient:
    ///     <c>RepairBehavior.TryResolveReachableApproach</c> resolves an approach purely from
    ///     the target position and the village region graph, never from the villager asking,
    ///     so the verdict cannot change until that graph is rebuilt. Retrying it on a timer
    ///     burns a dispatch slot every tick, forever, for an answer that is already known.
    ///     </para>
    /// </summary>
    public enum AssignmentResult
    {
        /// <summary>Assignment started; the behavior owns the task until it completes or aborts.</summary>
        Accepted,

        /// <summary>
        ///     Nothing to do at the target right now (no damaged piece, no finished food, no
        ///     work order below its floor), or the nav infrastructure isn't up yet. Retryable
        ///     without any village change — the dispatcher leaves the row on the board.
        /// </summary>
        NotActionable,

        /// <summary>
        ///     The target has no walkable approach under the CURRENT region graph. The
        ///     dispatcher blocks the row until that graph is rebuilt — see
        ///     <see cref="TaskBoard.MarkBlocked" />.
        /// </summary>
        Unreachable,
    }
}
