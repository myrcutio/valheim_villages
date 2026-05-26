namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Configuration settings for villager behavior.
    /// </summary>
    public static class VillagerSettings
    {
        /// <summary>Maximum distance from bed that NPC will wander.</summary>
        public const float MaxWanderRange = 200f;

        /// <summary>Radius to check for nearby POIs while wandering.</summary>
        public const float DiscoveryRadius = 15f;

        /// <summary>How often to re-evaluate behavior (seconds).</summary>
        public const float UpdateInterval = 15f;

        /// <summary>
        ///     Random jitter range (seconds) applied to each NPC's initial behavior tick.
        ///     Prime number so tick offsets rarely re-align over time.
        /// </summary>
        public const float BehaviorTickJitter = 11f;

        /// <summary>How close to target before considering arrived (meters).</summary>
        /// <remarks>
        ///     Set to 1m so NPCs get very close to their destination before stopping.
        ///     Tests may use a slightly higher tolerance (2m) to account for pathfinding.
        /// </remarks>
        public const float ArrivalThreshold = 2f;

        /// <summary>
        ///     Hard timeout: seconds since last successful waypoint arrival before
        ///     the guard is teleported back to their bed as a safety reset.
        /// </summary>
        public const float PatrolHardStuckTimeoutSeconds = 60f;

        /// <summary>
        ///     Fraction of the character's normal m_jumpForce used for automatic step-up
        ///     jumps when stuck against raised geometry. 1.0 = full jump, 0.5 = half height.
        /// </summary>
        public const float StepJumpForceFraction = 0.6f;

        // Time boundaries as day fraction (0-1 where 0.5 = noon)
        // Valheim: 0.25 = 6am, 0.5 = noon, 0.75 = 6pm
        public const float NightStart = 0.875f; // ~9pm
        public const float MorningStart = 0.25f; // 6am
        public const float DayStart = 0.417f; // ~10am
        public const float EveningStart = 0.708f; // ~5pm
    }

    /// <summary>
    ///     Configuration settings for door interaction.
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
    ///     Configuration settings for exploration behavior.
    ///     NPCs will explore when they lack variety in their known locations.
    /// </summary>
    public static class ExplorationSettings
    {
        /// <summary>
        ///     Minimum number of different location types before NPC stops exploring.
        ///     With 8 location types total (Bed, Shelter, Fire, Chair, Table, Farm, Animals, Patrol),
        ///     requiring 4 means NPC should know about half of them before being satisfied.
        /// </summary>
        public const int MinDesiredVariety = 4;

        /// <summary>
        ///     Probability (0-1) that an NPC will choose to explore instead of going to a known location
        ///     when their location variety is below the minimum.
        /// </summary>
        public const float ExplorationChance = 0.4f;

        /// <summary>
        ///     Minimum distance from all known locations when picking an exploration target.
        ///     This encourages exploring truly new areas.
        /// </summary>
        public const float MinDistanceFromKnown = 25f;

        /// <summary>
        ///     Maximum distance from bed when picking an exploration target.
        ///     Should be less than MaxWanderRange to stay safe.
        /// </summary>
        public const float MaxExplorationRange = 250f;

        /// <summary>
        ///     How long to wander at an exploration point before giving up (seconds).
        /// </summary>
        public const float ExplorationDuration = 5f;

        /// <summary>
        ///     Random movement range while exploring an area.
        /// </summary>
        public const float ExplorationWanderRadius = 25f;
    }

    /// <summary>
    ///     Configuration settings for NPC work order crafting behavior.
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
        ///     Arrival distance for work destinations (chests, stations).
        ///     Wider than the normal ArrivalThreshold because physical colliders on
        ///     furniture can prevent NPCs from getting within 1m.
        /// </summary>
        public const float WorkArrivalThreshold = 2.5f;

        /// <summary>How often to poll the cooking station when waiting for food to finish (seconds).</summary>
        public const float WaitingPollInterval = 3f;

        /// <summary>Extra seconds to wait after cook time before checking/removing (buffer for game to set Done).</summary>
        public const float CookingDoneGraceSeconds = 1.5f;

        /// <summary>Seconds without reaching the work destination (within 2m 3D) before giving up and trying something else.</summary>
        public const float WorkStuckTimeoutSeconds = 30f;
    }
}