using UnityEngine;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     What a candidate task asks a villager to do. Each kind maps to a behavior
    ///     the villager must already possess (see <see cref="CandidateTask.RequiredCapability" />).
    /// </summary>
    public enum TaskKind
    {
        /// <summary>Food on a cooking station is about to overcook into coal.</summary>
        CookRescue,

        /// <summary>A placed piece is damaged and needs repair.</summary>
        RepairPiece,
    }

    /// <summary>
    ///     One row in a village's task table — a candidate job a villager may pick up
    ///     when it goes idle. Scored per-villager by <see cref="TaskReranker" />.
    ///
    ///     <para>
    ///     NOTE: distinct from <c>ValheimVillages.Schemas.VillagerTask</c>, which is the
    ///     infrastructure task-queue message (partition bakes, container scans). This
    ///     type is the <i>villager work board</i>; that one is the engine work queue.
    ///     </para>
    /// </summary>
    public sealed class CandidateTask
    {
        /// <summary>Stable identity for upsert/dedup — typically the target's ZDOID string.</summary>
        public string SourceId;

        public TaskKind Kind;

        /// <summary>World position the villager must reach to perform the task.</summary>
        public Vector3 Position;

        /// <summary>Base importance in [0,1], independent of distance/urgency.</summary>
        public float Priority;

        /// <summary>
        ///     <c>Time.time</c> at which this task becomes worthless (e.g. food turns to
        ///     coal). Zero means no deadline — the slack gate treats it as always feasible.
        /// </summary>
        public float ExpiresAt;

        /// <summary>
        ///     Behavior tag the villager must possess to be eligible (e.g. "tidy", "repair").
        ///     Hard-filtered in the reranker — a cook is never offered a repair job.
        /// </summary>
        public string RequiredCapability;

        /// <summary>Region id of <see cref="Position" />, resolved/cached at produce time.</summary>
        public string RegionId;
    }
}
