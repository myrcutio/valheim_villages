namespace ValheimVillages.NPCs
{
    /// <summary>
    /// Categories of NPC types in the village system.
    /// </summary>
    public enum NpcCategory
    {
        /// <summary>Villagers provide indirect benefits through production.</summary>
        Villager,

        /// <summary>Specialists provide direct gameplay benefits.</summary>
        Specialist
    }

    /// <summary>
    /// All available NPC types in the village system.
    /// </summary>
    public enum NpcType
    {
        Farmer,
        Miner,
        Blacksmith,
        Carpenter,
        Scout,
        Trader,
        Guard,
        Mountaineer,
        Shipwright,
        TavernKeeper
    }

    /// <summary>
    /// Scaling type for NPC benefits.
    /// </summary>
    public enum BenefitScaling
    {
        /// <summary>Benefits scale with comfort level.</summary>
        Comfort,
        /// <summary>Benefits scale with workbench level.</summary>
        Workbench,
        /// <summary>Benefits scale with both comfort and workbench.</summary>
        Combined,
        /// <summary>Benefits scale with villager variety in the village.</summary>
        VillagerVariety,
        /// <summary>Benefits scale based on another villager's level.</summary>
        VillagerDependent
    }
}

