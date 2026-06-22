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
    ///     <para>Also an <see cref="IDirectedBehavior" />: in PrimaryMode the scheduler
    ///     owns work-start. A <see cref="TaskKind.CraftWork" /> assignment calls
    ///     <c>TryScanForWork</c> (the same detect-and-commit the legacy idle-scan used),
    ///     which finds the villager's next chest order OR farm task. The assignment stays
    ///     active while crafting/farming runs and releases when it finishes — so the
    ///     reranker schedules craft/farm alongside repair instead of it bypassing the
    ///     board via a self-scan.</para>
    /// </summary>
    [RegisterBehavior("craft")]
    public class CraftingBehaviorAdapter : IBehavior, IDirectedBehavior, IWorkScanBehavior, IPathUnreachableHandler
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

        public bool WantsControl(BehaviorContext ctx)
        {
            // PrimaryMode routes work-start through the scheduler (BeginAssignment), so
            // only keep control while an assigned craft/farm task is actually running.
            // IsWorking covers both crafting sub-states and the injected farming behavior.
            if (SchedulerSettings.PrimaryMode)
                return (Crafting?.IsWorking ?? false) || (Crafting?.ScanPending ?? false);
            return m_ai.CurrentState == BehaviorState.Working;
        }

        // --- IDirectedBehavior: scheduler-assigned execution (PrimaryMode) ---

        public bool CanExecute(TaskKind kind) => kind == TaskKind.CraftWork;

        // Active while work is running OR while the async work_order_scan kicked off by
        // BeginAssignment is still in flight. Without the ScanPending term the dispatcher would
        // see AssignmentActive=false in the frames between enqueue and the scan callback and drop
        // the claim. ScanPending self-expires, so a dropped scan cannot pin this true.
        public bool AssignmentActive => (Crafting?.IsWorking ?? false) || (Crafting?.ScanPending ?? false);

        public bool BeginAssignment(CandidateTask task)
        {
            if (Crafting == null) return false;
            // TryScanForWork enqueues an ASYNC work_order_scan and returns false at enqueue time —
            // the work only starts later in its callback. Accept the assignment if work is already
            // running OR a scan is now in flight; the dispatcher then holds the claim via
            // AssignmentActive until the scan resolves (starts work) or clears (nothing to do).
            // The old `return Crafting.IsWorking` could never be true here, so every craft
            // assignment was wrongly reserved (approach-failed) and churned SelectBest=null.
            Crafting.TryScanForWork(ignoreScanInterval: true);
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

        /// <summary>Try to find a work order and begin working. Delegates to CraftingBehavior.</summary>
        public bool TryScanForWork(bool ignoreScanInterval = false)
        {
            return Crafting?.TryScanForWork(ignoreScanInterval) ?? false;
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