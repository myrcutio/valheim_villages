using System;
using System.Collections.Generic;

namespace ValheimVillages.NPCs
{
    /// <summary>
    /// NPC type definition loaded from JSON via Unity's JsonUtility.
    /// Field names must match JSON keys exactly.
    /// </summary>
    [Serializable]
    public class NpcTypeDefinition
    {
        public string type = "";
        public string category = "";
        public string displayName = "";
        public string description = "";

        // Requirements
        public List<WorkbenchRequirement> workbenches = new();
        public List<string> requiredBiomes = new();
        public List<string> preferredBiomes = new();
        public bool requiresCoastal = false;
        public int minComfortLevel = 2;
        public string materialRequirements = "";

        // Benefits
        public string scalingType = "Comfort";
        public string dependentOnNpcType = "";
        public List<TieredBenefit> tieredBenefits = new();
        public List<ProductionOutput> productions = new();

        // Villager interdependence
        public List<string> providesLevelTo = new();
        public List<string> receivesLevelFrom = new();
        public string interdependenceDescription = "";

        // Visual / equipment
        public string preferredPrefab = "";
        public List<string> equipment = new();
        public string skinColor = "";
        public string weaponRotationFix = "";

        // Dialog lines (override Dverger defaults on NpcTalk component)
        public List<string> randomTalk = new();
        public List<string> randomGreets = new();
        public List<string> randomGoodbye = new();

        /// <summary>Parse the type string to NpcType enum.</summary>
        public NpcType GetNpcType() => Enum.TryParse<NpcType>(type, out var t) ? t : NpcType.Farmer;

        /// <summary>Parse the category string to NpcCategory enum.</summary>
        public NpcCategory GetCategory() => category == "Specialist" ? NpcCategory.Specialist : NpcCategory.Villager;

        /// <summary>Parse the scaling type string to BenefitScaling enum.</summary>
        public BenefitScaling GetScalingType() => Enum.TryParse<BenefitScaling>(scalingType, out var s) ? s : BenefitScaling.Comfort;
    }

    [Serializable]
    public class WorkbenchRequirement
    {
        public string name = "";
        public int minLevel = 1;
    }

    [Serializable]
    public class TieredBenefit
    {
        public int minLevel = 1;
        public int maxLevel = 99;
        public string description = "";
        public float effectMultiplier = 1.0f;
    }

    [Serializable]
    public class ProductionOutput
    {
        public int requiredLevel = 1;
        public string outputItem = "";
        public string description = "";
    }
}
