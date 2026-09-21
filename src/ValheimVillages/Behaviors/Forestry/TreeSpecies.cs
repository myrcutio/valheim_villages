using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Behaviors.Forestry
{
    /// <summary>
    ///     What the Lumberjack can grow, and what each species is FOR.
    ///
    ///     <para>One table, because these four facts are meaningless apart: the seed decides
    ///     what he can plant, the biome decides whether it will ever mature
    ///     (<c>Plant.m_biome</c> — a Fir in the Meadows sits at <c>WrongBiome</c> forever),
    ///     and the wood decides whether planting it serves the order the player actually
    ///     placed. Splitting them across three lookups is how you end up planting beech for a
    ///     fine-wood order.</para>
    ///
    ///     <para>Ancient wood and Mistwood are deliberately absent: the player cannot plant
    ///     those either, so a Lumberjack who could would be growing something the world does
    ///     not let anyone else grow.</para>
    ///
    ///     <para>Every prefab name here was verified against the running game — the seed
    ///     names are NOT guessable (<c>Acorn</c>, not <c>OakSeeds</c>; <c>PineCone</c>, not
    ///     <c>PineSeeds</c>) and an earlier guess at <c>sapling_fir</c> simply did not
    ///     exist.</para>
    /// </summary>
    public sealed class TreeSpecies
    {
        /// <summary>Item consumed to plant one of these.</summary>
        public string Seed;

        /// <summary>Piece prefab placed in the ground.</summary>
        public string Sapling;

        /// <summary>Item the grown tree ultimately yields once felled and split.</summary>
        public string Wood;

        /// <summary>Display name, for logs.</summary>
        public string Name;

        /// <summary>Biomes in which it will actually reach maturity.</summary>
        public Heightmap.Biome Biomes;

        /// <summary>
        ///     Ordered so that, all else equal, the commonest wood is planted first. Callers
        ///     filter this by the wood they want, the biome they are standing in, and the
        ///     seeds actually in the village's chests.
        /// </summary>
        public static readonly IReadOnlyList<TreeSpecies> All = new[]
        {
            new TreeSpecies
            {
                Name = "Beech",
                Seed = "BeechSeeds",
                Sapling = "Beech_Sapling",
                Wood = "Wood",
                Biomes = Heightmap.Biome.Meadows,
            },
            new TreeSpecies
            {
                Name = "Fir",
                Seed = "FirCone",
                Sapling = "FirTree_Sapling",
                Wood = "Wood",
                Biomes = Heightmap.Biome.BlackForest | Heightmap.Biome.Mountain,
            },
            new TreeSpecies
            {
                Name = "Pine",
                Seed = "PineCone",
                Sapling = "PineTree_Sapling",
                // "Core wood" — the only plantable source of it.
                Wood = "RoundLog",
                Biomes = Heightmap.Biome.BlackForest,
            },
            new TreeSpecies
            {
                Name = "Birch",
                Seed = "BirchSeeds",
                Sapling = "Birch_Sapling",
                Wood = "FineWood",
                Biomes = Heightmap.Biome.Meadows | Heightmap.Biome.Plains,
            },
            new TreeSpecies
            {
                Name = "Oak",
                Seed = "Acorn",
                Sapling = "Oak_Sapling",
                Wood = "FineWood",
                Biomes = Heightmap.Biome.Meadows | Heightmap.Biome.Plains,
            },
        };

        /// <summary>Every wood type a Lumberjack can produce, deduplicated, in table order.</summary>
        public static IEnumerable<string> WoodTypes()
        {
            var seen = new HashSet<string>();
            foreach (var s in All)
                if (seen.Add(s.Wood))
                    yield return s.Wood;
        }

        public bool GrowsIn(Heightmap.Biome biome) => (Biomes & biome) != 0;

        /// <summary>
        ///     Which species to put in the ground at <paramref name="p" />, and which chest is
        ///     paying for it. False when nothing can be planted here.
        ///
        ///     <para>Three filters, in order of who is entitled to decide: the PLAYER (a
        ///     standing work order for a wood type — fine wood means birch or oak, core wood
        ///     means pine), the WORLD (<c>Plant.m_biome</c>, or the sapling never matures),
        ///     and the STORES (a real seed in a village chest). Wanting fine wood does not
        ///     conjure acorns.</para>
        ///
        ///     <para>Containers are passed in rather than scanned here so the decision can be
        ///     reported (<c>vv_forestry</c>) with exactly the stock the villager would see.</para>
        /// </summary>
        public static bool Choose(
            Vector3 p,
            HashSet<string> wantedWood,
            List<Container> containers,
            out TreeSpecies species,
            out IngredientSource seed)
        {
            species = null;
            seed = null;

            var biome = Heightmap.FindBiome(p);

            // Pass 1: a species that serves an outstanding order. Pass 2: anything that will
            // grow here, so an idle woodlot still gets restocked.
            for (var pass = 0; pass < 2; pass++)
            foreach (var candidate in All)
            {
                if (!candidate.GrowsIn(biome)) continue;
                if (pass == 0 && (wantedWood == null || !wantedWood.Contains(candidate.Wood))) continue;

                foreach (var container in containers)
                {
                    var inv = container?.GetInventory();
                    if (inv == null) continue;
                    if (ContainerScanner.CountByPrefab(inv, candidate.Seed) < 1) continue;

                    species = candidate;
                    seed = new IngredientSource
                    {
                        Container = container,
                        PrefabName = candidate.Seed,
                        Amount = 1,
                    };
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     Spend the seed and put the sapling in the ground. <paramref name="failure" />
        ///     says why not, when it does not happen.
        ///
        ///     <para>Paid for FIRST, and abandoned if the seed has gone: a seed taken by the
        ///     player between choosing the spot and walking to it must mean no sapling, never a
        ///     free one. A plant with no seed behind it is a bug, not a freebie, so it fails
        ///     here rather than quietly conjuring one.</para>
        ///
        ///     <para>The spot is re-checked against <see cref="PlantSpace" /> immediately before
        ///     paying, because the walk from choosing it takes time and the seed is only worth
        ///     spending on a sapling that will actually mature. Asking the planted sapling
        ///     afterwards is useless — see <see cref="PlantSpace" /> for why it always answers
        ///     "Healthy".</para>
        /// </summary>
        public bool TryPlantAt(Vector3 spot, IngredientSource seed, out string failure)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Sapling) : null;
            if (prefab == null)
            {
                failure = $"sapling prefab '{Sapling}' not found";
                return false;
            }

            if (seed == null)
            {
                failure = $"no seed to pay for a {Name}";
                return false;
            }

            if (!PlantSpace.CanGrow(prefab, spot, out var blocked))
            {
                failure = $"the spot will not grow a {Name}: {blocked}";
                return false;
            }

            if (!ContainerScanner.RemoveIngredients(new List<IngredientSource> { seed }))
            {
                failure = $"the {seed.PrefabName} was gone before planting";
                return false;
            }

            Object.Instantiate(prefab, spot, Quaternion.Euler(0f, Random.Range(0, 360), 0f));
            failure = null;
            return true;
        }
    }
}
