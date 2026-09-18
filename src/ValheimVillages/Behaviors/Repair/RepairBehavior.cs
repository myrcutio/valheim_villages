using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Scheduling;
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
    ///     <para>Which piece to repair is chosen here, but WHETHER to repair is the
    ///     scheduler's call: this behavior never self-discovers work, it only acts on a
    ///     <see cref="TaskKind.RepairPiece" /> assignment. Tag: "repair", Priority: 35.</para>
    /// </summary>
    [RegisterBehavior("repair")]
    public class RepairBehavior : IBehavior, IDirectedBehavior
    {
        private const float MaxLegSeconds = 20f;
        private const float UnreachableCooldown = 30f;

        // Repair anything below this fraction of full health (avoids float jitter at 1.0).
        private const float DamagedThreshold = 0.99f;

        // On arrival, repair every damaged piece within this 3D range — clears a
        // cluster (a building's walls + the roof above) from one ground spot.
        private const float RepairRange = 3f;

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

        // The scheduler owns target selection — act only on an assignment (m_active set by
        // BeginAssignment), never self-discover. Deliberately identical to
        // AssignmentActive: the dispatcher holds a claim while that is true, so if the two
        // could disagree the villager would be "busy" to the dispatcher and idle to the
        // selector, and never get reassigned.
        public bool WantsControl(BehaviorContext ctx) => AssignmentActive;

        // --- IDirectedBehavior: scheduler-assigned execution ---

        public bool CanExecute(TaskKind kind) => kind == TaskKind.RepairPiece;

        public bool AssignmentActive => m_active;

        public AssignmentResult BeginAssignment(CandidateTask task)
        {
            // The assigned task carries the piece position; resolve the actual damaged
            // structure there (the on-arrival sweep repairs the whole cluster anyway).
            var wnt = FindDamagedNear(task.Position);
            if (wnt == null) return AssignmentResult.NotActionable;

            // Nav infrastructure not up yet. That is a "can't answer", not a verdict about
            // this piece, and must never be reported as Unreachable — the dispatcher would
            // block the row until the next repartition over a transient startup gap.
            var graph = Villages.Entity.VillageRegistry.GraphAt(m_ai.HomeAnchor);
            if (!VillagerAgentType.IsRegistered || graph == null)
                return AssignmentResult.NotActionable;

            if (!TryResolveReachableApproach(graph, wnt.transform.position, out var approach))
                return AssignmentResult.Unreachable;

            m_target = wnt;
            m_approach = approach;
            m_active = true;
            m_navIssued = false;
            m_legDeadline = Time.time + MaxLegSeconds;
            return AssignmentResult.Accepted;
        }

        /// <summary>
        ///     Nearest still-damaged structure within repair range of a point, skipping
        ///     pieces this carpenter recently gave up reaching. The blacklist check lives
        ///     here because this is now the ONLY place a repair target is chosen — the
        ///     scheduler dispatches the task, but which piece to actually swing at is still
        ///     resolved locally, and a piece that timed out must not be re-picked instantly.
        /// </summary>
        private WearNTear FindDamagedNear(Vector3 pos)
        {
            WearNTear best = null;
            var bestSq = float.MaxValue;
            foreach (var wnt in PhysicsHelper.GetAllInRadius<WearNTear>(pos, RepairRange))
            {
                if (!IsValid(wnt)) continue;
                if (IsBlacklisted(wnt)) continue;
                if (wnt.GetHealthPercentage() >= DamagedThreshold) continue;
                var d = (wnt.transform.position - pos).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = wnt;
                }
            }

            return best;
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
        ///     Resolve a reachable GROUND approach near <paramref name="piecePos"/>:
        ///     the nearest region-resident, navmesh-walkable cell within
        ///     <see cref="RepairRange"/> of the piece. For an elevated piece this is the
        ///     ground below/beside it, since repair has no range/LOS requirement. Out is
        ///     the approach point; false if unreachable. Deliberately NOT the station
        ///     approach-resolver — that requires a standoff pad and fails for plain
        ///     structural pieces (walls/floors).
        /// </summary>
        /// <summary>
        ///     Resolve a standable approach cell for a piece. Callers pre-check that the nav
        ///     infrastructure is up and pass the village <paramref name="graph" /> in, so a
        ///     false here means exactly one thing: this piece has no walkable approach under
        ///     THAT graph. <see cref="BeginAssignment" /> turns that into
        ///     <see cref="AssignmentResult.Unreachable" />, which blocks the task until the
        ///     graph is rebuilt — so it must not be able to mean "infrastructure not ready".
        /// </summary>
        private static bool TryResolveReachableApproach(
            Villager.AI.Navigation.RegionGraph graph, Vector3 piecePos, out Vector3 approach)
        {
            approach = Vector3.zero;

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
