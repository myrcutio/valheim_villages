using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Per-village cache of crafting/cooking/smelter station instances inside each
    ///     registered VillageArea polygon. Replaces per-villager LOS discovery for stations:
    ///     villagers query this registry by their bed position and get back stations from
    ///     their containing village.
    /// </summary>
    public static class VillageStationRegistry
    {
        private static readonly Dictionary<string, List<Component>> s_stationsByVillage = new();
        private static readonly Dictionary<string, HashSet<long>> s_outsideCellsByVillage = new();

        /// <summary>
        ///     (Re)scan stations inside the given VillageArea. Called from VillageAreaManager
        ///     when an area is registered or its waypoints change.
        /// </summary>
        public static void RefreshFor(VillageArea area)
        {
            if (area == null || area.Waypoints == null || area.Waypoints.Count < 3) return;

            // Compute AABB from the polygon
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var wp in area.Waypoints)
            {
                if (wp.x < minX) minX = wp.x;
                if (wp.x > maxX) maxX = wp.x;
                if (wp.z < minZ) minZ = wp.z;
                if (wp.z > maxZ) maxZ = wp.z;
                if (wp.y < minY) minY = wp.y;
                if (wp.y > maxY) maxY = wp.y;
            }

            // Pad Y bounds generously — patrol waypoints sit at terrain height, stations may be above
            var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            var halfExtents = new Vector3((maxX - minX) * 0.5f + 1f, (maxY - minY) * 0.5f + 20f, (maxZ - minZ) * 0.5f + 1f);

            // Outer-hull mask: cells outside the village's outermost wall ring. Stations whose XZ
            // cell IS in this set are outside the village. Cells NOT in this set are inside the
            // outer hull — even when they sit on top of a non-walkable obstacle like a smelter,
            // which HNA's walkable polygon excludes by design.
            var hullBounds = new Bounds(center, new Vector3(halfExtents.x * 2f, halfExtents.y * 2f, halfExtents.z * 2f));
            var outsideCells = RubberBandPrune.ComputeOutsideCellsForBake(hullBounds);

            var found = new List<Component>();
            var seen = new HashSet<Component>();

            // Diagnostic counters: how many of each type we encountered (a) in the AABB and
            // (b) kept after the outer-hull filter. If a Smelter shows up in AABB but
            // not in cache, it lives outside the village's outer hull.
            int aabbCraft = 0, aabbCook = 0, aabbSmelter = 0;
            int keptCraft = 0, keptCook = 0, keptSmelter = 0;

            var hits = Physics.OverlapBox(center, halfExtents, Quaternion.identity);
            foreach (var col in hits)
            {
                if (col == null || col.gameObject == null) continue;

                // Each prefab may have only one of these components — check all three.
                var cs = col.GetComponentInParent<CraftingStation>();
                if (cs != null && seen.Add(cs))
                {
                    aabbCraft++;
                    if (!RubberBandPrune.IsOutsideCell(cs.transform.position, outsideCells)) { found.Add(cs); keptCraft++; }
                }

                var ck = col.GetComponentInParent<CookingStation>();
                if (ck != null && seen.Add(ck))
                {
                    aabbCook++;
                    if (!RubberBandPrune.IsOutsideCell(ck.transform.position, outsideCells)) { found.Add(ck); keptCook++; }
                }

                var sm = col.GetComponentInParent<Smelter>();
                if (sm != null && seen.Add(sm))
                {
                    aabbSmelter++;
                    if (!RubberBandPrune.IsOutsideCell(sm.transform.position, outsideCells)) { found.Add(sm); keptSmelter++; }
                }
            }

            s_stationsByVillage[area.VillageKey] = found;
            s_outsideCellsByVillage[area.VillageKey] = outsideCells;

            if (Plugin.Log != null)
            {
                Plugin.Log.LogInfo(
                    $"[VillageStationRegistry] {area.VillageKey}: cached {found.Count} stations inside hull " +
                    $"(AABB→kept: Craft {aabbCraft}→{keptCraft}, Cook {aabbCook}→{keptCook}, Smelter {aabbSmelter}→{keptSmelter})");
                foreach (var comp in found)
                {
                    string kind;
                    string name;
                    if (comp is CraftingStation cs) { kind = "Craft"; name = cs.m_name ?? cs.gameObject.name; }
                    else if (comp is CookingStation ck) { kind = "Cook"; name = ck.m_name ?? ck.gameObject.name; }
                    else if (comp is Smelter sm) { kind = "Smelter"; name = sm.m_name ?? sm.gameObject.name; }
                    else { kind = comp.GetType().Name; name = comp.gameObject.name; }
                    var p = comp.transform.position;
                    Plugin.Log.LogInfo(
                        $"  [{kind}] {name} @ ({p.x:F1},{p.y:F1},{p.z:F1}) prefab={comp.gameObject.name}");
                }
            }
        }

        /// <summary>Remove a village's station cache (called on UnregisterArea).</summary>
        public static void RemoveFor(string villageKey)
        {
            s_stationsByVillage.Remove(villageKey);
            s_outsideCellsByVillage.Remove(villageKey);
        }

        /// <summary>True if the registry has a village whose polygon contains the position.</summary>
        public static bool HasVillageFor(Vector3 position)
        {
            return TryGetVillage(position, out _);
        }

        /// <summary>
        ///     Look up a station of type T matching the filter inside the village containing the position.
        ///     Returns the nearest match by position when multiple satisfy the filter.
        /// </summary>
        public static bool TryFindStation<T>(
            Vector3 position,
            Func<T, bool> filter,
            out Vector3 stationPos,
            out T component) where T : Component
        {
            stationPos = Vector3.zero;
            component = null;

            if (!TryGetVillage(position, out var villageKey)) return false;
            if (!s_stationsByVillage.TryGetValue(villageKey, out var list)) return false;

            T best = null;
            var bestDistSq = float.MaxValue;
            foreach (var comp in list)
            {
                if (comp == null) continue;
                var typed = comp as T;
                if (typed == null) continue;
                if (filter != null && !filter(typed)) continue;
                var d = (typed.transform.position - position).sqrMagnitude;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = typed;
                }
            }

            if (best == null) return false;

            if (!TryResolveApproach(best.transform.position, position, out var approach))
            {
                Plugin.Log?.LogDebug(
                    $"[VillageStationRegistry] {villageKey}: no HNA-valid approach to {best.gameObject.name} " +
                    $"@ ({best.transform.position.x:F1},{best.transform.position.y:F1},{best.transform.position.z:F1})");
                stationPos = Vector3.zero;
                component = null;
                return false;
            }

            stationPos = approach;
            component = best;
            return true;
        }

        /// <summary>
        ///     Resolve a world-space target (station, chest, anything) to a position the villager
        ///     can actually reach: walk the village's HNA lookup-grid cells in order of XZ
        ///     distance to the target, return the first cell that has a complete NavMesh path
        ///     from <paramref name="pathSource"/> (the villager's current or bed position).
        ///     <para>This is the SINGLE entry point for "where should the villager actually walk
        ///     to reach this thing?" Used by station lookup AND container-target navigation in
        ///     CraftingWorkflow — same rules everywhere, so when one path fails the diagnostic
        ///     applies to all the others too.</para>
        ///     <para>Lookup-grid cells (not region centroids) are the canonical HNA positions
        ///     that round-trip through PointToRegionId. Centroids are geometric averages and
        ///     can land in buckets the lookup grid never indexed.</para>
        ///     <para>No fallback. If no lookup cell in the path-source's village is reachable
        ///     AND close to the target, returns false. The caller must abandon the work, not
        ///     dispatch toward an unreachable position.</para>
        /// </summary>
        /// <summary>
        ///     Minimum XZ distance between the resolved approach and the target.
        ///     The HNA lookup grid can land a "navigable" cell directly on top
        ///     of a station's pivot — and NavMesh.CalculatePath will happily
        ///     compute a straight-line path to it because polygon connectivity
        ///     exists, but the agent capsule physically can't traverse through
        ///     the station's own collider. Forcing a 1.5m stand-off ensures the
        ///     resolved approach is genuinely *next to* the obstacle, not *at*
        ///     it. Still inside station RPC interaction range (~2m), so AddFuel
        ///     etc. still fire once the agent arrives.
        ///     <para>Confirmed by incident bundle 002_Blacksmith_stall_escape
        ///     (May 2026): target=(-2276.5, 39, 1300.5) resolved to approach
        ///     with same XZ, path[0]=same XZ; agent stalled 2.16m short
        ///     because the straight-line capsule path grazed the smelter
        ///     body.</para>
        /// </summary>
        private const float MinApproachStandoffXZ = 1.5f;

        /// <summary>
        ///     Max vertical gap (m) when snapping the path source onto the HNA
        ///     graph — keeps a ground-floor bed from snapping to an upper-floor
        ///     cell at the same XZ. Slightly wider than one HeightBucketSize (2m).
        /// </summary>
        private const float PathSourceSnapMaxY = 3f;

        public static bool TryResolveApproach(Vector3 target, Vector3 pathSource, out Vector3 approach)
        {
            approach = Vector3.zero;
            if (!TryGetVillage(pathSource, out var villageKey)) return false;

            var graph = Villager.AI.Navigation.RegionGraph.Get(villageKey);
            if (graph == null) return false;

            // Snap the path source onto the graph before planning. The usual
            // source is a bed, which sits on a cell the HNA prune carved out
            // for the bed/obstacle footprint — PointToRegionId is null there,
            // so the corridor planner can't seed a start cell and reports
            // EVERY target unreachable (a blacksmith concludes "no smelter in
            // the village"). The nearest lookup cell is the floor the villager
            // actually stands on next to the bed.
            //
            // Constrain the snap to the source's height: TryFindNearestLookupCell
            // ranks by XZ distance and ignores Y, so in a multi-storey building a
            // ground-floor bed would otherwise snap UP to an upper-floor cell at
            // the same XZ — and then only that upper floor is reachable, sending
            // every approach to the wrong level.
            Vector3 pathStart;
            if (!graph.TryFindNearestLookupCell(pathSource,
                    cell => Mathf.Abs(cell.y - pathSource.y) <= PathSourceSnapMaxY,
                    out pathStart, out _))
            {
                // Nothing at the source's height — fall back to nearest at any
                // height (degenerate, but better than refusing to path).
                if (!graph.TryFindNearestLookupCell(pathSource, null, out pathStart, out _))
                    pathStart = pathSource;
            }

            var minStandoffSq = MinApproachStandoffXZ * MinApproachStandoffXZ;
            var pathBuffer = new List<Vector3>();
            return graph.TryFindNearestLookupCell(
                target,
                candidate =>
                {
                    // Stand-off check first — cheaper than the path query and
                    // the dominant reason candidates near a station get
                    // rejected.
                    var dx = candidate.x - target.x;
                    var dz = candidate.z - target.z;
                    if (dx * dx + dz * dz < minStandoffSq) return false;
                    return Villager.AI.Navigation.VillagerMovement.TryFindCompletePath(
                        pathStart, candidate, pathBuffer);
                },
                out approach,
                out _);
        }

        [DevCommand("Dump cached village stations + HNA approach resolution for each villager bed",
            Name = "vv_stations")]
        public static void DumpStations()
        {
            var sb = new System.Text.StringBuilder();
            var beds = Villager.AI.VillagerAIManager.GetAllBedPositions();
            sb.AppendLine($"[vv_stations] {beds.Count} bed(s); {s_stationsByVillage.Count} village(s) cached");

            foreach (var bed in beds)
            {
                if (!TryGetVillage(bed, out var key))
                {
                    sb.AppendLine($"  bed=({bed.x:F1},{bed.y:F1},{bed.z:F1}) → NO containing village");
                    continue;
                }

                var list = s_stationsByVillage.TryGetValue(key, out var stations) ? stations : null;
                sb.AppendLine($"  bed=({bed.x:F1},{bed.y:F1},{bed.z:F1}) → village {key}: " +
                              $"{(list?.Count ?? 0)} station(s)");
                if (list != null)
                    foreach (var comp in list)
                    {
                        if (comp == null) continue;
                        var p = comp.transform.position;
                        var approached = TryResolveApproach(p, bed, out var approach);
                        sb.AppendLine(
                            $"    [{comp.GetType().Name}] {comp.gameObject.name} " +
                            $"@ ({p.x:F1},{p.y:F1},{p.z:F1}) dist={Vector3.Distance(bed, p):F1}m " +
                            $"approach={(approached ? $"({approach.x:F1},{approach.y:F1},{approach.z:F1})" : "UNREACHABLE")}");
                    }
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_stationsByVillage.Clear();
            s_outsideCellsByVillage.Clear();
        }

        /// <summary>
        ///     Pick the village whose polygon contains the position. If multiple match, take the smallest
        ///     polygon (most specific). Iterates VillageAreaManager's areas by reflection-free public API.
        /// </summary>
        private static bool TryGetVillage(Vector3 position, out string villageKey)
        {
            villageKey = null;
            string best = null;
            var bestSizeSq = float.MaxValue;

            foreach (var area in VillageAreaManager.AllAreas)
            {
                if (area == null || !area.IsInsideArea(position)) continue;
                // Smallest-area tiebreak: use the polygon bounding-box area as a proxy
                float minX = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var wp in area.Waypoints)
                {
                    if (wp.x < minX) minX = wp.x;
                    if (wp.x > maxX) maxX = wp.x;
                    if (wp.z < minZ) minZ = wp.z;
                    if (wp.z > maxZ) maxZ = wp.z;
                }
                var sizeSq = (maxX - minX) * (maxZ - minZ);
                if (sizeSq < bestSizeSq)
                {
                    bestSizeSq = sizeSq;
                    best = area.VillageKey;
                }
            }

            villageKey = best;
            return best != null;
        }
    }
}
