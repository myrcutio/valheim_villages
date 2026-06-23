using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Per-village cache of idle/comfort points of interest (fire, table,
    ///     chair, farm) inside each registered <see cref="VillageArea" /> polygon.
    ///     Replaces per-villager LOS discovery for these PoIs: the Explore
    ///     behavior, farming, and the unreachable-target recovery flow query this
    ///     registry by position instead of each villager's private memory.
    ///     <para>Mirrors <see cref="VillageStationRegistry" /> (same AABB scan +
    ///     outer-hull filter + smallest-containing-polygon lookup). Animals are
    ///     intentionally excluded — tamed creatures wander, so a partition-time
    ///     snapshot would be stale; shelter is a per-PoI flag rather than a
    ///     standalone PoI because there is no "shelter" object to scan.</para>
    /// </summary>
    public static class VillagePoiRegistry
    {
        private static readonly Dictionary<string, List<KnownLocation>> s_poisByVillage = new();

        /// <summary>
        ///     (Re)scan PoIs inside the given VillageArea. Called from
        ///     <see cref="VillageAreaManager" /> when an area is registered.
        /// </summary>
        public static void RefreshFor(VillageArea area)
        {
            if (area == null || area.Waypoints == null || area.Waypoints.Count < 3) return;

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

            // Pad Y generously — patrol waypoints sit at terrain height, PoIs
            // (fires on tables, elevated farms) may be above.
            var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            var halfExtents = new Vector3((maxX - minX) * 0.5f + 1f, (maxY - minY) * 0.5f + 20f, (maxZ - minZ) * 0.5f + 1f);

            var hullBounds = new Bounds(center,
                new Vector3(halfExtents.x * 2f, halfExtents.y * 2f, halfExtents.z * 2f));
            var outsideCells = RubberBandPrune.ComputeOutsideCellsForBake(hullBounds);

            var pois = new List<KnownLocation>();
            var hits = Physics.OverlapBox(center, halfExtents, Quaternion.identity);
            foreach (var col in hits)
            {
                if (col == null || col.gameObject == null) continue;
                var type = ClassifyPoi(col.gameObject);
                if (type == null) continue;

                var pos = col.gameObject.transform.position;
                // Drop PoIs whose XZ cell lies outside the village's outer hull.
                if (RubberBandPrune.IsOutsideCell(pos, outsideCells)) continue;

                var hasShelter = VillagerBehaviorLogic.CheckShelter(pos);
                var comfort = ComfortFor(type.Value, hasShelter);
                TryAddDeduped(pois, pos, type.Value, comfort, hasShelter);
            }

            s_poisByVillage[area.VillageId] = pois;
            Plugin.Log?.LogInfo(
                $"[VillagePoiRegistry] {area.VillageId}: cached {pois.Count} PoI(s) inside hull");
        }

        /// <summary>Remove a village's PoI cache (called on UnregisterArea).</summary>
        public static void RemoveFor(string villageKey)
        {
            s_poisByVillage.Remove(villageKey);
        }

        /// <summary>
        ///     PoIs in the village whose polygon contains the position. Empty when
        ///     the position isn't inside any registered village.
        /// </summary>
        public static IReadOnlyList<KnownLocation> GetPois(Vector3 position)
        {
            var village = VillageRegistry.GetVillageAt(position);
            if (village == null)
                return Array.Empty<KnownLocation>();
            return s_poisByVillage.TryGetValue(village.VillageId, out var list)
                ? list
                : (IReadOnlyList<KnownLocation>)Array.Empty<KnownLocation>();
        }

        /// <summary>PoIs of a single type in the containing village.</summary>
        public static IEnumerable<KnownLocation> GetPois(Vector3 position, LocationType type)
        {
            foreach (var poi in GetPois(position))
                if (poi.Type == type)
                    yield return poi;
        }

        /// <summary>
        ///     Classify a GameObject into a centralized PoI type, or null. Mirrors
        ///     the old per-villager classifier minus Animals (moving), Shelter
        ///     (no object), Bed (per-villager), and stations (own registry).
        /// </summary>
        private static LocationType? ClassifyPoi(GameObject obj)
        {
            if (obj == null) return null;

            // Hot tub: the piece_bathtub prefab is a Fireplace + comfort Piece, so it must be
            // matched by prefab name BEFORE the Fireplace branch below or it would be filed as
            // a plain Fire. No dedicated engine component exists for it.
            var piece = obj.GetComponent<Piece>() ?? obj.GetComponentInParent<Piece>();
            if (piece != null &&
                piece.gameObject.name.IndexOf("bathtub", StringComparison.OrdinalIgnoreCase) >= 0)
                return LocationType.HotTub;

            if (obj.GetComponent<Chair>() != null) return LocationType.Chair;
            if (obj.GetComponent<Fireplace>() != null) return LocationType.Fire;

            var name = obj.name.ToLowerInvariant();
            if (name.Contains("table") || name.Contains("bench")) return LocationType.Table;
            if (name.Contains("cultivat") || name.Contains("sapling") || name.Contains("plant_"))
                return LocationType.Farm;

            return null;
        }

        private static float ComfortFor(LocationType type, bool hasShelter)
        {
            return type switch
            {
                LocationType.Fire => hasShelter ? 2f : 0.5f,
                LocationType.HotTub => 2f, // warm + cozy, always a high-comfort relax spot
                _ => 1f,
            };
        }

        // Respect the same per-type spacing and count caps the per-villager
        // memory used, so the shared cache doesn't accumulate near-duplicate
        // fires/tables from overlapping colliders.
        private static void TryAddDeduped(List<KnownLocation> list, Vector3 pos,
            LocationType type, float comfort, bool hasShelter)
        {
            var minDist = KnownLocation.GetMinDistanceForType(type);
            var sameType = 0;
            foreach (var existing in list)
            {
                if (existing.Type != type) continue;
                sameType++;
                if (Vector3.Distance(existing.Position, pos) < minDist) return;
            }

            if (sameType >= KnownLocation.GetMaxLocationsForType(type)) return;

            list.Add(new KnownLocation
            {
                Position = pos,
                Type = type,
                ComfortValue = comfort,
                HasShelter = hasShelter,
            });
        }

        [DevCommand("List cached village PoIs (fire/table/chair/farm) for the village containing the player",
            Name = "vv_pois")]
        public static void DumpPois()
        {
            var player = Player.m_localPlayer;
            var sb = new System.Text.StringBuilder();
            if (player == null)
            {
                sb.AppendLine("[vv_pois] No local player.");
            }
            else
            {
                var pos = player.transform.position;
                var village = VillageRegistry.GetVillageAt(pos);
                if (village == null)
                {
                    sb.AppendLine(
                        $"[vv_pois] player at ({pos.x:F1},{pos.z:F1}) is not inside any registered village.");
                }
                else
                {
                    var key = village.VillageId;
                    var list = s_poisByVillage.TryGetValue(key, out var pois) ? pois : null;
                    sb.AppendLine($"[vv_pois] village {key}: {(list?.Count ?? 0)} PoI(s)");
                    if (list != null)
                        foreach (var p in list)
                            sb.AppendLine(
                                $"  [{p.Type}] @ ({p.Position.x:F1},{p.Position.y:F1},{p.Position.z:F1}) " +
                                $"shelter={p.HasShelter} comfort={p.ComfortValue:F1} " +
                                $"dist={Vector3.Distance(pos, p.Position):F1}m");
                }
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_poisByVillage.Clear();
        }
    }
}
