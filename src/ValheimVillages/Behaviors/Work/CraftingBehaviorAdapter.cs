using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Crafting;
using ValheimVillages.Behaviors.Farming;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Behaviors.Work
{
    /// <summary>
    ///     IBehavior adapter wrapping the existing CraftingBehavior for worker NPCs.
    ///     Tag: "craft", Priority: 50.
    /// </summary>
    [RegisterBehavior("craft")]
    public class CraftingBehaviorAdapter : IBehavior, IWorkScanBehavior
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
            return m_ai.CurrentState == BehaviorState.Working;
        }

        public void Update(float dt)
        {
            Crafting?.UpdateWorkAI(dt);
        }

        public void OnArrival(float dt)
        {
            Crafting?.HandleWorkArrival(dt);
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