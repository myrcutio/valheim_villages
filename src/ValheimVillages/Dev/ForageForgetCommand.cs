using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using ValheimVillages.Attributes;
using ValheimVillages.Items.VirtualRecipes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Repairs a character that learned forage recipes it should never have been offered.
    ///
    ///     <para>Before <see cref="ForageRecipeDiscoveryPatch" /> existed, every forage recipe
    ///     unlocked the moment a player knew the villager's station, because a zero-ingredient
    ///     recipe has no material for the engine's discovery check to gate on. Those unlocks are
    ///     written into the character profile (<c>m_knownRecipes</c>), so the patch stops new
    ///     ones but cannot undo the ones already saved — this does.</para>
    ///
    ///     <para>Deliberately a command rather than an automatic sweep on spawn: this edits saved
    ///     character knowledge, which should happen because the player asked, not silently behind
    ///     them. Nothing is lost either way — picking the plant re-learns the recipe on the
    ///     spot.</para>
    /// </summary>
    public static class ForageForgetCommand
    {
        [DevCommand("Forget forage recipes for plants this character has never picked: " +
                    "vv_forage_forget", Name = "vv_forage_forget")]
        public static void Forget(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Print("[vv_forage_forget] no local player on this peer");
                return;
            }

            if (!(AccessTools.Field(typeof(Player), "m_knownRecipes")?.GetValue(player)
                    is HashSet<string> known))
            {
                Print("[vv_forage_forget] could not read m_knownRecipes");
                return;
            }

            var db = ObjectDB.instance;
            if (db?.m_recipes == null)
            {
                Print("[vv_forage_forget] ObjectDB not ready");
                return;
            }

            // A known recipe is stored by the OUTPUT's shared name, not by recipe name, so an
            // item that some other recipe also produces must be left alone — forgetting
            // "Blueberries" would forget that other recipe too. Only drop a name when every
            // recipe producing it is one of ours and still unearned.
            var candidates = new HashSet<string>();
            var claimedElsewhere = new HashSet<string>();
            foreach (var r in db.m_recipes)
            {
                var shared = r?.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(shared)) continue;

                if (ForageRecipeDiscoveryPatch.IsUndiscoveredForage(player, r))
                    candidates.Add(shared);
                else
                    claimedElsewhere.Add(shared);
            }

            var removed = new List<string>();
            foreach (var shared in candidates)
            {
                if (claimedElsewhere.Contains(shared)) continue;
                if (known.Remove(shared)) removed.Add(shared);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_forage_forget] forgot {removed.Count} unearned forage recipe(s)");
            foreach (var name in removed)
                sb.AppendLine("    " + Localization.instance.Localize(name));
            if (removed.Count > 0)
                sb.AppendLine("  Pick one of these plants and its recipe comes back immediately.");

            Print(sb.ToString());
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
