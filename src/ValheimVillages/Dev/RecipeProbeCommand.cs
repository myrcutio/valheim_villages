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
                sb.AppendLine($"  recipe '{r.name}' station={station} enabled={r.m_enabled} " +
                              $"inputs={r.m_resources?.Length ?? 0} minLvl={r.m_minStationLevel}");
                // Name them. "inputs=4" says a villager needs four things and not WHICH four,
                // which is the only part that answers "why is this order not being worked?".
                if (r.m_resources != null)
                    foreach (var req in r.m_resources)
                        sb.AppendLine(
                            $"      needs {(req?.m_resItem != null ? req.m_resItem.name : "?")}" +
                            $" x{req?.m_amount}");
                sb.AppendLine($"    player-facing: {DescribePlayerGates(r)}");
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

        /// <summary>
        ///     Why the player's crafting list does (or doesn't) show this recipe.
        ///
        ///     <para>A recipe existing in ObjectDB is NOT enough to reach the Orders UI: the
        ///     work-order button acts on the SELECTED row of the craft list, and
        ///     <c>Player.GetAvailableRecipes</c> only emits a recipe the player has discovered
        ///     (<c>m_knownRecipes</c> keyed on the OUTPUT item's shared name) at a station it
        ///     requires. Those two gates are invisible from the recipe itself, which is exactly
        ///     how a correctly-registered recipe goes missing from the list.</para>
        /// </summary>
        private static string DescribePlayerGates(Recipe r)
        {
            var player = Player.m_localPlayer;
            if (player == null) return "no local player (dedicated server)";

            var shared = r.m_item?.m_itemData?.m_shared?.m_name;
            if (string.IsNullOrEmpty(shared)) return "output has no shared name";

            var known = player.IsRecipeKnown(shared);

            var current = player.GetCurrentCraftingStation();
            var currentName = current != null ? current.m_name : "(none)";

            var listed = false;
            var available = new System.Collections.Generic.List<Recipe>();
            player.GetAvailableRecipes(ref available);
            foreach (var a in available)
                if (ReferenceEquals(a, r))
                {
                    listed = true;
                    break;
                }

            return $"sharedName='{shared}' known={known} inCraftList={listed} " +
                   $"currentStation='{currentName}'";
        }

        private static void Print(string s)
        {
            // Capped + chunked: a single oversized write to a headless server's
            // stdout pipe blocks the main thread. See ConsoleReport.
            ValheimVillages.Dev.ConsoleReport.Emit(s);
        }
    }
}
