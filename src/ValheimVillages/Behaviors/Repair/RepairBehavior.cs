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
    ///     The carpenter's upkeep round. The board carries ONE repair task per village
    ///     (<see cref="Scheduling.Producers.RepairTaskProducer" />); on assignment the carpenter
    ///     works through the village itself — nearest reachable damaged piece, repair every
    ///     damaged piece within reach of where he stands, then straight on to the next —
    ///     until nothing reachable is left or the round runs long.
    ///
    ///     <para>Crucially it only targets a piece it can reach by a COMPLETE ground
    ///     path, so it never tries to climb onto a roof/beam to reach an elevated
    ///     piece (which strands it). Repair is a ZDO health restore with no range/LOS
    ///     requirement, so an elevated piece (a roof) is repaired from the ground
    ///     beside the building via the on-arrival radius sweep.</para>
    ///     <para>A piece he cannot get to is skipped for <see cref="StuckCooldown" />, long
    ///     enough that it cannot come back round within the same round: a short skip let
    ///     three unreachable wall pieces keep him walking a loop between them forever.
    ///     Tag: "repair", Priority: 35.</para>
    /// </summary>
    [RegisterBehavior("repair")]
    public class RepairBehavior : IBehavior, IDirectedBehavior
    {
        /// <summary>Outer bound on one walk to one piece.</summary>
        private const float MaxLegSeconds = 30f;

        /// <summary>
        ///     A walk that has not closed on the piece by <see cref="ProgressEpsilon" /> in this
        ///     long is stuck — typically the agent reached the clamped end of a partial path and
        ///     stands there out of reach. Far quicker to detect than waiting out the leg.
        /// </summary>
        private const float StallSeconds = 6f;

        private const float ProgressEpsilon = 0.5f;

        /// <summary>How long a piece that could not be reached (or approached) is skipped.</summary>
        private const float StuckCooldown = 600f;

        /// <summary>How long a piece the sweep could not repair is skipped.</summary>
        private const float NoEffectCooldown = 120f;

        /// <summary>
        ///     One round ends after this long even with work left, handing the villager back to
        ///     the scheduler (the village task stays on the board, so the next round picks up
        ///     where this left off). Well inside the dispatcher's assignment ceiling.
        /// </summary>
        private const float MaxRoundSeconds = 300f;

        // On arrival, repair every damaged piece within this 3D range — clears a
        // cluster (a building's walls + the roof above) from one ground spot.
        private const float RepairRange = 3f;

        // Snap radius used when landing a resolved region cell onto the agent navmesh.
        // Deliberately small (~one height bucket): the cell is already region-resident, so
        // a wide snap would risk drifting the approach back across the village boundary.
        private const float ApproachSnapRadius = 2f;

        private readonly VillagerAI m_ai;

        // Pieces we recently gave up on, so the round moves past them.
        private readonly Dictionary<ZDOID, float> m_skipUntil = new();

        private bool m_active;
        private Vector3 m_approach;
        private float m_legDeadline;
        private float m_roundEndsAt;
        private bool m_navIssued;
        private WearNTear m_target;
        private float m_bestDistance;
        private float m_lastProgressAt;
        private int m_repairedThisRound;

        public RepairBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "repair";

        // Below craft(50) so work orders take precedence; above patrol(30) so it
        // fills idle time. Combat/flee (100) still preempt.
        public int Priority => 35;

        // The scheduler owns WHETHER to repair — act only on an assignment (m_active set by
        // BeginAssignment), never self-discover. Deliberately identical to
        // AssignmentActive: the dispatcher holds a claim while that is true, so if the two
        // could disagree the villager would be "busy" to the dispatcher and idle to the
        // selector, and never get reassigned.
        public bool WantsControl(BehaviorContext ctx) => AssignmentActive;

        // --- IDirectedBehavior: scheduler-assigned execution ---

        public bool CanExecute(TaskKind kind) => kind == TaskKind.RepairPiece;

        public bool AssignmentActive => m_active;

        public void AbandonAssignment(string reason) => Reset();

        public AssignmentResult BeginAssignment(CandidateTask task)
        {
            // Every failure is NotActionable, never Unreachable: this one task stands for the
            // whole village, so blocking it until a repartition would stop all repairs because
            // some pieces are awkward. Unreachable pieces are skipped individually instead.
            var graph = Villages.Entity.VillageRegistry.GraphAt(m_ai.HomeAnchor);
            if (!VillagerAgentType.IsRegistered || graph == null)
                return AssignmentResult.NotActionable;

            if (!TryPickNext(graph)) return AssignmentResult.NotActionable;

            m_active = true;
            m_repairedThisRound = 0;
            m_roundEndsAt = Time.time + MaxRoundSeconds;
            return AssignmentResult.Accepted;
        }

        /// <summary>
        ///     Choose the nearest damaged piece in the village that is not being skipped and has
        ///     a walkable approach, and aim the next leg at it. Pieces with no approach are
        ///     skipped as they are found, so one pass settles them for the whole round.
        /// </summary>
        private bool TryPickNext(Villager.AI.Navigation.RegionGraph graph)
        {
            var village = Villages.Entity.VillageRegistry.GetVillageAt(m_ai.HomeAnchor);
            var damaged = VillageRepairs.FindDamaged(village);
            var from = m_ai.Position;
            damaged.Sort((a, b) =>
                (a.piece.transform.position - from).sqrMagnitude.CompareTo(
                    (b.piece.transform.position - from).sqrMagnitude));

            foreach (var (piece, _) in damaged)
            {
                if (IsSkipped(piece)) continue;
                if (!TryResolveReachableApproach(graph, piece.transform.position, out var approach))
                {
                    Skip(piece, StuckCooldown, "no_approach");
                    continue;
                }

                m_target = piece;
                m_approach = approach;
                m_navIssued = false;
                m_legDeadline = Time.time + MaxLegSeconds;
                m_bestDistance = Vector3.Distance(from, piece.transform.position);
                m_lastProgressAt = Time.time;
                return true;
            }

            return false;
        }

        /// <summary>Move on to the next piece, or end the round when there is none.</summary>
        private void NextOrFinish()
        {
            var graph = Villages.Entity.VillageRegistry.GraphAt(m_ai.HomeAnchor);
            if (Time.time < m_roundEndsAt && graph != null && TryPickNext(graph)) return;

            DebugLog.Event("Repair", "round_done",
                ("vid", DebugLog.Vid(m_ai.UniqueId)), ("repaired", m_repairedThisRound));
            Reset();
        }

        public void Update(float dt)
        {
            if (!IsValid(m_target) || PieceHealth.Fraction(m_target) >= VillageRepairs.DamagedThreshold)
            {
                // Gone, or someone else fixed it first.
                NextOrFinish();
                return;
            }

            // Tick fast while heading to a repair so the proximity check below catches
            // the target precisely (the default reselect cadence is ~2s).
            m_ai.RequestFastReselect(0.25f);

            // Repair as soon as we're within reach — DON'T wait for a PathComplete
            // "arrival" (AgentHasArrived), which never fires for the link-stitched
            // (PathPartial) routes that most cross-region targets produce.
            var distance = Vector3.Distance(m_ai.Position, m_target.transform.position);
            if (distance <= RepairRange)
            {
                SweepAndContinue();
                return;
            }

            // Knocked out of Traveling mid-leg (a flee settles the villager to Idle and leaves
            // our waypoint behind with movement stopped): walk the leg again, and don't count
            // the time spent away as a stall.
            if (m_navIssued && m_ai.CurrentState != BehaviorState.Traveling)
            {
                m_navIssued = false;
                m_lastProgressAt = Time.time;
            }

            if (distance < m_bestDistance - ProgressEpsilon)
            {
                m_bestDistance = distance;
                m_lastProgressAt = Time.time;
            }

            if (Time.time - m_lastProgressAt > StallSeconds || Time.time > m_legDeadline)
            {
                Skip(m_target, StuckCooldown, "stuck");
                NextOrFinish();
                return;
            }

            if (!m_navIssued)
            {
                if (!m_ai.NavTo(m_approach, BehaviorState.Traveling, "repair: go to structure",
                        snapToApproach: false))
                {
                    Skip(m_target, StuckCooldown, "nav_refused");
                    NextOrFinish();
                    return;
                }

                m_navIssued = true;
            }
        }

        public void OnArrival(float dt)
        {
            if (!m_active || !IsValid(m_target)) return;

            // Arrival can re-fire for the PREVIOUS leg's waypoint after TryPickNext has already
            // aimed at a new piece, and an approach can land just short of reach. Either way
            // sweeping here would repair nothing and wrongly skip the new target; leave it to
            // Update, which re-walks the leg (and stall-skips it if it never closes).
            if (Vector3.Distance(m_ai.Position, m_target.transform.position) > RepairRange)
            {
                m_navIssued = false;
                return;
            }

            SweepAndContinue();
        }

        private void SweepAndContinue()
        {
            // Nothing repaired means this piece is not actually repairable from here, whatever
            // its health reads. Skip it rather than walking straight back to it.
            var repaired = DoRepairSweep();
            if (repaired == 0) Skip(m_target, NoEffectCooldown, "no_effect");
            m_repairedThisRound += repaired;
            NextOrFinish();
        }

        /// <summary>
        ///     Repair every damaged structure within reach of where we stopped — the
        ///     targeted piece plus its neighbours (and a roof overhead), so a whole
        ///     building is patched from one safe ground spot.
        /// </summary>
        private int DoRepairSweep()
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

            return repaired;
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

        private bool IsSkipped(WearNTear wnt)
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

        private void Skip(WearNTear wnt, float seconds, string reason)
        {
            var id = PieceId(wnt);
            if (id == ZDOID.None) return;
            m_skipUntil[id] = Time.time + seconds;
            var pos = wnt.transform.position;
            DebugLog.Event("Repair", "skip_piece",
                ("vid", DebugLog.Vid(m_ai.UniqueId)), ("reason", reason),
                ("pos", $"{pos.x:F0},{pos.z:F0}"), ("for_s", seconds));
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
