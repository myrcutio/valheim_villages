using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Geometric patrol-route builder.
    ///
    ///     <para>Turns the region graph's perimeter boundary cells — the outer
    ///     flood frontier, each a world center plus a real outward normal (see
    ///     <c>RubberBandPrune</c>) — into an ordered, simplified, inset patrol
    ///     loop. The NavMeshAgent routes between consecutive vertices itself
    ///     (over stairs, through doors), so this stage is pure geometry: no
    ///     pathfinding, no link insertion, no navmesh validation. Whatever the
    ///     agent can't reach, the patrol state machine skips when its
    ///     <c>NavTo</c> returns false.</para>
    ///
    ///     <para>Pipeline:
    ///     <list type="number">
    ///       <item>dedup — collapse near-duplicate frontier cells;</item>
    ///       <item>tangent walk — order the ring by following each cell's wall
    ///         tangent (perpendicular to its outward normal), so the loop hugs
    ///         the perimeter and turns at corners. This MAXIMISES enclosed area
    ///         rather than minimising perimeter length — a patrol wants to follow
    ///         the outer wall, not shortcut across the courtyard;</item>
    ///       <item>normal decimation — drop a vertex whose outward normal barely
    ///         differs from the last kept one, collapsing each straight wall run
    ///         to its corners;</item>
    ///       <item>inward inset — shift each vertex along its (negated) outward
    ///         normal so the patroller walks just inside the wall.</item>
    ///     </list></para>
    ///
    ///     <para>Correctness depends entirely on the frontier being the OUTER
    ///     perimeter only and the outward normals being real — both produced
    ///     upstream by the perimeter flood. Region-edge boundaries (which include
    ///     interior obstacle rings) or defaulted normals make the tangent walk
    ///     weave and self-cross.</para>
    /// </summary>
    public static class PatrolRouteBuilder
    {
        /// <summary>Collapse boundary cells within this XZ radius (m) to one.</summary>
        private const float DedupXZ = 1.5f;

        /// <summary>Tangent-walk: reject candidates more than this far behind the tangent (cos).</summary>
        private const float AheadCutoff = -0.3f;

        /// <summary>Tangent-walk: how strongly to prefer forward (tangent-aligned) over near.</summary>
        private const float ForwardWeight = 1.5f;

        /// <summary>Drop a vertex whose normal is within this angle (deg) of the last kept vertex's.</summary>
        private const float NormalDecimateDeg = 25f;

        /// <summary>Distance (m) to shift each vertex inward along its (negated) outward normal.</summary>
        private const float InsetDistance = 0.7f;

        /// <summary>Max inward shift (m) when relocating a vertex off un-walkable wall geometry.</summary>
        private const float MaxInsetDistance = 3.0f;

        /// <summary>Step (m) to escalate the inward shift while searching for reachable ground.</summary>
        private const float InsetStep = 0.5f;

        /// <summary>NavMesh.SamplePosition search radius (m) when snapping a candidate vertex.</summary>
        private const float SampleRadius = 1.0f;

        private struct Node
        {
            public Vector3 Pos;
            public Vector3 OutDir;
        }

        /// <summary>
        ///     Build an ordered patrol loop from region-graph boundary cells.
        ///     Returns fewer than 3 points only when the input can't form a loop.
        /// </summary>
        public static List<Vector3> Build(
            List<(string cellId, Vector3 worldCenter, Vector3 outwardDir)> boundaryCells,
            Vector3? navAnchor = null)
        {
            var result = new List<Vector3>();
            if (boundaryCells == null || boundaryCells.Count < 3) return result;

            var nodes = Dedup(boundaryCells);
            if (nodes.Count < 3) return result;

            var order = TangentWalk(nodes);
            var ring = new List<Node>(order.Count);
            foreach (var idx in order) ring.Add(nodes[idx]);

            var simplified = NormalDecimate(ring, NormalDecimateDeg);
            if (simplified.Count < 3) simplified = ring;

            // Final stage: resolve each vertex to reachable walkable ground. With a
            // nav anchor (the bed) and a live agent navmesh, escalate the inward
            // inset until the vertex snaps onto the navmesh AND is reachable from
            // the anchor — dropping vertices stuck on un-walkable wall junctions
            // (the failure that parked patrols in NeedsHelp). Without an anchor, or
            // before the navmesh is baked, fall back to the pure-geometry inset.
            if (navAnchor.HasValue &&
                TrySampleAnchor(navAnchor.Value, out var anchor, out var filter))
            {
                var dropped = 0;
                foreach (var n in simplified)
                    if (TryResolveReachable(n, anchor, filter, out var wp))
                        result.Add(wp);
                    else
                        dropped++;
                if (dropped > 0)
                    Plugin.Log?.LogWarning(
                        $"[PatrolRoute] Dropped {dropped} vertex/vertices with no reachable " +
                        $"approach within {MaxInsetDistance:F1}m inset (wall-blocked geometry)");
            }
            else
            {
                foreach (var n in simplified)
                    result.Add(Inset(n, InsetDistance));
            }

            return result;
        }

        #region Pipeline stages

        /// <summary>Collapse cells within <see cref="DedupXZ" /> of an already-kept cell.</summary>
        private static List<Node> Dedup(
            List<(string cellId, Vector3 worldCenter, Vector3 outwardDir)> cells)
        {
            var kept = new List<Node>();
            var rSq = DedupXZ * DedupXZ;
            foreach (var (_, center, outDir) in cells)
            {
                var dup = false;
                foreach (var k in kept)
                    if (DistXZSq(center, k.Pos) < rSq)
                    {
                        dup = true;
                        break;
                    }

                if (!dup)
                    kept.Add(new Node { Pos = center, OutDir = outDir });
            }

            return kept;
        }

        /// <summary>
        ///     Order the ring by following each cell's wall tangent. The tangent
        ///     is the outward normal rotated +90° in XZ (interior on the left, a
        ///     consistent CCW winding). From the current cell, step to the
        ///     unvisited cell that is both near and "ahead" along the tangent;
        ///     when nothing lies ahead within reach (a corner or a gap), fall back
        ///     to the nearest unvisited cell. Starts at the most negative-X cell
        ///     for determinism.
        /// </summary>
        private static List<int> TangentWalk(List<Node> nodes)
        {
            var n = nodes.Count;
            var order = new List<int>(n);
            var used = new bool[n];

            var start = 0;
            for (var i = 1; i < n; i++)
                if (nodes[i].Pos.x < nodes[start].Pos.x)
                    start = i;

            order.Add(start);
            used[start] = true;
            var cur = start;
            for (var step = 1; step < n; step++)
            {
                var c = nodes[cur];
                // Tangent: outward normal (nx,nz) rotated +90° -> (-nz, nx).
                float tx = -c.OutDir.z, tz = c.OutDir.x;

                var best = -1;
                var bestKey = float.MaxValue;
                var fallback = -1;
                var fallbackSq = float.MaxValue;
                for (var j = 0; j < n; j++)
                {
                    if (used[j]) continue;
                    var dx = nodes[j].Pos.x - c.Pos.x;
                    var dz = nodes[j].Pos.z - c.Pos.z;
                    var dSq = dx * dx + dz * dz;
                    if (dSq < fallbackSq)
                    {
                        fallbackSq = dSq;
                        fallback = j;
                    }

                    var dist = Mathf.Sqrt(dSq);
                    if (dist < 1e-4f) continue;
                    var ahead = (dx * tx + dz * tz) / dist;
                    if (ahead < AheadCutoff) continue;
                    var key = dist - ForwardWeight * ahead;
                    if (key < bestKey)
                    {
                        bestKey = key;
                        best = j;
                    }
                }

                var next = best >= 0 ? best : fallback;
                if (next < 0) break;
                order.Add(next);
                used[next] = true;
                cur = next;
            }

            return order;
        }

        /// <summary>
        ///     Walk the ordered ring and keep a vertex only when its outward
        ///     normal turns at least <paramref name="minTurnDeg" /> from the last
        ///     kept vertex's normal. Straight wall runs (near-constant normal)
        ///     collapse to their corners.
        /// </summary>
        private static List<Node> NormalDecimate(List<Node> ring, float minTurnDeg)
        {
            if (ring.Count < 3) return new List<Node>(ring);

            var cosThresh = Mathf.Cos(minTurnDeg * Mathf.Deg2Rad);
            var kept = new List<Node> { ring[0] };
            var lastDir = Flatten(ring[0].OutDir);
            for (var i = 1; i < ring.Count; i++)
            {
                var dir = Flatten(ring[i].OutDir);
                if (Vector2.Dot(dir, lastDir) <= cosThresh)
                {
                    kept.Add(ring[i]);
                    lastDir = dir;
                }
            }

            return kept;
        }

        /// <summary>
        ///     Shift a vertex inward along its (negated, XZ-flattened) outward
        ///     normal. Y preserved; the consumer (<c>NavTo</c>) re-snaps onto the
        ///     navmesh.
        /// </summary>
        private static Vector3 Inset(Node n, float distance)
        {
            var dir = n.OutDir;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return n.Pos;
            dir.Normalize();
            return n.Pos - dir * distance;
        }

        /// <summary>
        ///     Build the villager-agent query filter and snap the anchor (bed) onto
        ///     the agent navmesh. Returns false when the agent isn't registered or
        ///     the navmesh isn't baked yet — callers then skip reachability checks.
        /// </summary>
        private static bool TrySampleAnchor(
            Vector3 anchor, out Vector3 onMesh, out NavMeshQueryFilter filter)
        {
            filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            if (VillagerAgentType.IsRegistered &&
                NavMesh.SamplePosition(anchor, out var hit, 3f, filter))
            {
                onMesh = hit.position;
                return true;
            }

            onMesh = anchor;
            return false;
        }

        /// <summary>
        ///     Find the smallest inward inset (from <see cref="InsetDistance" /> up
        ///     to <see cref="MaxInsetDistance" />) whose snapped position is on the
        ///     agent navmesh AND reachable from <paramref name="anchor" /> by a
        ///     complete path. The reachability check is what rejects on-mesh-but-
        ///     disconnected slivers at wall junctions (where SamplePosition alone
        ///     would falsely succeed). Returns false if nothing qualifies.
        /// </summary>
        private static bool TryResolveReachable(
            Node n, Vector3 anchor, NavMeshQueryFilter filter, out Vector3 result)
        {
            var path = new NavMeshPath();
            for (var d = InsetDistance; d <= MaxInsetDistance + 1e-3f; d += InsetStep)
            {
                var cand = Inset(n, d);
                if (!NavMesh.SamplePosition(cand, out var hit, SampleRadius, filter)) continue;
                if (NavMesh.CalculatePath(anchor, hit.position, filter, path) &&
                    path.status == NavMeshPathStatus.PathComplete)
                {
                    result = hit.position;
                    return true;
                }
            }

            result = default;
            return false;
        }

        #endregion

        #region Geometry helpers

        private static Vector2 Flatten(Vector3 v)
        {
            var f = new Vector2(v.x, v.z);
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector2.zero;
        }

        private static float DistXZSq(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        #endregion
    }
}
