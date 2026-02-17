using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// A single waypoint in a movement test route.
    /// </summary>
    public class MovementTestWaypoint
    {
        public Vector3 Position { get; }
        public string Label { get; }

        public MovementTestWaypoint(Vector3 position, string label)
        {
            Position = position;
            Label = label;
        }
    }

    /// <summary>
    /// Manages a multi-waypoint movement test for a villager.
    /// Builds a route from known locations, walks through them sequentially,
    /// and reports progress/results to the player.
    /// </summary>
    public class VillagerMovementTest
    {
        private readonly VillagerAI m_ai;
        private Queue<MovementTestWaypoint> m_waypoints;
        private MovementTestWaypoint m_currentWaypoint;
        private float m_testStartTime;
        private float m_waypointStartTime;
        private int m_waypointsCompleted;
        private int m_waypointsTotal;
        private bool m_finished;

        private const float WaypointTimeout = 30f;

        public VillagerMovementTest(VillagerAI ai)
        {
            m_ai = ai;
        }

        public bool IsActive => !m_finished;
        public int WaypointsCompleted => m_waypointsCompleted;
        public int WaypointsTotal => m_waypointsTotal;
        public string CurrentLabel => m_currentWaypoint?.Label ?? "";

        /// <summary>
        /// Build the test route and start the first waypoint.
        /// Returns false if no waypoints are available.
        /// </summary>
        public bool Start()
        {
            var route = BuildTestRoute();
            if (route.Count == 0)
            {
                Plugin.Log?.LogWarning($"[TEST:{m_ai.NpcName}] No waypoints available for test");
                return false;
            }

            m_waypoints = new Queue<MovementTestWaypoint>(route);
            m_waypointsTotal = route.Count;
            m_waypointsCompleted = 0;
            m_finished = false;
            m_testStartTime = Time.time;

            Plugin.Log?.LogInfo($"[TEST:{m_ai.NpcName}] Starting movement test with {m_waypointsTotal} waypoints");
            AdvanceToNextWaypoint();
            return true;
        }

        /// <summary>
        /// Cancel the running test.
        /// </summary>
        public void Cancel()
        {
            m_finished = true;
            m_waypoints = null;
            m_currentWaypoint = null;
            m_ai.Instance.StopMoving();
            m_ai.SetState(BehaviorState.Idle);
            Plugin.Log?.LogInfo($"[TEST:{m_ai.NpcName}] Movement test cancelled");
        }

        /// <summary>
        /// Called every AI tick to check for timeouts.
        /// </summary>
        public void Update()
        {
            if (m_finished) return;

            // If no current waypoint, advance to next
            if (m_currentWaypoint == null)
            {
                AdvanceToNextWaypoint();
                return;
            }

            // Check for waypoint timeout
            if (Time.time - m_waypointStartTime > WaypointTimeout)
            {
                Plugin.Log?.LogWarning(
                    $"[TEST:{m_ai.NpcName}] Waypoint '{m_currentWaypoint.Label}' timed out after {WaypointTimeout}s");
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                    $"TIMEOUT: Could not reach {m_currentWaypoint.Label}");
                m_waypointsCompleted++;
                m_currentWaypoint = null; // Will advance on next tick
            }
        }

        /// <summary>
        /// Called by VillagerAI when it arrives at its current target.
        /// </summary>
        public void OnWaypointArrived()
        {
            if (m_finished || m_currentWaypoint == null) return;

            float elapsed = Time.time - m_waypointStartTime;
            m_waypointsCompleted++;

            string msg = $"Arrived at {m_currentWaypoint.Label} ({elapsed:F1}s)";
            Plugin.Log?.LogInfo($"[TEST:{m_ai.NpcName}] {msg}");
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, msg);

            // Clear current waypoint -- next Update() tick will advance
            m_currentWaypoint = null;
        }

        private List<MovementTestWaypoint> BuildTestRoute()
        {
            var route = new List<MovementTestWaypoint>();
            var startPos = m_ai.Position;
            var locations = m_ai.Memory.KnownLocations;

            // Always start with bed (home base)
            var bed = locations.FirstOrDefault(l => l.Type == LocationType.Bed);
            if (bed != null)
                route.Add(new MovementTestWaypoint(bed.Position.ToVector3(), "Bed"));

            // Add fire if known
            var fire = locations.FirstOrDefault(l => l.Type == LocationType.Fire);
            if (fire != null)
                route.Add(new MovementTestWaypoint(fire.Position.ToVector3(), "Fire"));

            // Add chair if known
            var chair = locations.FirstOrDefault(l => l.Type == LocationType.Chair);
            if (chair != null)
                route.Add(new MovementTestWaypoint(chair.Position.ToVector3(), "Chair"));

            // Return to start position
            route.Add(new MovementTestWaypoint(startPos, "Origin"));

            return route;
        }

        private void AdvanceToNextWaypoint()
        {
            if (m_waypoints == null || m_waypoints.Count == 0)
            {
                Finish();
                return;
            }

            m_currentWaypoint = m_waypoints.Dequeue();
            m_waypointStartTime = Time.time;
            m_ai.SetState(BehaviorState.Traveling, new VillagerWaypoint(m_currentWaypoint.Position, PathingStrategyRegistry.DefaultId));

            float dist = Vector3.Distance(m_ai.Position, m_currentWaypoint.Position);
            string msg = $"[{m_waypointsCompleted + 1}/{m_waypointsTotal}] Going to {m_currentWaypoint.Label} ({dist:F0}m)";
            Plugin.Log?.LogInfo($"[TEST:{m_ai.NpcName}] {msg}");
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, msg);
        }

        private void Finish()
        {
            float totalTime = Time.time - m_testStartTime;
            m_finished = true;
            m_waypoints = null;
            m_currentWaypoint = null;

            string result = $"Movement test complete: {m_waypointsCompleted}/{m_waypointsTotal} waypoints in {totalTime:F1}s";
            Plugin.Log?.LogInfo($"[TEST:{m_ai.NpcName}] {result}");
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, result);

            m_ai.SetState(BehaviorState.Idle);
        }
    }
}
