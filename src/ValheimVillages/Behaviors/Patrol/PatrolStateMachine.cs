using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Patrol behavior state machine.
    ///     Uses HNA boundary detection to derive patrol waypoints, then cycles through them.
    /// </summary>
    public class PatrolStateMachine
    {
        private readonly VillagerAI m_ai;
        private readonly IVillager m_villager;
        private int m_currentWaypointIndex;
        private bool m_hnaPartitionRequested;
        private List<VillagerWaypoint> m_patrolWaypoints;

        // Set when the patrol parks in NeedsHelp: the waypoint it couldn't reach.
        private int m_helpWaypointIndex = -1;
        private Vector3 m_helpPosition;

        // One-shot self-heal. Before parking in NeedsHelp, try a single automatic
        // ResetDiscovery (same as vv_patrol_reset) — a stale route left by a navmesh
        // rebuild often clears instantly. Set true once attempted; re-armed by a manual
        // reset or a full clean patrol lap. Prevents a reset↔NeedsHelp loop: if the reset
        // lands right back in NeedsHelp, the patrol parks for diagnosis.
        private bool m_autoResetAttempted;

        public PatrolStateMachine(IVillager villager)
        {
            m_villager = villager;
        }

        public PatrolStateMachine(VillagerAI ai)
        {
            m_ai = ai;
            m_villager = new VillagerAdapter(ai.Villager);
        }

        public bool IsDiscoveryComplete { get; private set; }

        /// <summary>Bed position for map rendering and UI.</summary>
        public Vector3 BedPosition => m_ai?.Memory?.BedPosition ?? Vector3.zero;

        public int WaypointCount => m_patrolWaypoints?.Count ?? 0;
        public int ActiveWaypointCount => m_patrolWaypoints?.Count(w => w.Active) ?? 0;
        public IReadOnlyList<VillagerWaypoint> PatrolWaypoints => m_patrolWaypoints;
        public bool IsHnaRoute { get; private set; }

        /// <summary>Index of the waypoint the patrol parked at in NeedsHelp, or -1.</summary>
        public int HelpWaypointIndex => m_helpWaypointIndex;

        /// <summary>World position of the unreachable waypoint when in NeedsHelp.</summary>
        public Vector3 HelpPosition => m_helpPosition;

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
            // A manual reset (vv_patrol_reset) re-arms the one-shot auto-heal: the operator
            // is explicitly asking for a fresh attempt.
            m_autoResetAttempted = false;
            ResetDiscoveryInternal("manual");
        }

        private void ResetDiscoveryInternal(string reason)
        {
            Plugin.Log?.LogWarning($"[Patrol:{m_villager.VillagerName}] Discovery reset ({reason})");
            m_patrolWaypoints?.Clear();
            m_currentWaypointIndex = 0;
            IsDiscoveryComplete = false;
            IsHnaRoute = false;
            m_hnaPartitionRequested = false;
            m_helpWaypointIndex = -1;
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
                $"[Patrol:{m_villager.VillagerName}] Restored {m_patrolWaypoints.Count} waypoints" +
                $" (hna={isHna}), index={m_currentWaypointIndex}");
        }

        /// <summary>Main patrol AI update, called each behavior tick.</summary>
        public void UpdatePatrolAI(float dt)
        {
            if (m_ai.IsInBackoff) return;

            var state = m_ai.CurrentState;

            switch (state)
            {
                case BehaviorState.NeedsHelp:
                    break;
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


        private string GetVillageId()
        {
            var fromZdo = m_ai?.NView?.GetZDO()?.GetString(Village.IdKey);
            if (!string.IsNullOrEmpty(fromZdo)) return fromZdo;
            // No stamped id (legacy guard). Resolve by EXISTING graph coverage only —
            // never mint here: a read/discovery path must not fabricate a village ZDO.
            // If nothing covers the bed, return null so the caller's
            // FindById(null)?.Graph yields null and patrol takes its no-graph branch.
            var byPos = VillageRegistry.GetVillageAt(m_ai.Memory.BedPosition);
            if (byPos != null) return byPos.VillageId;
            Plugin.Log?.LogWarning(
                $"[Patrol:{m_villager?.VillagerName}] guard has no vv_village_id and no village " +
                $"covers its bed {m_ai.Memory.BedPosition}; waiting for a partition (not minting).");
            return null;
        }

        private void StartDiscovery()
        {
            var villageId = GetVillageId();
            // The graph is owned by the durable village (hydrated at load by
            // village_index, rebuilt by the partition). No per-guard ZDO restore.
            var graph = VillageRegistry.FindById(villageId)?.Graph;

            // No graph yet: enqueue the partition task (once) and wait. This is the
            // ONLY condition that warrants a partition — an available graph that
            // can't yet yield a route is a transient navmesh state, not a missing
            // graph, so it must not re-trigger partitions (which would loop with the
            // post-partition patrol-route rebuild).
            if (graph == null || !graph.IsAvailable)
            {
                if (!m_hnaPartitionRequested)
                {
                    m_hnaPartitionRequested = true;
                    var bedPos = m_ai.Memory.BedPosition;
                    Plugin.Log?.LogInfo($"[Patrol:{m_villager.VillagerName}] HNA graph unavailable, requesting hna_partition build");
                    GlobalTaskQueue.Enqueue(new VillagerTask
                    {
                        Name = "hna_partition",
                        SourceId = $"patrol:{m_ai.UniqueId}",
                        Priority = TaskPriority.Medium,
                        TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                        Attributes = new Dictionary<string, string>
                        {
                            { "village_id", villageId },
                            { "anchor_x", bedPos.x.ToString("F2", CultureInfo.InvariantCulture) },
                            { "anchor_z", bedPos.z.ToString("F2", CultureInfo.InvariantCulture) },
                        },
                    });
                }

                return;
            }

            var routePoints = PatrolRouteBuilder.Build(
                graph.GetBoundaryCells(), m_ai.Memory.BedPosition);
            if (routePoints.Count >= 3)
            {
                m_patrolWaypoints = routePoints
                    .Select(p => new VillagerWaypoint(p, VillagerWaypoint.DefaultStrategyId))
                    .ToList();
                IsHnaRoute = true;
                m_hnaPartitionRequested = false;
                CompleteHnaDiscovery();
                return;
            }

            // Graph is available but Build returned no reachability-verified loop —
            // the agent navmesh is still settling after a rebake (Build returns empty
            // until it can be queried), or every boundary vertex currently resolves
            // unreachable. Do NOT mark discovery complete with an unverified route and
            // do NOT enqueue another partition. Stay in discovery; UpdatePatrolAI
            // re-calls StartDiscovery each tick until a filtered route is produced.
        }

        private void CompleteHnaDiscovery()
        {
            m_currentWaypointIndex = 0;
            IsDiscoveryComplete = true;

            SaveState();

            // The region graph is persisted by the partition handler onto the durable
            // village ZDO (1-to-1), not here on the guard. The guard only persists its
            // own route via SaveState above.

            var first = m_patrolWaypoints[0].Position;
            var last = m_patrolWaypoints[m_patrolWaypoints.Count - 1].Position;
            var playerPos = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;

            Plugin.Log?.LogInfo(
                $"[Patrol:{m_villager.VillagerName}] HNA boundary discovery complete, {m_patrolWaypoints.Count} waypoints. " +
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

            // Step to the next ACTIVE waypoint in ring order. The NavMeshAgent
            // routes between waypoints itself, so patrol just feeds one target at
            // a time and re-advances on arrival — no pre-stitched corridor path.
            var idx = m_currentWaypointIndex;
            for (var i = 0; i < total; i++)
            {
                idx = (idx + 1) % total;
                if (m_patrolWaypoints[idx].Active) break;
            }

            if (!m_patrolWaypoints[idx].Active)
            {
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            // Wrapping back to (or before) the prior index means a full lap completed
            // without parking — the route is traversable again, so re-arm the one-shot
            // auto-reset for any FUTURE NeedsHelp. (While stuck at the same waypoint no lap
            // ever completes, so this never clears mid-loop and can't cause a reset cycle.)
            if (idx <= m_currentWaypointIndex)
                m_autoResetAttempted = false;

            m_currentWaypointIndex = idx;
            var wp = m_patrolWaypoints[idx];

            // snapToApproach=true: a boundary waypoint can land on or just inside
            // an obstacle near the wall (the inward inset nudges it off the
            // walkable frontier cell — e.g. into a charcoal kiln). NavTo's approach
            // resolver probes outward to the nearest standable, reachable point.
            if (m_ai.NavTo(wp.Position, BehaviorState.Patrolling, $"patrol:W{idx}",
                    snapToApproach: true))
                return;

            // One-shot self-heal before parking: a NeedsHelp is often just a stale route
            // left over from a navmesh rebuild. Try a single automatic reset (same as
            // vv_patrol_reset) to re-derive waypoints from the current graph. If that lands
            // us right back here, m_autoResetAttempted is already set and we fall through to
            // park for diagnosis — no reset↔NeedsHelp loop.
            if (!m_autoResetAttempted)
            {
                m_autoResetAttempted = true;
                Plugin.Log?.LogWarning(
                    $"[Patrol:{m_villager.VillagerName}] No approach to waypoint {idx} " +
                    $"({wp.Position.x:F1},{wp.Position.y:F1},{wp.Position.z:F1}) — " +
                    "attempting one automatic patrol-reset before parking in NeedsHelp.");
                ResetDiscoveryInternal("auto-heal");
                return;
            }

            // No reachable approach exists, and the one auto-reset didn't help. Do NOT skip
            // the waypoint — that would silently paper over a broken route or genuinely
            // impassable geometry. Park in NeedsHelp as an operator signal: inspect the
            // spot, then either fix the route algorithm or the physical geometry and run
            // vv_patrol_reset.
            m_helpWaypointIndex = idx;
            m_helpPosition = wp.Position;

            var reason = $"No reachable approach to waypoint {idx}.";
            Plugin.Log?.LogError(
                $"[Patrol:{m_villager.VillagerName}] NeedsHelp: {reason} " +
                $"({wp.Position.x:F1},{wp.Position.y:F1},{wp.Position.z:F1}) — " +
                "patrol parked, fix route/geometry then vv_patrol_reset.");
            // Surface as a structured "blocked" issue (deduped, with a map pin to
            // the unreachable waypoint) so it renders in the Info tab the same way
            // a blocked work order does.
            VillagerActivityLog.Instance.RecordBlocked(
                m_villager.UniqueID, "Patrol", null, null, reason, wp.Position);
            m_ai.SetState(BehaviorState.NeedsHelp);
        }
    }
}