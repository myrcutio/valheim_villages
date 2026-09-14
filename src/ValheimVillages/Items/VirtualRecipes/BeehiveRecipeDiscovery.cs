using System.Collections.Generic;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Discovers the honey harvest from every <see cref="Beehive" /> prefab in ZNetScene,
    ///     so a villager with the <c>recipe:beekeeping</c> tag can offer it as a work order.
    ///     Mods adding their own Beehive-component prefabs (or changing what a hive yields)
    ///     appear automatically, because the output is read off the hive's own
    ///     <c>m_honeyItem</c> rather than hard-coded.
    ///
    ///     <para>The entry has NO inputs: a hive already holds its honey, so harvesting costs
    ///     nothing but the trip. <see cref="VirtualRecipeEntry.physicalStation" /> routes the
    ///     villager to <see cref="BeehiveHelper" />, which finds a hive that actually has
    ///     honey in it, extracts, and sweeps up the drops.</para>
    ///
    ///     <para>Deliberately a DISCOVERY source rather than a hand-written entry in a
    ///     definition's <c>stationRecipes</c>: harvesting is a capability of whatever hives
    ///     exist in the world, the same shape as cooking and smelting, and it keeps beekeeping
    ///     on the code path the other virtual recipes already use.</para>
    /// </summary>
    public static class BeehiveRecipeDiscovery
    {
        public static List<VirtualRecipeEntry> GetBeehiveRecipes(HashSet<string> existingOutputs)
        {
            var list = new List<VirtualRecipeEntry>();
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return list;

            // One entry per distinct yield, not per hive prefab — several hive variants all
            // producing Honey must not become several identical work orders.
            var seenOutputs = new HashSet<string>();

            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null) continue;
                var hive = go.GetComponent<Beehive>();
                if (hive == null || hive.m_honeyItem == null) continue;

                // The Beehive COMPONENT is not exclusive to bee hives: piece_birdnest uses it
                // too (yielding Feathers). "recipe:beekeeping" means keeping bees, so scope to
                // hives by prefab name — discovering every Beehive-component prefab silently
                // handed the Farmer a Feathers order nobody asked for. A nest/coop harvest
                // would be its own capability tag, not a side effect of this one.
                if (go.name.IndexOf("beehive", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var output = hive.m_honeyItem.gameObject.name;
                if (string.IsNullOrEmpty(output)) continue;
                if (existingOutputs != null && existingOutputs.Contains(output)) continue;
                if (!seenOutputs.Add(output)) continue;

                list.Add(new VirtualRecipeEntry
                {
                    output = output,
                    outputAmount = 1,
                    inputs = null, // harvest — the hive already holds the honey
                    minStationLevel = 1,
                    physicalStation = BeehiveHelper.PhysicalStation,
                });
            }

            if (list.Count > 0)
                Plugin.Log?.LogInfo(
                    "[BeehiveRecipeDiscovery] discovered " + list.Count + " hive yield(s): " +
                    string.Join(", ", list.ConvertAll(e => e.output)));

            return list;
        }
    }
}
