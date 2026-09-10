using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: show every ObjectDB recipe producing an item, with the crafting station
    ///     it demands. Answers "why can't villager X craft Y?" — the usual causes are a recipe
    ///     with NO station (hand-craftable, which the station-matched lookup can never see) or a
    ///     station whose <c>m_name</c> differs from the villager's <c>workStations</c> entry.
    /// </summary>
    public static class RecipeProbeCommand
    {
        [DevCommand("Show recipes producing an item + the station they need: vv_recipe_probe <item> [villagerType]",
            Name = "vv_recipe_probe")]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                Print("usage: vv_recipe_probe <itemPrefabName> [villagerType]");
                return;
            }

            var item = args[1];
            var villagerType = args.Length > 2 ? args[2] : null;

            if (ObjectDB.instance == null)
            {
                Print("[vv_recipe_probe] ObjectDB not ready");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_recipe_probe] recipes producing '{item}':");

            var hits = 0;
            foreach (var r in ObjectDB.instance.m_recipes)
            {
                if (r?.m_item == null) continue;
                var produced = r.m_item.gameObject.name;
                if (produced != item) continue;

                hits++;
                var station = r.m_craftingStation != null
                    ? $"'{r.m_craftingStation.m_name}'"
                    : "(NONE — hand-craftable)";
                sb.AppendLine($"  recipe '{r.name}' station={station} enabled={r.m_enabled}");
            }

            if (hits == 0) sb.AppendLine("  (no recipe produces that item name)");

            if (!string.IsNullOrEmpty(villagerType))
            {
                var stations = StationMatcher.GetStationNames(villagerType);
                sb.AppendLine($"  {villagerType} workStations: " +
                              (stations.Length > 0 ? string.Join(", ", stations) : "(none)"));
                var match = StationMatcher.FindRecipeForNpc(item, villagerType);
                sb.AppendLine($"  FindRecipeForNpc -> {(match != null ? match.name : "NULL")}");
            }

            Print(sb.ToString().TrimEnd());
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
