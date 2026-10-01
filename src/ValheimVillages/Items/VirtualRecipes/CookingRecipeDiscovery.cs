using System.Collections.Generic;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Discovers cookable recipes from EVERY CookingStation prefab's m_conversion list — the
    ///     basic cooking station, the iron cooking station, the oven, and any modded ones — so the
    ///     farmer (or tavernkeeper) can offer them as work orders. Mods that add cookable items
    ///     via a conversion list appear automatically.
    ///
    ///     <para>It used to read only the FIRST CookingStation prefab in ZNetScene. That is the
    ///     basic station, so anything only the iron station or the oven can make — cooked
    ///     serpent meat, lox, bread, pies — was never offered. Each recipe is routed at work
    ///     time to a station that can actually make it (<c>StationFinder.CanCook</c>).</para>
    /// </summary>
    public static class CookingRecipeDiscovery
    {
        /// <summary>
        ///     Returns one virtual recipe entry per distinct cooked output across all
        ///     CookingStation prefabs (raw → cooked). An output two stations both make is
        ///     offered once.
        /// </summary>
        public static List<VirtualRecipeEntry> GetCookingRecipes(HashSet<string> existingOutputs)
        {
            var list = new List<VirtualRecipeEntry>();
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return list;

            var seenOutputs = new HashSet<string>();
            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null) continue;
                var station = go.GetComponent<CookingStation>();
                if (station?.m_conversion == null) continue;

                foreach (var conv in station.m_conversion)
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
                        physicalStation = "cookingstation",
                    });
                }
            }

            return list;
        }
    }
}