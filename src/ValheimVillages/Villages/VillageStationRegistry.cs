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

        /// <summary>
        ///     Max XZ distance (m) an approach cell may sit from the target. A
        ///     work approach should be right next to the station/chest; this also
        ///     bounds the capsule/path validation so an unreachable target can't
        ///     make the search walk the whole lookup grid.
        /// </summary>
        private const float ApproachMaxXzDist = 10f;

        /// <summary>
        ///     Max vertical gap (m) an approach cell may sit from the target —
        ///     ~one storey. Lookup-cell candidates are ranked by XZ distance and
        ///     ignore Y, so without this a cell directly below/above the target
        ///     (e.g. terrain ~10m under an elevated floor) could win and the
        ///     villager would interact with the chest/station through the floor
        ///     from another level (observed: a deposit from ~10m below). The
        ///     interaction itself (deposit / RPC) ignores distance, so the
        ///     approach is the only place to enforce "same level".
        /// </summary>
        private const float ApproachMaxYDelta = 3f;

        /// <summary>Diagnostic breakdown of the most recent TryResolveApproach call (vv_approach).</summary>
        public static string LastApproachDiag = "(none)";

        /// <param name="villageAnchor">
        ///     Position used to select the VILLAGE/graph (defaults to
        ///     <paramref name="pathSource" /> when null). Pass the villager's BED
        ///     here: the village a villager works in is defined by its home, not
        ///     by wherever it's physically standing. Without this a villager
        ///     bumped off the graph (its current position outside every village
        ///     polygon) resolves to "no village" / the wrong nearest village and
        ///     can't look up its own stations — leaving it stuck. The current
        ///     position is still used as the path START (snapped onto the chosen
        ///     graph).
        /// </param>
        public static bool TryResolveApproach(
            Vector3 target, Vector3 pathSource, out Vector3 approach, Vector3? villageAnchor = null)
        {
            approach = Vector3.zero;
            var anchor = villageAnchor ?? pathSource;
            Villager.AI.Navigation.RegionGraph graph;
            if (TryGetVillage(anchor, out var villageKey))
            {
                graph = Villager.AI.Navigation.RegionGraph.Get(villageKey);
            }
            else
            {
                // Anchor is outside every village AREA polygon — but the HNA
                // graph (and navmesh) can still cover it. This happens when the
                // anchor is a functional-but-not-polygon-enclosed spot, e.g. a
                // smelter platform just outside the drawn boundary: the graph has
                // regions there (PointToRegionId is non-null) yet TryGetVillage
                // returns false. A hard fail here made the villager abandon ALL
                // work the moment it stepped onto such a spot ("no HNA-valid
                // approach"). Fall back to the nearest village graph so
                // resolution proceeds — graph/navmesh coverage is the real "can I
                // path from here" test, not polygon membership.
                graph = Villager.AI.Navigation.RegionGraph.GetNearest(anchor);
                villageKey = "(nearest)";
            }
            if (graph == null)
            {
                LastApproachDiag = $"no graph for source ({pathSource.x:F1},{pathSource.y:F1},{pathSource.z:F1})";
                return false;
            }

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
            var snapped = true;
            if (!graph.TryFindNearestLookupCell(pathSource,
                    cell => Mathf.Abs(cell.y - pathSource.y) <= PathSourceSnapMaxY,
                    out pathStart, out _))
            {
                // Nothing at the source's height — fall back to nearest at any
                // height (degenerate, but better than refusing to path).
                snapped = false;
                if (!graph.TryFindNearestLookupCell(pathSource, null, out pathStart, out _))
                    pathStart = pathSource;
            }

            var minStandoffSq = MinApproachStandoffXZ * MinApproachStandoffXZ;
            var considered = 0;
            var levelPass = 0;
            var standoffPass = 0;
            var losPass = 0;
            var ok = graph.TryFindNearestLookupCell(
                target,
                candidate =>
                {
                    considered++;
                    // Same vertical level only — reject candidates more than ~one
                    // storey above/below the target so the villager can't approach
                    // (and then interact with) a chest/station from another floor.
                    if (Mathf.Abs(candidate.y - target.y) > ApproachMaxYDelta) return false;
                    levelPass++;
                    // Stand-off check next — cheaper than the path query and
                    // the dominant reason candidates near a station get
                    // rejected.
                    var dx = candidate.x - target.x;
                    var dz = candidate.z - target.z;
                    if (dx * dx + dz * dz < minStandoffSq) return false;
                    standoffPass++;
                    // No per-source path check. A same-level standoff cell with
                    // a clear line of sight to the station IS a valid approach
                    // position — and a valid approach position means the station
                    // is reachable. Whether THIS villager can route to it from
                    // where it currently stands is decided at execution time by
                    // NavTo (snap onto the navmesh + let the agent path, abandon
                    // if genuinely blocked), NOT pre-validated here with a
                    // complete-path-from-bed query. That pre-check produced false
                    // "unreachable" (e.g. blacksmith → smelter) whenever the bed
                    // had no precomputed corridor to the station, even though the
                    // station was right there with a perfectly good approach.
                    //
                    // LOS still matters: it rejects cells on the wrong side of a
                    // wall or a different floor (a ceiling/floor blocks the
                    // diagonal ray), so the resolved approach is genuinely next
                    // to and on the same level as the station.
                    if (!HasClearLineToStation(candidate, target)) return false;
                    losPass++;
                    return true;
                },
                out approach,
                out _,
                ApproachMaxXzDist);

            LastApproachDiag =
                $"src=({pathSource.x:F1},{pathSource.y:F1},{pathSource.z:F1}) snap={(snapped ? "y" : "fallback")} " +
                $"pathStart=({pathStart.x:F1},{pathStart.y:F1},{pathStart.z:F1}) " +
                $"tgt=({target.x:F1},{target.y:F1},{target.z:F1}) " +
                $"considered={considered} levelPass={levelPass} standoffPass={standoffPass} losPass={losPass} " +
                $"result={(ok ? $"({approach.x:F1},{approach.y:F1},{approach.z:F1})" : "FAIL")}";
            return ok;
        }

        /// <summary>Layers that block a villager↔station sightline (walls, floors, terrain).</summary>
        private static readonly int s_losMask =
            LayerMask.GetMask("Default", "static_solid", "piece", "terrain");

        /// <summary>
        ///     True when nothing solid sits between the approach point and the
        ///     station at interaction height. Casts at ~chest height and stops a
        ///     short pad before the station so the station's OWN collider doesn't
        ///     register as an obstruction. A wall (vertical) blocks the ray; a
        ///     different-floor station makes the ray climb into the intervening
        ///     floor/ceiling and blocks too — so this also enforces "same level".
        /// </summary>
        private static bool HasClearLineToStation(Vector3 approach, Vector3 station)
        {
            const float eyeHeight = 1.2f;   // villager/station interaction height

            var from = approach + Vector3.up * eyeHeight;
            var to = station + Vector3.up * eyeHeight;
            var delta = to - from;
            var dist = delta.magnitude;
            if (dist < 0.01f) return true; // adjacent — trivially usable
            var dir = delta / dist;

            // RaycastAll the FULL segment and ignore the station's OWN collider.
            // A fixed stand-off pad can't clear a large station: the charcoal
            // kiln's collider is ~5-6m wide, so a ray toward its centre stopped
            // 1.5m short still dead-ends inside the kiln's own body and reads as
            // "blocked" from every approach (losPass=0). Instead, a hit whose
            // bounds contain the target IS the station itself (the thing we're
            // walking to) — not an obstruction between us and it — so skip it.
            // Any OTHER solid hit along the segment is a real wall / floor /
            // different piece and genuinely blocks the sightline.
            var hits = Physics.RaycastAll(from, dir, dist, s_losMask,
                QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                if (h.collider.bounds.Contains(station)) continue; // the station's own body
                return false; // a real obstruction between approach and station
            }

            return true;
        }

        [DevCommand("Diagnose HNA approach resolution to a target from a source (default player). " +
                    "Usage: vv_approach <tx> <tz> [<sx> <sz>]", Name = "vv_approach")]
        public static void ApproachDiag(Terminal.ConsoleEventArgs args)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args?.Args == null || args.Args.Length < 3
                || !float.TryParse(args.Args[1], System.Globalization.NumberStyles.Float, inv, out var tx)
                || !float.TryParse(args.Args[2], System.Globalization.NumberStyles.Float, inv, out var tz))
            {
                global::Console.instance?.Print("Usage: vv_approach <tx> <tz> [<sx> <sz>]");
                return;
            }

            var src = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            if (args.Args.Length >= 5
                && float.TryParse(args.Args[3], System.Globalization.NumberStyles.Float, inv, out var sx)
                && float.TryParse(args.Args[4], System.Globalization.NumberStyles.Float, inv, out var sz))
                src = new Vector3(sx, src.y, sz);

            var groundT = ZoneSystem.instance != null
                ? ZoneSystem.instance.GetGroundHeight(new Vector3(tx, 0f, tz)) : 0f;
            var target = new Vector3(tx, groundT, tz);
            var ok = TryResolveApproach(target, src, out var approach);
            var msg = $"[vv_approach] ok={ok} approach=({approach.x:F1},{approach.y:F1},{approach.z:F1})\n  {LastApproachDiag}";
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
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
