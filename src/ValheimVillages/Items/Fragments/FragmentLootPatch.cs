using HarmonyLib;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Injects biome-appropriate ransom note fragments into chest loot tables. MUST be a
    ///     PREFIX on Container.Awake: the engine rolls a chest's contents inside Awake
    ///     (AddDefaultItems → m_defaultItems.GetDropListItems(), gated by the one-shot
    ///     s_addedDefaultItems ZDO flag). A postfix adds the fragment to m_defaultItems AFTER
    ///     that roll has happened and the flag is set, so it could never appear — we must
    ///     extend m_defaultItems BEFORE the original Awake consumes it.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class FragmentLootPatch
    {
        private const float FragmentDropWeight = 0.15f;

        /// <summary>
        ///     Extend the chest's default-item drop table with this biome's fragment BEFORE
        ///     the original Awake rolls it into the chest inventory (see class remarks).
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(Container __instance)
        {
            if (__instance.m_defaultItems?.m_drops == null || __instance.m_defaultItems.m_drops.Count == 0)
                return; // chests carry a default drop table; player-built storage doesn't

            var fragmentName = BiomeFragments.NameForPosition(__instance.transform.position);
            if (fragmentName == null)
                return;

            var fragmentPrefab = BiomeFragments.Prefab(fragmentName);
            if (fragmentPrefab == null)
                return;

            // Check if fragment is already in the drop table
            foreach (var drop in __instance.m_defaultItems.m_drops)
                if (drop.m_item != null && drop.m_item.name == fragmentName)
                    return;

            // Add fragment to the drop table with low weight
            __instance.m_defaultItems.m_drops.Add(new DropTable.DropData
            {
                m_item = fragmentPrefab,
                m_stackMin = 1,
                m_stackMax = 1,
                m_weight = FragmentDropWeight,
                m_dontScale = true,
            });
        }

    }
}