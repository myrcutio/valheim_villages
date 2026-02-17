using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// A known location that the NPC has discovered.
    /// </summary>
    public class KnownLocation
    {
        public Vector3 Position { get; set; }
        public LocationType Type { get; set; }
        public bool HasShelter { get; set; }
        public float ComfortValue { get; set; }
        
        /// <summary>
        /// Distance threshold for considering two locations as exactly the same spot.
        /// </summary>
        public const float SameLocationThreshold = 2f;

        /// <summary>
        /// Check if this is the exact same spot (within 2m).
        /// </summary>
        public bool IsSameLocation(Vector3 other)
        {
            return Vector3.Distance(Position, other) < SameLocationThreshold;
        }

        /// <summary>
        /// Check if another location of the same type is too close.
        /// Different location types have different minimum spacing requirements.
        /// </summary>
        public bool IsTooCloseForSameType(Vector3 other)
        {
            float minDistance = GetMinDistanceForType(Type);
            return Vector3.Distance(Position, other) < minDistance;
        }

        /// <summary>
        /// Get the minimum distance between locations of the same type.
        /// </summary>
        public static float GetMinDistanceForType(LocationType type)
        {
            return type switch
            {
                LocationType.Bed => 3f,        // Beds are specific - keep close threshold
                LocationType.Shelter => 20f,   // Shelters should be spread out
                LocationType.Fire => 15f,      // Fires/hearths spread out
                LocationType.Chair => 8f,      // Chairs can be closer (furniture clusters)
                LocationType.Table => 10f,     // Tables spread out moderately
                LocationType.Farm => 25f,      // Farm areas spread out widely
                LocationType.Animals => 20f,   // Animal areas spread out
                LocationType.Patrol => 30f,    // Patrol points well-spaced
                LocationType.CraftStation => 5f, // Craft stations close together is fine
                LocationType.CookingStation => 5f, // Cooking stations close together is fine
                _ => 10f
            };
        }

        /// <summary>
        /// Get the maximum number of locations to remember per type.
        /// </summary>
        public static int GetMaxLocationsForType(LocationType type)
        {
            return type switch
            {
                LocationType.Bed => 1,         // Only one home bed
                LocationType.Shelter => 3,     // A few shelter spots
                LocationType.Fire => 2,        // Couple of fire locations
                LocationType.Chair => 3,       // A few seating spots
                LocationType.Table => 2,       // Couple of dining spots
                LocationType.Farm => 2,        // Farm areas
                LocationType.Animals => 2,     // Animal spots
                LocationType.Patrol => 5,      // Several patrol waypoints
                LocationType.CraftStation => 3, // A few craft stations
                LocationType.CookingStation => 5, // A few cooking stations
                _ => 3
            };
        }

        /// <summary>
        /// Calculate a quality score for this location (for comparison).
        /// Higher is better.
        /// </summary>
        public float GetQualityScore()
        {
            float score = ComfortValue * 10f;
            if (HasShelter) score += 5f;
            return score;
        }
    }
}
