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

        [DevCommand("Audit bake collider coverage at a position (defaults to player). Usage: vv_bake_audit [x y z]",
            Name = "vv_bake_audit")]
        public static void Audit(Terminal.ConsoleEventArgs args)
        {
            if (!TryResolvePosition(args, out var pos, out var source)) return;

            var sb = new StringBuilder();
            sb.AppendLine(
                $"[BakeAudit] pos=({pos.x:F2}, {pos.y:F2}, {pos.z:F2}) source={source} radius={Radius:F1}m");

            ReportNavMeshSample(sb, pos);
            ReportRuntimePhysics(sb, pos, out var runtimeHits);
            ReportBakeSources(sb, pos, out var sourceMatches, out var phantomMatches);
            ReportCrossReference(sb, runtimeHits, sourceMatches, phantomMatches);
            ReportPhantomCoverage(sb, pos, phantomMatches);
            ReportCharacterOverlap(sb, pos);

            var output = sb.ToString();
            Console.instance?.Print(output);
            Plugin.Log?.LogInfo(output);
        }

        private static bool TryResolvePosition(
            Terminal.ConsoleEventArgs args, out Vector3 pos, out string source)
        {
            if (args?.Args != null && args.Args.Length >= 4)
            {
                var inv = CultureInfo.InvariantCulture;
                if (!float.TryParse(args.Args[1], NumberStyles.Float, inv, out var x)
                    || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var y)
                    || !float.TryParse(args.Args[3], NumberStyles.Float, inv, out var z))
                {
                    Console.instance?.Print(
                        "Usage: vv_bake_audit (no args = player pos) | vv_bake_audit <x> <y> <z>");
                    pos = default;
                    source = "";
                    return false;
                }

                pos = new Vector3(x, y, z);
                source = "args";
                return true;
            }

            var p = Player.m_localPlayer;
            if (p == null || p.transform == null)
            {
                Console.instance?.Print(
                    "No local player; pass coords instead: vv_bake_audit <x> <y> <z>");
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

            foreach (var c in hits)
            {
                var go = c.gameObject;
                var rootName = go.transform.root != null ? go.transform.root.name : "(no root)";
                var layerName = LayerMask.LayerToName(go.layer);
                if (string.IsNullOrEmpty(layerName)) layerName = "(unnamed)";
                var b = c.bounds;
                sb.AppendLine(
                    $"  '{go.name}' root='{rootName}' layer={go.layer}:{layerName} " +
                    $"colliderType={c.GetType().Name} bounds=center({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) " +
                    $"size({b.size.x:F2},{b.size.y:F2},{b.size.z:F2}) " +
                    $"dist={Vector3.Distance(b.center, pos):F2}m");
            }
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
                var src = terrain[i];
                var componentName = src.component != null ? src.component.gameObject.name : "(synth)";
                var componentLayer = src.component != null ? src.component.gameObject.layer : -1;
                sb.AppendLine(
                    $"  terrain[#{i}] shape={src.shape} area={src.area} comp='{componentName}' " +
                    $"layer={componentLayer} bounds=center({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) " +
                    $"size({b.size.x:F2},{b.size.y:F2},{b.size.z:F2})");
                if (src.component != null) sourceMatches.Add(src.component.gameObject);
            }

            var realPieceCount = piece.Count - phantomCount;
            var pieceHits = 0;
            for (var i = 0; i < realPieceCount; i++)
            {
                if (!TryGetSourceBounds(piece[i], out var b)) continue;
                if (!b.Intersects(queryBounds)) continue;
                pieceHits++;
                var src = piece[i];
                var componentName = src.component != null ? src.component.gameObject.name : "(synth)";
                var componentLayer = src.component != null ? src.component.gameObject.layer : -1;
                sb.AppendLine(
                    $"  piece[#{i}] shape={src.shape} area={src.area} comp='{componentName}' " +
                    $"layer={componentLayer} bounds=center({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) " +
                    $"size({b.size.x:F2},{b.size.y:F2},{b.size.z:F2})");
                if (src.component != null) sourceMatches.Add(src.component.gameObject);
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

            foreach (var c in runtimeHits)
            {
                var go = c.gameObject;
                var layerBit = 1 << go.layer;
                var inMask = (layerBit & (pieceMask | terrainMask)) != 0;
                var sizeMax = Mathf.Max(c.bounds.size.x, c.bounds.size.y, c.bounds.size.z);
                var subVoxel = sizeMax < voxel * 2f;

                if (sourceMatches.Contains(go))
                {
                    sb.AppendLine($"  [OK]   '{go.name}' (layer {go.layer}) - captured in bake");
                }
                else if (!inMask)
                {
                    sb.AppendLine(
                        $"  [MISS] '{go.name}' (layer {go.layer}:{LayerMask.LayerToName(go.layer)}) - " +
                        $"layer NOT in bake mask (piece={LayerMaskToString(pieceMask)}, terrain={LayerMaskToString(terrainMask)})");
                }
                else if (subVoxel)
                {
                    sb.AppendLine(
                        $"  [MISS] '{go.name}' (layer {go.layer}) - sub-voxel " +
                        $"(maxSize {sizeMax:F2}m < {voxel * 2f:F2}m = 2x voxel); likely voxelized away");
                }
                else
                {
                    sb.AppendLine(
                        $"  [MISS] '{go.name}' (layer {go.layer}) - in mask, not sub-voxel, " +
                        $"but absent from bake (collider inactive at bake time, or spawned/enabled after partition)");
                }
            }
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
