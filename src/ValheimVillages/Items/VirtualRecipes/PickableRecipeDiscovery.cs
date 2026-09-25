using System.Collections.Generic;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Discovers what every <see cref="Pickable" /> prefab in ZNetScene yields, so a villager
    ///     with the <c>recipe:foraging</c> tag can offer it as a work order — a blueberry or
    ///     raspberry bush standing in the village becomes an orderable "Blueberries" job, and the
    ///     Farmer walks over and picks it when it is ripe.
    ///
    ///     <para>The entry has NO inputs: a ripe bush already holds its fruit, so harvesting
    ///     costs nothing but the trip. <see cref="VirtualRecipeEntry.physicalStation" /> routes
    ///     the villager to <see cref="ForageHelper" />, which finds a ripe pickable inside the
    ///     village footprint that yields THIS order's item, picks it, and sweeps up the drops.
    ///     Same shape as beekeeping, which is the same shape as cooking and smelting.</para>
    ///
    ///     <para>Yields are read off each prefab's own <c>m_itemPrefab</c> rather than
    ///     hard-coded, so mod-added forageables appear automatically. Because the whole
    ///     Pickable population is far broader than any one villager's job — surtling cores, tar,
    ///     dungeon loot and ore all use the same component — only pickables that REGROW in place
    ///     (<c>m_respawnTimeMinutes &gt; 0</c>) count. A bush, a mushroom or a thistle comes back
    ///     on its own; a Fuling totem, a charred skull, a pot shard or a Dvergr tankard is a
    ///     one-shot prop that is gone once taken, so it is never something a villager can be
    ///     ordered to keep supplying. A denylist alone let every unlisted prop through. The
    ///     definition's <c>forageExclusions</c> still applies on top, for regrowing things the
    ///     Farmer shouldn't touch.</para>
    ///
    ///     <para>Outputs already registered for the station are skipped, which is what keeps a
    ///     PLANTABLE crop (Carrot, Turnip, Barley...) on the farm route: cultivator discovery
    ///     claims those first, so they keep plant-then-harvest and only wild-only yields take
    ///     the forage route.</para>
    /// </summary>
    public static class PickableRecipeDiscovery
    {
        public static List<VirtualRecipeEntry> GetForagingRecipes(
            HashSet<string> existingOutputs, IReadOnlyList<string> exclusionSubstringsLower)
        {
            var list = new List<VirtualRecipeEntry>();
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return list;

            var objectDB = ObjectDB.instance;
            if (objectDB == null) return list;

            // One entry per distinct yield, not per pickable prefab — the six bush variants that
            // all drop Raspberries must not become six identical work orders.
            var seenOutputs = new HashSet<string>();
            var excluded = new List<string>();
            var oneShot = new List<string>();

            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null) continue;
                var pickable = go.GetComponent<Pickable>();
                if (pickable == null || pickable.m_itemPrefab == null) continue;

                var output = pickable.m_itemPrefab.name;
                if (string.IsNullOrEmpty(output)) continue;
                if (existingOutputs != null && existingOutputs.Contains(output)) continue;

                // Checked per prefab, BEFORE the per-output dedup: if any prefab yielding this
                // output regrows, that is the one that makes it forageable.
                if (pickable.m_respawnTimeMinutes <= 0f)
                {
                    oneShot.Add(go.name + "->" + output);
                    continue;
                }

                if (!seenOutputs.Add(output)) continue;

                // Match exclusions on BOTH names: the prefab says what is being harvested
                // (Pickable_SurtlingCore), the output says what comes out (SurtlingCore), and a
                // player tuning the list will reach for whichever one they can see in game.
                if (CultivatorRecipeDiscovery.MatchesExclusion(go.name, exclusionSubstringsLower)
                    || CultivatorRecipeDiscovery.MatchesExclusion(output, exclusionSubstringsLower))
                {
                    excluded.Add(output);
                    continue;
                }

                // A yield with no ObjectDB item prefab cannot become a Recipe at all; skipping it
                // here keeps CreateRecipe from logging a warning per prefab for things that were
                // never orderable (world-only drops).
                if (objectDB.GetItemPrefab(output) == null) continue;

                list.Add(new VirtualRecipeEntry
                {
                    output = output,
                    outputAmount = pickable.m_amount > 0 ? pickable.m_amount : 1,
                    inputs = null, // harvest — the plant already holds the crop
                    minStationLevel = 1,
                    physicalStation = ForageHelper.PhysicalStation,
                });
            }

            if (list.Count > 0)
                Plugin.Log?.LogInfo(
                    "[PickableRecipeDiscovery] discovered " + list.Count + " forageable yield(s): " +
                    string.Join(", ", list.ConvertAll(e => e.output)));
            if (excluded.Count > 0)
                Plugin.Log?.LogInfo(
                    "[PickableRecipeDiscovery] excluded " + excluded.Count + " yield(s) by definition filter: " +
                    string.Join(", ", excluded.ToArray()));
            if (oneShot.Count > 0)
                Plugin.Log?.LogInfo(
                    "[PickableRecipeDiscovery] skipped " + oneShot.Count + " one-shot pickable(s) that never regrow: " +
                    string.Join(", ", oneShot.ToArray()));

            return list;
        }
    }
}
