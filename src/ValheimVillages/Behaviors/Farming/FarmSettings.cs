namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    /// Configuration settings for NPC farming behavior.
    /// </summary>
    public static class FarmSettings
    {
        /// <summary>Radius to search for planting positions from the farm center.</summary>
        public const float PlantSearchRadius = 10f;

        /// <summary>Radius to scan for harvestable crops around farm locations.</summary>
        public const float HarvestScanRadius = 20f;

        /// <summary>
        /// Maximum number of seeds to plant per farming session.
        /// Prevents NPC from planting endlessly if seeds are abundant.
        /// </summary>
        public const int MaxPlantsPerSession = 10;

        /// <summary>
        /// Maximum harvested items the NPC carries per trip before walking back to the container.
        /// </summary>
        public const int MaxHarvestCarryPerTrip = 5;
    }
}
