using System.Collections.Generic;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Discovers smeltable recipes from every Smelter prefab in
    ///     ZNetScene (smelter, blastfurnace, charcoal_kiln, etc) so the
    ///     blacksmith (or any villager with the <c>recipe:smelter</c> tag)
    ///     can offer them as work orders. Mods that add custom Smelter
    ///     conversions via the same component will appear automatically.
    ///     Each smelter-class prefab has its own m_conversion list — for
    ///     example the regular Smelter converts CopperOre → Copper, the
    ///     charcoal_kiln converts Wood → Coal, the blastfurnace handles
    ///     IronScrap → Iron, etc. We bucket recipes by the source
    ///     prefab's name and use that as <see cref="VirtualRecipeEntry.physicalStation"/>
    ///     so the villager routes to the correct physical station type
    ///     for each conversion.
    /// </summary>
    public static class SmelterRecipeDiscovery
    {
        public static List<VirtualRecipeEntry> GetSmelterRecipes(HashSet<string> existingOutputs)
        {
            var list = new List<VirtualRecipeEntry>();
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return list;

            // Local dedup across prefabs within this discovery pass. Several
            // mod-added prefabs ship Smelter components with overlapping
            // outputs (e.g. multiple charcoal_kiln variants registered under
            // the same name, or both Smelter and BlastFurnace producing
            // Iron). We want one Orders-tab entry per output, not one per
            // source prefab — otherwise the user sees duplicate work orders
            // for the same item. First emit wins, which means the first
            // matching Smelter prefab's physicalStation gets used; later
            // duplicates are silently skipped.
            var seenOutputs = new HashSet<string>();

            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null) continue;
                var smelter = go.GetComponent<Smelter>();
                if (smelter == null) continue;
                if (smelter.m_conversion == null || smelter.m_conversion.Count == 0) continue;

                var stationPrefab = go.name; // "smelter", "blastfurnace", "charcoal_kiln", ...

                foreach (var conv in smelter.m_conversion)
                {
                    if (conv?.m_from == null || conv.m_to == null) continue;
                    var fromName = conv.m_from.gameObject.name;
                    var toName = conv.m_to.gameObject.name;
                    if (string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toName)) continue;
                    if (existingOutputs != null && existingOutputs.Contains(toName)) continue;
                    if (!seenOutputs.Add(toName)) continue;

                    list.Add(new VirtualRecipeEntry
                    {
                        output = toName,
                        outputAmount = 1,
                        inputs = new[] { new VirtualRecipeInput { item = fromName, amount = 1 } },
                        minStationLevel = 1,
                        // The smelter consumes fuel (Coal) separately via its
                        // m_useFuel flag; StationFuelHelper handles the fuel
                        // delivery so we don't include it as a recipe input.
                        // Wood → Coal in the kiln is a "no fuel needed" case
                        // (the kiln IS the fuel producer) and naturally lands
                        // with a single Wood input.
                        physicalStation = stationPrefab,
                    });
                }
            }

            return list;
        }
    }
}
