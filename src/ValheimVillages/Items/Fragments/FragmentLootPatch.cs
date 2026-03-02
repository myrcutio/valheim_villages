using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Items;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    /// Injects ransom note fragments into chest loot tables.
    /// Fragments match the biome where the chest is located.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class FragmentLootPatch
    {
        private const float FragmentDropWeight = 0.15f;

        private static Dictionary<Heightmap.Biome, string> s_biomeFragmentMap;

        /// <summary>
        /// Maps Heightmap.Biome values to fragment item names,
        /// derived from ItemFactory.FragmentBiomes.
        /// </summary>
        private static Dictionary<Heightmap.Biome, string> BiomeFragmentMap
        {
            get
            {
                if (s_biomeFragmentMap != null) return s_biomeFragmentMap;
                s_biomeFragmentMap = new Dictionary<Heightmap.Biome, string>();
                foreach (var (biomeEnum, key, _, _, _) in ItemFactory.FragmentBiomes)
                    s_biomeFragmentMap[(Heightmap.Biome)biomeEnum] = $"vv_fragment_{key}";
                return s_biomeFragmentMap;
            }
        }

        /// <summary>
        /// Patch Container.Awake to inject biome-appropriate fragment into chest drop tables.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Container __instance)
        {
            if (__instance.m_defaultItems == null || __instance.m_defaultItems.m_drops == null)
                return;

            // Only add to containers that already have loot (chests, not player-built storage)
            if (__instance.m_defaultItems.m_drops.Count == 0)
                return;

            var biome = GetBiomeAtPosition(__instance.transform.position);
            if (!BiomeFragmentMap.TryGetValue(biome, out var fragmentName))
                return;

            var fragmentPrefab = GetFragmentPrefab(fragmentName);
            if (fragmentPrefab == null)
                return;

            // Check if fragment is already in the drop table
            foreach (var drop in __instance.m_defaultItems.m_drops)
            {
                if (drop.m_item != null && drop.m_item.name == fragmentName)
                    return;
            }

            // Add fragment to the drop table with low weight
            __instance.m_defaultItems.m_drops.Add(new DropTable.DropData
            {
                m_item = fragmentPrefab,
                m_stackMin = 1,
                m_stackMax = 1,
                m_weight = FragmentDropWeight,
                m_dontScale = true
            });
        }

        private static Heightmap.Biome GetBiomeAtPosition(Vector3 position)
        {
            var worldGen = WorldGenerator.instance;
            if (worldGen != null)
                return worldGen.GetBiome(position);

            return Heightmap.Biome.Meadows;
        }

        private static GameObject GetFragmentPrefab(string fragmentName)
        {
            if (ZNetScene.instance == null)
                return null;

            return ZNetScene.instance.GetPrefab(fragmentName);
        }
    }
}
