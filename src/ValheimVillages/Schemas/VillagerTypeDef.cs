using System;
using System.Collections.Generic;

namespace ValheimVillages.Schemas
{
    /// <summary>
    ///     Villager type definition loaded from JSON.
    ///     Field names must match JSON keys exactly.
    /// </summary>
    [Serializable]
    public class VillagerDef
    {
        public string type = "";
        public string category = "";
        public string displayName = "";
        public string description = "";

        // Not-yet-implemented villager types are flagged disabled: their JSON is kept
        // but they are hidden from registry recruit lists and excluded from any random
        // villager generation (generic pawns, fragment rescue quests). Defaults to false
        // so existing definitions stay enabled unless the JSON opts out.
        public bool disabled;

        // Requirements
        public List<WorkbenchRequirement> workbenches = new();
        public List<string> requiredBiomes = new();
        public List<string> preferredBiomes = new();
        public bool requiresCoastal;
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

        /// <summary>
        ///     Item prefab whose model is grafted into the villager's hand, replacing whatever
        ///     the body prefab came holding. Cosmetic only.
        ///
        ///     <para>Deliberately not <see cref="equipment" />: that list is functional gear, and
        ///     a Guard's entry there is a Club it must NOT appear to hold — it fights with the
        ///     Dvergr's native crossbow.</para>
        /// </summary>
        public string heldTool = "";

        /// <summary>
        ///     Local euler rotation "x,y,z" for the held tool's mesh, or blank to keep the
        ///     rotation the item prefab authored for a player's grip.
        ///
        ///     <para>Per-type, not global: each model runs along its own axis, so the angle that
        ///     lines a cultivator up with the Dvergr's grip would twist an axe out of it. The
        ///     graft wrapper and the body's own attach root both sit at identity under the same
        ///     hand bone, so a value here means exactly what the replaced prop's own local
        ///     rotation means — copying the staff's lines the tool up with the staff.</para>
        /// </summary>
        public string heldToolRotation = "";

        /// <summary>
        ///     Local position "x,y,z" for the held tool's mesh, or blank to keep the offset the
        ///     item prefab authored. Expressed in the hand bone's frame, like
        ///     <see cref="heldToolRotation" />.
        /// </summary>
        public string heldToolPosition = "";

        /// <summary>
        ///     Item carried in the off hand. Defaults to a lantern for every villager so a
        ///     village reads as inhabited after dark; blank it to carry nothing.
        /// </summary>
        public string offHandItem = "Lantern";

        /// <summary>
        ///     The type's signature colour, "r,g,b" with each component 0..1. Drives the cot
        ///     blanket today and is intended for the villager's clothing too, so it is a general
        ///     identity colour rather than a furniture setting. Validated at registry load.
        /// </summary>
        public string themeColor = "";

        // Station configuration
        public string stationName = "";
        public string stationIcon = "";
        public List<string> workStations = new();
        public List<StationRecipe> stationRecipes = new();
        public List<string> cultivatorExclusions = new();

        /// <summary>
        ///     Substrings (case-insensitive) that keep a Pickable out of this villager's
        ///     foraging list, matched against both the pickable prefab name and the item it
        ///     yields. Same mechanism as <see cref="cultivatorExclusions" />, and needed for the
        ///     same reason: the Pickable component covers far more than any one job — surtling
        ///     cores, tar, ore and dungeon loot all use it — so a Farmer's berry-and-mushroom
        ///     list is defined by what it rules out. Only consulted when the definition carries
        ///     the <c>recipe:foraging</c> tag.
        /// </summary>
        public List<string> forageExclusions = new();

        // Behavior composition
        public List<string> behaviors = new();
        public List<string> tags = new();

        // Dialog lines (override Dverger defaults on NpcTalk component)
        public List<string> randomTalk = new();
        public List<string> randomGreets = new();
        public List<string> randomGoodbye = new();
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

        /// <summary>
        ///     Room features the village must provide for this tier, by
        ///     <c>VillageRoomCatalog.Feature</c> name (Fire, Seat, Table, Bed, WorkStation,
        ///     Storage, Farm). All of them must be present for the tier to be reached.
        ///
        ///     <para>Replaces expressing tiers as raw comfort points. A villager's level is a
        ///     property of the rooms its village has, so the requirement names the thing to go
        ///     and build — "needs a bed" instead of "needs comfort 7", which told the player
        ///     nothing about what to do. Empty means the tier has no room requirement.</para>
        /// </summary>
        public List<string> requires = new();
    }

    [Serializable]
    public class ProductionOutput
    {
        public int requiredLevel = 1;
        public string outputItem = "";
        public string description = "";
    }

    /// <summary>
    ///     A single-input recipe for a villager's virtual station.
    ///     Flat schema (no nested arrays) to avoid Unity JsonUtility deserialization issues.
    /// </summary>
    [Serializable]
    public class StationRecipe
    {
        public string output = "";
        public int outputAmount = 1;
        public string input = "";
        public int inputAmount = 1;
        public int minStationLevel = 1;

        /// <summary>
        ///     Optional physical station this recipe is performed AT, instead of the villager's
        ///     virtual station. Matches <see cref="Items.VirtualRecipes.VirtualRecipeEntry.physicalStation" />
        ///     ("cookingstation", "farm", "beehive", or a smelter prefab name). Empty = the
        ///     virtual station.
        /// </summary>
        public string physicalStation = "";
    }
}