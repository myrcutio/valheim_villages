using System;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Item definition loaded from JSON via Unity's JsonUtility.
    ///     Field names must match JSON keys exactly.
    /// </summary>
    [Serializable]
    public class ItemDefinition
    {
        public string name = "";
        public string source = "";
        public string basePrefab = "";
        public string displayName = "";
        public string description = "";
        public int maxStackSize = 1;
        public float weight = 1.0f;
        public int variants;

        /// <summary>
        ///     Item category: "pawn", "fragment", "workorder". Empty for legacy items.
        /// </summary>
        public string itemType = "";

        /// <summary>
        ///     Biome association for fragment items (e.g., "Meadows", "BlackForest").
        /// </summary>
        public string biome = "";

        /// <summary>
        ///     Crafting station type for work order items (e.g., "Workbench", "Forge").
        /// </summary>
        public string stationType = "";
    }
}