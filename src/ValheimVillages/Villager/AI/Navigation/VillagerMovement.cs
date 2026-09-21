using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Navigation helpers for VillagerAI.
    /// </summary>
    public static class VillagerMovement
    {
        /// <summary>
        ///     Single source of truth for "is the villager close enough to this
        ///     position to be considered there?" 3D distance against an
        ///     explicit threshold. Callers pass
        ///     <see cref="VillagerSettings.ArrivalThreshold"/> for final
        ///     arrival at stations / chests (generous, ~2m) and
        ///     <see cref="VillagerSettings.PathNodePopThreshold"/> for
        ///     intermediate path-node popping (tight, ~0.5m). Same shape, two
        ///     distinct semantics — using the generous arrival radius for
        ///     intermediate pops eats routing corners and strands the agent
        ///     against obstacles those corners were supposed to detour around.
        /// </summary>
        public static bool IsAtPosition(Vector3 villagerPos, Vector3 target, float threshold)
        {
            return Vector3.Distance(villagerPos, target) < threshold;
        }

        /// <summary>
        ///     Attempt to compute a COMPLETE path against the villager NavMesh
        ///     (slot 31) that ALSO lies entirely on the HNA region graph.
        ///     This is the load-bearing invariant that prevents villagers
        ///     walking into piece-layer colliders the HNA prune correctly
        ///     excluded but Unity's voxelizer re-introduced as sliver/orphan
        ///     polygons. Returns true only when all of:
        ///     <list type="bullet">
        ///       <item>RegionGraph A* over the LookupGrid resolves both endpoints to cells and finds a connecting cell sequence,</item>
        ///       <item>each per-segment <c>NavMesh.CalculatePath</c> between consecutive corridor waypoints returns <see cref="NavMeshPathStatus.PathComplete"/>,</item>
        ///       <item>every returned corner satisfies <see cref="RegionGraph.PointToRegionId"/> ≠ null.</item>
        ///     </list>
        ///     Failure → outPath empty, caller enters PathUnreachable recovery.
        ///     No silent fallback to unconstrained CalculatePath: a
        ///     same-shape-but-degraded path here would mask the bake/HNA
        ///     drift the corridor planner exists to catch.
        ///     <para>If no RegionGraph is available for either endpoint
        ///     (graph not yet built, villager outside any registered
        ///     village), the planner falls through to the legacy
        ///     unconstrained NavMesh.CalculatePath — pre-graph behavior is
        ///     preserved for villagers who haven't entered a partition yet.
        ///     The fallthrough is logged so cases that *should* be
        ///     corridor-planned but slip through are visible.</para>
        /// </summary>

        public static bool TryFindCompletePath(Vector3 start, Vector3 end, List<Vector3> outPath)
        {
            outPath?.Clear();

            if (!VillagerAgentType.IsRegistered) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            return TryFindUnconstrainedPath(start, end, filter, outPath);
        }


        /// <summary>
        ///     Legacy single-shot CalculatePath retained for the
        ///     no-graph-available fallthrough. Behaviour matches the
        ///     pre-corridor implementation exactly.
        /// </summary>
        private static bool TryFindUnconstrainedPath(
            Vector3 start, Vector3 end, NavMeshQueryFilter filter, List<Vector3> outPath)
        {
            // Snap radius 3m (was 1m). The start/end here are HNA graph LOOKUP
            // CELLS (from TryResolveApproach's snap), which don't always sit on
            // the combined-bake navmesh — a anchor whose nearest lookup cell lands
            // in an eroded/blocked spot can be >1m off the walkable surface. With
            // a 1m radius that start failed to map and EVERY approach from that
            // anchor reported unreachable (observed: the Farmer's anchor-2, snapped
            // pathStart 1.24m off-mesh, resolved 0 of 270 candidates). 3m maps it
            // onto the nearby walkable surface without reaching a different level.
            if (!NavMesh.SamplePosition(start, out var startHit, 3f, filter)) return false;
            if (!NavMesh.SamplePosition(end, out var endHit, 3f, filter)) return false;
            var navPath = new NavMeshPath();
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, filter, navPath))
                return false;
            if (navPath.status != NavMeshPathStatus.PathComplete) return false;
            if (outPath != null)
            {
                var corners = navPath.corners;
                for (var i = 0; i < corners.Length; i++) outPath.Add(corners[i]);
            }
            return true;
        }


        /// <summary>Block layers for the anchor clearance capsule (terrain excluded — only
        /// structures/pieces count, so a point on open terrain passes).</summary>
        private static readonly int s_clearanceMask = LayerMask.GetMask("Default", "static_solid", "piece");

        /// <summary>
        ///     Find the nearest reachable point on the villager NavMesh within
        ///     <paramref name="maxRadius"/> of <paramref name="target"/>. Use this for stations
        ///     whose own transform sits on a non-walkable obstacle (e.g. Smelter, CharcoalKiln):
        ///     the villager can't path TO the obstacle, but they CAN path to a walkable cell
        ///     adjacent to it, which is close enough for RPC interaction (RPC_AddOre, RPC_AddFuel, …).
        ///     Returns true and writes the approach point on success; returns false (and leaves
        ///     <paramref name="approachPoint"/> = target) when nothing walkable is found within radius.
        /// </summary>
        public static bool TryFindReachableApproach(
            Vector3 target, float maxRadius, out Vector3 approachPoint, float minClearance = 0f)
        {
            approachPoint = target;
            if (!VillagerAgentType.IsRegistered) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            if (!NavMesh.SamplePosition(target, out var hit, maxRadius, filter)) return false;

            // Clearance gate (anchor sampling): require room for a body of radius minClearance
            // (2-3x a humanoid) so anchors land on open ground, never on a cramped sliver like a
            // station top. A vertical capsule of that radius at the sampled point must be clear of
            // structure/piece colliders (terrain excluded). A station top is rejected because the
            // station's own piece colliders fall inside the capsule; open terrain passes.
            if (minClearance > 0f)
            {
                var p0 = hit.position + Vector3.up * 0.3f;
                var p1 = hit.position + Vector3.up * 1.8f;
                if (Physics.CheckCapsule(p0, p1, minClearance, s_clearanceMask, QueryTriggerInteraction.Ignore))
                    return false;
            }

            approachPoint = hit.position;
            return true;
        }

        /// <summary>
        ///     Resolve a navigable approach point for a target whose own centroid may sit on a
        ///     non-walkable obstacle (smelter on a foundation, chest on a shelf, etc). Probes the
        ///     centroid first, then expanding compass-offset rings, sampling the NavMesh at each
        ///     and validating with a complete-path check from <paramref name="pathSource"/>.
        ///     <para>Pass an optional <paramref name="hullPredicate"/> that returns false for
        ///     candidates outside the village hull — used by station lookup to reject points on
        ///     the wrong side of an outer wall. Pass null when hull filtering isn't applicable
        ///     (e.g. container targets — no hull data available at the workflow layer).</para>
        ///     Returns true and writes the approach on success. Returns false (writes
        ///     <paramref name="approach"/> = target) when no probe in the ring is both walkable
        ///     AND reachable from <paramref name="pathSource"/>.
        /// </summary>
        /// <param name="trace">
        ///     Optional diagnostic sink. When non-null, every candidate reports which gate
        ///     rejected it. This is why <c>vv_approach</c> can explain a failure instead of
        ///     guessing at it: the command drives THIS resolver rather than a second copy of
        ///     the rules that would drift from it.
        /// </param>
        public static bool TryResolveApproach(
            Vector3 target,
            Vector3 pathSource,
            System.Func<Vector3, bool> hullPredicate,
            out Vector3 approach,
            float minClearance = 0f,
            System.Action<string> trace = null)
        {
            approach = target;
            var probes = s_probeOffsets;
            var pathBuffer = new List<Vector3>();

            // Two passes. The first insists the point sit at least an agent-radius away from
            // the NavMesh EDGE; the second accepts anything reachable, as before.
            //
            // A point can sample onto the mesh and still be unusable: the villager agent has
            // radius 0.4, so it cannot centre itself 4cm from an edge — NavMeshAgent clamps and
            // the villager stalls a metre short with desiredVel high and velocity zero, looking
            // frozen mid-task. (Observed: a cooking station whose approach landed on a 1.62m²
            // piece-region, 0.04m from its edge.) This is distinct from `minClearance`, which is
            // a physics capsule test against structure colliders — that point passed physics
            // fine; it was the mesh geometry that made it unstandable.
            //
            // The fallback pass matters: rejecting outright would make a station with no roomy
            // approach simply unreachable, which is worse than a tight approach that usually
            // works. Preference, not veto.
            // Pass 3 is the REACH pass — see TryReachApproach. It runs only when the first two
            // find nothing, so ordinary ground-level targets behave exactly as before.
            for (var pass = 0; pass < 2; pass++)
            {
                var requireEdgeRoom = pass == 0;
                for (var i = 0; i < probes.Length; i++)
                {
                    var probe = target + probes[i];
                    if (!TryFindReachableApproach(probe, ApproachProbeRadius, out var hit, minClearance))
                    {
                        trace?.Invoke($"p{pass}#{i} {Where(probe)}: no navmesh within {ApproachProbeRadius:F1}m");
                        continue;
                    }

                    if (requireEdgeRoom && !HasEdgeClearance(hit))
                    {
                        trace?.Invoke($"p{pass}#{i} {Where(hit)}: too close to a navmesh edge");
                        continue;
                    }

                    if (hullPredicate != null && !hullPredicate(hit))
                    {
                        trace?.Invoke($"p{pass}#{i} {Where(hit)}: outside the village hull");
                        continue;
                    }

                    if (!TryFindCompletePath(pathSource, hit, pathBuffer))
                    {
                        trace?.Invoke($"p{pass}#{i} {Where(hit)}: no complete path from the villager");
                        continue;
                    }

                    if (!requireEdgeRoom)
                        // Throttled per target, because this resolves EVERY frame for every
                        // candidate a villager weighs: unthrottled, three awkward targets in one
                        // village wrote three lines a frame and buried the log — the failure mode
                        // that wedges a headless server through its 64KiB pipe.
                        DebugLog.ThrottledWindow(
                            $"approach-tight:{target.x:F0},{target.z:F0}",
                            System.TimeSpan.FromSeconds(ApproachLogWindowSeconds),
                            "Approach", "tight",
                            ("target", target), ("standing", hit),
                            ("note", "no edge-clear approach; villager may stall short"));

                    approach = hit;
                    return true;
                }
            }

            return TryReachApproach(target, pathSource, hullPredicate, pathBuffer, minClearance,
                out approach, trace);
        }

        /// <summary>
        ///     Last resort: stand BESIDE-AND-BELOW something and reach it.
        ///
        ///     <para>The probes above all sit at the target's own height and snap within
        ///     <see cref="ApproachProbeRadius" /> (1.5m), so anything mounted out of the
        ///     villager's plane is simply unreachable — a chest on a shelf 2.46m up reads as
        ///     "no approach" and every order depending on it starves, while the player opens it
        ///     from underneath without noticing. Valheim itself allows
        ///     <c>Player.m_maxInteractDistance</c> = 5m; this takes a deliberately shorter
        ///     <see cref="InteractReach" /> so a chest on a high balcony is not suddenly
        ///     "reachable" from the ground below it.</para>
        ///
        ///     <para><b>Line of sight is the gate.</b> Reach without it would let a villager
        ///     work through a wall, which is worse than the starvation it fixes: the standing
        ///     point must SEE the target, tested by raycast from the villager's eye with
        ///     nothing solid in between. A few aim points are tried up the target's face,
        ///     because the lip of the shelf a chest stands on will occlude its base from
        ///     below while its front is in plain view.</para>
        ///
        ///     <para><b>Find the floor by looking DOWN, not by asking the navmesh what is
        ///     nearest.</b> This pass first probed at the target's own height and let
        ///     <c>SamplePosition</c> fall to whatever mesh was closest in 3D. Measured with
        ///     <c>vv_approach</c> on a shelf chest: the floor 2.5m below lost every time to a
        ///     1.4m-high navmesh sliver on a neighbouring structure, which then read as 3.2m
        ///     from the target and was rejected as out of reach — so the one standing spot that
        ///     works (and demonstrably works, for the chest beside it) was never even tested.
        ///     Raycasting down from each probe finds the surface a villager would actually
        ///     stand on, and a tight snap radius keeps the sample on THAT surface.</para>
        /// </summary>
        private static bool TryReachApproach(
            Vector3 target,
            Vector3 pathSource,
            System.Func<Vector3, bool> hullPredicate,
            List<Vector3> pathBuffer,
            float minClearance,
            out Vector3 approach,
            System.Action<string> trace = null)
        {
            approach = target;

            for (var i = 0; i < s_reachOffsets.Length; i++)
            {
                var probe = target + s_reachOffsets[i];
                if (!TryGroundBelow(probe, target.y, out var ground))
                {
                    trace?.Invoke($"reach#{i} {Where(probe)}: nothing to stand on below it");
                    continue;
                }

                if (!TryFindReachableApproach(ground, ReachSnapRadius, out var hit, minClearance))
                {
                    trace?.Invoke($"reach#{i} {Where(ground)}: floor here, but no navmesh within " +
                                  $"{ReachSnapRadius:F1}m of it");
                    continue;
                }

                var reach = Vector3.Distance(hit, target);
                if (reach > InteractReach)
                {
                    trace?.Invoke($"reach#{i} {Where(hit)}: {reach:F1}m away, out of reach ({InteractReach:F1}m)");
                    continue;
                }

                if (!HasLineOfSight(hit, target))
                {
                    trace?.Invoke($"reach#{i} {Where(hit)}: {reach:F1}m away but something solid is in the way"
                                  + SightBlocker(hit, target));
                    continue;
                }

                if (hullPredicate != null && !hullPredicate(hit))
                {
                    trace?.Invoke($"reach#{i} {Where(hit)}: outside the village hull");
                    continue;
                }

                if (!TryFindCompletePath(pathSource, hit, pathBuffer))
                {
                    trace?.Invoke($"reach#{i} {Where(hit)}: no complete path from the villager");
                    continue;
                }

                DebugLog.ThrottledWindow(
                    $"approach-reach:{target.x:F0},{target.y:F0},{target.z:F0}",
                    System.TimeSpan.FromSeconds(ApproachLogWindowSeconds),
                    "Approach", "reach",
                    ("target", target), ("standing", hit),
                    ("dist", Vector3.Distance(hit, target)),
                    ("note", "nothing at its own height; reaching with line of sight"));
                approach = hit;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     How far a villager will reach for something it cannot stand level with. Short of
        ///     the player's own 5m on purpose.
        /// </summary>
        private const float InteractReach = 3f;

        /// <summary>
        ///     How far the reach pass will snap from the floor point it raycast to. Deliberately
        ///     tight: the point of raycasting down was to choose the surface, so a generous snap
        ///     would hand the choice straight back to whatever mesh happens to be nearest.
        /// </summary>
        private const float ReachSnapRadius = 1f;

        /// <summary>Headroom above the target the floor-finding ray starts from.</summary>
        private const float ReachProbeUp = 0.5f;

        private static int s_groundMask;

        /// <summary>
        ///     The surface a villager would stand on at <paramref name="probe" />'s XZ, found by
        ///     raycasting down from just above the target. Only looks as far down as a villager
        ///     can reach up, so a chest on a balcony does not resolve to the ground floor.
        /// </summary>
        private static bool TryGroundBelow(Vector3 probe, float topY, out Vector3 ground)
        {
            ground = probe;
            if (s_groundMask == 0)
                s_groundMask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain");

            var origin = new Vector3(probe.x, topY + ReachProbeUp, probe.z);
            if (!Physics.Raycast(origin, Vector3.down, out var hit,
                    ReachProbeUp + InteractReach, s_groundMask, QueryTriggerInteraction.Ignore))
                return false;

            ground = hit.point;
            return true;
        }

        /// <summary>Villager eye height, where the sight ray starts.</summary>
        private const float EyeHeight = 1.5f;

        /// <summary>
        ///     Heights up the target to aim at. A chest's origin is at its base, which the shelf
        ///     it stands on hides from anyone below; its front face does not.
        /// </summary>
        private static readonly float[] SightAimHeights = { 0.1f, 0.45f, 0.8f };

        /// <summary>How close to the target the ray stops, so the target's own collider is not the blocker.</summary>
        private const float TargetSkin = 0.5f;

        private static int s_sightMask;

        private static string Where(Vector3 p) => $"({p.x:F1},{p.y:F1},{p.z:F1})";

        /// <summary>
        ///     Names what the sight ray hit, for diagnostics only. "Something is in the way" is
        ///     useless advice; "the fermenter is in the way" tells you what to move.
        /// </summary>
        private static string SightBlocker(Vector3 standing, Vector3 target)
        {
            var eye = standing + Vector3.up * EyeHeight;
            var aim = target + Vector3.up * SightAimHeights[0];
            var delta = aim - eye;
            var distance = delta.magnitude;
            if (distance <= TargetSkin) return "";
            if (!Physics.Raycast(eye, delta / distance, out var hit, distance - TargetSkin,
                    s_sightMask, QueryTriggerInteraction.Ignore))
                return "";

            var piece = hit.collider.GetComponentInParent<Piece>();
            var name = piece != null ? piece.m_name : hit.collider.name;
            return $" — {name} at {Where(hit.point)}";
        }

        private static bool HasLineOfSight(Vector3 standing, Vector3 target)
        {
            if (s_sightMask == 0)
                s_sightMask = LayerMask.GetMask("Default", "static_solid", "piece");

            var eye = standing + Vector3.up * EyeHeight;
            foreach (var aimHeight in SightAimHeights)
            {
                var aim = target + Vector3.up * aimHeight;
                var delta = aim - eye;
                var distance = delta.magnitude;
                if (distance <= TargetSkin) return true;

                if (!Physics.Raycast(eye, delta / distance, distance - TargetSkin, s_sightMask,
                        QueryTriggerInteraction.Ignore))
                    return true;
            }

            return false;
        }

        /// <summary>Minimum distance from a NavMesh edge for a villager to stand comfortably.</summary>
        private const float AgentEdgeClearance = 0.45f;

        /// <summary>
        ///     True when <paramref name="point" /> is far enough from the nearest NavMesh edge for
        ///     the villager agent (radius 0.4) to actually occupy it.
        /// </summary>
        private static bool HasEdgeClearance(Vector3 point)
        {
            if (!VillagerAgentType.IsRegistered) return true;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            // No edge found means the sample sits well inside a mesh island — that is the
            // roomy case, not a failure.
            if (!NavMesh.FindClosestEdge(point, out var edge, filter)) return true;
            return edge.distance >= AgentEdgeClearance;
        }

        /// <summary>
        ///     How long the same approach notice stays quiet for one target. Long, because these
        ///     describe a standing property of the geometry, not an event.
        /// </summary>
        private const float ApproachLogWindowSeconds = 300f;

        private const float ApproachProbeRadius = 1.5f;

        private static readonly Vector3[] s_probeOffsets = BuildProbeOffsets();

        /// <summary>
        ///     Ring for the REACH pass. Finer and tighter than
        ///     <see cref="s_probeOffsets" />: every candidate has to end up within
        ///     <see cref="InteractReach" /> of the target, so a 4m ring (5.7m on the diagonal)
        ///     is wasted effort, while 45 degrees of angular resolution at 2m spacing steps
        ///     clean over the metre-wide gap that is the only place to stand.
        /// </summary>
        private static readonly Vector3[] s_reachOffsets = BuildReachOffsets();

        private static Vector3[] BuildReachOffsets()
        {
            var list = new List<Vector3> { Vector3.zero };
            float[] rings = { 1f, 1.75f, 2.5f };
            const int directions = 12;
            foreach (var r in rings)
                for (var i = 0; i < directions; i++)
                {
                    var angle = 360f / directions * i * Mathf.Deg2Rad;
                    list.Add(new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r));
                }

            return list.ToArray();
        }

        private static Vector3[] BuildProbeOffsets()
        {
            var list = new List<Vector3> { Vector3.zero };
            float[] rings = { 2f, 4f };
            foreach (var r in rings)
            {
                list.Add(new Vector3(0f, 0f, r));
                list.Add(new Vector3(r, 0f, 0f));
                list.Add(new Vector3(0f, 0f, -r));
                list.Add(new Vector3(-r, 0f, 0f));
                list.Add(new Vector3(r, 0f, r));
                list.Add(new Vector3(r, 0f, -r));
                list.Add(new Vector3(-r, 0f, -r));
                list.Add(new Vector3(-r, 0f, r));
            }
            return list.ToArray();
        }
    }
}