using System.Collections.Generic;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Discovers cookable recipes from the CookingStation's m_conversion list so the farmer
    ///     (or tavernkeeper) can offer them as work orders. Mods that add custom cookable items
    ///     via the same conversion list will appear automatically.
    /// </summary>
    public static class CookingRecipeDiscovery
    {
        /// <summary>
        ///     Returns virtual recipe entries for all CookingStation conversions (raw → cooked).
        ///     Uses the first CookingStation prefab found in ZNetScene; mods that add conversions
        ///     to that station will be included.
        /// </summary>
        public static List<VirtualRecipeEntry> GetCookingRecipes(HashSet<string> existingOutputs)
        {
            var list = new List<VirtualRecipeEntry>();
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return list;

            CookingStation cookingStation = null;
            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null) continue;
                var cs = go.GetComponent<CookingStation>();
                if (cs == null) continue;
                cookingStation = cs;
                break;
            }

            if (cookingStation?.m_conversion == null || cookingStation.m_conversion.Count == 0)
                return list;

            foreach (var conv in cookingStation.m_conversion)
            {
                if (conv?.m_from == null || conv.m_to == null) continue;
                var fromName = conv.m_from.gameObject.name;
                var toName = conv.m_to.gameObject.name;
                if (string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toName)) continue;
                if (existingOutputs != null && existingOutputs.Contains(toName)) continue;

                list.Add(new VirtualRecipeEntry
                {
                    output = toName,
                    outputAmount = 1,
                    inputs = new[] { new VirtualRecipeInput { item = fromName, amount = 1 } },
                    minStationLevel = 1,
                    physicalStation = "cookingstation",
                });
            }

            return list;
        }
    }
}