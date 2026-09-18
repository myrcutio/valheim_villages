using HarmonyLib;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Holds a forage recipe back until the player has actually seen the thing it harvests.
    ///
    ///     <para>Valheim discovers a recipe when you know its MATERIALS:
    ///     <c>Player.UpdateKnownRecipesList</c> calls
    ///     <c>HaveRequirements(recipe, discover: true)</c>, which walks <c>m_resources</c> and
    ///     checks each against <c>m_knownMaterial</c>. A forage recipe has NO resources — a ripe
    ///     bush already holds its fruit — so that loop never executes and the check returns true
    ///     for everyone. The only remaining gate is knowing the villager's station, so the
    ///     instant a player learns <c>$vv_farmer</c> the game unlocks a forage recipe for every
    ///     pickable in the game and queues a "$msg_newrecipe" popup for each: a Meadows character
    ///     was handed recipes for plants that only grow in biomes they have never visited.</para>
    ///
    ///     <para>The fix applies vanilla's own rule to the only material a harvest actually has —
    ///     the thing being harvested. Blocking <c>AddKnownRecipe</c> suppresses both the popup and
    ///     the <c>m_knownRecipes</c> entry, and nothing else: the recipe stays
    ///     <c>m_enabled</c>, so <see cref="Villager.AI.Work.StationMatcher" /> still resolves it
    ///     for a villager working an order that was already placed, and a dedicated server (no
    ///     local player, nothing to gate) is untouched.</para>
    ///
    ///     <para>It re-opens on its own: picking one blueberry runs
    ///     <c>OnInventoryChanged</c> → <c>AddKnownItem</c> (which adds the material) →
    ///     <c>UpdateKnownRecipesList</c>, so the recipe is discovered on that same call with one
    ///     popup, for a plant the player is holding.</para>
    ///
    ///     <para>Deliberately NOT solved by giving the recipe a token self-ingredient to gate on:
    ///     a requirement only gates discovery when its amount is 1 or more, and an amount the
    ///     engine can see is an amount OUR scan sees too — the villager would then need a
    ///     blueberry in a chest before it was allowed to go pick blueberries.</para>
    /// </summary>
    [HarmonyPatch(typeof(Player), "AddKnownRecipe")]
    public static class ForageRecipeDiscoveryPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Player __instance, Recipe recipe)
        {
            return !IsUndiscoveredForage(__instance, recipe);
        }

        /// <summary>
        ///     True for one of our forage recipes whose harvested item this player has never
        ///     held. False for every other recipe in the game, so vanilla discovery is untouched.
        /// </summary>
        public static bool IsUndiscoveredForage(Player player, Recipe recipe)
        {
            if (player == null || recipe == null || recipe.m_item == null) return false;
            if (VirtualRecipeLoader.GetPhysicalStation(recipe.name) != ForageHelper.PhysicalStation)
                return false;

            var sharedName = recipe.m_item.m_itemData?.m_shared?.m_name;
            if (string.IsNullOrEmpty(sharedName)) return false;

            return !player.IsMaterialKnown(sharedName);
        }
    }
}
