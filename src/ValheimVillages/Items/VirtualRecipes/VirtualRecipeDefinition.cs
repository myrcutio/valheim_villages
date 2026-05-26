using System;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     A virtual recipe entry produced by recipe discovery (cultivator, cooking).
    ///     Also used internally when converting StationRecipe definitions from VillagerDef.
    /// </summary>
    [Serializable]
    public class VirtualRecipeEntry
    {
        public string output = "";
        public int outputAmount = 1;
        public VirtualRecipeInput[] inputs;
        public int minStationLevel = 1;

        /// <summary>
        ///     Physical station type override. When set, the NPC walks to this
        ///     station type instead of the virtual station.
        ///     Values: "cookingstation" (routes to CookingStation component),
        ///     "farm" (routes to cultivator planting).
        /// </summary>
        public string physicalStation = "";
    }

    /// <summary>
    ///     An input resource for a virtual recipe.
    /// </summary>
    [Serializable]
    public class VirtualRecipeInput
    {
        public string item = "";
        public int amount = 1;
    }
}