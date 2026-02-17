using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Time of day periods for NPC behavior decisions.
    /// </summary>
    public enum TimeOfDay
    {
        Night,      // 9pm - 6am: Sleep time
        Morning,    // 6am - 10am: Wake up, start activities
        Day,        // 10am - 5pm: Main activity period
        Evening     // 5pm - 9pm: Social/feast time
    }

    /// <summary>
    /// Types of locations NPCs can discover and remember.
    /// </summary>
    public enum LocationType
    {
        Bed,        // Home bed - sleep location
        Shelter,    // Any covered area (roof overhead)
        Fire,       // Fireplace/campfire (preferably sheltered)
        Chair,      // Seating furniture
        Table,      // Tables for feasting/socializing
        Farm,       // Cultivated soil areas
        Animals,    // Tame animal locations
        Patrol,     // Outdoor patrol waypoints
        CraftStation, // Crafting stations (Forge, Workbench, etc.)
        CookingStation // Cooking stations (CookingStation, Cauldron, etc.)
    }

    /// <summary>
    /// Current state of the NPC's behavior.
    /// </summary>
    public enum BehaviorState
    {
        Idle,           // Standing/sitting at a location
        Wandering,      // Moving randomly around current area
        Traveling,      // Moving directly to a specific target location
        Sleeping,       // In bed at night
        Patrolling,     // Outdoor patrol route
        Exploring,      // Searching for new location types
        Working,        // Crafting: executing a work order
        Scouting,       // Guard: walking out from bed to find walls
        CircuitTracing, // Guard: tracing circle around bed, building waypoints
        Alarmed         // Guard: breach detected, waiting for player
    }

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

    /// <summary>
    /// Current environmental context for behavior decisions.
    /// </summary>
    public struct BehaviorContext
    {
        public bool IsRaining;
        public TimeOfDay TimeOfDay;
        public bool InShelter;
        public float CurrentComfort;
        public Vector3 CurrentPosition;
    }

    /// <summary>
    /// Configuration settings for villager behavior.
    /// </summary>
    public static class VillagerSettings
    {
        /// <summary>Maximum distance from bed that NPC will wander.</summary>
        public const float MaxWanderRange = 300f;

        /// <summary>Radius to check for nearby POIs while wandering.</summary>
        public const float DiscoveryRadius = 15f;

        /// <summary>How often to re-evaluate behavior (seconds).</summary>
        public const float UpdateInterval = 15f;

        /// <summary>
        /// Random jitter range (seconds) applied to each NPC's initial behavior tick.
        /// Prime number so tick offsets rarely re-align over time.
        /// </summary>
        public const float BehaviorTickJitter = 11f;

        /// <summary>How close to target before considering arrived (meters).</summary>
        /// <remarks>
        /// Set to 1m so NPCs get very close to their destination before stopping.
        /// Tests may use a slightly higher tolerance (2m) to account for pathfinding.
        /// </remarks>
        public const float ArrivalThreshold = 1.0f;

        // Time boundaries as day fraction (0-1 where 0.5 = noon)
        // Valheim: 0.25 = 6am, 0.5 = noon, 0.75 = 6pm
        public const float NightStart = 0.875f;   // ~9pm
        public const float MorningStart = 0.25f;  // 6am
        public const float DayStart = 0.417f;     // ~10am
        public const float EveningStart = 0.708f; // ~5pm
    }

    /// <summary>
    /// Configuration settings for door interaction.
    /// </summary>
    public static class DoorSettings
    {
        /// <summary>Delay before closing a door after NPC passes through (seconds).</summary>
        public const float DoorCloseDelay = 1.5f;

        /// <summary>Radius to detect nearby doors (meters).</summary>
        public const float DoorDetectionRadius = 3f;

        /// <summary>Time without movement progress before checking for blocking doors (seconds).</summary>
        public const float MovementStallThreshold = 1f;

        /// <summary>Minimum distance NPC must move to be considered making progress (meters).</summary>
        public const float MovementProgressThreshold = 0.5f;
    }

    /// <summary>
    /// Configuration settings for exploration behavior.
    /// NPCs will explore when they lack variety in their known locations.
    /// </summary>
    public static class ExplorationSettings
    {
        /// <summary>
        /// Minimum number of different location types before NPC stops exploring.
        /// With 8 location types total (Bed, Shelter, Fire, Chair, Table, Farm, Animals, Patrol),
        /// requiring 4 means NPC should know about half of them before being satisfied.
        /// </summary>
        public const int MinDesiredVariety = 4;

        /// <summary>
        /// Probability (0-1) that an NPC will choose to explore instead of going to a known location
        /// when their location variety is below the minimum.
        /// </summary>
        public const float ExplorationChance = 0.4f;

        /// <summary>
        /// Minimum distance from all known locations when picking an exploration target.
        /// This encourages exploring truly new areas.
        /// </summary>
        public const float MinDistanceFromKnown = 25f;

        /// <summary>
        /// Maximum distance from bed when picking an exploration target.
        /// Should be less than MaxWanderRange to stay safe.
        /// </summary>
        public const float MaxExplorationRange = 250f;

        /// <summary>
        /// How long to wander at an exploration point before giving up (seconds).
        /// </summary>
        public const float ExplorationDuration = 5f;

        /// <summary>
        /// Random movement range while exploring an area.
        /// </summary>
        public const float ExplorationWanderRadius = 25f;
    }

    /// <summary>
    /// Configuration settings for NPC work order crafting behavior.
    /// </summary>
    public static class WorkSettings
    {
        /// <summary>Radius to scan for chests containing work orders (meters).</summary>
        public const float ChestScanRadius = 20f;

        /// <summary>Time spent at crafting station per craft cycle (seconds).</summary>
        public const float CraftDuration = 5f;

        /// <summary>How often to re-scan for work when idle (seconds).</summary>
        public const float WorkScanInterval = 30f;

        /// <summary>
        /// Arrival distance for work destinations (chests, stations).
        /// Wider than the normal ArrivalThreshold because physical colliders on
        /// furniture can prevent NPCs from getting within 1m.
        /// </summary>
        public const float WorkArrivalThreshold = 2.5f;

        /// <summary>How often to poll the cooking station when waiting for food to finish (seconds).</summary>
        public const float CookingPollInterval = 1f;

        /// <summary>Extra seconds to wait after cook time before checking/removing (buffer for game to set Done).</summary>
        public const float CookingDoneGraceSeconds = 1.5f;

        /// <summary>Seconds without reaching the work destination (within 2m 3D) before giving up and trying something else.</summary>
        public const float WorkStuckTimeoutSeconds = 30f;
    }
}
