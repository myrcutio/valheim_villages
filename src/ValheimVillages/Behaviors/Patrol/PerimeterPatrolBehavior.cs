using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// IBehavior wrapper around GuardBehavior for the patrol behavior composition system.
    /// Tag: "patrol", Priority: 30. Wraps the existing GuardBehavior which handles
    /// patrol discovery, waypoints, circuit tracing, and stall recovery.
    /// </summary>
    [RegisterBehavior("patrol")]
    public class PerimeterPatrolBehavior : IBehavior
    {
        private readonly VillagerAI m_ai;
        private GuardBehavior m_guard;

        public string Tag => "patrol";
        public int Priority => 30;

        /// <summary>Direct access to the underlying GuardBehavior for UI and persistence.</summary>
        public GuardBehavior Guard => m_guard;

        public PerimeterPatrolBehavior(VillagerAI ai)
        {
            m_ai = ai;
            m_guard = new GuardBehavior(ai);
        }

        public bool WantsControl(BehaviorContext ctx)
        {
            if (m_guard == null) return false;
            // Guard patrol wants control when not alarmed (alarm behavior has higher priority)
            return !m_guard.IsAlarmed;
        }

        public void Update(float dt)
        {
            m_guard?.UpdateGuardAI(dt);
        }

        public void OnArrival()
        {
            m_guard?.HandleArrival();
        }

        public void Save(ZDO zdo)
        {
            if (m_guard != null && zdo != null)
                GuardPersistence.Save(m_guard, zdo);
        }

        public void Load(ZDO zdo)
        {
            if (m_guard != null && zdo != null)
                GuardPersistence.Load(m_guard, zdo);
        }

        public string GetStatusText()
        {
            if (m_guard == null) return "";
            var state = m_ai.CurrentState;
            if (state == BehaviorState.Patrolling)
                return $"Patrolling ({m_guard.ActiveWaypointCount} waypoints)";
            if (state == BehaviorState.Scouting)
                return "Scouting perimeter...";
            if (state == BehaviorState.CircuitTracing)
                return "Tracing patrol route...";
            return m_guard.IsDiscoveryComplete ? "On patrol" : "Mapping village";
        }
    }
}
