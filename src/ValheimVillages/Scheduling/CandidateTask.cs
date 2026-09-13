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

        /// <summary>
        ///     The villager has crafting/farming work to do (a chest work order below its
        ///     target, or farm plots needing tending). The actual detect-and-commit lives
        ///     in <c>CraftingBehavior.TryScanForWork</c>; the producer only flags that a
        ///     crafter exists so the scheduler offers it the slot.
        /// </summary>
        CraftWork,
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

        /// <summary>
        ///     Villager this row was minted FOR, or null when any capable villager may take it.
        ///
        ///     <para>Location-keyed rows (a damaged piece, food about to burn) belong to whoever
        ///     is best placed to handle them, so they leave this null. A <see cref="TaskKind.CraftWork" />
        ///     row is different: it is keyed <c>craft:&lt;villagerId&gt;</c>, positioned at THAT
        ///     villager's anchor, and its work is discovered from that villager's own chest orders.
        ///     Without this field a capability check alone let any craft-capable villager claim
        ///     someone else's row, immediately fail <c>BeginAssignment</c> (it has no orders of its
        ///     own), and leave the dispatcher's "(approach-failed)" sentinel claim on the row —
        ///     locking it against the villager it was minted for, and against everyone else.</para>
        /// </summary>
        public string OwnerVillagerId;

        /// <summary>
        ///     For <see cref="TaskKind.CraftWork" />: the output item prefab this row represents,
        ///     or null for a generic "this crafter has work" row (the farming floor candidate).
        ///
        ///     <para>Work orders used to be collapsed into ONE row per villager whose priority was
        ///     the worst deficit across all of them, so the reranker could only decide WHETHER a
        ///     villager crafts — never WHICH order. Which-order fell to the first-viable scan loop,
        ///     and orders late in record order were starved outright. One row per order lets the
        ///     reranker score them against each other with its spatial and slack terms, and gives a
        ///     trainer something per-order to learn from.</para>
        ///
        ///     <para>Plumbed through <c>BeginAssignment</c> -> <c>TryScanForWork</c> ->
        ///     the <c>work_order_scan</c> attributes, so the behavior executes THIS order instead
        ///     of re-deciding for itself.</para>
        /// </summary>
        public string TargetItemPrefab;

        /// <summary>
        ///     How stocked this order is, <c>have / Max</c> in [0,1]. Distinct from
        ///     <see cref="Priority" /> (the deficit): a learner can use the level and the gap
        ///     differently, e.g. treating "nearly empty" as urgent beyond what a linear deficit says.
        /// </summary>
        public float StockFraction;

        /// <summary>
        ///     How far below the player's MINIMUM this order sits, <c>(Min - have) / Max</c> clamped
        ///     to [0,1]. Zero once the floor is met. Min is the number the player actually asked to
        ///     always have on hand, so falling under it is a different kind of urgent from merely
        ///     being under Max — the closed-form deficit cannot express that distinction.
        /// </summary>
        public float MinShortfall;

        /// <summary>
        ///     <c>Time.time</c> when this order was last started, or 0 if never. Feeds a staleness
        ///     feature so a long-ignored order can gain ground — the anti-starvation signal, learned
        ///     rather than hard-coded.
        /// </summary>
        public float LastWorkedAt;
    }
}
