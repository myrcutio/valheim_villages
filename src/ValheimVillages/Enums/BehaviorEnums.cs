namespace ValheimVillages.Enums
{
    /// <summary>
    ///     Time of day periods for NPC behavior decisions.
    /// </summary>
    public enum TimeOfDay
    {
        Night, // 9pm - 6am: Rest time
        Morning, // 6am - 10am: Wake up, start activities
        Day, // 10am - 5pm: Main activity period
        Evening, // 5pm - 9pm: Social/feast time
    }

    /// <summary>
    ///     Types of locations NPCs can discover and remember.
    /// </summary>
    public enum LocationType
    {
        Bed, // Home bed - sleep location
        Shelter, // Any covered area (roof overhead)
        Fire, // Fireplace/campfire (preferably sheltered)
        Table, // Tables for feasting/socializing
        Chair, // Chairs for sitting
        Farm, // Cultivated soil areas
        Animals, // Tame animal locations
        CraftStation, // Crafting stations (Forge, Workbench, etc.)
        CookingStation, // Cooking stations (CookingStation, Cauldron, etc.)
    }

    /// <summary>
    ///     Current state of the NPC's behavior.
    /// </summary>
    public enum BehaviorState
    {
        Idle, // Standing/sitting at a location
        Wandering, // Moving randomly around current area
        Traveling, // Moving directly to a specific target location
        Patrolling, // Outdoor patrol route
        Exploring, // Searching for new location types
        Working, // Crafting: executing a work order
        Scouting, // Patrol: walking out from bed to find walls
        CircuitTracing, // Patrol: tracing circle around bed, building waypoints
        Alarmed, // Patrol: breach detected, waiting for player
    }
}