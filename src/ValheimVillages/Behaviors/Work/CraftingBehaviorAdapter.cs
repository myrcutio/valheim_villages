using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs.AI;
using ValheimVillages.NPCs.AI.Work;
using ValheimVillages.NPCs.AI.Work.Farming;

namespace ValheimVillages.Behaviors.Work
{
    /// <summary>
    /// IBehavior adapter wrapping the existing CraftingBehavior for worker NPCs.
    /// Tag: "craft", Priority: 50.
    /// </summary>
    [RegisterBehavior("craft")]
    public class CraftingBehaviorAdapter : IBehavior
    {
        private readonly VillagerAI m_ai;
        private CraftingBehavior m_crafting;

        public string Tag => "craft";
        public int Priority => 50;

        /// <summary>Direct access to the underlying CraftingBehavior for UI and state queries.</summary>
        public CraftingBehavior Crafting => m_crafting;

        public CraftingBehaviorAdapter(VillagerAI ai)
        {
            m_ai = ai;
            m_crafting = new CraftingBehavior(ai);
        }

        /// <summary>Inject a farming sub-behavior into the crafting behavior.</summary>
        public void SetFarmingBehavior(FarmingBehavior farming)
        {
            m_crafting?.SetFarmingBehavior(farming);
        }

        public bool WantsControl(BehaviorContext ctx)
        {
            return m_ai.CurrentState == BehaviorState.Working;
        }

        public void Update(float dt)
        {
            m_crafting?.UpdateWorkAI(dt);
        }

        public void OnArrival()
        {
            m_crafting?.HandleWorkArrival();
        }

        public void Save(ZDO zdo) { }
        public void Load(ZDO zdo) { }

        public string GetStatusText()
        {
            if (m_crafting == null) return "";
            if (m_crafting.FarmingBehavior?.IsWorking == true)
                return $"Farming: {m_crafting.FarmingBehavior.SubState}";
            if (m_ai.CurrentState == BehaviorState.Working)
                return $"Working: {m_crafting.SubState}";
            return "Idle";
        }
    }
}
