using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Patrol behavior state machine.
    ///     Uses HNA boundary detection to derive patrol waypoints, then cycles through them.
    /// </summary>
    public class PatrolStateMachine
    {
        private readonly VillagerAI m_ai;
        private readonly Villager.Villager m_villager;
        private int m_currentWaypointIndex;
        private bool m_hnaPartitionRequested;
        private List<VillagerWaypoint> m_patrolWaypoints;

        public PatrolStateMachine(Villager.Villager villager)
        {
            m_villager = villager;
            m_ai = villager.villagerAI != null ? villager.villagerAI : villager.GetComponent<VillagerAI>();
        }

        public bool IsDiscoveryComplete { get; private set; }

        /// <summary>Bed position for map rendering and UI.</summary>
        public Vector3 BedPosition => m_ai?.Memory?.BedPosition ?? Vector3.zero;

        public int WaypointCount => m_patrolWaypoints?.Count ?? 0;
        public int ActiveWaypointCount => m_patrolWaypoints?.Count(w => w.Active) ?? 0;
        public IReadOnlyList<VillagerWaypoint> PatrolWaypoints => m_patrolWaypoints;
        public bool IsHnaRoute { get; private set; }

        /// <summary>Returns the persistent state for saving to ZDO.</summary>
        public (List<VillagerWaypoint> waypoints, int wpIndex, bool complete, bool isHna) GetPersistentState()
        {
            return (m_patrolWaypoints, m_currentWaypointIndex, IsDiscoveryComplete, IsHnaRoute);
        }

        /// <summary>
        ///     Reset patrol route. Clears waypoints and re-derives them from the existing
        ///     HNA graph (if available). Does NOT clear the HNA graph itself — it's expensive
        ///     to rebuild and the boundary mapper can re-derive waypoints instantly.
        /// </summary>
        public void ResetDiscovery()
        {
            Plugin.Log?.LogWarning($"[Patrol:{m_ai.NpcName}] Discovery reset requested (debug)");
            m_patrolWaypoints?.Clear();
            m_currentWaypointIndex = 0;
            IsDiscoveryComplete = false;
            IsHnaRoute = false;
            m_hnaPartitionRequested = false;
            VillageAreaManager.UnregisterArea(GetVillageKey());
            SaveState();
            m_ai.SetState(BehaviorState.Idle);
            StartDiscovery();
        }

        /// <summary>Restores state from ZDO data.</summary>
        public void RestoreState(List<VillagerWaypoint> waypoints, int wpIndex, bool isHna = false)
        {
            m_patrolWaypoints = waypoints;
            m_currentWaypointIndex = wpIndex % (waypoints?.Count ?? 1);
            IsDiscoveryComplete = true;
            IsHnaRoute = isHna;
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
                    if (!IsDiscoveryComplete) StartDiscovery();
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
        {
            return RegionGraph.VillageKey(m_ai.Memory.BedPosition);
        }

        private void StartDiscovery()
        {
            var villageKey = GetVillageKey();
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
                IsHnaRoute = true;
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
                        { "anchor_z", bedPos.z.ToString("F2", CultureInfo.InvariantCulture) },
                    },
                });
            }
        }

        private void CompleteHnaDiscovery()
        {
            m_currentWaypointIndex = 0;
            IsDiscoveryComplete = true;

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

            var total = m_patrolWaypoints.Count;

            // Advance to the next active waypoint — this is where the circuit starts
            var firstIdx = m_currentWaypointIndex;
            for (var i = 0; i < total; i++)
            {
                firstIdx = (firstIdx + 1) % total;
                if (m_patrolWaypoints[firstIdx].Active)
                    break;
            }

            // Collect all active waypoints in circuit order starting from firstIdx
            var circuitWps = new List<(int index, VillagerWaypoint wp)>();
            for (var i = 0; i < total; i++)
            {
                var idx = (firstIdx + i) % total;
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
                Plugin.Log?.LogDebug(
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
        ///     Build a concatenated path through all circuit waypoints using Valheim's Pathfinding.
        ///     Returns null if any segment fails to path.
        /// </summary>
        private List<Vector3> BuildCircuitPath(
            Vector3 startPos, List<(int index, VillagerWaypoint wp)> circuitWps)
        {
            var pf = Pathfinding.instance;
            if (pf == null) return null;

            var agentType = m_ai.Instance.m_pathAgentType;
            var fullPath = new List<Vector3>();
            var segment = new List<Vector3>();
            var from = startPos;

            for (var i = 0; i < circuitWps.Count; i++)
            {
                segment.Clear();
                var to = circuitWps[i].wp.Position;

                if (!pf.GetPath(from, to, segment, agentType))
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
                    if (pt.y < 1f || pt.y > 500f)
                        Plugin.Log?.LogWarning(
                            $"[Patrol:{m_ai.NpcName}] Anomalous path point Y={pt.y:F1} " +
                            $"at ({pt.x:F1},{pt.y:F1},{pt.z:F1}) in segment {i}");

                fullPath.AddRange(segment);
                from = to;
            }

            return fullPath;
        }
    }
}