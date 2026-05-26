using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.Villager.AI.Pathfinding;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Runtime NavMesh bake for the custom villager agent (slot 31).
    ///     Valheim bakes the Humanoid agent (slot 1) for its own NPCs but the
    ///     custom slot 31 has no baked surface — `EnsureRegistered()` only adds
    ///     the settings, it does not bake. This manager owns the per-village bake
    ///     lifecycle: collect sources from colliders in the village bounds,
    ///     `BuildNavMeshData` for slot 31, and `AddNavMeshData` so that queries
    ///     against slot 31 (RegionBuilder, NavMeshLinkPlacer, patrol) return real
    ///     triangles instead of misses.
    ///     One bake per `hna_partition` run; the previous instance is removed
    ///     before the next add so NavMesh data does not accumulate. Hot reload
    ///     cleanup via `[RegisterCleanup]`.
    /// </summary>
    public static class NavMeshBakeManager
    {
        // Two separate bakes, both into agent slot 31. Unity's NavMesh allows
        // multiple NavMeshData instances to coexist for the same agent type;
        // queries union them. Splitting the bake by source kind lets
        // RegionBuilder run two filter/build passes with per-kind tuning
        // (terrain has gentler edge cases than buildable pieces).
        //
        // Instances are stored on a DontDestroyOnLoad MonoBehaviour found by
        // name so that a hot-reloaded assembly inherits the prior assembly's
        // active bakes and can Remove() them before adding fresh data. Plain
        // static fields would leak: on reload the static resets to default,
        // .valid becomes false, the Remove guard skips, and the prior
        // instance stays orphaned inside Unity's NavMesh — stacking layer
        // after layer of village geometry across reloads.
        private const string HolderName = "VV_NavMeshBakeHolder";
        private static NavMeshBakeHolder s_holder;

        private static readonly List<NavMeshBuildSource> s_terrainSources = new();
        private static readonly List<NavMeshBuildSource> s_pieceSources = new();

        /// <summary>
        ///     Count of phantom door-blocker sources appended at the END of
        ///     <see cref="s_pieceSources" /> for the most recent bake. Excluded
        ///     from triangle extraction (they're synthetic obstacles, not
        ///     extractable walkable geometry). Door blockers attach to the piece
        ///     bake because doors are piece-adjacent.
        /// </summary>
        private static int s_piecePhantomCount;

        private static readonly int s_terrainMask = LayerMask.GetMask("terrain");

        private static readonly int s_pieceMask =
            LayerMask.GetMask("Default", "static_solid", "piece");

        private static NavMeshBakeHolder Holder
        {
            get
            {
                if (s_holder != null) return s_holder;
                // Find by name first — a holder created by a prior assembly
                // survives via DontDestroyOnLoad and still owns its instances.
                var existing = GameObject.Find(HolderName);
                if (existing != null)
                {
                    s_holder = existing.GetComponent<NavMeshBakeHolder>()
                               ?? existing.AddComponent<NavMeshBakeHolder>();
                    return s_holder;
                }

                var go = new GameObject(HolderName);
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideInHierarchy;
                s_holder = go.AddComponent<NavMeshBakeHolder>();
                return s_holder;
            }
        }

        /// <summary>Whether any bake is currently active.</summary>
        public static bool HasBakedData => Holder.HasAny;

        /// <summary>
        ///     Snapshot of the source list used by the most recent bake (terrain
        ///     + piece concatenated). Diagnostics-only; per-kind callers should
        ///     use <see cref="GetSources" />.
        /// </summary>
        public static IReadOnlyList<NavMeshBuildSource> LastSources
        {
            get
            {
                var combined = new List<NavMeshBuildSource>(s_terrainSources.Count + s_pieceSources.Count);
                combined.AddRange(s_terrainSources);
                combined.AddRange(s_pieceSources);
                return combined;
            }
        }

        /// <summary>
        ///     Bake a fresh NavMesh for the villager agent over <paramref name="bounds" />.
        ///     Two passes (terrain + piece) both register into slot 31; queries
        ///     union the results. Removes any previous bakes first.
        /// </summary>
        public static BakeResult BakeVillage(Bounds bounds)
        {
            var result = new BakeResult();
            var sw = Stopwatch.StartNew();

            if (!VillagerAgentType.IsRegistered)
            {
                result.FailureReason = "agent_not_registered";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Remove previous bakes to avoid accumulating data across
            // partition runs. Routed through the holder so a hot-reloaded
            // assembly correctly clears the prior assembly's instances.
            Holder.RemoveTerrain();
            Holder.RemovePiece();

            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);

            // --- Terrain bake ---
            var terrainSw = Stopwatch.StartNew();
            var terrainSources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                bounds, s_terrainMask, NavMeshCollectGeometry.PhysicsColliders,
                0, new List<NavMeshBuildMarkup>(), terrainSources);

            s_terrainSources.Clear();
            s_terrainSources.AddRange(terrainSources);
            result.TerrainSourceCount = terrainSources.Count;

            if (terrainSources.Count > 0)
            {
                var data = NavMeshBuilder.BuildNavMeshData(
                    settings, terrainSources, bounds, Vector3.zero, Quaternion.identity);
                if (data != null) Holder.SetTerrain(NavMesh.AddNavMeshData(data));
            }

            terrainSw.Stop();
            result.TerrainDurationMs = (float)terrainSw.Elapsed.TotalMilliseconds;

            // --- Piece bake (includes phantom door blockers at the tail) ---
            var pieceSw = Stopwatch.StartNew();
            var pieceSources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                bounds, s_pieceMask, NavMeshCollectGeometry.PhysicsColliders,
                0, new List<NavMeshBuildMarkup>(), pieceSources);

            // Add phantom solid blockers for every Door in bounds. Doors are
            // dynamic (rotate to open/close), but the bake snapshots one
            // moment in time — if a door is open when CollectSources runs,
            // the NavMesh will have a path through the doorway that persists
            // in the cached graph even after the door closes. Phantom boxes
            // sit in the doorway position regardless of the actual door's
            // orientation, ensuring the bake always sees the doorway as
            // solid. area=1 (NotWalkable) prevents the bake from making the
            // phantom's top face walkable.
            var phantomDoors = AddDoorBlockers(pieceSources, bounds);
            result.DoorsBlocked = phantomDoors;

            var phantomBeds = AddBedBlockers(pieceSources, bounds);
            result.BedsBlocked = phantomBeds;

            // Compute outside cells via a pre-bake perimeter flood (uses
            // Physics.CheckBox on the piece layer + ZoneSystem.GetGroundHeight,
            // doesn't require any NavMesh data), then append phantom
            // NotWalkable box sources covering each outside cell. The bake's
            // voxelizer marks those cells as unwalkable, so the agent NavMesh
            // excludes outside-the-wall surface up front — no need for a
            // second HNA-only rebake afterwards. The bake also keeps the
            // real piece colliders, so wall corners and small interior
            // obstacles (chairs, decorations) get carved properly.
            var outsideCells = ValheimVillages.Villager.AI.Navigation
                .RubberBandPrune.ComputeOutsideCellsForBake(bounds);
            var phantomOutside = AddOutsideCellBlockers(pieceSources, outsideCells, bounds);
            result.OutsideCellsBlocked = phantomOutside;

            s_pieceSources.Clear();
            s_pieceSources.AddRange(pieceSources);
            s_piecePhantomCount = phantomDoors + phantomBeds + phantomOutside;
            result.PieceSourceCount = pieceSources.Count;

            if (pieceSources.Count > 0)
            {
                var data = NavMeshBuilder.BuildNavMeshData(
                    settings, pieceSources, bounds, Vector3.zero, Quaternion.identity);
                if (data != null) Holder.SetPiece(NavMesh.AddNavMeshData(data));
            }

            pieceSw.Stop();
            result.PieceDurationMs = (float)pieceSw.Elapsed.TotalMilliseconds;

            result.SourceCount = result.TerrainSourceCount + result.PieceSourceCount;

            if (result.SourceCount == 0)
            {
                result.FailureReason = "no_sources_collected";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            result.Success = Holder.HasAny;
            if (!result.Success) result.FailureReason = "build_returned_null";
            sw.Stop();
            result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            return result;
        }

        public static IReadOnlyList<NavMeshBuildSource> GetSources(SurfaceKind kind)
        {
            return kind == SurfaceKind.Terrain ? (IReadOnlyList<NavMeshBuildSource>)s_terrainSources : s_pieceSources;
        }

        /// <summary>
        ///     Result of a <see cref="RebakeFromHnaTriangles" /> call.
        /// </summary>
        public struct HnaRebakeResult
        {
            public bool Success;
            public int TriangleCount;
            public int VertexCount;
            public float DurationMs;
            public string FailureReason;
        }

        /// <summary>
        ///     REPLACE the slot-31 agent NavMesh with one baked solely from
        ///     the HNA-accepted triangle set. Call this after
        ///     <see cref="RubberBandPrune.Apply" /> finishes and
        ///     <see cref="RegionBuilder.CachedTriangles" /> reflects the
        ///     final inside-village geometry.
        ///
        ///     Removes the existing terrain + piece bakes (they include
        ///     outside-the-wall walkable surface that the prune excluded
        ///     but the bake doesn't know about) and replaces them with a
        ///     single NavMesh built from the HNA tris only.
        ///
        ///     Why: <c>NavMesh.SamplePosition</c> and Valheim's
        ///     <c>Pathfinding.GetPath</c> query whatever NavMesh data is
        ///     registered for the agent type. If the bake covers the open
        ///     terrain around the village, link endpoint snaps can land
        ///     outside the perimeter and path queries can route through
        ///     non-village surface — both observed in practice. Carving
        ///     the bake down to HNA-only surfaces makes the agent NavMesh
        ///     and the HNA RegionGraph represent the same walkable set.
        ///
        ///     The bake is voxelized at slot-31's voxel size (~0.166m), so
        ///     adjacent HNA triangles whose vertices are sub-voxel apart
        ///     should remain connected. Pass 4 border snapping is what
        ///     keeps this true; if snap misses leave gaps, the rebaked
        ///     NavMesh will reflect those gaps as real discontinuities
        ///     (then NavMeshLinkPlacer fills them with explicit links).
        /// </summary>
        internal static HnaRebakeResult RebakeFromHnaTriangles(
            IReadOnlyList<RegionBuilder.CachedTriangle> triangles,
            Bounds bounds)
        {
            var result = new HnaRebakeResult();
            var sw = Stopwatch.StartNew();

            if (!VillagerAgentType.IsRegistered)
            {
                result.FailureReason = "agent_not_registered";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }
            if (triangles == null || triangles.Count == 0)
            {
                result.FailureReason = "no_triangles";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Pack HNA triangles into a single Mesh. Each cached triangle
            // contributes 3 unique vertices (no dedup — Unity's voxelizer
            // handles the redundancy and the bake's voxel size is much
            // larger than typical vertex coincidence, so dedup wouldn't
            // change the output).
            var triCount = triangles.Count;
            var verts = new Vector3[triCount * 3];
            var idx = new int[triCount * 3];
            for (var i = 0; i < triCount; i++)
            {
                var t = triangles[i];
                var i0 = i * 3;
                verts[i0] = t.V0;
                verts[i0 + 1] = t.V1;
                verts[i0 + 2] = t.V2;
                idx[i0] = i0;
                idx[i0 + 1] = i0 + 1;
                idx[i0 + 2] = i0 + 2;
            }

            var mesh = new Mesh
            {
                indexFormat = triCount * 3 > ushort.MaxValue
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
                vertices = verts,
                triangles = idx,
            };
            mesh.RecalculateBounds();

            var sources = new List<NavMeshBuildSource>(1)
            {
                new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = Matrix4x4.identity,
                    area = 0, // Walkable
                },
            };

            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            var data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (data == null)
            {
                Object.DestroyImmediate(mesh);
                result.FailureReason = "build_returned_null";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // REPLACE the previous bakes. Remove terrain + piece (they
            // included outside-the-wall surface) so SamplePosition and
            // Pathfinding queries see ONLY the HNA-derived navmesh.
            Holder.RemoveTerrain();
            Holder.RemovePiece();
            Holder.SetHna(NavMesh.AddNavMeshData(data));

            result.Success = true;
            result.TriangleCount = triCount;
            result.VertexCount = verts.Length;
            sw.Stop();
            result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        ///     Extract a triangulation (vertices + indices, world space) from
        ///     every cached source list (terrain + piece concatenated). Diagnostics
        ///     path only; per-kind callers should use the
        ///     <see cref="ExtractBakedTriangles(SurfaceKind)" /> overload so they
        ///     can run separate filter passes.
        /// </summary>
        public static (Vector3[] vertices, int[] indices, int[] triangleLayers) ExtractBakedTriangles()
        {
            var (tv, ti, tl) = ExtractBakedTriangles(SurfaceKind.Terrain);
            var (pv, pi, pl) = ExtractBakedTriangles(SurfaceKind.Piece);
            if (tv.Length == 0) return (pv, pi, pl);
            if (pv.Length == 0) return (tv, ti, tl);

            var verts = new Vector3[tv.Length + pv.Length];
            Array.Copy(tv, 0, verts, 0, tv.Length);
            Array.Copy(pv, 0, verts, tv.Length, pv.Length);

            var idx = new int[ti.Length + pi.Length];
            Array.Copy(ti, 0, idx, 0, ti.Length);
            for (var i = 0; i < pi.Length; i++) idx[ti.Length + i] = pi[i] + tv.Length;

            var layers = new int[tl.Length + pl.Length];
            Array.Copy(tl, 0, layers, 0, tl.Length);
            Array.Copy(pl, 0, layers, tl.Length, pl.Length);

            return (verts, idx, layers);
        }

        /// <summary>
        ///     Extract a triangulation (vertices + indices, world space) from the
        ///     source list of the requested kind. This is the canonical way to get
        ///     triangles for runtime-baked data: <c>NavMesh.CalculateTriangulation()</c>
        ///     returns only static scene-baked data, missing both Valheim's runtime
        ///     bakes and ours. Handles Mesh and Box source shapes; other shapes
        ///     (Sphere, Capsule, ModifierBox) are skipped — they're rare in Valheim
        ///     piece geometry. Phantom door blockers at the tail of the piece
        ///     sources are skipped (they're synthetic obstacles, not extractable
        ///     walkable geometry).
        /// </summary>
        public static (Vector3[] vertices, int[] indices, int[] triangleLayers) ExtractBakedTriangles(SurfaceKind kind)
        {
            // Sentinel bounds = no filter. Use the overload to skip
            // out-of-bounds triangles at extraction time.
            return ExtractBakedTriangles(kind, float.NegativeInfinity, float.NegativeInfinity,
                float.PositiveInfinity, float.PositiveInfinity);
        }

        /// <summary>
        ///     Same as <see cref="ExtractBakedTriangles(SurfaceKind)" /> but drops
        ///     triangles whose centroid is outside the XZ bounds. Terrain meshes
        ///     are full Valheim TerrainComp tiles (~32m × ~32m each, ~8k tris
        ///     each); a village spanning 60m × 60m typically pulls in 4 tiles ≈
        ///     32k tris of which ~80% are outside village bounds. Filtering at
        ///     extraction skips the per-triangle work RegionBuilder would have
        ///     otherwise wasted on rej_bounds.
        /// </summary>
        public static (Vector3[] vertices, int[] indices, int[] triangleLayers) ExtractBakedTriangles(
            SurfaceKind kind, float minX, float minZ, float maxX, float maxZ)
        {
            var sources = kind == SurfaceKind.Terrain ? s_terrainSources : s_pieceSources;
            var phantom = kind == SurfaceKind.Piece ? s_piecePhantomCount : 0;
            if (sources.Count == 0)
                return (Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>());

            var verts = new List<Vector3>(sources.Count * 32);
            var idx = new List<int>(sources.Count * 96);
            var triLayers = new List<int>(sources.Count * 32);
            var skippedUnreadable = 0;
            var skippedShape = 0;

            var realCount = sources.Count - phantom;
            for (var i = 0; i < realCount; i++)
            {
                var src = sources[i];
                // Layer is read from the source Collider's GameObject. Box
                // sources we synthesize (door blockers) have no component
                // and get -1; they're phantom obstacles and excluded from
                // extraction by realCount anyway.
                var layer = src.component != null ? src.component.gameObject.layer : -1;
                switch (src.shape)
                {
                    case NavMeshBuildSourceShape.Mesh:
                        var mesh = src.sourceObject as Mesh;
                        if (mesh == null)
                        {
                            skippedShape++;
                            continue;
                        }

                        if (!mesh.isReadable)
                        {
                            skippedUnreadable++;
                            continue;
                        }

                        AppendMesh(verts, idx, triLayers, layer, src.transform, mesh, minX, minZ, maxX, maxZ);
                        break;
                    case NavMeshBuildSourceShape.Box:
                        AppendBox(verts, idx, triLayers, layer, src.transform, src.size, minX, minZ, maxX, maxZ);
                        break;
                    default:
                        skippedShape++;
                        break;
                }
            }

            if (skippedUnreadable > 0 || skippedShape > 0)
                Plugin.Log?.LogDebug(
                    $"[NavMeshBake] extract({kind}): {skippedUnreadable} unreadable mesh(es) skipped, " +
                    $"{skippedShape} non-Mesh/Box source(s) skipped");

            // Terrain-only: weld coincident vertices across source meshes
            // (heightmap + cultivated_ground + paved_road + other terrain-
            // layer pieces). Each source produces its own vertex indices;
            // without welding, tris from different sources at the same
            // physical position have different indices, no shared edges,
            // and end up in separate union-find components — even though
            // they're physically continuous. Welding makes shared-position
            // vertices share an index, so RegionBuilder's adjacency works
            // naturally across the cultivated/natural boundary.
            // Piece pass is not welded (its prefabs are intentionally
            // distinct walkable surfaces; we don't want adjacent piece
            // prefabs to silently bridge their regions).
            // Weld remaps indices but preserves triangle count, so
            // triLayers stays parallel to idx (length = idx.Count / 3).
            if (kind == SurfaceKind.Terrain)
                WeldCoincidentVertices(verts, idx);

            return (verts.ToArray(), idx.ToArray(), triLayers.ToArray());
        }

        /// <summary>
        ///     In-place vertex welding via spatial hash. Vertices within the
        ///     same 5cm quantum bucket are unified to a single index. Triangle
        ///     indices are remapped accordingly. Unused vertices are NOT pruned
        ///     (they just become orphans in the vertex array — cheap to leave
        ///     since downstream only iterates triangles).
        /// </summary>
        private static void WeldCoincidentVertices(List<Vector3> verts, List<int> idx)
        {
            if (verts.Count == 0 || idx.Count == 0) return;
            const float weldQuantum = 0.05f; // 5cm, matches MergeCoplanarRegions
            const long bias = 1L << 20;
            const long mask = (1L << 21) - 1L;

            var posToIndex = new Dictionary<long, int>(verts.Count);
            var remap = new int[verts.Count];
            for (var i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                long qx = Mathf.RoundToInt(v.x / weldQuantum);
                long qy = Mathf.RoundToInt(v.y / weldQuantum);
                long qz = Mathf.RoundToInt(v.z / weldQuantum);
                var key = (((qx + bias) & mask) << 42)
                          | (((qy + bias) & mask) << 21)
                          | ((qz + bias) & mask);
                if (posToIndex.TryGetValue(key, out var existing))
                {
                    remap[i] = existing;
                }
                else
                {
                    posToIndex[key] = i;
                    remap[i] = i;
                }
            }

            var welded = 0;
            for (var j = 0; j < idx.Count; j++)
            {
                var oldIdx = idx[j];
                var newIdx = remap[oldIdx];
                if (newIdx != oldIdx)
                {
                    idx[j] = newIdx;
                    welded++;
                }
            }

            Plugin.Log?.LogInfo(
                $"[NavMeshBake] terrain weld: {welded} index refs re-pointed across " +
                $"{verts.Count - posToIndex.Count} merged vertex positions " +
                $"({verts.Count} total verts, {posToIndex.Count} unique positions)");
        }

        /// <summary>
        ///     Append a phantom solid box source for every <see cref="Door" /> in
        ///     bounds. These block the bake from producing NavMesh through
        ///     doorways regardless of the door's current open/closed orientation.
        /// </summary>
        private static int AddDoorBlockers(List<NavMeshBuildSource> sources, Bounds bounds)
        {
            var added = 0;
            var allDoors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var door in allDoors)
            {
                if (door == null || door.transform == null) continue;
                if (!bounds.Contains(door.transform.position)) continue;

                // Center the blocker at door pivot + 1m up (so the box's
                // 2m height covers ground to ~2m), oriented with the door's
                // rotation so the thin axis aligns with the doorway plane.
                // Slightly wider than a standard 1m Valheim door to ensure
                // full blockage even at angled placements.
                var transform = Matrix4x4.TRS(
                    door.transform.position + Vector3.up * 1.0f,
                    door.transform.rotation,
                    Vector3.one);
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = new Vector3(1.2f, 2.0f, 0.3f),
                    transform = transform,
                    area = 1, // NotWalkable — block but don't make walkable surface on top
                });
                added++;
            }

            return added;
        }

        /// <summary>
        ///     Append a phantom <c>NotWalkable</c> box source for each
        ///     outside cell computed by the pre-bake perimeter flood. The
        ///     box is a 1m × bake-height × 1m column centered on the cell —
        ///     tall enough to carve the cell out of the agent NavMesh at
        ///     every altitude (terrain ground, piece tops, rampart tops),
        ///     so the bake doesn't produce walkable surface anywhere in an
        ///     outside cell.
        ///
        ///     Effect: NavMesh.SamplePosition snaps inside the village
        ///     stay inside; Pathfinding.GetPath queries can't route through
        ///     outside-the-wall surfaces; NavMeshLink endpoints can't snap
        ///     outside. The HNA RegionGraph and the agent NavMesh end up
        ///     representing the same walkable set in a single bake.
        ///
        ///     area=1 (NotWalkable) — same approach as AddDoorBlockers.
        /// </summary>
        /// <summary>
        ///     Append a phantom <c>NotWalkable</c> box source over every
        ///     <c>Bed</c> in bake bounds. Without this, the bake produces
        ///     walkable mesh on the bed's flat top face: villagers
        ///     pathfinding past a bed can route OVER it (treating the bed
        ///     top as a shortcut, then getting stuck because the bed's
        ///     height exceeds the agent's climb), and the agent's
        ///     CapsuleCollider catches on the bed's frame when trying to
        ///     pass at floor level.
        ///
        ///     The blocker is generously sized (2.5m × 1.2m × 1.5m, raised
        ///     0.6m off the bed pivot) so it covers the bed's footprint
        ///     plus a small clearance margin that pushes the agent's
        ///     navmesh further away from the bed's collider. Beds remain
        ///     valid Pass-2 seed POSITIONS — Pass 2 uses GetGroundHeight,
        ///     not the navmesh, so the seed cell's "walkability" in the
        ///     agent navmesh doesn't matter for seeding. (Villagers that
        ///     need to physically reach the bed for sleep can still get
        ///     close — the blocker is above floor, so the floor next to
        ///     the bed remains walkable.)
        /// </summary>
        private static int AddBedBlockers(List<NavMeshBuildSource> sources, Bounds bounds)
        {
            var added = 0;
            var allBeds = Object.FindObjectsByType<Bed>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var bed in allBeds)
            {
                if (bed == null || bed.transform == null) continue;
                if (!bounds.Contains(bed.transform.position)) continue;

                var transform = Matrix4x4.TRS(
                    bed.transform.position + Vector3.up * 0.6f,
                    bed.transform.rotation,
                    Vector3.one);
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = new Vector3(2.5f, 1.2f, 1.5f),
                    transform = transform,
                    area = 1, // NotWalkable
                });
                added++;
            }
            return added;
        }

        private static int AddOutsideCellBlockers(
            List<NavMeshBuildSource> sources,
            HashSet<long> outsideCells,
            Bounds bounds)
        {
            if (outsideCells == null || outsideCells.Count == 0) return 0;
            var cell = ValheimVillages.Villager.AI.Navigation.RegionGraph.LookupCellSize;
            var half = cell * 0.5f;
            var ySize = bounds.size.y + 4f;
            var yCenter = bounds.center.y;
            var boxSize = new Vector3(cell, ySize, cell);
            var added = 0;
            foreach (var key in outsideCells)
            {
                ValheimVillages.Villager.AI.Navigation
                    .RubberBandPrune.UnpackOutsideCellKey(key, out var gx, out var gz);
                var transform = Matrix4x4.TRS(
                    new Vector3(gx * cell + half, yCenter, gz * cell + half),
                    Quaternion.identity, Vector3.one);
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = boxSize,
                    transform = transform,
                    area = 1, // NotWalkable
                });
                added++;
            }
            return added;
        }

        private static void AppendMesh(List<Vector3> verts, List<int> idx, List<int> triLayers, int layer,
            Matrix4x4 t, Mesh mesh,
            float minX, float minZ, float maxX, float maxZ)
        {
            Vector3[] localVerts;
            int[] localTris;
            try
            {
                localVerts = mesh.vertices;
                localTris = mesh.triangles;
            }
            catch
            {
                // Defensive: even isReadable=true meshes can throw on some builds.
                return;
            }

            // Transform once into a scratch array so we can per-triangle
            // bounds-check using world-space centroids without re-transforming.
            // Triangles whose centroid is outside [minX..maxX, minZ..maxZ] are
            // dropped here; unused vertices are pruned by tracking which
            // local indices actually get used.
            var worldVerts = new Vector3[localVerts.Length];
            for (var i = 0; i < localVerts.Length; i++)
                worldVerts[i] = t.MultiplyPoint3x4(localVerts[i]);

            // Map local-index -> output-index (lazy, only for used vertices).
            var remap = new int[localVerts.Length];
            for (var i = 0; i < remap.Length; i++) remap[i] = -1;

            for (var i = 0; i < localTris.Length; i += 3)
            {
                int li0 = localTris[i], li1 = localTris[i + 1], li2 = localTris[i + 2];
                var v0 = worldVerts[li0];
                var v1 = worldVerts[li1];
                var v2 = worldVerts[li2];
                var cx = (v0.x + v1.x + v2.x) * (1f / 3f);
                var cz = (v0.z + v1.z + v2.z) * (1f / 3f);
                if (cx < minX || cx > maxX || cz < minZ || cz > maxZ) continue;

                if (remap[li0] < 0)
                {
                    remap[li0] = verts.Count;
                    verts.Add(v0);
                }

                if (remap[li1] < 0)
                {
                    remap[li1] = verts.Count;
                    verts.Add(v1);
                }

                if (remap[li2] < 0)
                {
                    remap[li2] = verts.Count;
                    verts.Add(v2);
                }

                idx.Add(remap[li0]);
                idx.Add(remap[li1]);
                idx.Add(remap[li2]);
                triLayers.Add(layer);
            }
        }

        private static void AppendBox(List<Vector3> verts, List<int> idx, List<int> triLayers, int layer,
            Matrix4x4 t, Vector3 size,
            float minX, float minZ, float maxX, float maxZ)
        {
            // Box bounds check via the world-space transform origin; boxes
            // are typically small (door blockers, piece colliders) so a
            // single-point center test is sufficient — partial-overlap boxes
            // would have been rejected by CollectSources' bounds anyway.
            var worldCenter = t.MultiplyPoint3x4(Vector3.zero);
            if (worldCenter.x < minX || worldCenter.x > maxX ||
                worldCenter.z < minZ || worldCenter.z > maxZ) return;

            var h = size * 0.5f;
            var b = verts.Count;
            verts.Add(t.MultiplyPoint3x4(new Vector3(-h.x, -h.y, -h.z))); // 0
            verts.Add(t.MultiplyPoint3x4(new Vector3(h.x, -h.y, -h.z))); // 1
            verts.Add(t.MultiplyPoint3x4(new Vector3(h.x, -h.y, h.z))); // 2
            verts.Add(t.MultiplyPoint3x4(new Vector3(-h.x, -h.y, h.z))); // 3
            verts.Add(t.MultiplyPoint3x4(new Vector3(-h.x, h.y, -h.z))); // 4
            verts.Add(t.MultiplyPoint3x4(new Vector3(h.x, h.y, -h.z))); // 5
            verts.Add(t.MultiplyPoint3x4(new Vector3(h.x, h.y, h.z))); // 6
            verts.Add(t.MultiplyPoint3x4(new Vector3(-h.x, h.y, h.z))); // 7

            // 6 faces × 2 triangles, counter-clockwise from outside.
            int[] boxTris =
            {
                0, 1, 2, 0, 2, 3, // -Y bottom
                4, 6, 5, 4, 7, 6, // +Y top
                0, 5, 1, 0, 4, 5, // -Z back
                2, 7, 3, 2, 6, 7, // +Z front
                0, 3, 7, 0, 7, 4, // -X left
                1, 5, 2, 5, 6, 2, // +X right
            };
            for (var i = 0; i < boxTris.Length; i++)
                idx.Add(b + boxTris[i]);
            // 12 triangles (6 faces × 2), all sharing this box's source layer.
            for (var i = 0; i < 12; i++)
                triLayers.Add(layer);
        }

        /// <summary>
        ///     Remove the active bake's NavMesh data. Invoked automatically on
        ///     hot reload via <see cref="RegisterCleanupAttribute" />; safe to call
        ///     when no bake is active.
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            // Route through holder so a fresh assembly cleanup also clears
            // instances inherited from the prior assembly.
            Holder.RemoveAll();
            s_terrainSources.Clear();
            s_pieceSources.Clear();
            s_piecePhantomCount = 0;
        }

        public struct BakeResult
        {
            public bool Success;
            public int SourceCount;
            public int DoorsBlocked;
            public int BedsBlocked;
            public int OutsideCellsBlocked;
            public float DurationMs;
            public string FailureReason;

            // Per-kind sub-counts so the caller can log each bake separately
            // while still seeing the rollup totals in SourceCount / DurationMs.
            public int TerrainSourceCount;
            public int PieceSourceCount;
            public float TerrainDurationMs;
            public float PieceDurationMs;
        }
    }

    /// <summary>
    ///     Owns the active <see cref="NavMeshDataInstance" /> handles for the
    ///     villager bake on a <see cref="Object.DontDestroyOnLoad" /> GameObject
    ///     named <c>VV_NavMeshBakeHolder</c>. New assemblies find the existing
    ///     holder by name so they inherit and can clean up the prior assembly's
    ///     bake instances. <see cref="OnDestroy" /> is a safety net for full
    ///     teardown (game exit, scene unload).
    /// </summary>
    internal class NavMeshBakeHolder : MonoBehaviour
    {
        private NavMeshDataInstance m_piece;
        private NavMeshDataInstance m_terrain;
        private NavMeshDataInstance m_hna;

        internal bool HasAny => m_terrain.valid || m_piece.valid || m_hna.valid;

        private void OnDestroy()
        {
            var removed = 0;
            if (m_terrain.valid)
            {
                m_terrain.Remove();
                m_terrain = default;
                removed++;
            }

            if (m_piece.valid)
            {
                m_piece.Remove();
                m_piece = default;
                removed++;
            }

            if (m_hna.valid)
            {
                m_hna.Remove();
                m_hna = default;
                removed++;
            }

            if (removed > 0)
                Plugin.Log?.LogInfo(
                    $"[NavMeshBakeHolder] OnDestroy: removed {removed} orphaned NavMesh data instances");
        }

        internal void SetTerrain(NavMeshDataInstance instance)
        {
            RemoveTerrain();
            m_terrain = instance;
        }

        internal void SetPiece(NavMeshDataInstance instance)
        {
            RemovePiece();
            m_piece = instance;
        }

        internal void SetHna(NavMeshDataInstance instance)
        {
            RemoveHna();
            m_hna = instance;
        }

        internal void RemoveTerrain()
        {
            if (m_terrain.valid)
            {
                m_terrain.Remove();
                m_terrain = default;
            }
        }

        internal void RemovePiece()
        {
            if (m_piece.valid)
            {
                m_piece.Remove();
                m_piece = default;
            }
        }

        internal void RemoveHna()
        {
            if (m_hna.valid)
            {
                m_hna.Remove();
                m_hna = default;
            }
        }

        internal void RemoveAll()
        {
            RemoveTerrain();
            RemovePiece();
            RemoveHna();
        }
    }
}