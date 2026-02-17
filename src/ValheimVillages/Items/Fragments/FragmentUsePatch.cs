using HarmonyLib;
using ValheimVillages.Items;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    /// Intercepts item use for ransom note fragments.
    /// When a player uses a fragment, attempts to combine 3 same-biome fragments
    /// into a rescue quest marker on the map.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
    public static class FragmentUsePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (__instance is not Player player)
                return true;

            string itemName = item?.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(itemName))
                return true;

            // Check if this is a fragment item
            var def = ItemFactory.GetDefinition(itemName);
            if (def == null || def.itemType != "fragment")
                return true;

            // Attempt to combine fragments of this biome
            string biome = def.biome;
            if (string.IsNullOrEmpty(biome))
                return true;

            FragmentCombiner.TryCombine(player, biome);
            return false; // Consume the interaction
        }
    }
}
