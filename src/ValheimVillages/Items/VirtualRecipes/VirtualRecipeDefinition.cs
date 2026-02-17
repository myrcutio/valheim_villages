using System;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    /// Top-level container for a set of virtual recipes associated with a station.
    /// Loaded from embedded JSON via Unity's JsonUtility.
    /// </summary>
    [Serializable]
    public class VirtualRecipeFile
    {
        /// <summary>
        /// Virtual station name (e.g., "$vv_farmer").
        /// </summary>
        public string station = "";

        /// <summary>
        /// Recipes available at this virtual station.
        /// </summary>
        public VirtualRecipeEntry[] recipes;

        /// <summary>
        /// For farmer station: substrings to exclude when discovering cultivator pieces.
        /// If a piece name (lowercased) contains any of these, it is not offered as a planting recipe.
        /// E.g. "cultivate", "grass", "rock", "firtree_sapling".
        /// </summary>
        public string[] cultivatorPieceExclusions;
    }

    /// <summary>
    /// A single virtual recipe entry describing an NPC task.
    /// </summary>
    [Serializable]
    public class VirtualRecipeEntry
    {
        /// <summary>
        /// Output item prefab name (e.g., "CookedMeat").
        /// </summary>
        public string output = "";

        /// <summary>
        /// Number of output items produced per craft.
        /// </summary>
        public int outputAmount = 1;

        /// <summary>
        /// Input items required to craft.
        /// </summary>
        public VirtualRecipeInput[] inputs;

        /// <summary>
        /// Minimum station level required for this recipe.
        /// </summary>
        public int minStationLevel = 1;

        /// <summary>
        /// Physical station type override. When set, the NPC walks to this
        /// station type instead of the virtual station.
        /// Values: "cookingstation" (routes to CookingStation component).
        /// </summary>
        public string physicalStation = "";
    }

    /// <summary>
    /// Minimal recipe entry (no nested inputs) for JsonUtility fallback parsing.
    /// Unity often fails to deserialize nested arrays; this type parses reliably.
    /// </summary>
    [Serializable]
    public class VirtualRecipeEntryMinimal
    {
        public string output = "";
        public int outputAmount = 1;
        public int minStationLevel = 1;
        public string physicalStation = "";
    }

    /// <summary>
    /// An input resource for a virtual recipe.
    /// </summary>
    [Serializable]
    public class VirtualRecipeInput
    {
        /// <summary>
        /// Input item prefab name (e.g., "RawMeat").
        /// </summary>
        public string item = "";

        /// <summary>
        /// Amount of this item required.
        /// </summary>
        public int amount = 1;
    }

}
