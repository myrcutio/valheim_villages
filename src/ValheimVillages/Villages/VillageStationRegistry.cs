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

            // For stations whose centroid sits on a non-walkable obstacle (e.g. Smelter, CharcoalKiln),
            // direct pathing to transform.position fails. We need a walkable approach point INSIDE the
            // village hull AND with a complete NavMesh path from the village interior — Unity's
            // NavMesh.SamplePosition has no containment-polygon mask, and a sampled cell can sit on a
            // disconnected NavMesh island (close in straight-line distance, no path from inside).
            // We probe compass offsets around the centroid and validate each candidate with both
            // checks before committing.
            s_outsideCellsByVillage.TryGetValue(villageKey, out var outsideCells);
            stationPos = best.transform.position;
            System.Func<Vector3, bool> hullPredicate = outsideCells == null
                ? (System.Func<Vector3, bool>)null
                : p => !Villager.AI.Navigation.RubberBandPrune.IsOutsideCell(p, outsideCells);
            if (Villager.AI.Navigation.VillagerMovement.TryResolveApproach(
                    best.transform.position, position, hullPredicate, out var approach))
                stationPos = approach;
            component = best;
            return true;
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
