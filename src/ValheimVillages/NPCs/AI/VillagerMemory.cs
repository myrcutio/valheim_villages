using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Stores locations the NPC has discovered and remembers.
    /// Persisted to ZDO for save/load across sessions.
    /// </summary>
    public class VillagerMemory
    {
        private readonly List<KnownLocation> m_locations = new();
        
        /// <summary>The NPC's home bed position.</summary>
        public Vector3 BedPosition { get; set; }
        
        /// <summary>Highest comfort level experienced.</summary>
        public float BestComfortLevel { get; set; }
        
        /// <summary>Position where best comfort was experienced.</summary>
        public Vector3? BestComfortPosition { get; set; }

        /// <summary>All known locations.</summary>
        public IReadOnlyList<KnownLocation> KnownLocations => m_locations;

        /// <summary>
        /// Initialize memory with the home bed position.
        /// </summary>
        public VillagerMemory(Vector3 bedPosition)
        {
            BedPosition = bedPosition;
            
            // Bed is always known
            DiscoverLocation(bedPosition, LocationType.Bed, 0f, true);
        }

        /// <summary>
        /// Record a discovered location. Ignores duplicates within threshold distance.
        /// </summary>
        public void DiscoverLocation(Vector3 position, LocationType type, float comfortValue, bool hasShelter = false)
        {
            // Check if within max range of bed
            if (Vector3.Distance(position, BedPosition) > VillagerSettings.MaxWanderRange)
                return;

            // Check for existing location of same type nearby
            var existing = m_locations.FirstOrDefault(l => 
                l.Type == type && l.IsSameLocation(position.ToVec3()));

            if (existing != null)
            {
                // Update existing location if this one is better
                if (comfortValue > existing.ComfortValue)
                {
                    existing.ComfortValue = comfortValue;
                }
                if (hasShelter && !existing.HasShelter)
                {
                    existing.HasShelter = true;
                }
                return;
            }

            // Add new location
            m_locations.Add(new KnownLocation
            {
                Position = position.ToVec3(),
                Type = type,
                HasShelter = hasShelter,
                ComfortValue = comfortValue
            });

            Plugin.Log?.LogDebug($"Discovered {type} at {position} (shelter: {hasShelter}, comfort: {comfortValue})");
        }

        /// <summary>
        /// Update best comfort level if current is higher.
        /// </summary>
        public void UpdateBestComfort(float comfort, Vector3 position)
        {
            if (comfort > BestComfortLevel)
            {
                BestComfortLevel = comfort;
                BestComfortPosition = position;
                Plugin.Log?.LogDebug($"New best comfort: {comfort} at {position}");
            }
        }

        /// <summary>
        /// Get all known locations of a specific type.
        /// </summary>
        public IEnumerable<KnownLocation> GetLocationsOfType(LocationType type)
        {
            return m_locations.Where(l => l.Type == type);
        }

        /// <summary>
        /// Get all known locations within a certain distance.
        /// </summary>
        public IEnumerable<KnownLocation> GetLocationsWithinRange(Vector3 from, float range)
        {
            return m_locations.Where(l => Vector3.Distance(from, l.Position.ToVector3()) <= range);
        }

        /// <summary>
        /// Get all sheltered locations.
        /// </summary>
        public IEnumerable<KnownLocation> GetShelteredLocations()
        {
            return m_locations.Where(l => l.HasShelter);
        }

        /// <summary>
        /// Whether this villager should explore to find more locations.
        /// Returns true if the villager knows fewer distinct location types than the threshold.
        /// </summary>
        public bool ShouldExplore()
        {
            int variety = GetLocationTypeVariety();
            // Explore until we know at least 5 distinct location types
            return variety < 5;
        }

        /// <summary>
        /// Get the number of distinct location types this villager knows about.
        /// </summary>
        public int GetLocationTypeVariety()
        {
            return m_locations.Select(l => l.Type).Distinct().Count();
        }

        /// <summary>
        /// Get location types the villager has not yet discovered.
        /// </summary>
        public IEnumerable<LocationType> GetMissingLocationTypes()
        {
            var known = new HashSet<LocationType>(m_locations.Select(l => l.Type));
            foreach (LocationType lt in System.Enum.GetValues(typeof(LocationType)))
            {
                if (!known.Contains(lt))
                    yield return lt;
            }
        }

        /// <summary>
        /// Get locations that can be validated (everything except the home bed).
        /// </summary>
        public IEnumerable<KnownLocation> GetValidatableLocations()
        {
            return m_locations.Where(l => l.Type != LocationType.Bed);
        }

        /// <summary>
        /// Remove a known location from memory.
        /// </summary>
        public void RemoveLocation(KnownLocation location)
        {
            m_locations.Remove(location);
        }

        #region ZDO Persistence

        private const string ZdoKeyLocations = "vv_memory_locations";
        private const string ZdoKeyBestComfort = "vv_memory_best_comfort";
        private const string ZdoKeyBestComfortPos = "vv_memory_best_comfort_pos";

        /// <summary>
        /// Save memory to ZDO for persistence.
        /// </summary>
        public void SaveToZDO(ZDO zdo)
        {
            if (zdo == null) return;

            try
            {
                // Save locations as simple serialized format
                var locationData = SerializeLocations();
                zdo.Set(ZdoKeyLocations, locationData);
                zdo.Set(ZdoKeyBestComfort, BestComfortLevel);
                
                if (BestComfortPosition.HasValue)
                {
                    zdo.Set(ZdoKeyBestComfortPos, BestComfortPosition.Value);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to save villager memory: {ex.Message}");
            }
        }

        /// <summary>
        /// Load memory from ZDO.
        /// </summary>
        public void LoadFromZDO(ZDO zdo)
        {
            if (zdo == null) return;

            try
            {
                var locationData = zdo.GetString(ZdoKeyLocations, "");
                if (!string.IsNullOrEmpty(locationData))
                {
                    DeserializeLocations(locationData);
                }

                BestComfortLevel = zdo.GetFloat(ZdoKeyBestComfort, 0f);
                
                var comfortPos = zdo.GetVec3(ZdoKeyBestComfortPos, Vector3.zero);
                if (comfortPos != Vector3.zero)
                {
                    BestComfortPosition = comfortPos;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to load villager memory: {ex.Message}");
            }
        }

        /// <summary>
        /// Simple serialization format: type,x,y,z,shelter,comfort|type,x,y,z,shelter,comfort|...
        /// </summary>
        private string SerializeLocations()
        {
            var parts = m_locations.Select(l =>
                $"{(int)l.Type},{l.Position.X:F1},{l.Position.Y:F1},{l.Position.Z:F1},{(l.HasShelter ? 1 : 0)},{l.ComfortValue:F1}");
            return string.Join("|", parts);
        }

        private void DeserializeLocations(string data)
        {
            m_locations.Clear();
            
            foreach (var part in data.Split('|'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                
                var fields = part.Split(',');
                if (fields.Length < 6) continue;

                try
                {
                    var loc = new KnownLocation
                    {
                        Type = (LocationType)int.Parse(fields[0]),
                        Position = new Vec3(
                            float.Parse(fields[1]),
                            float.Parse(fields[2]),
                            float.Parse(fields[3])),
                        HasShelter = fields[4] == "1",
                        ComfortValue = float.Parse(fields[5])
                    };
                    m_locations.Add(loc);
                }
                catch
                {
                    // Skip malformed entries
                }
            }

            // Ensure bed is always in the list
            if (!m_locations.Any(l => l.Type == LocationType.Bed))
            {
                DiscoverLocation(BedPosition, LocationType.Bed, 0f, true);
            }
        }

        #endregion
    }
}
