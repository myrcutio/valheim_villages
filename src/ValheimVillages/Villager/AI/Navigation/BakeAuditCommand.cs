using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Diagnostic that cross-references runtime physics colliders at a
    ///     world position against what's in NavMeshBakeManager's source list
    ///     for the most recent bake. Answers the question: "does the bake
    ///     know about the colliders that are physically present here?"
    ///     Built for triaging persistent path stalls where NavMesh.CalculatePath
    ///     returns PathComplete but the villager can't physically traverse
    ///     the path — strong signal of a bake-vs-runtime mismatch (character
    ///     in the line, sub-voxel piece, phantom over-blocker, layer-mask
    ///     gap, or piece spawned/activated after the snapshot).
    /// </summary>
    public static class BakeAuditCommand
    {
        /// <summary>Search radius for runtime physics overlap and source bounds checks (m).</summary>
        private const float Radius = 1.5f;

        /// <summary>Wider radius for character-layer overlap (we want to know about NPCs across a wider area).</summary>
        private const float CharacterRadius = 3f;

        /// <summary>
        ///     Appends the bake-coverage audit at <paramref name="pos" />: the agent-body waist
        ///     probe, runtime colliders, enumerated bake sources, and the collider-to-source
        ///     cross-reference that attributes phantom blockers (door/bed/outside_cell).
        ///     <para>
        ///         Formerly the standalone <c>vv_bake_audit</c>. It shared its first two sections
        ///         with <c>vv_probe</c> (same slot-31/Humanoid sample, same collider listing) and
        ///         answers the other half of the same question, so it is now <c>vv_probe bake</c>.
        ///     </para>
        /// </summary>
        internal static void AppendBakeAudit(StringBuilder sb, Vector3 pos)
        {
            sb.AppendLine($"--- bake audit (radius {Radius:F1}m) ---");
            sb.AppendLine($"  agent-body waist probe: IsAgentBodyBlocked(pos)={NavMeshBakeManager.IsAgentBodyBlocked(pos)}");
            ReportNavMeshSample(sb, pos);
            ReportRuntimePhysics(sb, pos, out var runtimeHits);
            ReportBakeSources(sb, pos, out var sourceMatches, out var phantomMatches);
            ReportCrossReference(sb, runtimeHits, sourceMatches, phantomMatches);
            ReportPhantomCoverage(sb, pos, phantomMatches);
            ReportCharacterOverlap(sb, pos);
        }


        [DevCommand("Compare a NavMesh path on the villager (slot 31) bake vs Valheim's Humanoid agent. " +
                    "Usage: vv_path <fromX> <fromZ> <toX> <toZ> [draw] | vv_path off",
            Name = "vv_path")]
        public static void PathCompare(Terminal.ConsoleEventArgs args)
        {
            // Absorbed vv_drawpath: it took the same four coords and repeated this
            // command's endpoint sampling and NavMesh.CalculatePath verbatim, differing
            // only in drawing the result. `draw` now adds the magenta overlay here.
            if (args?.Args != null && args.Args.Length >= 2 &&
                args.Args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                PathDebugRenderer.ClearRawPathOverlay();
                Console.instance?.Print("[vv_path] overlay cleared");
                return;
            }

            var inv = CultureInfo.InvariantCulture;
            if (args?.Args == null || args.Args.Length < 5
                || !float.TryParse(args.Args[1], NumberStyles.Float, inv, out var fx)
                || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var fz)
                || !float.TryParse(args.Args[3], NumberStyles.Float, inv, out var tx)
                || !float.TryParse(args.Args[4], NumberStyles.Float, inv, out var tz))
            {
                Console.instance?.Print("Usage: vv_path <fromX> <fromZ> <toX> <toZ> [draw] | vv_path off");
                return;
            }

            var draw = args.Args.Length > 5 &&
                       args.Args[5].Equals("draw", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine($"[PathCompare] from=({fx:F1},{fz:F1}) to=({tx:F1},{tz:F1})");
            AppendAgentPath(sb, "raw slot31", VillagerAgentType.UnityAgentTypeID, fx, fz, tx, tz);
            AppendAgentPath(sb, "raw Humanoid",
                VillagerAgentType.ResolveValheimHumanoidAgentTypeID(), fx, fz, tx, tz);

            // HNA corridor path — what the villager actually walks (A* over the
            // HNA lookup grid + per-segment NavMesh validation), as opposed to
            // the raw NavMesh.CalculatePath above. This is the one the graph viz
            // reflects.
            var slot31Filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            var y = NavMesh.SamplePosition(new Vector3(fx, 40f, fz), out var yh, 8f, slot31Filter)
                ? yh.position.y
                : 37.4f;
            var hnaBuf = new List<Vector3>();
            var hnaOk = VillagerMovement.TryFindCompletePath(
                new Vector3(fx, y, fz), new Vector3(tx, y, tz), hnaBuf);
            var hnaLen = 0f;
            for (var i = 1; i < hnaBuf.Count; i++) hnaLen += Vector3.Distance(hnaBuf[i - 1], hnaBuf[i]);
            var hnaStraight = Vector3.Distance(new Vector3(fx, y, fz), new Vector3(tx, y, tz));
            sb.AppendLine(
                $"  HNA corridor (villager): complete={hnaOk} corners={hnaBuf.Count} " +
                $"len={hnaLen:F1}m straight={hnaStraight:F1}m " +
                $"detour={(hnaStraight > 0.01f ? hnaLen / hnaStraight : 1f):F2}x");

            if (draw)
            {
                var overlay = PathDebugRenderer.DrawRawPathBetween(fx, fz, tx, tz);
                sb.AppendLine($"  {overlay ?? "overlay: endpoint sample failed"}");
            }

            var output = sb.ToString();
            Console.instance?.Print(output);
            Plugin.Log?.LogInfo(output);
        }

        // Sample both endpoints onto the given agent's mesh, compute a path, and
        // report status + corner count + length + detour ratio (path / straight
        // line). A detour > ~1.1x with extra corners means the mesh routed the
        // agent AROUND an obstacle; ~1.0x with 2 corners means a straight line
        // (no obstacle carved on that agent's mesh).
        private static void AppendAgentPath(StringBuilder sb, string label, int agentTypeId,
            float fx, float fz, float tx, float tz)
        {
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeId,
                areaMask = NavMesh.AllAreas,
            };
            var fromOk = NavMesh.SamplePosition(new Vector3(fx, 40f, fz), out var fHit, 8f, filter);
            var toOk = NavMesh.SamplePosition(new Vector3(tx, 40f, tz), out var tHit, 8f, filter);
            if (!fromOk || !toOk)
            {
                sb.AppendLine($"  {label} (id={agentTypeId}): sample fail (from={fromOk}, to={toOk})");
                return;
            }

            var path = new NavMeshPath();
            NavMesh.CalculatePath(fHit.position, tHit.position, filter, path);
            var corners = path.corners;
            var len = 0f;
            for (var i = 1; i < corners.Length; i++) len += Vector3.Distance(corners[i - 1], corners[i]);
            var straight = Vector3.Distance(fHit.position, tHit.position);
            sb.AppendLine(
                $"  {label} (id={agentTypeId}): status={path.status} corners={corners.Length} " +
                $"len={len:F1}m straight={straight:F1}m " +
                $"detour={(straight > 0.01f ? len / straight : 1f):F2}x " +
                $"fromSnap=({fHit.position.x:F1},{fHit.position.y:F1},{fHit.position.z:F1}) " +
                $"toSnap=({tHit.position.x:F1},{tHit.position.y:F1},{tHit.position.z:F1})");
        }

        private static bool TryResolvePosition(
            Terminal.ConsoleEventArgs args, out Vector3 pos, out string source)
        {
            if (args?.Args != null && args.Args.Length >= 3)
            {
                var inv = CultureInfo.InvariantCulture;
                // Valheim convention: X Z [Y]. Matches goto/tp/pos so coords
                // copy between commands without reordering. Y is optional and
                // defaults to the ground height at (X, Z) like goto.
                if (!float.TryParse(args.Args[1], NumberStyles.Float, inv, out var x)
                    || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var z))
                {
                    Console.instance?.Print(
                        "Usage: vv_bake_audit (no args = player pos) | vv_bake_audit <x> <z> [y]");
                    pos = default;
                    source = "";
                    return false;
                }

                pos = new Vector3(x, MeshProbe.ResolveY(x, z, args, 3, inv), z);
                source = "args";
                return true;
            }

            var p = Player.m_localPlayer;
            if (p == null || p.transform == null)
            {
                Console.instance?.Print(
                    "No local player; pass coords instead: vv_bake_audit <x> <z> [y]");
                pos = default;
                source = "";
                return false;
            }

            pos = p.transform.position;
            source = "player";
            return true;
        }

        private static void ReportNavMeshSample(StringBuilder sb, Vector3 pos)
        {
            sb.AppendLine("--- NavMesh sample at this position ---");

            var slot31Filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            if (NavMesh.SamplePosition(pos, out var slot31Hit, 2f, slot31Filter))
            {
                sb.AppendLine(
                    $"  slot 31 ({VillagerAgentType.UnityAgentTypeID}): HIT at " +
                    $"({slot31Hit.position.x:F2},{slot31Hit.position.y:F2},{slot31Hit.position.z:F2}) " +
                    $"dist={Vector3.Distance(slot31Hit.position, pos):F2}m");
                if (NavMesh.FindClosestEdge(pos, out var edge, slot31Filter))
                    sb.AppendLine(
                        $"  slot 31 closest edge: ({edge.position.x:F2},{edge.position.y:F2},{edge.position.z:F2}) " +
                        $"dist={edge.distance:F2}m");
            }
            else
            {
                sb.AppendLine("  slot 31: MISS within 2m");
            }

            var humanoidId = VillagerAgentType.ResolveValheimHumanoidAgentTypeID();
            var humanoidFilter = new NavMeshQueryFilter
            {
                agentTypeID = humanoidId,
                areaMask = NavMesh.AllAreas,
            };
            if (NavMesh.SamplePosition(pos, out var humanoidHit, 2f, humanoidFilter))
                sb.AppendLine(
                    $"  Humanoid ({humanoidId}): HIT at " +
                    $"({humanoidHit.position.x:F2},{humanoidHit.position.y:F2},{humanoidHit.position.z:F2}) " +
                    $"dist={Vector3.Distance(humanoidHit.position, pos):F2}m");
            else
                sb.AppendLine($"  Humanoid ({humanoidId}): MISS within 2m");
        }

        private static void ReportRuntimePhysics(StringBuilder sb, Vector3 pos, out List<Collider> hits)
        {
            sb.AppendLine($"--- Runtime physics (Physics.OverlapBox, half-extent {Radius:F1}m, all layers) ---");
            var raw = Physics.OverlapBox(
                pos, Vector3.one * Radius, Quaternion.identity, -1, QueryTriggerInteraction.Ignore);
            hits = new List<Collider>(raw.Length);
            foreach (var c in raw)
            {
                if (c == null) continue;
                hits.Add(c);
            }

            if (hits.Count == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            // Sort by distance for readable output
            hits.Sort((a, b) => Vector3.Distance(a.bounds.center, pos)
                .CompareTo(Vector3.Distance(b.bounds.center, pos)));

            // Counted by layer rather than listed. The per-collider rows were ~21 lines of
            // walls; the cross-reference below already names any collider that matters.
            // MeshColliders ARE still listed individually: a non-readable sharedMesh is
            // collected as a bake source but contributes no geometry, and convex changes
            // the PhysX shape — neither is visible from a count.
            var byLayer = new Dictionary<string, int>();
            var meshRows = new List<string>();
            foreach (var c in hits)
            {
                var go = c.gameObject;
                var layerName = LayerMask.LayerToName(go.layer);
                if (string.IsNullOrEmpty(layerName)) layerName = "(unnamed)";
                byLayer.TryGetValue(layerName, out var n);
                byLayer[layerName] = n + 1;

                if (!(c is MeshCollider mc)) continue;
                var m = mc.sharedMesh;
                var info = m != null
                    ? $"readable={m.isReadable} convex={mc.convex} tris={(m.isReadable ? m.triangles.Length / 3 : -1)}"
                    : "null";
                meshRows.Add(
                    $"    MeshCollider '{go.name}' layer={go.layer}:{layerName} " +
                    $"dist={Vector3.Distance(c.bounds.center, pos):F2}m mesh[{info}]");
            }

            var parts = new List<string>();
            foreach (var kv in byLayer) parts.Add($"{kv.Key}={kv.Value}");
            parts.Sort();
            sb.AppendLine($"  {hits.Count} collider(s) by layer: {string.Join(" ", parts.ToArray())}");
            foreach (var r in meshRows) sb.AppendLine(r);
        }

        private static void ReportBakeSources(
            StringBuilder sb, Vector3 pos,
            out HashSet<GameObject> sourceMatches,
            out List<(int idx, string category, Bounds bounds)> phantomMatches)
        {
            sb.AppendLine("--- Bake sources whose bounds overlap this point ---");
            sourceMatches = new HashSet<GameObject>();
            phantomMatches = new List<(int, string, Bounds)>();

            var terrain = NavMeshBakeManager.TerrainSources;
            var piece = NavMeshBakeManager.PieceSources;
            var phantomCount = NavMeshBakeManager.PiecePhantomCount;
            var doorN = NavMeshBakeManager.LastDoorPhantoms;
            var bedN = NavMeshBakeManager.LastBedPhantoms;

            sb.AppendLine($"  TerrainSources={terrain.Count} PieceSources={piece.Count} (phantoms={phantomCount})");

            var queryBounds = new Bounds(pos, Vector3.one * (Radius * 2f));

            var terrainHits = 0;
            for (var i = 0; i < terrain.Count; i++)
            {
                if (!TryGetSourceBounds(terrain[i], out var b)) continue;
                if (!b.Intersects(queryBounds)) continue;
                terrainHits++;
                // Counted, not listed: the cross-reference below reports these from the
                // runtime-collider side, which is the direction that can reveal a problem.
                if (terrain[i].component != null) sourceMatches.Add(terrain[i].component.gameObject);
            }

            var realPieceCount = piece.Count - phantomCount;
            var pieceHits = 0;
            for (var i = 0; i < realPieceCount; i++)
            {
                if (!TryGetSourceBounds(piece[i], out var b)) continue;
                if (!b.Intersects(queryBounds)) continue;
                pieceHits++;
                if (piece[i].component != null) sourceMatches.Add(piece[i].component.gameObject);
            }

            // Phantom tail: door blockers first, then bed blockers, then outside-cell blockers.
            for (var i = realPieceCount; i < piece.Count; i++)
            {
                if (!TryGetSourceBounds(piece[i], out var b)) continue;
                if (!b.Intersects(queryBounds)) continue;
                var phantomIdx = i - realPieceCount;
                string category;
                if (phantomIdx < doorN) category = "door";
                else if (phantomIdx < doorN + bedN) category = "bed";
                else category = "outside_cell";
                phantomMatches.Add((i, category, b));
            }

            sb.AppendLine($"  → terrain matches: {terrainHits}, real piece matches: {pieceHits}, phantom matches: {phantomMatches.Count}");
        }

        private static void ReportCrossReference(
            StringBuilder sb,
            List<Collider> runtimeHits,
            HashSet<GameObject> sourceMatches,
            List<(int idx, string category, Bounds bounds)> _phantomMatches)
        {
            sb.AppendLine("--- Cross-reference: runtime collider → bake source ---");
            if (runtimeHits.Count == 0)
            {
                sb.AppendLine("  (no runtime colliders to cross-reference)");
                return;
            }

            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            var voxel = settings.voxelSize;
            var pieceMask = LayerMask.GetMask("Default", "static_solid", "piece");
            var terrainMask = LayerMask.GetMask("terrain");

            // Only the last bucket is a finding. A collider on a non-bake layer, or one
            // smaller than two voxels, is SUPPOSED to be absent from the bake — printing a
            // row for each just buried the handful that matter (13 [OK] + 8 expected
            // [MISS] rows to surface 0 real ones, in the run that prompted this).
            int captured = 0, outOfMask = 0, subVoxelMiss = 0;
            var unexplained = new List<string>();

            foreach (var c in runtimeHits)
            {
                var go = c.gameObject;
                var layerBit = 1 << go.layer;
                var inMask = (layerBit & (pieceMask | terrainMask)) != 0;
                var sizeMax = Mathf.Max(c.bounds.size.x, c.bounds.size.y, c.bounds.size.z);
                var subVoxel = sizeMax < voxel * 2f;

                if (sourceMatches.Contains(go)) captured++;
                else if (!inMask) outOfMask++;
                else if (subVoxel) subVoxelMiss++;
                else
                    unexplained.Add(
                        $"    '{go.name}' (layer {go.layer}:{LayerMask.LayerToName(go.layer)}) " +
                        $"maxSize={sizeMax:F2}m");
            }

            sb.AppendLine(
                $"  {runtimeHits.Count} collider(s): captured={captured} " +
                $"expected-miss(layer off bake mask)={outOfMask} " +
                $"expected-miss(sub-voxel <{voxel * 2f:F2}m)={subVoxelMiss} " +
                $"UNEXPLAINED={unexplained.Count}");
            if (unexplained.Count == 0)
            {
                sb.AppendLine("  → no bake-vs-runtime mismatch here");
                return;
            }

            sb.AppendLine(
                "  → in mask, not sub-voxel, yet absent from the bake (inactive at bake time, " +
                "or spawned/enabled after partition):");
            foreach (var r in unexplained) sb.AppendLine(r);
        }

        private static void ReportPhantomCoverage(
            StringBuilder sb, Vector3 pos,
            List<(int idx, string category, Bounds bounds)> phantomMatches)
        {
            sb.AppendLine("--- Phantom blockers covering this position ---");
            if (phantomMatches.Count == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            foreach (var (idx, category, b) in phantomMatches)
                sb.AppendLine(
                    $"  phantom[#{idx}] category={category} bounds=center({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) " +
                    $"size({b.size.x:F2},{b.size.y:F2},{b.size.z:F2}) dist={Vector3.Distance(b.center, pos):F2}m");
        }

        private static void ReportCharacterOverlap(StringBuilder sb, Vector3 pos)
        {
            sb.AppendLine($"--- Characters within {CharacterRadius:F1}m (NEVER in bake; runtime obstacles) ---");
            var characterMask = LayerMask.GetMask("character", "character_net", "character_noenv", "character_trigger");
            var hits = Physics.OverlapSphere(pos, CharacterRadius, characterMask, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
            {
                sb.AppendLine("  (none)");
                return;
            }

            // Dedupe by root GameObject — characters often have multiple colliders.
            var seen = new HashSet<GameObject>();
            foreach (var c in hits)
            {
                if (c == null) continue;
                var root = c.transform.root != null ? c.transform.root.gameObject : c.gameObject;
                if (!seen.Add(root)) continue;
                var d = Vector3.Distance(root.transform.position, pos);
                sb.AppendLine(
                    $"  '{root.name}' at ({root.transform.position.x:F1},{root.transform.position.y:F1},{root.transform.position.z:F1}) " +
                    $"dist={d:F2}m");
            }
        }

        /// <summary>
        ///     Compute world-space AABB for a NavMeshBuildSource. Mesh sources
        ///     use the mesh's local bounds transformed by src.transform; Box
        ///     sources use the half-size and transform. Other shapes return false.
        /// </summary>
        private static bool TryGetSourceBounds(NavMeshBuildSource src, out Bounds bounds)
        {
            bounds = default;
            switch (src.shape)
            {
                case NavMeshBuildSourceShape.Box:
                {
                    var h = src.size * 0.5f;
                    // World AABB of the 8 transformed corners.
                    var corners = new Vector3[8];
                    var k = 0;
                    for (var sx = -1; sx <= 1; sx += 2)
                    for (var sy = -1; sy <= 1; sy += 2)
                    for (var sz = -1; sz <= 1; sz += 2)
                        corners[k++] = src.transform.MultiplyPoint3x4(
                            new Vector3(sx * h.x, sy * h.y, sz * h.z));
                    var min = corners[0];
                    var max = corners[0];
                    for (var i = 1; i < 8; i++)
                    {
                        min = Vector3.Min(min, corners[i]);
                        max = Vector3.Max(max, corners[i]);
                    }
                    bounds = new Bounds((min + max) * 0.5f, max - min);
                    return true;
                }
                case NavMeshBuildSourceShape.Mesh:
                {
                    var mesh = src.sourceObject as Mesh;
                    if (mesh == null) return false;
                    var local = mesh.bounds;
                    // Approximate world AABB by transforming 8 local corners.
                    var ext = local.extents;
                    var corners = new Vector3[8];
                    var k = 0;
                    for (var sx = -1; sx <= 1; sx += 2)
                    for (var sy = -1; sy <= 1; sy += 2)
                    for (var sz = -1; sz <= 1; sz += 2)
                        corners[k++] = src.transform.MultiplyPoint3x4(local.center +
                                                                       new Vector3(sx * ext.x, sy * ext.y, sz * ext.z));
                    var min = corners[0];
                    var max = corners[0];
                    for (var i = 1; i < 8; i++)
                    {
                        min = Vector3.Min(min, corners[i]);
                        max = Vector3.Max(max, corners[i]);
                    }
                    bounds = new Bounds((min + max) * 0.5f, max - min);
                    return true;
                }
                default:
                    return false;
            }
        }

        private static string LayerMaskToString(int mask)
        {
            var parts = new List<string>();
            for (var i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0)
                {
                    var n = LayerMask.LayerToName(i);
                    parts.Add(string.IsNullOrEmpty(n) ? $"#{i}" : n);
                }
            return parts.Count == 0 ? "(empty)" : string.Join("|", parts);
        }
    }
}
