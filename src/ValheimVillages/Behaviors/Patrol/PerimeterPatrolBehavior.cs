using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// IBehavior wrapper around PatrolStateMachine for the behavior composition system.
    /// Tag: "patrol", Priority: 30. Any villager with behavior:patrol gets this behavior.
    /// </summary>
    [RegisterBehavior("patrol")]
    public class PerimeterPatrolBehavior : IBehavior
    {
        private readonly VillagerAI m_ai;
        private readonly PatrolStateMachine m_patrol;

        public string Tag => "patrol";
        public int Priority => 30;

        #region Pass-through patrol state

        public bool IsDiscoveryComplete => m_patrol?.IsDiscoveryComplete ?? false;
        public bool IsAlarmed => m_patrol?.IsAlarmed ?? false;
        public Vector3? BreachPosition => m_patrol?.BreachPosition;
        public Vector3 BedPosition => m_patrol?.BedPosition ?? Vector3.zero;
        public int WaypointCount => m_patrol?.WaypointCount ?? 0;
        public int ActiveWaypointCount => m_patrol?.ActiveWaypointCount ?? 0;
        public IReadOnlyList<VillagerWaypoint> PatrolWaypoints => m_patrol?.PatrolWaypoints;
        public bool IsHnaRoute => m_patrol?.IsHnaRoute ?? false;

        #endregion

        public PerimeterPatrolBehavior(VillagerAI ai)
        {
            m_ai = ai;
            m_patrol = new PatrolStateMachine(ai.Villager);
        }

        public bool WantsControl(BehaviorContext ctx)
        {
            if (m_patrol == null) return false;
            return !m_patrol.IsAlarmed;
        }

        public void Update(float dt)
        {
            m_patrol?.UpdatePatrolAI(dt);
        }

        public void OnArrival()
        {
            m_patrol?.HandleArrival();
        }

        public void Save(ZDO zdo)
        {
            if (m_patrol != null && zdo != null)
                PatrolPersistence.Save(m_patrol, zdo);
        }

        public void Load(ZDO zdo)
        {
            if (m_patrol != null && zdo != null)
                PatrolPersistence.Load(m_patrol, zdo);
        }

        public void WalkToBreach() => m_patrol?.WalkToBreach();
        public void ClearBreach() => m_patrol?.ClearBreach();
        public void ResetDiscovery() => m_patrol?.ResetDiscovery();

        public string GetStatusText()
        {
            if (m_patrol == null) return "";
            var state = m_ai.CurrentState;
            if (state == BehaviorState.Patrolling)
                return $"Patrolling ({m_patrol.ActiveWaypointCount} waypoints)";
            if (state == BehaviorState.Scouting)
                return "Scouting perimeter...";
            if (state == BehaviorState.CircuitTracing)
                return "Tracing patrol route...";
            return m_patrol.IsDiscoveryComplete ? "On patrol" : "Mapping village";
        }
    }
}
