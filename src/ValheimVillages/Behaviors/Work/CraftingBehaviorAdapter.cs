using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Crafting;
using ValheimVillages.Behaviors.Farming;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Scheduling;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Behaviors.Work
{
    /// <summary>
    ///     IBehavior adapter wrapping the existing CraftingBehavior for worker NPCs.
    ///     Tag: "craft", Priority: 50.
    ///
    ///     <para>Also an <see cref="IDirectedBehavior" />, and the scheduler owns work-start
    ///     outright. A <see cref="TaskKind.CraftWork" /> assignment calls <c>TryScanForWork</c>
    ///     with the order the reranker chose, which commits the villager to that chest order
    ///     (or, for the farming floor row, to a farm task). The assignment stays active while
    ///     crafting/farming runs and releases when it finishes, so craft/farm is scheduled
    ///     alongside repair instead of bypassing the board via a self-scan.</para>
    /// </summary>
    [RegisterBehavior("craft")]
    public class CraftingBehaviorAdapter : IBehavior, IDirectedBehavior, IPathUnreachableHandler
    {
        private readonly VillagerAI m_ai;

        public CraftingBehaviorAdapter(VillagerAI ai)
        {
            m_ai = ai;
            Crafting = new CraftingBehavior(ai);
        }

        /// <summary>Direct access to the underlying CraftingBehavior for UI and state queries.</summary>
        public CraftingBehavior Crafting { get; }

        public string Tag => "craft";
        public int Priority => 50;

        // Work-start is routed through the scheduler (BeginAssignment), so this only keeps
        // control while an assigned craft/farm task is actually running. Deliberately
        // identical to AssignmentActive: the dispatcher holds a claim while that is true,
        // so if the two could disagree the villager would be "busy" to the dispatcher and
        // idle to the selector, and never get reassigned.
        public bool WantsControl(BehaviorContext ctx) => AssignmentActive;

        // --- IDirectedBehavior: scheduler-assigned execution ---

        public bool CanExecute(TaskKind kind) => kind == TaskKind.CraftWork;

        // Active while work is running OR while the async work_order_scan kicked off by
        // BeginAssignment is still in flight. Without the ScanPending term the dispatcher would
        // see AssignmentActive=false in the frames between enqueue and the scan callback and drop
        // the claim. ScanPending self-expires, so a dropped scan cannot pin this true.
        public bool AssignmentActive => (Crafting?.IsWorking ?? false) || (Crafting?.ScanPending ?? false);

        public bool BeginAssignment(CandidateTask task)
        {
            if (Crafting == null) return false;

            // Honour the IDirectedBehavior contract: "returns false if it can't start
            // (nothing actionable at the target)". Without this the adapter accepts every
            // offer, enqueues a scan, comes back empty, releases, and is re-offered the same
            // candidate next tick — an endless churn that keeps the villager pinned at
            // dispatch step 2 so it never reaches the routine tier to wander or relax.
            if (!Crafting.IsWorking && Crafting.NothingToDo) return false;
            // TryScanForWork enqueues an ASYNC work_order_scan and returns false at enqueue time —
            // the work only starts later in its callback. Accept the assignment if work is already
            // running OR a scan is now in flight; the dispatcher then holds the claim via
            // AssignmentActive until the scan resolves (starts work) or clears (nothing to do).
            // The old `return Crafting.IsWorking` could never be true here, so every craft
            // assignment was wrongly reserved (approach-failed) and churned SelectBest=null.
            // Forward the order the reranker actually chose. Null (the farming floor row) keeps
            // the old self-discovery path.
            Crafting.TryScanForWork(ignoreScanInterval: true, targetItemPrefab: task?.TargetItemPrefab);
            return Crafting.IsWorking || Crafting.ScanPending;
        }

        public void Update(float dt)
        {
            Crafting?.UpdateWorkAI(dt);
        }

        public void OnArrival(float dt)
        {
            Crafting?.HandleWorkArrival(dt);
        }

        public void OnPathUnreachable(Vector3 target)
        {
            Plugin.Log?.LogWarning(
                $"[Work:{m_ai?.NpcName ?? "?"}] Path unreachable to {target}; abandoning work.");
            Crafting?.AbandonWorkPublic("path unreachable after recovery attempts");
        }

        public string GetStatusText()
        {
            if (Crafting == null) return "";
            if (Crafting.FarmingBehavior?.IsWorking == true)
                return $"Farming: {Crafting.FarmingBehavior.SubState}";
            if (m_ai.CurrentState == BehaviorState.Working)
                return $"Working: {Crafting.SubState}";
            return "Idle";
        }

        /// <summary>Inject a farming sub-behavior into the crafting behavior.</summary>
        public void SetFarmingBehavior(FarmingBehavior farming)
        {
            Crafting?.SetFarmingBehavior(farming);
        }

        public void Save(ZDO zdo)
        {
        }

        public void Load(ZDO zdo)
        {
        }
    }
}