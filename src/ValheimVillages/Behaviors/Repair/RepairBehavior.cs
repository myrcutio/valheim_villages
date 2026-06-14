using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Repair
{
    /// <summary>
    ///     The carpenter's upkeep behavior: wander the village, find damaged
    ///     structures (pieces whose <see cref="WearNTear"/> health is below full) and
    ///     repair them. Walk to a ground spot near a damaged cluster, repair every
    ///     damaged piece within reach, then re-scan for the next — which reads as the
    ///     carpenter wandering around fixing things.
    ///
    ///     <para>Crucially it only targets a piece it can reach by a COMPLETE ground
    ///     path, so it never tries to climb onto a roof/beam to reach an elevated
    ///     piece (which strands it). Repair is a ZDO health restore with no range/LOS
    ///     requirement, so an elevated piece (a roof) is repaired from the ground
    ///     beside the building via the on-arrival radius sweep.</para>
    ///     <para>Only starts when idle, so it never interrupts active crafting (work
    ///     orders). Tag: "repair", Priority: 35 (below craft, above patrol).</para>
    /// </summary>
    [RegisterBehavior("repair")]
    public class RepairBehavior : IBehavior
    {
        private const float ScanInterval = 8f;
        private const float MaxLegSeconds = 20f;
        private const float UnreachableCooldown = 30f;

        // Repair anything below this fraction of full health (avoids float jitter at 1.0).
        private const float DamagedThreshold = 0.99f;

        // Pieces below this durability are "critical" and are headed to first, ahead
        // of any merely-closer healthier piece.
        private const float CriticalDurability = 0.45f;

        // On arrival, repair every damaged piece within this 3D range — clears a
        // cluster (a building's walls + the roof above) from one ground spot.
        private const float RepairRange = 3f;

        // Cap on reachability path-checks per scan so a wrecked village (many damaged
        // pieces) can't spike a frame.
        private const int MaxReachabilityChecks = 12;

        // Snap radius used when landing a resolved region cell onto the agent navmesh.
        // Deliberately small (~one height bucket): the cell is already region-resident, so
        // a wide snap would risk drifting the approach back across the village boundary.
        private const float ApproachSnapRadius = 2f;

        private readonly VillagerAI m_ai;

        // Structures we couldn't reach recently, so we don't keep re-targeting them.
        private readonly Dictionary<ZDOID, float> m_skipUntil = new();

        private bool m_active;
        private Vector3 m_approach;
        private float m_legDeadline;
        private float m_lastScanTime;
        private bool m_navIssued;
        private WearNTear m_target;

        public RepairBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "repair";

        // Below craft(50) so work orders take precedence; above patrol(30) so it
        // fills idle time. Combat/flee (100) still preempt.
        public int Priority => 35;

        public bool WantsControl(BehaviorContext ctx)
        {
            if (m_active) return true;

            // Only start when idle so we never interrupt active work (crafting).
            if (m_ai.CurrentState != BehaviorState.Idle) return false;
            if (m_ai.IsInBackoff) return false;

            if (Time.time - m_lastScanTime < ScanInterval) return false;
            m_lastScanTime = Time.time;

            return FindDamaged();
        }

        public void Update(float dt)
        {
            if (!IsValid(m_target))
            {
                Reset();
                return;
            }

            if (Time.time > m_legDeadline)
            {
                // Stuck reaching this structure too long — skip it for a while.
                Blacklist();
                Reset();
                return;
            }

            // Tick fast while heading to a repair so the proximity check below catches
            // the target precisely (the default reselect cadence is ~2s).
            m_ai.RequestFastReselect(0.25f);

            // Repair as soon as we're within reach — DON'T wait for a PathComplete
            // "arrival" (AgentHasArrived), which never fires for the link-stitched
            // (PathPartial) routes that most cross-region targets produce.
            if ((m_ai.Position - m_target.transform.position).sqrMagnitude
                <= RepairRange * RepairRange)
            {
                DoRepairSweep();
                Reset();
                return;
            }

            if (!m_navIssued)
            {
                if (!m_ai.NavTo(m_approach, BehaviorState.Traveling, "repair: go to structure",
                        snapToApproach: false))
                {
                    Blacklist();
                    Reset();
                    return;
                }

                m_navIssued = true;
            }
        }

        public void OnArrival(float dt)
        {
            DoRepairSweep();
            Reset();
        }

        /// <summary>
        ///     Repair every damaged structure within reach of where we stopped — the
        ///     targeted piece plus its neighbours (and a roof overhead), so a whole
        ///     building is patched from one safe ground spot.
        /// </summary>
        private void DoRepairSweep()
        {
            var seen = new HashSet<WearNTear>();
            var repaired = 0;
            foreach (var wnt in PhysicsHelper.GetAllInRadius<WearNTear>(m_ai.Position, RepairRange))
            {
                if (wnt == null || !seen.Add(wnt)) continue;
                if (!IsValid(wnt)) continue;
                if (wnt.Repair()) repaired++;
            }

            if (repaired > 0)
                Plugin.Log?.LogInfo($"[Repair:{m_ai.NpcName}] Repaired {repaired} structure(s).");
        }

        public string GetStatusText()
        {
            return m_active ? "Repairing structures" : "";
        }

        /// <summary>
        ///     Nearest damaged structure that has a COMPLETE ground path to a resolved
        ///     approach. Skipping pieces without a complete path is what stops the
        ///     carpenter from climbing onto roofs to reach elevated pieces and
        ///     stranding itself.
        /// </summary>
        private bool FindDamaged()
        {
            var center = m_ai.HomeAnchor;
            var radius = WorkSettings.RepairScanRadius;
            var myPos = m_ai.Position;

            // Collect damaged candidates, nearest first.
            var seen = new HashSet<WearNTear>();
            var candidates = new List<(WearNTear wnt, float distSq, float hp)>();
            foreach (var wnt in PhysicsHelper.GetAllInRadius<WearNTear>(center, radius))
            {
                if (wnt == null || !seen.Add(wnt)) continue;
                if (!IsValid(wnt)) continue;
                if (IsBlacklisted(wnt)) continue;
                var hp = wnt.GetHealthPercentage();
                if (hp >= DamagedThreshold) continue;
                candidates.Add((wnt, (wnt.transform.position - myPos).sqrMagnitude, hp));
            }

            if (candidates.Count == 0) return false;

            // Critical (sub-45% durability) pieces first, then nearest. So the
            // carpenter heads to the most-worn structures before topping up cosmetic
            // damage on closer pieces.
            candidates.Sort((a, b) =>
            {
                var aCrit = a.hp < CriticalDurability;
                var bCrit = b.hp < CriticalDurability;
                if (aCrit != bCrit) return aCrit ? -1 : 1;
                return a.distSq.CompareTo(b.distSq);
            });

            var checks = 0;
            foreach (var (wnt, _, _) in candidates)
            {
                if (checks++ >= MaxReachabilityChecks) break;
                if (!TryResolveReachableApproach(wnt.transform.position, out var approach))
                    continue;

                m_target = wnt;
                m_approach = approach;
                m_active = true;
                m_navIssued = false;
                m_legDeadline = Time.time + MaxLegSeconds;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Resolve a reachable GROUND approach near <paramref name="piecePos"/>:
        ///     the nearest region-resident, navmesh-walkable cell within
        ///     <see cref="RepairRange"/> of the piece. For an elevated piece this is the
        ///     ground below/beside it, since repair has no range/LOS requirement. Out is
        ///     the approach point; false if unreachable. Deliberately NOT the station
        ///     approach-resolver — that requires a standoff pad and fails for plain
        ///     structural pieces (walls/floors).
        /// </summary>
        private bool TryResolveReachableApproach(Vector3 piecePos, out Vector3 approach)
        {
            approach = Vector3.zero;
            if (!VillagerAgentType.IsRegistered) return false;

            var graph = Villages.Entity.VillageRegistry.GraphAt(m_ai.HomeAnchor);
            if (graph == null) return false;

            // Reachable = the approach lies inside this village's operable area (the
            // region graph resolves it). We deliberately DON'T use a raw
            // NavMesh.CalculatePath == PathComplete check: the village navmesh is stitched
            // together by NavMeshLinks at doors/gates, and CalculatePath reports any route
            // crossing a link as PathPartial — which rejected every target outside the
            // carpenter's immediate region. The agent mover crosses those links at
            // runtime; the region graph is the true reachability/extent.
            //
            // But a raw NavMesh.SamplePosition snap to the NEAREST mesh cell betrays this
            // for PERIMETER pieces (walls/gates): the closest mesh point to a boundary
            // wall lands on the threshold OUTSIDE any indexed region, so PointToRegionId
            // returns empty and the piece is rejected forever. Walk the region lookup grid
            // instead — it returns only cells PointToRegionId agrees with — and take the
            // nearest one within RepairRange that the agent can actually stand on.
            if (!graph.TryFindNearestLookupCell(
                    piecePos,
                    pos => NavMesh.SamplePosition(pos, out _, ApproachSnapRadius, AgentFilter()),
                    out var cell,
                    out _,
                    RepairRange))
                return false;

            // Snap the region cell onto the exact mesh surface for the mover, with a small
            // radius so it stays inside the region cell (a wide snap would drift back to
            // the boundary we just avoided).
            if (!NavMesh.SamplePosition(cell, out var near, ApproachSnapRadius, AgentFilter()))
                return false;

            approach = near.position;
            return true;
        }

        private static NavMeshQueryFilter AgentFilter()
        {
            return new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
        }

        private static bool IsValid(WearNTear wnt)
        {
            if (wnt == null) return false;
            var nview = wnt.GetComponent<ZNetView>();
            return nview != null && nview.IsValid();
        }

        private static ZDOID PieceId(WearNTear wnt)
        {
            var nview = wnt != null ? wnt.GetComponent<ZNetView>() : null;
            return nview != null && nview.GetZDO() != null ? nview.GetZDO().m_uid : ZDOID.None;
        }

        private bool IsBlacklisted(WearNTear wnt)
        {
            var id = PieceId(wnt);
            if (id == ZDOID.None) return false;
            if (!m_skipUntil.TryGetValue(id, out var until)) return false;
            if (Time.time >= until)
            {
                m_skipUntil.Remove(id);
                return false;
            }

            return true;
        }

        private void Blacklist()
        {
            var id = PieceId(m_target);
            if (id != ZDOID.None)
                m_skipUntil[id] = Time.time + UnreachableCooldown;
        }

        private void Reset()
        {
            m_active = false;
            m_target = null;
            m_navIssued = false;
            m_ai.SetState(BehaviorState.Idle);
        }
    }
}
