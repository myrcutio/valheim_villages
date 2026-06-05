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
    ///     IBehavior wrapper around PatrolStateMachine for the behavior composition system.
    ///     Tag: "patrol", Priority: 30. Any villager with behavior:patrol gets this behavior.
    /// </summary>
    [RegisterBehavior("patrol")]
    public class PerimeterPatrolBehavior : IBehavior
    {
        private readonly VillagerAI m_ai;
        private readonly PatrolStateMachine m_patrol;

        public PerimeterPatrolBehavior(VillagerAI ai)
        {
            m_ai = ai;
            m_patrol = new PatrolStateMachine(ai);
        }

        public string Tag => "patrol";
        public int Priority => 30;

        public bool WantsControl(BehaviorContext ctx)
        {
            return m_patrol != null;
        }

        public void Update(float dt)
        {
            m_patrol?.UpdatePatrolAI(dt);
        }

        public void OnArrival(float dt)
        {
            m_patrol?.HandleArrival(dt);
        }

        public string GetStatusText()
        {
            if (m_patrol == null) return "";
            var state = m_ai.CurrentState;
            // Keep this a short label (it's the list-row title). The full reason +
            // a map pin to the unreachable waypoint are surfaced as a "blocked"
            // activity-log entry, which renders in the detail panel.
            if (state == BehaviorState.NeedsHelp)
                return $"Needs help: waypoint {m_patrol.HelpWaypointIndex}";

            if (state == BehaviorState.Patrolling)
                return $"Patrolling ({m_patrol.ActiveWaypointCount} waypoints)";
            return m_patrol.IsDiscoveryComplete ? "On patrol" : "Mapping village";
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

        public void ResetDiscovery()
        {
            m_patrol?.ResetDiscovery();
        }

        #region Pass-through patrol state

        public bool IsDiscoveryComplete => m_patrol?.IsDiscoveryComplete ?? false;
        public Vector3 BedPosition => m_patrol?.BedPosition ?? Vector3.zero;

        /// <summary>Index of the waypoint the patrol parked at in NeedsHelp, or -1.</summary>
        public int HelpWaypointIndex => m_patrol?.HelpWaypointIndex ?? -1;

        /// <summary>World position of the unreachable waypoint when in NeedsHelp.</summary>
        public Vector3 HelpPosition => m_patrol?.HelpPosition ?? Vector3.zero;
        public int WaypointCount => m_patrol?.WaypointCount ?? 0;
        public int ActiveWaypointCount => m_patrol?.ActiveWaypointCount ?? 0;
        public IReadOnlyList<VillagerWaypoint> PatrolWaypoints => m_patrol?.PatrolWaypoints;
        public bool IsHnaRoute => m_patrol?.IsHnaRoute ?? false;

        #endregion
    }
}