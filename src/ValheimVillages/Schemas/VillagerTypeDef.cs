using System;
using System.Collections.Generic;

namespace ValheimVillages.Schemas
{
    /// <summary>
    /// Villager type definition loaded from JSON.
    /// Field names must match JSON keys exactly.
    /// </summary>
    [Serializable]
    public class VillagerDef
    {
        public string type = "";
        public string category = "";
        public string displayName = "";
        public string description = "";

        // Requirements
        public List<WorkbenchRequirement> workbenches = new List<WorkbenchRequirement>();
        public List<string> requiredBiomes = new List<string>();
        public List<string> preferredBiomes = new List<string>();
        public bool requiresCoastal = false;
        public int minComfortLevel = 2;
        public string materialRequirements = "";

        // Benefits
        public string scalingType = "Comfort";
        public string dependentOnNpcType = "";
        public List<TieredBenefit> tieredBenefits = new List<TieredBenefit>();
        public List<ProductionOutput> productions = new List<ProductionOutput>();

        // Villager interdependence
        public List<string> providesLevelTo = new List<string>();
        public List<string> receivesLevelFrom = new List<string>();
        public string interdependenceDescription = "";

        // Visual / equipment
        public string preferredPrefab = "";
        public List<string> equipment = new List<string>();
        public string skinColor = "";
        public string weaponRotationFix = "";

        // Station configuration
        public string stationName = "";
        public string stationIcon = "";
        public List<string> workStations = new List<string>();
        public List<StationRecipe> stationRecipes = new List<StationRecipe>();
        public List<string> cultivatorExclusions = new List<string>();

        // Behavior composition
        public List<string> behaviors = new List<string>();
        public List<string> tags = new List<string>();

        // Dialog lines (override Dverger defaults on NpcTalk component)
        public List<string> randomTalk = new List<string>();
        public List<string> randomGreets = new List<string>();
        public List<string> randomGoodbye = new List<string>();
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

    /// <summary>
    /// A single-input recipe for a villager's virtual station.
    /// Flat schema (no nested arrays) to avoid Unity JsonUtility deserialization issues.
    /// </summary>
    [Serializable]
    public class StationRecipe
    {
        public string output = "";
        public int outputAmount = 1;
        public string input = "";
        public int inputAmount = 1;
        public int minStationLevel = 1;
    }
}
