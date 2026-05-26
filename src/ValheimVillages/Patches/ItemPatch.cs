using HarmonyLib;
using ValheimVillages.Items;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Villager;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Matches Azumatt's ItemManager pattern exactly.
    /// </summary>
    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    [HarmonyPriority(Priority.VeryHigh)]
    public static class ObjectDBAwakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ObjectDB __instance)
        {
            ItemFactory.RegisterAll(__instance);
            VirtualRecipeLoader.RegisterAll(__instance);
        }
    }

    [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
    [HarmonyPriority(Priority.VeryHigh)]
    public static class ObjectDBCopyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ObjectDB __instance)
        {
            ItemFactory.RegisterAll(__instance);
            VirtualRecipeLoader.RegisterAll(__instance);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    [HarmonyPriority(Priority.VeryHigh)]
    public static class ZNetSceneAwakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ZNetScene __instance)
        {
            ItemFactory.RegisterAllInZNetScene(__instance);
            VirtualRecipeLoader.RegisterCookingRecipesIfNeeded(ObjectDB.instance);
            // Log available Dvergr prefabs for debugging (single spawn path: Villager)
            VillagerPawnPatch.LogAvailableDvergrPrefabs();
        }
    }
}