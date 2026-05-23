using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Builds the village region graph from NavMesh triangulation data.
    /// Extracts triangles via <see cref="NavMesh.CalculateTriangulation"/>,
    /// filters to humanoid-walkable geometry within village bounds, then
    /// merges adjacent triangles into regions using connected components
    /// with spatial subdivision.
    /// </summary>
    internal static class RegionBuilder
    {
        private const float SubdivCellSize = 3f;
        private const float HeightBandSize = 2f;
        private const float AgentFilterRadius = 0.5f;
        /// <summary>Max distance a vertex can be below terrain before the triangle is rejected.</summary>
        private const float MaxBelowTerrain = 1.5f;
        /// <summary>Minimum total region area in m². Regions smaller than this are noise from
        /// collider edges, wall tops, etc. and are pruned after region building.</summary>
        private const float MinRegionArea = 0.25f;
        /// <summary>Minimum NavMesh edge distance at a region's centroid. Currently 0
        /// (narrow pruning disabled at the centroid-based check) because coplanar-merged
        /// regions can have centroids that legitimately land on shared edges between
        /// merged pieces, which the original logic mistook for narrow slivers and pruned
        /// (eating player-built rampart tops). Wall-top slivers are still caught by
        /// <see cref="MinRegionArea"/> via PruneSmallRegions. A smarter narrow check
        /// (multi-point sampling inside the region) can re-enable this in the future.</summary>
        private const float MinCentroidEdgeDist = 0f;
        /// <summary>Max combined Y spread for the coplanar merge pass to apply.
        /// Derived from the villager agent's max slope (27°) over one subdivision cell:
        /// 3m × tan(27°) ≈ 1.53m. Regions sharing a vertex within this Y spread are
        /// merged. The 2m height band in spatial subdivision still separates true floors.</summary>
        private const float CoplanarMergeMaxDeltaY = 1.5f;
        /// <summary>Max internal ΔY for a region to be considered a flat plane
        /// in the enclosure pruning pass.</summary>
        private const float EnclosurePlaneDeltaY = 0.2f;
        /// <summary>Max distance Region A may sit below Region B for enclosure
        /// pruning. A must be lower (or equal) and within this distance.</summary>
        private const float EnclosureMaxBelow = 0.5f;

        // --- Terrain-only "is this spot walkable" checks ---
        // Slope cutoff matches the villager agent's max climbable slope (27°,
        // cloned from Humanoid). Looser values (32°, 45°) over-merged regions
        // in coplanar pass, which drifted area-weighted centroids far from
        // beds and broke centroid-based BFS seeding (seeds=1 with fallback,
        // dropping reachable count from 12 to ~2). 27° keeps centroids local
        // enough that both beds reliably seed.
        private const float MinTerrainNormalY = 0.891007f; // cos(27°)

        // Villager-sized vertical capsule used to probe for obstructions on
        // terrain spots. Tuned smaller than the agent's strict body bounds
        // (radius 0.3 vs ~0.4) to avoid catching pieces NEAR but not actually
        // on the terrain (paving edges, banners, fences). Height 1.4 (vs
        // ~1.8) skips overhead clearance which the agent NavMesh already
        // accounts for. Lift 0.1m above centroid so fuzzy heightmap bumps
        // and the high side of a sloped tri don't self-hit.
        private const float CapsuleRadius = 0.3f;
        private const float CapsuleHeight = 1.4f;
        private const float CapsuleLift = 0.25f;

        // Same layers as the piece bake (Default + static_solid + piece).
        // Deliberately excludes "terrain" so the capsule doesn't self-hit
        // the heightmap it's standing on.
        private static readonly int s_blockMask =
            LayerMask.GetMask("Default", "static_solid", "piece");

        // Terrain regions smaller than this AND with no shared-edge links to
        // other regions are dropped as inside-wall pockets. The capsule pass
        // legitimately splits terrain at wall lines (a villager can't cross),
        // but the resulting patches inside walls have no path in or out and
        // just clutter the graph. 16 m² ≈ 4×4m — catches small towers and
        // multi-piece walled enclosures while preserving e.g. a 5×5m alcove
        // a player carved into the side of a hill. NOTE: this still only
        // catches enclosures that survive as a SINGLE region after subdiv;
        // larger enclosures split into multiple sub-regions all "linked" to
        // each other will need a BFS-from-bed-seed reachability pass.
        private const float TerrainIsolatedMaxArea = 16f; // m²

        internal struct CachedTriangle
        {
            public Vector3 V0, V1, V2;
            public string RegionId;
            public SurfaceKind Kind;
            // Source Unity layer (NavMeshBuildSource.component.gameObject.layer).
            // -1 if unknown (synthetic/phantom sources or pre-tag legacy entries).
            public int Layer;
        }

        /// <summary>
        /// Filtered triangles from the most recent build, tagged with their
        /// region assignment. Consumed by <see cref="PathDebugRenderer"/> for
        /// wireframe overlay.
        /// </summary>
        internal static List<CachedTriangle> CachedTriangles { get; set; }
            = new List<CachedTriangle>();

        /// <summary>
        /// Clear cached per-triangle state on world unload / hot reload.
        /// Without this, stale CachedTriangle entries from a prior assembly
        /// (carrying default/zero Layer values from before the field existed,
        /// or stale region IDs from a previous bake) would taint the next
        /// partition's RubberBandPrune static_solid mask sweep.
        /// </summary>
        [RegisterCleanup]
        public static void ClearCachedState()
        {
            CachedTriangles.Clear();
        }

        internal struct BuildResult
        {
            public HashSet<string> RegionIds;
            public Dictionary<string, Vector3> Centroids;
            public List<RegionLink> Links;
            public Dictionary<long, string> LookupGrid;
            public List<(string id, Vector3 center, Vector3 outDir)> BoundaryCells;
            public List<CachedTriangle> Triangles;
            public SurfaceKind Kind;

            // For each surviving region, the set of OTHER regions it shares
            // a triangle edge with within this pass. Used by the handler-
            // level cross-kind BFS reachability prune.
            public Dictionary<string, HashSet<string>> Adjacency;

            // For each surviving region, the set of quantized vertex
            // positions (5cm quantum, packed XYZ → long). Used for cross-
            // kind adjacency: two regions from different passes share a
            // position iff they physically meet at that 5cm cell.
            public Dictionary<string, HashSet<long>> RegionVertexPositions;

            // For each surviving region, the axis-aligned 3D bounding box
            // of its triangle vertices. Used by the handler-level cross-kind
            // prune for edge-to-edge distance checks between terrain and
            // piece regions (more honest than centroid-to-centroid since
            // it doesn't penalize sprawling regions).
            public Dictionary<string, Bounds> RegionBounds;

            // Per-region list of triangle vertex world positions. Used by
            // the handler-level cross-kind prune for vertex-to-vertex min
            // distance checks — handles non-convex regions correctly where
            // AABB-distance falsely matches a piece sitting inside a
            // sprawling terrain region's bounding box.
            public Dictionary<string, List<Vector3>> RegionVertexList;
        }

        internal static BuildResult BuildFromTriangulation(
            SurfaceKind kind,
            float minX, float minZ, float maxX, float maxZ,
            List<Vector3> beds)
        {
            var result = new BuildResult
            {
                RegionIds = new HashSet<string>(),
                Centroids = new Dictionary<string, Vector3>(),
                Links = new List<RegionLink>(),
                LookupGrid = new Dictionary<long, string>(),
                BoundaryCells = new List<(string, Vector3, Vector3)>(),
                Triangles = new List<CachedTriangle>(),
                Kind = kind,
                Adjacency = new Dictionary<string, HashSet<string>>(),
                RegionVertexPositions = new Dictionary<string, HashSet<long>>(),
                RegionBounds = new Dictionary<string, Bounds>(),
                RegionVertexList = new Dictionary<string, List<Vector3>>(),
            };
            if (beds == null || beds.Count == 0) return result;

            int agentTypeId = VillagerAgentType.IsRegistered
                ? VillagerAgentType.UnityAgentTypeID
                : VillagerAgentType.ResolveValheimHumanoidAgentTypeID();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeId,
                areaMask = NavMesh.AllAreas
            };

            // Triangulation comes from NavMeshBakeManager's collected source list,
            // not NavMesh.CalculateTriangulation(). The latter returns only
            // static scene-baked data and silently omits runtime-added meshes
            // (both Valheim's runtime bakes AND ours), which means player-built
            // structures (ramparts, stairs added after world load) never get
            // any cached triangles. Sourcing from the bake's input list ensures
            // we see exactly the geometry we baked.
            var (verts, idx, triangleLayers) = NavMeshBakeManager.ExtractBakedTriangles(kind, minX, minZ, maxX, maxZ);
            if (verts == null || idx == null || idx.Length < 3)
            {
                Plugin.Log?.LogWarning($"[Region] No baked sources to extract triangles from (kind={kind}) — has NavMeshBakeManager.BakeVillage run?");
                return result;
            }
            int triCount = idx.Length / 3;

            // --- Filter triangles: normal, bounds, polygon/bed, villager agent, terrain ---
            var rawAccepted = new List<int>();
            var triCentroids = new Vector3[triCount];
            int rejNormal = 0, rejBounds = 0, rejAgent = 0, rejTerrain = 0;
            int rejSteep = 0, rejBlocked = 0;

            // Walkable surfaces have an upward-facing normal. cos(60°) = 0.5, so
            // a triangle whose normal.y < 0.5 is steeper than ~60° from horizontal
            // — i.e. a wall or ceiling face. Skip them here so the expensive
            // per-triangle NavMesh.SamplePosition agent check doesn't run on
            // ~70% of input geometry that obviously isn't walkable.
            const float MinNormalY = 0.5f;

            // Piece scoping is deliberately patrol-independent:
            // VillageAreaManager polygons are derived from the previous
            // region graph via BoundaryMapper + patrol circuits, so feeding
            // them back into piece scoping is circular and self-reinforces
            // outside-wall leaks. Pieces rely on the bake-bounds reject +
            // the agent NavMesh sample below.

            for (int t = 0; t < triCount; t++)
            {
                var v0 = verts[idx[t * 3]];
                var v1 = verts[idx[t * 3 + 1]];
                var v2 = verts[idx[t * 3 + 2]];
                var c = (v0 + v1 + v2) / 3f;
                triCentroids[t] = c;

                var normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                if (normal.y < MinNormalY)
                { rejNormal++; continue; }

                if (c.x < minX || c.x > maxX || c.z < minZ || c.z > maxZ)
                { rejBounds++; continue; }

                if (kind == SurfaceKind.Terrain)
                {
                    // Terrain scope = bake bounds only (rejBounds above is
                    // the only spatial filter). Deliberately patrol-independent:
                    // the polygon path re-introduces the chicken-egg dependency
                    // the skill warns about, and bed-distance drops outlying
                    // terrain when the village extends past 30m from any bed.
                    // Bake bounds = village beds + patrol bounds + 30m pad,
                    // which is already a tight, patrol-influenced-but-bake-time
                    // scope set in RegionPartitionHandler.

                    // Steep terrain reject (stricter than the generic MinNormalY
                    // 60° cutoff): drops cliffs the villager agent can't climb.
                    // Cheap, run before the capsule check.
                    if (normal.y < MinTerrainNormalY)
                    { rejSteep++; continue; }

                    // Villager-sized capsule overlap: catches walls, decorative
                    // pieces, rocks, and modded items by physical collision —
                    // independent of prefab name. Expensive (Physics broadphase
                    // + narrowphase per call), so it runs after every cheaper
                    // filter has had a chance to reject.
                    Vector3 capP0 = c + Vector3.up * (CapsuleLift + CapsuleRadius);
                    Vector3 capP1 = c + Vector3.up * (CapsuleLift + CapsuleHeight - CapsuleRadius);
                    if (Physics.CheckCapsule(capP0, capP1, CapsuleRadius, s_blockMask))
                    { rejBlocked++; continue; }
                }
                // Piece pass: no patrol-polygon / bed-distance gating. Falls
                // through to the shared agent NavMesh sample below.

                if (!NavMesh.SamplePosition(c, out NavMeshHit hit, AgentFilterRadius, filter) ||
                    Vector3.Distance(hit.position, c) > AgentFilterRadius)
                { rejAgent++; continue; }

                if (IsAnyVertexBelowTerrain(v0, v1, v2))
                { rejTerrain++; continue; }

                rawAccepted.Add(t);
            }

            // --- Deduplicate overlapping triangles ---
            // CalculateTriangulation() merges surfaces from all baked agent types.
            // Two agent types over the same colliders produce near-identical triangles
            // with separate vertex sets, so they appear as distinct entries.
            // Quantize centroids to 1cm and keep the first occurrence per cell.
            var accepted = new List<int>(rawAccepted.Count);
            var seenCells = new HashSet<(int, int, int)>();
            foreach (int t in rawAccepted)
            {
                var c = triCentroids[t];
                var cell = (
                    Mathf.RoundToInt(c.x * 100f),
                    Mathf.RoundToInt(c.y * 100f),
                    Mathf.RoundToInt(c.z * 100f));
                if (seenCells.Add(cell))
                    accepted.Add(t);
            }
            int dedupDropped = rawAccepted.Count - accepted.Count;

            DebugLog.Event("Region", "triangulation",
                ("kind", kind),
                ("total", triCount), ("filtered", rawAccepted.Count), ("accepted", accepted.Count),
                ("rej_normal", rejNormal), ("rej_bounds", rejBounds),
                ("rej_agent", rejAgent), ("rej_terrain", rejTerrain),
                ("rej_steep", rejSteep), ("rej_blocked", rejBlocked),
                ("rej_dedup", dedupDropped));
            if (accepted.Count == 0) return result;

            // --- Adjacency from shared edges ---
            var edgeToTris = new Dictionary<long, List<int>>();
            foreach (int t in accepted)
            {
                int i0 = idx[t * 3], i1 = idx[t * 3 + 1], i2 = idx[t * 3 + 2];
                AddEdge(edgeToTris, i0, i1, t);
                AddEdge(edgeToTris, i1, i2, t);
                AddEdge(edgeToTris, i2, i0, t);
            }

            // Terrain only: break adjacency edges that pass through a piece
            // collider (typically a wall). Terrain heightmap is a 1m grid and
            // walls are ~0.3m thick — two terrain tris on opposite sides of
            // a wall can both pass the per-tri capsule check (centroids ~0.5m
            // from wall, capsule radius 0.4m), but their shared edge goes
            // THROUGH the wall. Without this break, union-find merges them
            // into one region that visually spans both sides of the wall.
            // Same idea was tried for pieces but the capsule sits in the
            // head-height band (0.55-1.35m above edge midpoint) where village
            // interiors are full of overhead beams/banners/eaves — broke
            // 30%+ of piece edges incorrectly. Pieces need a different
            // primitive; see workflow scratch piece-region-wall-aware.md.
            int rejEdge = 0;
            if (kind == SurfaceKind.Terrain)
            {
                var edgesToRemove = new List<long>();
                foreach (var kvp in edgeToTris)
                {
                    long key = kvp.Key;
                    int v1 = (int)(key >> 32);
                    int v2 = (int)(key & 0xFFFFFFFFL);
                    Vector3 mid = (verts[v1] + verts[v2]) * 0.5f;
                    Vector3 ep0 = mid + Vector3.up * (CapsuleLift + CapsuleRadius);
                    Vector3 ep1 = mid + Vector3.up * (CapsuleLift + CapsuleHeight - CapsuleRadius);
                    if (Physics.CheckCapsule(ep0, ep1, CapsuleRadius, s_blockMask))
                        edgesToRemove.Add(key);
                }
                foreach (long key in edgesToRemove) edgeToTris.Remove(key);
                rejEdge = edgesToRemove.Count;
                Plugin.Log?.LogInfo(
                    $"[Region] Edge-midpoint capsule break (kind={kind}): " +
                    $"removed {rejEdge}/{rejEdge + edgeToTris.Count} edges " +
                    $"({100f * rejEdge / Mathf.Max(1, rejEdge + edgeToTris.Count):F1}%)");
            }

            // --- Union-Find connected components ---
            var parent = new int[triCount];
            var ufRank = new int[triCount];
            foreach (int t in accepted) parent[t] = t;

            foreach (var tris in edgeToTris.Values)
            {
                for (int i = 0; i < tris.Count; i++)
                for (int j = i + 1; j < tris.Count; j++)
                    Union(parent, ufRank, tris[i], tris[j]);
            }

            // --- Group by (component root, spatial cell) → region ---
            var groups = new Dictionary<long, List<int>>();
            foreach (int t in accepted)
            {
                int root = Find(parent, t);
                var c = triCentroids[t];
                int gx = Mathf.FloorToInt(c.x / SubdivCellSize);
                int gz = Mathf.FloorToInt(c.z / SubdivCellSize);
                int hb = Mathf.FloorToInt(c.y / HeightBandSize);
                long key = PackGroup(root, gx, gz, hb);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }
                list.Add(t);
            }

            Plugin.Log?.LogInfo($"[Region] Components → {groups.Count} regions (kind={kind}, subdiv {SubdivCellSize}m)");

            // --- Build region centroids ---
            int rIdx = 0;
            string idPrefix = kind == SurfaceKind.Terrain ? "t" : "p";
            var triToRegion = new Dictionary<int, string>();
            // regionToComponentRoot[regionId] = union-find tri root. Used by
            // MergeCoplanarRegions to keep terrain wall-respecting: a merge
            // between two terrain regions is only allowed if they were in the
            // same UF component (i.e., transitively connected via surviving
            // edges after the edge-midpoint capsule break). Vertex coincidence
            // alone isn't enough adjacency proof when walls can sit between.
            var regionToComponentRoot = new Dictionary<string, int>();
            foreach (var kv in groups)
            {
                string regionId = $"{idPrefix}{rIdx++}";
                var tris = kv.Value;
                if (tris.Count > 0)
                    regionToComponentRoot[regionId] = Find(parent, tris[0]);

                float totalArea = 0f;
                var weighted = Vector3.zero;
                foreach (int t in tris)
                {
                    float a = TriArea(verts[idx[t * 3]], verts[idx[t * 3 + 1]], verts[idx[t * 3 + 2]]);
                    totalArea += a;
                    weighted += triCentroids[t] * a;
                }
                var centroid = totalArea > 0.001f ? weighted / totalArea : triCentroids[tris[0]];

                result.RegionIds.Add(regionId);
                result.Centroids[regionId] = centroid;
                foreach (int t in tris) triToRegion[t] = regionId;
            }

            // --- Merge coplanar regions split by spatial subdivision ---
            MergeCoplanarRegions(accepted, idx, verts, triCentroids,
                triToRegion, result, regionToComponentRoot);

            // --- Prune regions smaller than MinRegionArea ---
            PruneSmallRegions(accepted, idx, verts, triToRegion, result);

            // --- Prune narrow regions (SurfaceWide=false + small area) ---
            PruneNarrowRegions(accepted, idx, verts, triToRegion, result, filter);

            // --- Prune regions enclosed by a larger coplanar region ---
            PruneEnclosedRegions(accepted, idx, verts, triToRegion, result);

            // --- Terrain only: prune small AND isolated regions ---
            // The capsule pass splits terrain at wall lines, leaving small
            // unreachable pockets inside wall corners. Drop them if both
            // tiny (<TerrainIsolatedMaxArea) AND share no edges with any
            // other surviving region.
            if (kind == SurfaceKind.Terrain)
                PruneIsolatedSmallTerrain(accepted, idx, verts, edgeToTris, triToRegion, result);

            // --- Build per-region adjacency + quantized vertex positions ---
            // Both are consumed by the cross-kind BFS reachability prune
            // running later in RegionPartitionHandler. Adjacency captures
            // in-pass region-to-region links via shared edges; the vertex
            // position sets enable cross-kind matching between terrain and
            // piece regions that physically touch.
            foreach (var rid in result.RegionIds)
                result.Adjacency[rid] = new HashSet<string>();
            foreach (var tris in edgeToTris.Values)
            {
                string firstRid = null;
                HashSet<string> regionsOnEdge = null;
                foreach (int t in tris)
                {
                    if (!triToRegion.TryGetValue(t, out string rid)) continue;
                    if (firstRid == null) { firstRid = rid; continue; }
                    if (rid == firstRid) continue;
                    if (regionsOnEdge == null) regionsOnEdge = new HashSet<string> { firstRid };
                    regionsOnEdge.Add(rid);
                }
                if (regionsOnEdge == null) continue;
                var rlist = new List<string>(regionsOnEdge);
                for (int i = 0; i < rlist.Count; i++)
                for (int j = i + 1; j < rlist.Count; j++)
                {
                    if (result.Adjacency.TryGetValue(rlist[i], out var na)) na.Add(rlist[j]);
                    if (result.Adjacency.TryGetValue(rlist[j], out var nb)) nb.Add(rlist[i]);
                }
            }

            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                if (!result.RegionVertexPositions.TryGetValue(rid, out var posSet))
                {
                    posSet = new HashSet<long>();
                    result.RegionVertexPositions[rid] = posSet;
                }
                for (int vi = 0; vi < 3; vi++)
                {
                    var v = verts[idx[t * 3 + vi]];
                    posSet.Add(PackQuantizedPos(v));

                    if (result.RegionBounds.TryGetValue(rid, out var b))
                    {
                        b.Encapsulate(v);
                        result.RegionBounds[rid] = b;
                    }
                    else
                    {
                        result.RegionBounds[rid] = new Bounds(v, Vector3.zero);
                    }

                    if (!result.RegionVertexList.TryGetValue(rid, out var vlist))
                    {
                        vlist = new List<Vector3>();
                        result.RegionVertexList[rid] = vlist;
                    }
                    vlist.Add(v);
                }
            }

            // --- Cross-region links from shared edges ---
            var seenPairs = new HashSet<string>();
            foreach (var kv in edgeToTris)
            {
                var tris = kv.Value;
                for (int i = 0; i < tris.Count; i++)
                for (int j = i + 1; j < tris.Count; j++)
                {
                    if (!triToRegion.TryGetValue(tris[i], out string ra)) continue;
                    if (!triToRegion.TryGetValue(tris[j], out string rb)) continue;
                    if (ra == rb) continue;
                    string pairKey = string.CompareOrdinal(ra, rb) < 0 ? $"{ra}|{rb}" : $"{rb}|{ra}";
                    if (!seenPairs.Add(pairKey)) continue;

                    int ev1 = (int)(kv.Key >> 32), ev2 = (int)(kv.Key & 0xFFFFFFFFL);
                    Vector3 edgeMid = (verts[ev1] + verts[ev2]) * 0.5f;
                    result.Links.Add(new RegionLink
                    {
                        FromRegionId = ra, ToRegionId = rb,
                        LinkType = RegionLinkType.Slope,
                        PositionStart = result.Centroids[ra],
                        PositionEnd = result.Centroids[rb]
                    });
                }
            }

            // --- Boundary detection ---
            DetectBoundary(edgeToTris, triToRegion, result);

            // --- Rasterized lookup grid ---
            BuildLookupGrid(accepted, idx, verts, triToRegion, result);

            // --- Cache triangles for debug visualization ---
            // Caller (RegionPartitionHandler) is responsible for concatenating
            // the per-kind triangle lists and assigning to the static
            // CachedTriangles after both passes finish. We just emit them in
            // BuildResult here so the second pass doesn't clobber the first.
            var cached = result.Triangles;
            cached.Capacity = accepted.Count;
            float yMin = float.MaxValue, yMax = float.MinValue;
            float maxEdge = 0f;
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                var tv0 = verts[idx[t * 3]];
                var tv1 = verts[idx[t * 3 + 1]];
                var tv2 = verts[idx[t * 3 + 2]];
                cached.Add(new CachedTriangle
                {
                    V0 = tv0, V1 = tv1, V2 = tv2,
                    RegionId = rid,
                    Kind = kind,
                    Layer = (triangleLayers != null && t < triangleLayers.Length) ? triangleLayers[t] : -1,
                });

                float lo = Mathf.Min(tv0.y, Mathf.Min(tv1.y, tv2.y));
                float hi = Mathf.Max(tv0.y, Mathf.Max(tv1.y, tv2.y));
                if (lo < yMin) yMin = lo;
                if (hi > yMax) yMax = hi;
                float e0 = Vector3.Distance(tv0, tv1);
                float e1 = Vector3.Distance(tv1, tv2);
                float e2 = Vector3.Distance(tv2, tv0);
                float em = Mathf.Max(e0, Mathf.Max(e1, e2));
                if (em > maxEdge) maxEdge = em;
            }

            if (cached.Count > 0)
            {
                var sample = cached[0];
                Plugin.Log?.LogInfo(
                    $"[Region] TriCache (kind={kind}): {cached.Count} tris, " +
                    $"Y range [{yMin:F1}, {yMax:F1}], max edge {maxEdge:F1}m. " +
                    $"Sample tri: ({sample.V0.x:F1},{sample.V0.y:F1},{sample.V0.z:F1}) " +
                    $"({sample.V1.x:F1},{sample.V1.y:F1},{sample.V1.z:F1}) " +
                    $"({sample.V2.x:F1},{sample.V2.y:F1},{sample.V2.z:F1})");
            }

            Plugin.Log?.LogInfo(
                $"[Region] Built {result.RegionIds.Count} regions (kind={kind}), " +
                $"{result.Links.Count} links, {result.LookupGrid.Count} lookup cells, " +
                $"{result.BoundaryCells.Count} boundary regions");

            return result;
        }

        /// <summary>
        /// Add door links by sampling both sides of each door and looking up
        /// their regions in the already-built graph.
        /// </summary>
        internal static void CollectDoorLinks(RegionGraph graph,
            float minX, float minZ, float maxX, float maxZ,
            List<RegionLink> links)
        {
            var doors = Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (doors == null || graph == null) return;

            float doorOff = RegionGraph.CellSize * 0.6f;
            int linked = 0;
            foreach (var door in doors)
            {
                if (door == null) continue;
                var pos = door.transform.position;
                if (pos.x < minX - 3 || pos.x > maxX + 3 ||
                    pos.z < minZ - 3 || pos.z > maxZ + 3) continue;
                if (door.GetComponentInParent<Piece>() == null) continue;

                Vector3 fwd = door.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) continue;
                fwd.Normalize();

                var sideA = pos - fwd * doorOff;
                var sideB = pos + fwd * doorOff;
                string idA = graph.PointToRegionId(sideA);
                string idB = graph.PointToRegionId(sideB);
                if (idA == null || idB == null || idA == idB) continue;

                linked++;
                links.Add(new RegionLink
                {
                    FromRegionId = idA, ToRegionId = idB,
                    LinkType = RegionLinkType.Door,
                    PositionStart = sideA, PositionEnd = sideB
                });
            }
            Plugin.Log?.LogInfo($"[Region] Door links: {linked} added");
        }

        #region Helpers

        /// <summary>
        /// Post-processing pass: merge regions that were split by the 3m spatial
        /// subdivision but are coplanar (flat) and share edges. Uses Union-Find
        /// on region IDs, then rebuilds centroids for merged regions.
        /// </summary>
        private static void MergeCoplanarRegions(
            List<int> accepted, int[] idx, Vector3[] verts, Vector3[] triCentroids,
            Dictionary<int, string> triToRegion,
            BuildResult result,
            Dictionary<string, int> regionToComponentRoot)
        {
            // Compute Y range per region
            var regionY = new Dictionary<string, (float min, float max)>();
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                float y0 = verts[idx[t * 3]].y;
                float y1 = verts[idx[t * 3 + 1]].y;
                float y2 = verts[idx[t * 3 + 2]].y;
                float lo = Mathf.Min(y0, Mathf.Min(y1, y2));
                float hi = Mathf.Max(y0, Mathf.Max(y1, y2));
                if (regionY.TryGetValue(rid, out var cur))
                    regionY[rid] = (Mathf.Min(lo, cur.min), Mathf.Max(hi, cur.max));
                else
                    regionY[rid] = (lo, hi);
            }

            // Build position → region set map. Vertex positions are quantized to
            // 5cm so that vertices from different agent type meshes at the same
            // physical location match even when their indices differ.
            const float posQ = 0.05f;
            var posToRegions = new Dictionary<(int, int, int), HashSet<string>>();
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                for (int vi = 0; vi < 3; vi++)
                {
                    var v = verts[idx[t * 3 + vi]];
                    var qv = (
                        Mathf.RoundToInt(v.x / posQ),
                        Mathf.RoundToInt(v.y / posQ),
                        Mathf.RoundToInt(v.z / posQ));
                    if (!posToRegions.TryGetValue(qv, out var regions))
                    {
                        regions = new HashSet<string>();
                        posToRegions[qv] = regions;
                    }
                    regions.Add(rid);
                }
            }

            // Union-Find on region IDs: merge regions sharing a vertex position
            // when both are coplanar (flat, small Y spread).
            var mergeParent = new Dictionary<string, string>();
            foreach (string rid in result.RegionIds)
                mergeParent[rid] = rid;

            foreach (var regions in posToRegions.Values)
            {
                if (regions.Count < 2) continue;
                var rlist = new List<string>(regions);
                for (int i = 0; i < rlist.Count; i++)
                for (int j = i + 1; j < rlist.Count; j++)
                {
                    string rootA = FindRoot(mergeParent, rlist[i]);
                    string rootB = FindRoot(mergeParent, rlist[j]);
                    if (rootA == rootB) continue;

                    // Terrain only: never merge across union-find components.
                    // Vertex coincidence between two terrain regions in
                    // different components means a wall (or other obstruction)
                    // sits between them — the edge-midpoint capsule break
                    // already separated them. Bridging here would re-merge
                    // exactly what we just broke.
                    if (result.Kind == SurfaceKind.Terrain &&
                        regionToComponentRoot.TryGetValue(rootA, out int compA) &&
                        regionToComponentRoot.TryGetValue(rootB, out int compB) &&
                        compA != compB)
                        continue;

                    if (!regionY.TryGetValue(rootA, out var yA)) continue;
                    if (!regionY.TryGetValue(rootB, out var yB)) continue;

                    float mergedMin = Mathf.Min(yA.min, yB.min);
                    float mergedMax = Mathf.Max(yA.max, yB.max);
                    if (mergedMax - mergedMin > CoplanarMergeMaxDeltaY) continue;

                    mergeParent[rootB] = rootA;
                    regionY[rootA] = (mergedMin, mergedMax);
                }
            }

            // Count how many triangles move to a different region
            int reassigned = 0;
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                string root = FindRoot(mergeParent, rid);
                if (root != rid)
                {
                    triToRegion[t] = root;
                    reassigned++;
                }
            }
            if (reassigned == 0) return;

            // Rebuild RegionIds and Centroids from the merged mapping
            result.RegionIds.Clear();
            result.Centroids.Clear();

            var trisByRegion = new Dictionary<string, List<int>>();
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                if (!trisByRegion.TryGetValue(rid, out var list))
                {
                    list = new List<int>();
                    trisByRegion[rid] = list;
                }
                list.Add(t);
            }

            foreach (var kv in trisByRegion)
            {
                string regionId = kv.Key;
                result.RegionIds.Add(regionId);

                float totalArea = 0f;
                var weighted = Vector3.zero;
                foreach (int t in kv.Value)
                {
                    float a = TriArea(verts[idx[t * 3]], verts[idx[t * 3 + 1]], verts[idx[t * 3 + 2]]);
                    totalArea += a;
                    weighted += triCentroids[t] * a;
                }
                result.Centroids[regionId] = totalArea > 0.001f
                    ? weighted / totalArea
                    : triCentroids[kv.Value[0]];
            }

            Plugin.Log?.LogInfo(
                $"[Region] Coplanar merge: {reassigned} tris reassigned, " +
                $"{trisByRegion.Count} regions after merge");
        }

        /// <summary>
        /// Remove regions whose total triangle area is below <see cref="MinRegionArea"/>.
        /// These are typically thin strips on wall tops, gate edges, or collider seams
        /// that the NavMesh tessellation produces but are too small for NPC pathfinding.
        /// </summary>
        private static void PruneSmallRegions(
            List<int> accepted, int[] idx, Vector3[] verts,
            Dictionary<int, string> triToRegion,
            BuildResult result)
        {
            var regionArea = new Dictionary<string, float>();
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                float a = TriArea(verts[idx[t * 3]], verts[idx[t * 3 + 1]], verts[idx[t * 3 + 2]]);
                if (regionArea.TryGetValue(rid, out float cur))
                    regionArea[rid] = cur + a;
                else
                    regionArea[rid] = a;
            }

            var tooSmall = new HashSet<string>();
            foreach (var kv in regionArea)
            {
                if (kv.Value < MinRegionArea)
                    tooSmall.Add(kv.Key);
            }
            if (tooSmall.Count == 0) return;

            // Compute centroids of dropped regions for diagnostics.
            var dropCentroids = new Dictionary<string, Vector3>();
            foreach (string rid in tooSmall)
            {
                if (result.Centroids.TryGetValue(rid, out var c))
                    dropCentroids[rid] = c;
            }

            foreach (string rid in tooSmall)
            {
                result.RegionIds.Remove(rid);
                result.Centroids.Remove(rid);
            }

            // Remove triangle→region entries so pruned regions don't appear in
            // links, boundary detection, or the lookup grid.
            var toRemove = new List<int>();
            foreach (var kv in triToRegion)
            {
                if (tooSmall.Contains(kv.Value))
                    toRemove.Add(kv.Key);
            }
            foreach (int t in toRemove)
                triToRegion.Remove(t);

            Plugin.Log?.LogInfo(
                $"[Region] Area pruning: dropped {tooSmall.Count} regions < {MinRegionArea}m², " +
                $"{result.RegionIds.Count} regions remaining");
            DebugLog.List("RegionPrune", "area_dropped",
                dropCentroids.Select(kv => (object)$"{kv.Key}:{kv.Value.x:F1},{kv.Value.y:F1},{kv.Value.z:F1}:area={regionArea[kv.Key]:F2}"));
        }

        /// <summary>
        /// Drop regions whose centroid is closer than <see cref="MinCentroidEdgeDist"/>
        /// to the nearest NavMesh edge. These regions sit on wall tops, gate edges, or
        /// thin collider seams that are too narrow for a villager to walk on.
        /// </summary>
        private static void PruneNarrowRegions(
            List<int> accepted, int[] idx, Vector3[] verts,
            Dictionary<int, string> triToRegion,
            BuildResult result, NavMeshQueryFilter filter)
        {
            var narrow = new HashSet<string>();
            foreach (string rid in result.RegionIds)
            {
                if (!result.Centroids.TryGetValue(rid, out Vector3 centroid)) continue;
                if (!NavMesh.FindClosestEdge(centroid, out NavMeshHit hit, filter))
                {
                    // "No edge found" is NOT the same as "too narrow". On a
                    // freshly runtime-baked surface, FindClosestEdge frequently
                    // fails on perfectly-walkable centroids that sit in the
                    // interior of a fragment with no nearby boundary. Treating
                    // this as a prune would (and did) wipe out player-built
                    // rampart tops. Skip — wall-top slivers are still caught
                    // by the explicit distance check below.
                    continue;
                }
                if (hit.distance < MinCentroidEdgeDist)
                    narrow.Add(rid);
            }
            if (narrow.Count == 0) return;

            // Capture centroids + edge distances before removing for diagnostics.
            var dropDetails = new List<string>();
            foreach (string rid in narrow)
            {
                if (!result.Centroids.TryGetValue(rid, out var c)) continue;
                NavMesh.FindClosestEdge(c, out NavMeshHit eh, filter);
                dropDetails.Add($"{rid}:{c.x:F1},{c.y:F1},{c.z:F1}:edge={eh.distance:F3}");
            }

            foreach (string rid in narrow)
            {
                result.RegionIds.Remove(rid);
                result.Centroids.Remove(rid);
            }

            var toRemove = new List<int>();
            foreach (var kv in triToRegion)
            {
                if (narrow.Contains(kv.Value))
                    toRemove.Add(kv.Key);
            }
            foreach (int t in toRemove)
                triToRegion.Remove(t);

            Plugin.Log?.LogInfo(
                $"[Region] Narrow pruning: dropped {narrow.Count} regions " +
                $"(edge dist < {MinCentroidEdgeDist}m), " +
                $"{result.RegionIds.Count} regions remaining");
            DebugLog.List("RegionPrune", "narrow_dropped",
                dropDetails.Cast<object>());
        }

        /// <summary>
        /// Drop Region B when a larger Region A sits just below it and fully
        /// encloses it in XZ. Catches duplicate NavMesh layers on gates, walls,
        /// and other colliders that produce near-identical surfaces at slightly
        /// different heights.
        /// </summary>
        private static void PruneEnclosedRegions(
            List<int> accepted, int[] idx, Vector3[] verts,
            Dictionary<int, string> triToRegion,
            BuildResult result)
        {
            var bounds = new Dictionary<string, (float yMin, float yMax,
                float xMin, float xMax, float zMin, float zMax)>();

            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                Vector3 v0 = verts[idx[t * 3]];
                Vector3 v1 = verts[idx[t * 3 + 1]];
                Vector3 v2 = verts[idx[t * 3 + 2]];

                float txMin = Mathf.Min(v0.x, Mathf.Min(v1.x, v2.x));
                float txMax = Mathf.Max(v0.x, Mathf.Max(v1.x, v2.x));
                float tzMin = Mathf.Min(v0.z, Mathf.Min(v1.z, v2.z));
                float tzMax = Mathf.Max(v0.z, Mathf.Max(v1.z, v2.z));
                float tyMin = Mathf.Min(v0.y, Mathf.Min(v1.y, v2.y));
                float tyMax = Mathf.Max(v0.y, Mathf.Max(v1.y, v2.y));

                if (bounds.TryGetValue(rid, out var cur))
                {
                    bounds[rid] = (
                        Mathf.Min(tyMin, cur.yMin), Mathf.Max(tyMax, cur.yMax),
                        Mathf.Min(txMin, cur.xMin), Mathf.Max(txMax, cur.xMax),
                        Mathf.Min(tzMin, cur.zMin), Mathf.Max(tzMax, cur.zMax));
                }
                else
                {
                    bounds[rid] = (tyMin, tyMax, txMin, txMax, tzMin, tzMax);
                }
            }

            var enclosed = new HashSet<string>();
            var rids = new List<string>(bounds.Keys);

            for (int i = 0; i < rids.Count; i++)
            {
                var a = bounds[rids[i]];
                if (a.yMax - a.yMin >= EnclosurePlaneDeltaY) continue;
                float aMidY = (a.yMin + a.yMax) * 0.5f;

                for (int j = 0; j < rids.Count; j++)
                {
                    if (i == j || enclosed.Contains(rids[j])) continue;
                    var b = bounds[rids[j]];
                    if (b.yMax - b.yMin >= EnclosurePlaneDeltaY) continue;
                    float bMidY = (b.yMin + b.yMax) * 0.5f;

                    if (aMidY > bMidY) continue;
                    if (bMidY - aMidY > EnclosureMaxBelow) continue;
                    if (a.xMin > b.xMin || a.xMax < b.xMax) continue;
                    if (a.zMin > b.zMin || a.zMax < b.zMax) continue;

                    enclosed.Add(rids[j]);
                }
            }

            if (enclosed.Count == 0) return;

            // Capture centroids before removal for diagnostics.
            var dropDetails = new List<string>();
            foreach (string rid in enclosed)
            {
                if (!result.Centroids.TryGetValue(rid, out var c)) continue;
                dropDetails.Add($"{rid}:{c.x:F1},{c.y:F1},{c.z:F1}");
            }

            foreach (string rid in enclosed)
            {
                result.RegionIds.Remove(rid);
                result.Centroids.Remove(rid);
            }

            var toRemove = new List<int>();
            foreach (var kv in triToRegion)
            {
                if (enclosed.Contains(kv.Value))
                    toRemove.Add(kv.Key);
            }
            foreach (int t in toRemove)
                triToRegion.Remove(t);

            Plugin.Log?.LogInfo(
                $"[Region] Enclosure pruning: dropped {enclosed.Count} regions enclosed by " +
                $"larger coplanar regions, {result.RegionIds.Count} remaining");
            DebugLog.List("RegionPrune", "enclosed_dropped", dropDetails.Cast<object>());
        }

        private static string FindRoot(Dictionary<string, string> parent, string id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }

        /// <summary>
        /// Pack a Vector3 quantized to <see cref="QuantumStep"/> into a long
        /// key. Used by the cross-kind BFS to detect terrain↔piece adjacency
        /// via shared vertex positions. 25cm bucket is loose enough to
        /// tolerate the snap-grid mismatch between heightmap (1m) and piece
        /// snap points (~0.25m) — at 5cm, terrain and piece vertices that
        /// physically touch within a few cm round to different buckets.
        /// Each axis fits in 21 bits at 25cm over the Valheim world.
        /// </summary>
        internal const float QuantumStep = 0.25f;
        internal static long PackQuantizedPos(Vector3 v)
        {
            long qx = Mathf.RoundToInt(v.x / QuantumStep);
            long qy = Mathf.RoundToInt(v.y / QuantumStep);
            long qz = Mathf.RoundToInt(v.z / QuantumStep);
            // 21 bits per axis, biased so negatives fit in unsigned range.
            const long bias = 1L << 20;
            const long mask = (1L << 21) - 1L;
            return ((qx + bias) & mask) << 42
                 | ((qy + bias) & mask) << 21
                 | ((qz + bias) & mask);
        }

        /// <summary>
        /// Terrain-only post-prune: drop regions that are both small
        /// (&lt; <see cref="TerrainIsolatedMaxArea"/>) AND share no edges with
        /// any other surviving region. These come from inside-wall corner
        /// pockets where the capsule check rejected the surrounding wall
        /// strip and isolated the patch from the main terrain mesh.
        /// </summary>
        private static void PruneIsolatedSmallTerrain(
            List<int> accepted, int[] idx, Vector3[] verts,
            Dictionary<long, List<int>> edgeToTris,
            Dictionary<int, string> triToRegion,
            BuildResult result)
        {
            // Find which regions share an edge with at least one other region.
            var linked = new HashSet<string>();
            foreach (var tris in edgeToTris.Values)
            {
                string firstRid = null;
                foreach (int t in tris)
                {
                    if (!triToRegion.TryGetValue(t, out string rid)) continue;
                    if (firstRid == null) { firstRid = rid; continue; }
                    if (rid != firstRid)
                    {
                        linked.Add(firstRid);
                        linked.Add(rid);
                    }
                }
            }

            // Compute area per surviving region.
            var regionArea = new Dictionary<string, float>();
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string rid)) continue;
                float a = TriArea(verts[idx[t * 3]], verts[idx[t * 3 + 1]], verts[idx[t * 3 + 2]]);
                if (regionArea.TryGetValue(rid, out float cur))
                    regionArea[rid] = cur + a;
                else
                    regionArea[rid] = a;
            }

            var dropped = new HashSet<string>();
            var dropDetails = new List<string>();
            foreach (var kv in regionArea)
            {
                if (kv.Value >= TerrainIsolatedMaxArea) continue;
                if (linked.Contains(kv.Key)) continue;
                dropped.Add(kv.Key);
                if (result.Centroids.TryGetValue(kv.Key, out var c))
                    dropDetails.Add($"{kv.Key}:{c.x:F1},{c.y:F1},{c.z:F1}:area={kv.Value:F2}");
            }
            if (dropped.Count == 0) return;

            foreach (string rid in dropped)
            {
                result.RegionIds.Remove(rid);
                result.Centroids.Remove(rid);
            }

            var toRemove = new List<int>();
            foreach (var kv in triToRegion)
                if (dropped.Contains(kv.Value)) toRemove.Add(kv.Key);
            foreach (int t in toRemove) triToRegion.Remove(t);

            Plugin.Log?.LogInfo(
                $"[Region] Isolated terrain pruning: dropped {dropped.Count} regions " +
                $"(<{TerrainIsolatedMaxArea}m², no shared edges), " +
                $"{result.RegionIds.Count} regions remaining");
            DebugLog.List("RegionPrune", "isolated_terrain_dropped",
                dropDetails.Cast<object>());
        }

        private static bool IsAnyVertexBelowTerrain(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            return IsVertexBelowTerrain(v0)
                || IsVertexBelowTerrain(v1)
                || IsVertexBelowTerrain(v2);
        }

        private static bool IsVertexBelowTerrain(Vector3 v)
        {
            if (ZoneSystem.instance == null) return false;
            float terrainY = ZoneSystem.instance.GetGroundHeight(new Vector3(v.x, 0f, v.z));
            if (terrainY == 0f) return false;
            return v.y < terrainY - MaxBelowTerrain;
        }

        private static long EdgeKey(int a, int b)
            => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private static void AddEdge(Dictionary<long, List<int>> map, int v1, int v2, int tri)
        {
            long key = EdgeKey(v1, v2);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }
            list.Add(tri);
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int[] rank, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (rank[ra] < rank[rb]) parent[ra] = rb;
            else if (rank[ra] > rank[rb]) parent[rb] = ra;
            else { parent[rb] = ra; rank[ra]++; }
        }

        private static long PackGroup(int root, int gx, int gz, int hb)
            => ((long)root * 1_000_003L + gx) * 1_000_003L * 1_000_003L
             + (long)gz * 1_000_003L + hb;

        private static float TriArea(Vector3 a, Vector3 b, Vector3 c)
            => Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        private static bool PointInTri2D(float px, float pz,
            float ax, float az, float bx, float bz, float cx, float cz)
        {
            float d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
            float d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
            float d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }

        private static float InterpHeight(float px, float pz, Vector3 a, Vector3 b, Vector3 c)
        {
            float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(det) < 0.0001f) return (a.y + b.y + c.y) / 3f;
            float l1 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / det;
            float l2 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / det;
            return l1 * a.y + l2 * b.y + (1f - l1 - l2) * c.y;
        }

        private static void DetectBoundary(
            Dictionary<long, List<int>> edgeToTris,
            Dictionary<int, string> triToRegion,
            BuildResult result)
        {
            var boundaryDirs = new Dictionary<string, Vector3>();
            foreach (var kv in edgeToTris)
            {
                var tris = kv.Value;
                int valid = 0;
                string lastR = null;
                foreach (int t in tris)
                    if (triToRegion.TryGetValue(t, out string r)) { valid++; lastR = r; }

                if (valid != 1 || lastR == null) continue;

                if (!boundaryDirs.TryGetValue(lastR, out var dir))
                    dir = Vector3.zero;
                if (result.Centroids.TryGetValue(lastR, out Vector3 centroid))
                {
                    int v1 = (int)(kv.Key >> 32), v2 = (int)(kv.Key & 0xFFFFFFFFL);
                    // This edge only appears on the boundary, but we don't have direct
                    // access to verts here, so we skip outward dir refinement.
                    // A zero dir defaults to Vector3.forward in the consumer.
                }
                boundaryDirs[lastR] = dir;
            }

            foreach (var kv in boundaryDirs)
            {
                if (!result.Centroids.TryGetValue(kv.Key, out Vector3 c)) continue;
                var dir = kv.Value.sqrMagnitude > 0.01f ? kv.Value.normalized : Vector3.forward;
                result.BoundaryCells.Add((kv.Key, c, dir));
            }
        }

        private static void BuildLookupGrid(
            List<int> accepted, int[] indices, Vector3[] verts,
            Dictionary<int, string> triToRegion,
            BuildResult result)
        {
            float cellSize = RegionGraph.LookupCellSize;
            foreach (int t in accepted)
            {
                if (!triToRegion.TryGetValue(t, out string regionId)) continue;
                var v0 = verts[indices[t * 3]];
                var v1 = verts[indices[t * 3 + 1]];
                var v2 = verts[indices[t * 3 + 2]];

                float txMin = Mathf.Min(v0.x, Mathf.Min(v1.x, v2.x));
                float txMax = Mathf.Max(v0.x, Mathf.Max(v1.x, v2.x));
                float tzMin = Mathf.Min(v0.z, Mathf.Min(v1.z, v2.z));
                float tzMax = Mathf.Max(v0.z, Mathf.Max(v1.z, v2.z));

                int gxMin = Mathf.FloorToInt(txMin / cellSize);
                int gxMax = Mathf.FloorToInt(txMax / cellSize);
                int gzMin = Mathf.FloorToInt(tzMin / cellSize);
                int gzMax = Mathf.FloorToInt(tzMax / cellSize);

                for (int gx = gxMin; gx <= gxMax; gx++)
                for (int gz = gzMin; gz <= gzMax; gz++)
                {
                    float cx = (gx + 0.5f) * cellSize;
                    float cz = (gz + 0.5f) * cellSize;
                    if (!PointInTri2D(cx, cz, v0.x, v0.z, v1.x, v1.z, v2.x, v2.z))
                        continue;

                    float h = InterpHeight(cx, cz, v0, v1, v2);
                    int hb = RegionGraph.HeightBucket(h);
                    long key = RegionGraph.PackLookup(gx, gz, hb);
                    result.LookupGrid[key] = regionId;
                }
            }
        }

        #endregion
    }
}
