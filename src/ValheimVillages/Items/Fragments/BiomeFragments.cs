using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Shared biome→ransom-fragment mapping — one source of truth for both the chest loot
    ///     injection (<see cref="FragmentLootPatch" />) and the runestone reward
    ///     (<see cref="RuneStoneFragmentPatch" />), derived from ItemFactory.FragmentBiomes.
    /// </summary>
    internal static class BiomeFragments
    {
        private static Dictionary<Heightmap.Biome, string> s_map;

        private static Dictionary<Heightmap.Biome, string> Map
        {
            get
            {
                if (s_map != null) return s_map;
                s_map = new Dictionary<Heightmap.Biome, string>();
                foreach (var (biomeEnum, key, _, _, _) in ItemFactory.FragmentBiomes)
                    s_map[(Heightmap.Biome)biomeEnum] = $"vv_fragment_{key}";
                return s_map;
            }
        }

        /// <summary>
        ///     The fragment item name for the biome at <paramref name="position" />, or null if
        ///     the world generator isn't ready yet or the biome has no mapped fragment.
        /// </summary>
        public static string NameForPosition(Vector3 position)
        {
            if (WorldGenerator.instance == null) return null;
            var biome = WorldGenerator.instance.GetBiome(position);
            return Map.TryGetValue(biome, out var name) ? name : null;
        }

        /// <summary>The registered fragment prefab for <paramref name="name" />, or null.</summary>
        public static GameObject Prefab(string name)
        {
            return ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
        }
    }
}
