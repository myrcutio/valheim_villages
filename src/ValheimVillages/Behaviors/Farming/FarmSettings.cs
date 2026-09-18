namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    ///     Configuration settings for NPC farming behavior.
    /// </summary>
    public static class FarmSettings
    {
        /// <summary>Radius to search for planting positions from the farm center.</summary>
        public const float PlantSearchRadius = 20f;


        /// <summary>
        ///     Fallback radius for the ripe-crop scan, used ONLY when the village has no
        ///     published footprint yet (no partition since world load). Normally the scan is
        ///     scoped to the whole village footprint instead — a radius around one anchor cuts
        ///     off the far half of a real settlement.
        /// </summary>
        public const float HarvestScanRadius = 20f;

        /// <summary>
        ///     Maximum number of seeds to plant per farming session.
        ///     Prevents NPC from planting endlessly if seeds are abundant.
        /// </summary>
        public const int MaxPlantsPerSession = 10;

        /// <summary>
        ///     Maximum harvested items the NPC carries per trip before walking back to the container.
        /// </summary>
        public const int MaxHarvestCarryPerTrip = 2;

        /// <summary>Seconds the NPC waits at a plant spot before placing the seed.</summary>
        public const float PlantCooldownSeconds = 2f;

        /// <summary>NPC must be within this distance of the target position to plant.</summary>
        public const float PlantProximityRequired = 1.5f;
    }
}