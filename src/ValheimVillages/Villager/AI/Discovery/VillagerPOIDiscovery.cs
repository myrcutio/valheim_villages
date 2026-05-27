using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;

namespace ValheimVillages.Villager.AI
{
    /// <summary>
    ///     POI discovery and validation logic for villager AI.
    ///     Scans nearby objects to discover beds, fires, chairs, tables, farms, and animals.
    /// </summary>
    public static class VillagerPOIDiscovery
    {
        private const float LOSDiscoveryRadius = 50f;

        /// <summary>
        ///     Discover points of interest near the given transform (Villager.AI path).
        /// </summary>
        public static void DiscoverNearbyPOIs(Transform transform, IVillagerMemory memory)
        {
            if (transform == null) return;
            var pos = transform.position;

            if (Vector3.Distance(pos, memory.BedPosition) > VillagerSettings.MaxWanderRange)
                return;

            var currentShelter = VillagerBehaviorLogic.CheckShelter(pos);
            if (currentShelter)
                memory.DiscoverLocation(pos, LocationType.Shelter, 0f, true);

            var comfort = CalculateCurrentComfort(transform, pos);
            memory.UpdateBestComfort(comfort, pos);

            var colliders = Physics.OverlapSphere(pos, VillagerSettings.DiscoveryRadius);
            foreach (var collider in colliders)
            {
                if (collider == null || collider.gameObject == null) continue;
                TryDiscoverPOI(collider.gameObject, memory);
            }
        }

        private static void TryDiscoverPOI(GameObject obj, IVillagerMemory memory)
        {
            if (obj == null) return;

            var locType = ClassifyObject(obj);
            if (locType == null) return;

            var pos = obj.transform.position;

            // For CraftStation, use the station component's actual position
            // instead of the child collider position (which may be offset)
            if (locType.Value == LocationType.CraftStation)
            {
                var cs = obj.GetComponentInParent<CraftingStation>();
                if (cs != null)
                {
                    pos = cs.transform.position;
                }
                else
                {
                    var cookStation = obj.GetComponentInParent<CookingStation>();
                    if (cookStation != null)
                        pos = cookStation.transform.position;
                }
            }

            var hasShelter = VillagerBehaviorLogic.CheckShelter(pos);

            var comfort = locType.Value switch
            {
                LocationType.Fire => hasShelter ? 2f : 0.5f,
                LocationType.Animals => 1.5f,
                _ => 1f,
            };

            memory.DiscoverLocation(pos, locType.Value, comfort, hasShelter);
        }

        /// <summary>
        ///     Extended-range discovery using line-of-sight (Villager.AI path).
        /// </summary>
        public static void DiscoverVisiblePOIs(Transform transform, IVillagerMemory memory)
        {
            if (transform == null) return;
            var missingTypes = new HashSet<LocationType>(memory.GetMissingLocationTypes());
            if (missingTypes.Count == 0) return;

            var eyePos = transform.position + Vector3.up * 1.5f;
            var colliders = Physics.OverlapSphere(transform.position, LOSDiscoveryRadius);

            foreach (var collider in colliders)
            {
                if (collider == null || collider.gameObject == null) continue;

                var locType = ClassifyObject(collider.gameObject);
                if (locType == null || !missingTypes.Contains(locType.Value)) continue;

                var targetPos = collider.gameObject.transform.position;

                if (Vector3.Distance(transform.position, targetPos) < VillagerSettings.DiscoveryRadius)
                    continue;

                var direction = targetPos - eyePos;
                if (Physics.Raycast(eyePos, direction.normalized, out var hit, direction.magnitude))
                    if (Vector3.Distance(hit.point, targetPos) > 3f)
                        continue;

                var hasShelter = VillagerBehaviorLogic.CheckShelter(targetPos);
                var comfort = locType.Value == LocationType.Fire && hasShelter ? 2f : 1f;
                memory.DiscoverLocation(targetPos, locType.Value, comfort, hasShelter);

                Plugin.Log?.LogInfo($"[LOS] Spotted {locType.Value} at {targetPos} ({direction.magnitude:F0}m away)");
                missingTypes.Remove(locType.Value);

                if (missingTypes.Count == 0) break;
            }
        }

        /// <summary>
        ///     Classify a GameObject as a location type, or null if unrecognized.
        ///     Single source of truth for both discovery and validation (IsLocationStillValid).
        /// </summary>
        private static LocationType? ClassifyObject(GameObject obj)
        {
            if (obj == null) return null;
            if (obj.GetComponent<Chair>() != null) return LocationType.Chair;
            if (obj.GetComponent<Fireplace>() != null) return LocationType.Fire;

            var name = obj.name.ToLower();
            if (name.Contains("table") || name.Contains("bench")) return LocationType.Table;
            if (name.Contains("cultivat") || name.Contains("sapling") || name.Contains("plant_"))
                return LocationType.Farm;

            var character = obj.GetComponent<Character>();
            if (character != null && character.IsTamed()) return LocationType.Animals;

            return null;
        }

        /// <summary>
        ///     Validate that known locations still have their expected objects (any IVillagerMemory).
        /// </summary>
        public static void ValidateKnownLocations(IVillagerMemory memory)
        {
            var locationsToRemove = new List<KnownLocation>();

            foreach (var location in memory.GetValidatableLocations())
                if (!IsLocationStillValid(location))
                    locationsToRemove.Add(location);

            foreach (var location in locationsToRemove)
                memory.RemoveLocation(location);

            if (locationsToRemove.Count > 0)
                Plugin.Log?.LogDebug($"Removed {locationsToRemove.Count} invalid location(s) from memory");
        }

        private static bool IsLocationStillValid(KnownLocation location)
        {
            var colliders = Physics.OverlapSphere(location.Position, KnownLocation.SameLocationThreshold);
            foreach (var collider in colliders)
            {
                if (collider == null || collider.gameObject == null) continue;
                if (ClassifyObject(collider.gameObject) == location.Type)
                    return true;
            }

            return false;
        }

        private static float CalculateCurrentComfort(Transform transform, Vector3 position)
        {
            var comfort = 0f;

            if (VillagerBehaviorLogic.CheckShelter(position))
                comfort += 1f;

            var colliders = Physics.OverlapSphere(position, 10f);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                if (col.GetComponent<Fireplace>() != null)
                {
                    comfort += 1f;
                    break;
                }
            }

            return comfort;
        }
    }
}