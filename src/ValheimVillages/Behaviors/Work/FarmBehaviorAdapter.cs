using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Farming;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Behaviors.Work
{
    /// <summary>
    ///     IBehavior adapter for farming. This adapter creates a FarmingBehavior and injects
    ///     it into the sibling CraftingBehaviorAdapter. The farm behavior itself does not
    ///     independently take control — farming is a sub-state of crafting.
    ///     Tag: "farming", Priority: 50.
    /// </summary>
    [RegisterBehavior("farming")]
    public class FarmBehaviorAdapter : IBehavior
    {
        private readonly VillagerAI m_ai;

        public FarmBehaviorAdapter(VillagerAI ai)
        {
            m_ai = ai;
            Farming = new FarmingBehavior(ai);
        }

        /// <summary>Direct access to the underlying FarmingBehavior.</summary>
        public FarmingBehavior Farming { get; }

        public string Tag => "farming";
        public int Priority => 50;

        public bool WantsControl(BehaviorContext ctx)
        {
            return false;
            // Farming is a sub-state of crafting
        }

        public void Update(float dt)
        {
        }

        public void OnArrival(float dt)
        {
        }

        public string GetStatusText()
        {
            if (Farming?.IsWorking == true)
                return $"Farming: {Farming.SubState}";
            return "";
        }

        /// <summary>
        ///     Connect this farm behavior to its crafting behavior sibling.
        ///     Called by VillagerAI after all behaviors are created.
        /// </summary>
        public void LinkToCraftingAdapter(CraftingBehaviorAdapter crafting)
        {
            crafting?.SetFarmingBehavior(Farming);
        }

        public void Save(ZDO zdo)
        {
        }

        public void Load(ZDO zdo)
        {
        }
    }
}