using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Behaviors.Alarm
{
    /// <summary>
    /// IBehavior for breach alarm handling. Reads breach state from PerimeterPatrolBehavior
    /// and takes control at priority 100 when a breach is detected.
    /// Tag: "alarm", Priority: 100.
    /// </summary>
    [RegisterBehavior("alarm")]
    public class BreachAlarmBehavior : IBehavior
    {
        private readonly VillagerAI m_ai;
        private PerimeterPatrolBehavior m_patrolBehavior;

        public string Tag => "alarm";
        public int Priority => 100;

        /// <summary>True when a breach has been detected and not yet cleared.</summary>
        public bool IsActive => m_patrolBehavior?.IsAlarmed == true;

        /// <summary>The breach position, if any.</summary>
        public UnityEngine.Vector3? BreachPosition => m_patrolBehavior?.BreachPosition;

        public BreachAlarmBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        /// <summary>
        /// Connect this alarm behavior to its patrol behavior sibling.
        /// Called by VillagerAI after all behaviors are created.
        /// </summary>
        public void SetPatrolBehavior(PerimeterPatrolBehavior patrol)
        {
            m_patrolBehavior = patrol;
        }

        public bool WantsControl(BehaviorContext ctx)
        {
            return IsActive;
        }

        public void Update(float dt)
        {
            // Alarm state is managed by the PatrolStateMachine internally.
            // When alarmed, the patroller stops patrolling (handled by WantsControl priorities).
            // The UI shows the alarm and provides "Show me" / "Dismiss" actions.
        }

        public void OnArrival()
        {
        }

        public void Save(ZDO zdo)
        {
            // Breach state is persisted by PatrolPersistence
        }

        public void Load(ZDO zdo)
        {
            // Breach state is loaded by PatrolPersistence
        }

        /// <summary>Send the patroller to the breach location.</summary>
        public void WalkToBreach()
        {
            m_patrolBehavior?.WalkToBreach();
        }

        /// <summary>Clear breach alarm and resume patrol.</summary>
        public void ClearBreach()
        {
            m_patrolBehavior?.ClearBreach();
        }

        public string GetStatusText()
        {
            if (!IsActive) return "";
            var pos = BreachPosition;
            if (pos.HasValue)
                return $"BREACH at ({pos.Value.x:F0}, {pos.Value.z:F0})!";
            return "Wall breach detected!";
        }
    }
}
