using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Patrol behavior state machine.
    /// Uses HNA boundary detection to derive patrol waypoints, then cycles through them.
    /// </summary>
    public class PatrolStateMachine
    {
        private readonly Villager.Villager m_villager;
        private readonly VillagerAI m_ai;
        private List<VillagerWaypoint> m_patrolWaypoints;
        private int m_currentWaypointIndex;
        private bool m_discoveryComplete;
        private bool m_isHnaRoute;
        private bool m_hnaPartitionRequested;

        public PatrolStateMachine(ValheimVillages.Villager.Villager villager)
        {
            m_villager = villager;
            m_ai = villager.villagerAI != null ? villager.villagerAI : villager.GetComponent<VillagerAI>();
        }

        public bool IsDiscoveryComplete => m_discoveryComplete;
        /// <summary>Bed position for map rendering and UI.</summary>
        public Vector3 BedPosition => m_ai?.Memory?.BedPosition ?? Vector3.zero;
        public int WaypointCount => m_patrolWaypoints?.Count ?? 0;
        public int ActiveWaypointCount => m_patrolWaypoints?.Count(w => w.Active) ?? 0;
        public IReadOnlyList<VillagerWaypoint> PatrolWaypoints => m_patrolWaypoints;
        public bool IsHnaRoute => m_isHnaRoute;

        /// <summary>Returns the persistent state for saving to ZDO.</summary>
        public (List<VillagerWaypoint> waypoints, int wpIndex, bool complete, bool isHna) GetPersistentState()
            => (m_patrolWaypoints, m_currentWaypointIndex, m_discoveryComplete, m_isHnaRoute);

        /// <summary>
        /// Reset patrol route. Clears waypoints and re-derives them from the existing
        /// HNA graph (if available). Does NOT clear the HNA graph itself — it's expensive
        /// to rebuild and the boundary mapper can re-derive waypoints instantly.
        /// </summary>
        public void ResetDiscovery()
        {
            Plugin.Log?.LogWarning($"[Patrol:{m_ai.NpcName}] Discovery reset requested (debug)");
            m_patrolWaypoints?.Clear();
            m_currentWaypointIndex = 0;
            m_discoveryComplete = false;
            m_isHnaRoute = false;
            m_hnaPartitionRequested = false;
            VillageAreaManager.UnregisterArea(m_ai.UniqueId);
            SaveState();
            m_ai.SetState(BehaviorState.Idle);
            StartDiscovery();
        }

        /// <summary>Restores state from ZDO data. Re-registers village area.</summary>
        public void RestoreState(List<VillagerWaypoint> waypoints, int wpIndex, bool isHna = false)
        {
            m_patrolWaypoints = waypoints;
            m_currentWaypointIndex = wpIndex % (waypoints?.Count ?? 1);
            m_discoveryComplete = true;
            m_isHnaRoute = isHna;
            RegisterVillageArea();
            Plugin.Log?.LogInfo(
                $"[Patrol:{m_ai.NpcName}] Restored {m_patrolWaypoints.Count} waypoints" +
                $" (hna={isHna}), index={m_currentWaypointIndex}");
        }

        /// <summary>Main patrol AI update, called each behavior tick.</summary>
        public void UpdatePatrolAI(float dt)
        {
            if (m_ai.IsInBackoff) return;

            var state = m_ai.CurrentState;
            var context = VillagerBehaviorLogic.GetCurrentContext(m_ai);

            switch (state)
            {
                case BehaviorState.Idle:
                    if (!m_discoveryComplete) StartDiscovery();
                    else AdvanceToNextWaypoint();
                    break;
                case BehaviorState.Patrolling:
                    break;
            }
        }

        /// <summary>Called when the patroller arrives at its target.</summary>
        public void HandleArrival(float dt)
        {
            switch (m_ai.CurrentState)
            {
                case BehaviorState.Patrolling:
                    AdvanceToNextWaypoint();
                    break;
                default:
                    m_ai.SetState(BehaviorState.Idle);
                    break;
            }
        }


        private string GetVillageKey()
            => RegionGraph.VillageKey(m_ai.Memory.BedPosition);

        private void StartDiscovery()
        {
            string villageKey = GetVillageKey();
            var graph = RegionGraph.Get(villageKey);

            if (graph == null || !graph.IsAvailable)
            {
                var zdo = m_ai?.NView?.GetZDO();
                if (zdo != null && PatrolPersistence.TryRestoreHnaGraph(zdo, villageKey))
                {
                    graph = RegionGraph.Get(villageKey);
                    Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Restored HNA graph from ZDO (key={villageKey})");
                }
            }

            var hnaWaypoints = BoundaryMapper.ComputeBoundaryWaypoints(m_ai.Memory.BedPosition);
            if (hnaWaypoints.Count >= 3)
            {
                    m_patrolWaypoints = hnaWaypoints
                    .Select(p => new VillagerWaypoint(p, VillagerWaypoint.DefaultStrategyId))
                    .ToList();
                m_isHnaRoute = true;
                m_hnaPartitionRequested = false;
                CompleteHnaDiscovery();
                return;
            }

            // HNA graph unavailable: enqueue the partition task and wait
            if (!m_hnaPartitionRequested)
            {
                m_hnaPartitionRequested = true;
                var bedPos = m_ai.Memory.BedPosition;
                Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] HNA graph unavailable, requesting hna_partition build");
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "hna_partition",
                    SourceId = $"patrol:{m_ai.UniqueId}",
                    Priority = TaskPriority.Medium,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>
                    {
                        { "anchor_x", bedPos.x.ToString("F2", CultureInfo.InvariantCulture) },
                        { "anchor_z", bedPos.z.ToString("F2", CultureInfo.InvariantCulture) }
                    }
                });
                return;
            }
        }

        private void CompleteHnaDiscovery()
        {
            m_currentWaypointIndex = 0;
            m_discoveryComplete = true;

            RegisterVillageArea();
            SaveState();

            var zdo = m_ai?.NView?.GetZDO();
            if (zdo != null)
                PatrolPersistence.SaveHnaGraph(zdo, GetVillageKey());

            var first = m_patrolWaypoints[0].Position;
            var last = m_patrolWaypoints[m_patrolWaypoints.Count - 1].Position;
            var playerPos = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;

            Plugin.Log?.LogInfo(
                $"[Patrol:{m_ai.NpcName}] HNA boundary discovery complete, {m_patrolWaypoints.Count} waypoints. " +
                $"First=({first.x:F1},{first.y:F1},{first.z:F1}) " +
                $"Last=({last.x:F1},{last.y:F1},{last.z:F1}) " +
                $"Player=({playerPos.x:F1},{playerPos.y:F1},{playerPos.z:F1})");

            AdvanceToNextWaypoint();
        }

        private void RegisterVillageArea()
        {
            if (m_patrolWaypoints == null || ActiveWaypointCount == 0) return;
            var positions = m_patrolWaypoints.Where(w => w.Active).Select(w => w.Position).ToList();
            var area = new VillageArea(m_ai.UniqueId, m_ai.Memory.BedPosition, positions);
            VillageAreaManager.RegisterArea(area);
        }

        private void SaveState()
        {
            var zdo = m_ai?.NView?.GetZDO();
            if (zdo != null) PatrolPersistence.Save(this, zdo);
        }

        private void AdvanceToNextWaypoint()
        {
            if (m_patrolWaypoints == null || ActiveWaypointCount == 0)
            {
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            int total = m_patrolWaypoints.Count;

            // Advance to the next active waypoint — this is where the circuit starts
            int firstIdx = m_currentWaypointIndex;
            for (int i = 0; i < total; i++)
            {
                firstIdx = (firstIdx + 1) % total;
                if (m_patrolWaypoints[firstIdx].Active)
                    break;
            }

            // Collect all active waypoints in circuit order starting from firstIdx
            var circuitWps = new List<(int index, VillagerWaypoint wp)>();
            for (int i = 0; i < total; i++)
            {
                int idx = (firstIdx + i) % total;
                if (m_patrolWaypoints[idx].Active)
                    circuitWps.Add((idx, m_patrolWaypoints[idx]));
            }

            if (circuitWps.Count == 0)
            {
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            var fullPath = BuildCircuitPath(m_ai.Position, circuitWps);
            if (fullPath != null && fullPath.Count > 0)
            {
                var lastEntry = circuitWps[circuitWps.Count - 1];
                m_currentWaypointIndex = lastEntry.index;
                m_ai.SetPatrolCircuit(lastEntry.wp, fullPath);
                Plugin.Log?.LogInfo(
                    $"[Patrol:{m_ai.NpcName}] Circuit built: {circuitWps.Count} waypoints, " +
                    $"{fullPath.Count} path nodes, final=W{lastEntry.index}");
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[Patrol:{m_ai.NpcName}] Circuit build failed for {circuitWps.Count} waypoints. Going idle.");
                m_ai.SetState(BehaviorState.Idle);
            }
        }

        /// <summary>
        /// Build a concatenated path through all circuit waypoints using Valheim's Pathfinding.
        /// Returns null if any segment fails to path.
        /// </summary>
        private List<Vector3> BuildCircuitPath(
            Vector3 startPos, List<(int index, VillagerWaypoint wp)> circuitWps)
        {
            var pf = Pathfinding.instance;
            if (pf == null) return null;

            var agentType = m_ai.Instance.m_pathAgentType;
            var fullPath = new List<Vector3>();
            var segment = new List<Vector3>();
            Vector3 from = startPos;

            for (int i = 0; i < circuitWps.Count; i++)
            {
                segment.Clear();
                Vector3 to = circuitWps[i].wp.Position;

                if (!pf.GetPath(from, to, segment, agentType, false, true, false))
                {
                    Plugin.Log?.LogInfo(
                        $"[Patrol:{m_ai.NpcName}] Circuit segment {i}/{circuitWps.Count} FAILED: " +
                        $"from ({from.x:F1},{from.y:F1},{from.z:F1}) -> " +
                        $"to ({to.x:F1},{to.y:F1},{to.z:F1})");
                    return null;
                }
                if (segment.Count == 0)
                    return null;

                // Flag path points with anomalous Y
                foreach (var pt in segment)
                {
                    if (pt.y < 1f || pt.y > 500f)
                    {
                        Plugin.Log?.LogWarning(
                            $"[Patrol:{m_ai.NpcName}] Anomalous path point Y={pt.y:F1} " +
                            $"at ({pt.x:F1},{pt.y:F1},{pt.z:F1}) in segment {i}");
                    }
                }

                fullPath.AddRange(segment);
                from = to;
            }

            return fullPath;
        }
    }
}
