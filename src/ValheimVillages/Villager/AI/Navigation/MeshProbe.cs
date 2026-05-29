using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Diagnostic probe at a world position. Answers: does slot 31 see a NavMesh
    ///     triangle here? Does Humanoid? Where's the nearest NavMesh edge for each?
    ///     What colliders live at this point and what physics layer are they on?
    ///     Built for triaging "the triangulation stops here" reports: a position
    ///     where Humanoid sees a triangle but slot 31 doesn't is a slot 31 settings
    ///     issue; if neither sees one but a collider exists, it's a layer/geometry
    ///     issue; if nothing's there at all, it's missing geometry entirely.
    /// </summary>
    public static class MeshProbe
    {
        /// <summary>NavMesh.SamplePosition / FindClosestEdge search radius (m).</summary>
        private const float SampleRadius = 5f;

        /// <summary>Physics.OverlapSphere radius for collider report (m).</summary>
        private const float ColliderRadius = 1f;

        [DevCommand("Probe NavMesh + colliders at the player's position (or pass x y z to override)",
            Name = "vv_probe")]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            Vector3 pos;
            bool fromPlayer;
            if (args?.Args != null && args.Args.Length >= 4)
            {
                var inv = CultureInfo.InvariantCulture;
                if (!float.TryParse(args.Args[1], NumberStyles.Float, inv, out var x)
                    || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var y)
                    || !float.TryParse(args.Args[3], NumberStyles.Float, inv, out var z))
                {
                    Console.instance?.Print("Usage: vv_probe (no args = player pos) | vv_probe <x> <y> <z>");
                    return;
                }

                pos = new Vector3(x, y, z);
                fromPlayer = false;
            }
            else
            {
                var p = Player.m_localPlayer;
                if (p == null || p.transform == null)
                {
                    Console.instance?.Print("No local player; pass coords instead: vv_probe <x> <y> <z>");
                    return;
                }

                pos = p.transform.position;
                fromPlayer = true;
            }

            var sb = new StringBuilder();
            sb.AppendLine(
                $"[vv_probe] pos=({pos.x:F2}, {pos.y:F2}, {pos.z:F2}) " +
                $"source={(fromPlayer ? "player" : "args")}");

            sb.AppendLine(
                $"--- agent slot 31 (agentTypeID={VillagerAgentType.UnityAgentTypeID}, registered={VillagerAgentType.IsRegistered}) ---");
            ReportNavMeshSample(sb, pos, VillagerAgentType.UnityAgentTypeID);

            var humanoidId = VillagerAgentType.ResolveValheimHumanoidAgentTypeID();
            sb.AppendLine($"--- Humanoid (agentTypeID={humanoidId}) ---");
            ReportNavMeshSample(sb, pos, humanoidId);

            sb.AppendLine($"--- colliders within {ColliderRadius:F1}m ---");
            ReportColliders(sb, pos);

            sb.AppendLine("--- capsule-hit colliders (would trigger rej_blocked) ---");
            ReportCapsuleHits(sb, pos);

            sb.AppendLine("--- RegionBuilder.CachedTriangles within 2m ---");
            ReportCachedTriangles(sb, pos);

            sb.AppendLine("--- NavMesh bake sources (within 5m) ---");
            ReportBakeSources(sb, pos);

            sb.AppendLine("--- raw extracted triangles (within 5m, pre-filter) ---");
            ReportRawExtracted(sb, pos);

            sb.AppendLine("--- filter trace: upward-facing triangles within 3m ---");
            ReportFilterTrace(sb, pos);

            sb.AppendLine("--- RegionGraph at this point ---");
            ReportRegionGraph(sb, pos);

            sb.AppendLine("--- Pass 1 flood reachability at this cell ---");
            ReportFloodReachability(sb, pos);

            var output = sb.ToString();
            Console.instance?.Print(output);
            Plugin.Log?.LogInfo(output);
        }

        private static void ReportNavMeshSample(StringBuilder sb, Vector3 pos, int agentTypeID)
        {
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            if (NavMesh.SamplePosition(pos, out var hit, SampleRadius, filter))
            {
                var dist = Vector3.Distance(hit.position, pos);
                sb.AppendLine(
                    $"  SamplePosition: HIT at ({hit.position.x:F2}, {hit.position.y:F2}, {hit.position.z:F2}) " +
                    $"dist={dist:F2}m");
            }
            else
            {
                sb.AppendLine($"  SamplePosition: MISS (no triangle within {SampleRadius:F1}m)");
            }

            if (NavMesh.FindClosestEdge(pos, out var edge, filter))
            {
                var edgeDist = Vector3.Distance(edge.position, pos);
                sb.AppendLine(
                    $"  FindClosestEdge: at ({edge.position.x:F2}, {edge.position.y:F2}, {edge.position.z:F2}) " +
                    $"dist={edgeDist:F2}m");
            }
            else
            {
                sb.AppendLine("  FindClosestEdge: no edge found");
            }
        }

        private static void ReportColliders(StringBuilder sb, Vector3 pos)
        {
            var cols = Physics.OverlapSphere(pos, ColliderRadius, ~0, QueryTriggerInteraction.Ignore);
            if (cols == null || cols.Length == 0)
            {
                sb.AppendLine("  (none — this point has NO collider, no geometry to bake)");
                return;
            }

            // Sort by distance from probe point for readability.
            Array.Sort(cols, (a, b) =>
            {
                var da = (a.ClosestPoint(pos) - pos).sqrMagnitude;
                var db = (b.ClosestPoint(pos) - pos).sqrMagnitude;
                return da.CompareTo(db);
            });

            const int maxShown = 8;
            var shown = Mathf.Min(cols.Length, maxShown);
            for (var i = 0; i < shown; i++)
            {
                var c = cols[i];
                var layerName = LayerMask.LayerToName(c.gameObject.layer);
                if (string.IsNullOrEmpty(layerName)) layerName = "(unnamed)";
                var closest = c.ClosestPoint(pos);
                var dist = Vector3.Distance(closest, pos);
                sb.AppendLine(
                    $"  [layer={c.gameObject.layer}:{layerName}] {c.gameObject.name} " +
                    $"({c.GetType().Name}) dist={dist:F2}m");
            }

            if (cols.Length > maxShown)
                sb.AppendLine($"  ... and {cols.Length - maxShown} more");
        }

        /// <summary>
        ///     Run the same capsule check the terrain pass uses and list every
        ///     collider it hits. The capsule has the agent's villager-sized body
        ///     at the probe position; any hit means rej_blocked would fire for a
        ///     terrain tri at this XZ. Shows GameObject name, prefab name (if a
        ///     Piece component exists), layer, and collider bounds Y range so we
        ///     can see how high above the ground the piece extends.
        /// </summary>
        private static void ReportCapsuleHits(StringBuilder sb, Vector3 pos)
        {
            const float capsuleRadius = 0.3f;
            const float capsuleHeight = 1.4f;
            const float capsuleLift = 0.25f;
            var blockMask = LayerMask.GetMask("Default", "static_solid", "piece");
            var p0 = pos + Vector3.up * (capsuleLift + capsuleRadius);
            var p1 = pos + Vector3.up * (capsuleLift + capsuleHeight - capsuleRadius);
            var hits = Physics.OverlapCapsule(p0, p1, capsuleRadius, blockMask, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
            {
                sb.AppendLine("  (capsule clear — no rej_blocked at this position)");
                return;
            }

            sb.AppendLine(
                $"  capsule p0=({p0.x:F2},{p0.y:F2},{p0.z:F2}) " +
                $"p1=({p1.x:F2},{p1.y:F2},{p1.z:F2}) r={capsuleRadius:F2}");
            for (var i = 0; i < hits.Length; i++)
            {
                var c = hits[i];
                if (c == null) continue;
                var layerName = LayerMask.LayerToName(c.gameObject.layer);
                if (string.IsNullOrEmpty(layerName)) layerName = "(unnamed)";
                var prefabName = "(no Piece)";
                var piece = c.GetComponentInParent<Piece>();
                if (piece != null) prefabName = piece.gameObject.name;
                var b = c.bounds;
                sb.AppendLine(
                    $"  HIT [{layerName}] go={c.gameObject.name} prefab={prefabName} " +
                    $"type={c.GetType().Name} " +
                    $"bounds_y=[{b.min.y:F2}..{b.max.y:F2}] " +
                    $"yAbovePos={b.max.y - pos.y:F2}m");
            }
        }

        private static void ReportCachedTriangles(StringBuilder sb, Vector3 pos)
        {
            var cached = RegionBuilder.CachedTriangles;
            if (cached == null || cached.Count == 0)
            {
                sb.AppendLine(
                    "  CachedTriangles is empty — RegionBuilder has not run, or last run produced 0 triangles");
                return;
            }

            sb.AppendLine($"  (total in cache: {cached.Count})");

            // Closest cached triangle (any distance) — tells us how far away the
            // pipeline thinks the nearest walkable surface is, even if it's
            // well beyond the 2m radius.
            var closestDist = float.MaxValue;
            var closestCentroid = Vector3.zero;
            var closestRegion = "";
            var withinY = 0; // triangles at the same altitude (±2m) within 10m XZ
            var within2m = 0;
            var within5m = 0;

            foreach (var t in cached)
            {
                var centroid = (t.V0 + t.V1 + t.V2) / 3f;
                var d = Vector3.Distance(centroid, pos);
                if (d < closestDist)
                {
                    closestDist = d;
                    closestCentroid = centroid;
                    closestRegion = t.RegionId ?? "(none)";
                }

                if (d <= 2f) within2m++;
                if (d <= 5f) within5m++;

                // Same-altitude XZ count
                var dy = Mathf.Abs(centroid.y - pos.y);
                if (dy <= 2f)
                {
                    var dxz = Mathf.Sqrt(
                        (centroid.x - pos.x) * (centroid.x - pos.x) +
                        (centroid.z - pos.z) * (centroid.z - pos.z));
                    if (dxz <= 10f) withinY++;
                }
            }

            sb.AppendLine(
                $"  closest cached: ({closestCentroid.x:F2}, {closestCentroid.y:F2}, {closestCentroid.z:F2}) dist={closestDist:F2}m region={closestRegion}");
            sb.AppendLine(
                $"  within 2m: {within2m}    within 5m: {within5m}    same-altitude (±2m Y) within 10m XZ: {withinY}");

            if (within2m == 0)
                sb.AppendLine(
                    "  → RegionBuilder filter rejected this surface (see [Region] triangulation rej_* counters)");
        }

        private static void ReportBakeSources(StringBuilder sb, Vector3 pos)
        {
            var sources = NavMeshBakeManager.LastSources;
            if (sources == null || sources.Count == 0)
            {
                sb.AppendLine("  No baked sources cached");
                return;
            }

            const float radius = 5f;
            int near = 0, box = 0, mesh = 0, sphere = 0, capsule = 0, modBox = 0, other = 0;
            var boxNearAndY = 0;
            var yTol = 2f;

            foreach (var s in sources)
            {
                var srcPos = s.transform.MultiplyPoint3x4(Vector3.zero);
                var d = Vector3.Distance(srcPos, pos);
                if (d > radius) continue;
                near++;

                switch (s.shape)
                {
                    case NavMeshBuildSourceShape.Box:
                        box++;
                        if (Mathf.Abs(srcPos.y - pos.y) <= yTol) boxNearAndY++;
                        break;
                    case NavMeshBuildSourceShape.Mesh: mesh++; break;
                    case NavMeshBuildSourceShape.Sphere: sphere++; break;
                    case NavMeshBuildSourceShape.Capsule: capsule++; break;
                    case NavMeshBuildSourceShape.ModifierBox: modBox++; break;
                    default: other++; break;
                }
            }

            sb.AppendLine(
                $"  {near} source(s) within {radius:F0}m " +
                $"(Mesh={mesh} Box={box} Sphere={sphere} Capsule={capsule} ModBox={modBox} Other={other})");
            sb.AppendLine($"  Box sources at this altitude (±{yTol:F0}m Y): {boxNearAndY}");
        }

        private static void ReportRawExtracted(StringBuilder sb, Vector3 pos)
        {
            var (verts, idx, _) = NavMeshBakeManager.ExtractBakedTriangles();
            if (verts == null || verts.Length == 0)
            {
                sb.AppendLine("  Extractor produced 0 vertices");
                return;
            }

            var triCount = idx.Length / 3;
            const float radius = 5f;
            var near = 0;
            var nearAndUpward = 0;
            var nearAndY = 0;
            var yTol = 2f;

            for (var t = 0; t < triCount; t++)
            {
                var v0 = verts[idx[t * 3]];
                var v1 = verts[idx[t * 3 + 1]];
                var v2 = verts[idx[t * 3 + 2]];
                var c = (v0 + v1 + v2) / 3f;
                var d = Vector3.Distance(c, pos);
                if (d > radius) continue;
                near++;

                var n = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                if (n.y >= 0.5f) nearAndUpward++;

                if (Mathf.Abs(c.y - pos.y) <= yTol) nearAndY++;
            }

            sb.AppendLine(
                $"  total extracted: {triCount}; within {radius:F0}m: {near} " +
                $"(upward-facing: {nearAndUpward}, at this altitude ±{yTol:F0}m: {nearAndY})");
        }

        private static void ReportFilterTrace(StringBuilder sb, Vector3 pos)
        {
            var (verts, idx, _) = NavMeshBakeManager.ExtractBakedTriangles();
            if (verts == null || verts.Length == 0)
            {
                sb.AppendLine("  No extracted triangles");
                return;
            }

            // Use a generous box for the bounds check (real bounds live in
            // RegionPartitionHandler; this approximates).
            float bMinX = pos.x - 40f, bMaxX = pos.x + 40f;
            float bMinZ = pos.z - 40f, bMaxZ = pos.z + 40f;

            var beds = VillagerAIManager.GetAllBedPositions()
                       ?? new List<Vector3>();
            const float bedRadius = 30f;
            const float bedR2 = bedRadius * bedRadius;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            var triCount = idx.Length / 3;
            const float traceRadius = 3f;

            int total = 0, passBounds = 0, passDist = 0, pass05 = 0, pass10 = 0, pass20 = 0;
            int passSteep = 0, passBlocked = 0;
            var shown = 0;
            const int maxShown = 8;

            // Match the RegionBuilder terrain-pass constants so the trace
            // reflects what kind=Terrain would actually accept.
            const float minTerrainNormalY = 0.891007f; // cos(27°)
            const float capsuleRadius = 0.3f;
            const float capsuleHeight = 1.4f;
            const float capsuleLift = 0.25f;
            var blockMask = LayerMask.GetMask("Default", "static_solid", "piece");

            for (var t = 0; t < triCount; t++)
            {
                var v0 = verts[idx[t * 3]];
                var v1 = verts[idx[t * 3 + 1]];
                var v2 = verts[idx[t * 3 + 2]];
                var c = (v0 + v1 + v2) / 3f;
                if (Vector3.Distance(c, pos) > traceRadius) continue;

                var n = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                if (n.y < 0.5f) continue;
                total++;

                var bnds = c.x >= bMinX && c.x <= bMaxX && c.z >= bMinZ && c.z <= bMaxZ;
                if (bnds) passBounds++;

                var near = false;
                foreach (var bed in beds)
                {
                    float dx = c.x - bed.x, dz = c.z - bed.z;
                    if (dx * dx + dz * dz <= bedR2)
                    {
                        near = true;
                        break;
                    }
                }

                if (near) passDist++;

                var h05 = NavMesh.SamplePosition(c, out var hit05, 0.5f, filter)
                          && Vector3.Distance(hit05.position, c) <= 0.5f;
                var h10 = NavMesh.SamplePosition(c, out var hit10, 1.0f, filter)
                          && Vector3.Distance(hit10.position, c) <= 1.0f;
                var h20 = NavMesh.SamplePosition(c, out var hit20, 2.0f, filter)
                          && Vector3.Distance(hit20.position, c) <= 2.0f;
                if (h05) pass05++;
                if (h10) pass10++;
                if (h20) pass20++;

                // Terrain-only checks (still reported on every probe so we
                // see what would happen if the spot were treated as terrain).
                var steepOk = n.y >= minTerrainNormalY;
                if (steepOk) passSteep++;

                var capP0 = c + Vector3.up * (capsuleLift + capsuleRadius);
                var capP1 = c + Vector3.up * (capsuleLift + capsuleHeight - capsuleRadius);
                var blocked = Physics.CheckCapsule(capP0, capP1, capsuleRadius, blockMask);
                var capsuleOk = !blocked;
                if (capsuleOk) passBlocked++;

                if (shown < maxShown)
                {
                    var h05str = h05 ? $"HIT@{Vector3.Distance(hit05.position, c):F2}" : "miss";
                    var h10str = h10 ? $"HIT@{Vector3.Distance(hit10.position, c):F2}" : "miss";
                    var h20str = h20 ? $"HIT@{Vector3.Distance(hit20.position, c):F2}" : "miss";
                    sb.AppendLine(
                        $"  tri@({c.x:F2},{c.y:F2},{c.z:F2}) " +
                        $"bounds={(bnds ? "Y" : "n")} dist={(near ? "Y" : "n")} " +
                        $"agent[0.5]={h05str} [1.0]={h10str} [2.0]={h20str} " +
                        $"steep={(steepOk ? "Y" : "n")} capsule={(capsuleOk ? "Y" : "BLOCKED")}");
                    shown++;
                }
            }

            if (total == 0)
            {
                sb.AppendLine($"  No upward-facing triangles within {traceRadius:F1}m");
                return;
            }

            sb.AppendLine(
                $"  totals (of {total}): " +
                $"bounds={passBounds} dist={passDist} agent[0.5]={pass05} agent[1.0]={pass10} agent[2.0]={pass20} " +
                $"steep_ok={passSteep} capsule_ok={passBlocked}");
            sb.AppendLine($"  beds in scene: {beds.Count}");
        }

        private static void ReportRegionGraph(StringBuilder sb, Vector3 pos)
        {
            var graph = RegionGraph.GetNearest(pos);
            if (graph == null)
            {
                sb.AppendLine("  No RegionGraph available (none built/restored yet)");
                return;
            }

            var resolved = graph.PointToRegionId(pos);
            sb.AppendLine($"  PointToRegionId: {resolved ?? "(unresolved)"}");

            // For the resolved region, count cached triangles + sum area + count
            // links. Helps verify whether a "visually isolated single tri" really
            // is a single tri or is part of a larger merged region whose other
            // triangles sit elsewhere (e.g., merged via vertex coincidence in
            // the coplanar pass).
            if (!string.IsNullOrEmpty(resolved))
            {
                var triCount = 0;
                var totalArea = 0f;
                float yMin = float.MaxValue, yMax = float.MinValue;
                var cachedTris = RegionBuilder.CachedTriangles;
                if (cachedTris != null)
                    foreach (var ct in cachedTris)
                    {
                        if (ct.RegionId != resolved) continue;
                        triCount++;
                        totalArea += Vector3.Cross(ct.V1 - ct.V0, ct.V2 - ct.V0).magnitude * 0.5f;
                        var lo = Mathf.Min(ct.V0.y, Mathf.Min(ct.V1.y, ct.V2.y));
                        var hi = Mathf.Max(ct.V0.y, Mathf.Max(ct.V1.y, ct.V2.y));
                        if (lo < yMin) yMin = lo;
                        if (hi > yMax) yMax = hi;
                    }

                var linkCount = 0;
                var linksFrom = graph.GetLinksFromRegion(resolved);
                if (linksFrom != null) linkCount = linksFrom.Count;
                var kind = graph.GetRegionKind(resolved);
                sb.AppendLine(
                    $"  Resolved region {resolved}: kind={kind}, tris={triCount}, " +
                    $"area={totalArea:F2}m², Y=[{(triCount > 0 ? yMin.ToString("F2") : "?")}, " +
                    $"{(triCount > 0 ? yMax.ToString("F2") : "?")}], links={linkCount}");

                if (linksFrom != null && linkCount > 0)
                    foreach (var lnk in linksFrom)
                    {
                        var dest = lnk.ToRegionId;
                        var destKind = graph.GetRegionKind(dest);
                        var destAlive = graph.IsValidRegion(dest) ? "alive" : "DROPPED";
                        sb.AppendLine(
                            $"    link -> {dest} ({destKind}, {lnk.LinkType}, {destAlive}) " +
                            $"at ({lnk.PositionEnd.x:F1}, {lnk.PositionEnd.y:F1}, {lnk.PositionEnd.z:F1})");
                    }
            }

            var closestDist = float.MaxValue;
            var closestCenter = Vector3.zero;
            var closestId = "";
            var allCenters = graph.Diagnostics.GetAllRegionCenters();
            var idx = 0;
            foreach (var center in allCenters)
            {
                var d = Vector3.Distance(center, pos);
                if (d < closestDist)
                {
                    closestDist = d;
                    closestCenter = center;
                    closestId = $"#{idx}";
                }

                idx++;
            }

            if (allCenters.Count == 0)
                sb.AppendLine("  No centroids in graph");
            else
                sb.AppendLine(
                    $"  Closest centroid {closestId}: " +
                    $"({closestCenter.x:F2}, {closestCenter.y:F2}, {closestCenter.z:F2}) " +
                    $"dist3d={closestDist:F2}m " +
                    $"(graph has {graph.RegionCount} regions, {graph.LinkCount} links)");
        }

        private static void ReportFloodReachability(StringBuilder sb, Vector3 pos)
        {
            if (!RubberBandPrune.HasSnapshot ||
                RubberBandPrune.LastOutsideCells == null ||
                RubberBandPrune.LastXzMaxY == null ||
                RubberBandPrune.LastCell <= 0f)
            {
                sb.AppendLine("  No RubberBandPrune snapshot — run vv_repartition first");
                return;
            }

            var cell = RubberBandPrune.LastCell;
            var mask = RubberBandPrune.LastPieceMask;
            var gx = Mathf.FloorToInt(pos.x / cell);
            var gz = Mathf.FloorToInt(pos.z / cell);
            var selfKey = RubberBandPrune.DiagnoseXzKey(gx, gz);
            var selfOutside = RubberBandPrune.LastOutsideCells.Contains(selfKey);
            var selfPopulated = RubberBandPrune.LastXzMaxY.ContainsKey(selfKey);
            var selfY = RubberBandPrune.DiagnoseCellY(gx, gz);
            sb.AppendLine(
                $"  cell gx={gx} gz={gz}  Y={selfY:F2}  " +
                $"populated={(selfPopulated ? "yes" : "no")}  " +
                $"in_outsideCells={(selfOutside ? "YES" : "NO")}");
            sb.AppendLine(
                $"  bake bounds gx=[{RubberBandPrune.LastGxMin}..{RubberBandPrune.LastGxMax}] " +
                $"gz=[{RubberBandPrune.LastGzMin}..{RubberBandPrune.LastGzMax}] " +
                $"cell={cell:F2}m  pieceMask=0x{mask:X}");

            string[] cardLabels = { "E ", "W ", "N ", "S " };
            int[] cardDx = { 1, -1, 0, 0 };
            int[] cardDz = { 0, 0, 1, -1 };
            string[] diagLabels = { "NE", "NW", "SE", "SW" };
            int[] diagDx = { 1, -1, 1, -1 };
            int[] diagDz = { 1, 1, -1, -1 };

            sb.AppendLine("  4-connected neighbors (used by Pass 1):");
            for (var i = 0; i < 4; i++)
                ReportNeighbor(sb, cardLabels[i], gx, gz, cardDx[i], cardDz[i], cell, mask);
            sb.AppendLine("  8-connected diagonal neighbors (NOT used by Pass 1 today):");
            for (var i = 0; i < 4; i++)
                ReportNeighbor(sb, diagLabels[i], gx, gz, diagDx[i], diagDz[i], cell, mask);
        }

        private static void ReportNeighbor(StringBuilder sb, string label,
            int gx, int gz, int dx, int dz, float cell, int mask)
        {
            int ngx = gx + dx, ngz = gz + dz;
            var nKey = RubberBandPrune.DiagnoseXzKey(ngx, ngz);
            var nOutside = RubberBandPrune.LastOutsideCells.Contains(nKey);
            var nPopulated = RubberBandPrune.LastXzMaxY.ContainsKey(nKey);
            var yA = RubberBandPrune.DiagnoseCellY(gx, gz);
            var yB = RubberBandPrune.DiagnoseCellY(ngx, ngz);
            var blocked = RubberBandPrune.Diagnose(gx, gz, ngx, ngz,
                yA, yB, cell, mask, out var hits);
            sb.AppendLine(
                $"    {label} gx={ngx} gz={ngz}  Y={yB:F2}  " +
                $"populated={(nPopulated ? "y" : "n")}  outside={(nOutside ? "YES" : "no ")}  " +
                $"WallBlocks={(blocked ? "TRUE" : "false")}" +
                (blocked ? $"  hits=[{hits}]" : ""));
        }
    }
}