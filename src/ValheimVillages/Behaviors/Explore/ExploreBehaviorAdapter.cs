using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.Behaviors.Explore
{
    /// <summary>
    /// IBehavior adapter wrapping the existing VillagerExploration for exploration NPCs.
    /// Tag: "explore", Priority: 20.
    /// Exploration is currently handled by VillagerBehaviorLogic; this adapter marks
    /// the NPC as exploration-capable for tag-based discovery.
    /// </summary>
    [RegisterBehavior("explore")]
    public class ExploreBehaviorAdapter : IBehavior
    {
        private readonly VillagerAI m_ai;

        public string Tag => "explore";
        public int Priority => 20;

        public ExploreBehaviorAdapter(VillagerAI ai)
        {
            m_ai = ai;
        }

        public bool WantsControl(BehaviorContext ctx)
        {
            return m_ai.CurrentState == BehaviorState.Exploring;
        }

        public void Update(float dt)
        {
            VillagerBehaviorLogic.UpdateBehavior(m_ai);
        }

        public void OnArrival()
        {
            VillagerBehaviorLogic.HandleArrival(m_ai);
        }

        public void Save(ZDO zdo) { }
        public void Load(ZDO zdo) { }

        public string GetStatusText()
        {
            if (m_ai.CurrentState == BehaviorState.Exploring)
                return "Exploring...";
            return "";
        }
    }
}
