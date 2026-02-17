using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs.AI;

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
        public bool IsActive => m_patrolBehavior?.Guard?.IsAlarmed == true;

        /// <summary>The breach position, if any.</summary>
        public UnityEngine.Vector3? BreachPosition => m_patrolBehavior?.Guard?.BreachPosition;

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
            // Alarm state is managed by the GuardBehavior internally.
            // When alarmed, the guard stops patrolling (handled by WantsControl priorities).
            // The UI shows the alarm and provides "Show me" / "Dismiss" actions.
        }

        public void OnArrival()
        {
            // Walking to breach complete
        }

        public void Save(ZDO zdo)
        {
            // Breach state is persisted by the patrol behavior's GuardPersistence
        }

        public void Load(ZDO zdo)
        {
            // Breach state is loaded by the patrol behavior's GuardPersistence
        }

        /// <summary>Send the guard to the breach location.</summary>
        public void WalkToBreach()
        {
            m_patrolBehavior?.Guard?.WalkToBreach();
        }

        /// <summary>Clear breach alarm and resume patrol.</summary>
        public void ClearBreach()
        {
            m_patrolBehavior?.Guard?.ClearBreach();
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
