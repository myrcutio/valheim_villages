using System;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// A known location that the NPC has discovered.
    /// Uses Vec3 instead of UnityEngine.Vector3 for Core compatibility.
    /// </summary>
    public class KnownLocation
    {
        public Vec3 Position { get; set; }
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
        public bool IsSameLocation(Vec3 other)
        {
            return Position.DistanceTo(other) < SameLocationThreshold;
        }

        /// <summary>
        /// Check if another location of the same type is too close.
        /// Different location types have different minimum spacing requirements.
        /// </summary>
        public bool IsTooCloseForSameType(Vec3 other)
        {
            float minDistance = GetMinDistanceForType(Type);
            return Position.DistanceTo(other) < minDistance;
        }

        /// <summary>
        /// Get the minimum distance between locations of the same type.
        /// </summary>
        public static float GetMinDistanceForType(LocationType type)
        {
            switch (type)
            {
                case LocationType.Bed: return 3f;
                case LocationType.Shelter: return 20f;
                case LocationType.Fire: return 15f;
                case LocationType.Chair: return 8f;
                case LocationType.Table: return 10f;
                case LocationType.Farm: return 25f;
                case LocationType.Animals: return 20f;
                case LocationType.Patrol: return 30f;
                case LocationType.CraftStation: return 5f;
                case LocationType.CookingStation: return 5f;
                default: return 10f;
            }
        }

        /// <summary>
        /// Get the maximum number of locations to remember per type.
        /// </summary>
        public static int GetMaxLocationsForType(LocationType type)
        {
            switch (type)
            {
                case LocationType.Bed: return 1;
                case LocationType.Shelter: return 3;
                case LocationType.Fire: return 2;
                case LocationType.Chair: return 3;
                case LocationType.Table: return 2;
                case LocationType.Farm: return 2;
                case LocationType.Animals: return 2;
                case LocationType.Patrol: return 5;
                case LocationType.CraftStation: return 3;
                case LocationType.CookingStation: return 5;
                default: return 3;
            }
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
