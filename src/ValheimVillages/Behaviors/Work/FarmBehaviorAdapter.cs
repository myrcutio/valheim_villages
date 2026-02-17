using ValheimVillages.NPCs.AI;
using ValheimVillages.NPCs.AI.Work.Farming;

namespace ValheimVillages.Behaviors.Work
{
    /// <summary>
    /// IBehavior adapter for farming. This adapter creates a FarmingBehavior and injects
    /// it into the sibling CraftingBehaviorAdapter. The farm behavior itself does not
    /// independently take control — farming is a sub-state of crafting.
    /// Tag: "farm", Priority: 50.
    /// </summary>
    public class FarmBehaviorAdapter : IBehavior
    {
        private readonly VillagerAI m_ai;
        private FarmingBehavior m_farming;

        public string Tag => "farm";
        public int Priority => 50;

        /// <summary>Direct access to the underlying FarmingBehavior.</summary>
        public FarmingBehavior Farming => m_farming;

        public FarmBehaviorAdapter(VillagerAI ai)
        {
            m_ai = ai;
            m_farming = new FarmingBehavior(ai);
        }

        /// <summary>
        /// Connect this farm behavior to its crafting behavior sibling.
        /// Called by VillagerAI after all behaviors are created.
        /// </summary>
        public void LinkToCraftingAdapter(CraftingBehaviorAdapter crafting)
        {
            crafting?.SetFarmingBehavior(m_farming);
        }

        public bool WantsControl(BehaviorContext ctx) => false; // Farming is a sub-state of crafting
        public void Update(float dt) { }
        public void OnArrival() { }
        public void Save(ZDO zdo) { }
        public void Load(ZDO zdo) { }

        public string GetStatusText()
        {
            if (m_farming?.IsWorking == true)
                return $"Farming: {m_farming.SubState}";
            return "";
        }
    }
}
