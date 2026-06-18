using HarmonyLib;
using ValheimVillages.Attributes;
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
            // ItemFactory + PieceFactory ZNetScene/hammer-table registration depends on
            // ObjectDB being populated, which may not have happened yet at ZNetScene.Awake
            // (the two Awakes race on world load). Enqueue them as deferred [RequireObjectDB]
            // tasks instead of calling eagerly, so they run once ObjectDB is alive rather than
            // silently no-opping when it isn't.
            AttributeScanner.EnqueueObjectDBDependentTasks();
            // Agent-dependent setup ([RequireAgent]) defers further still — until the slot-31
            // bake is installed (which waits on zone load + piece instantiation).
            AttributeScanner.EnqueueAgentDependentTasks();
            ValheimVillages.Villager.Records.RecordPrefabFactory.RegisterInZNetScene(__instance);
            ValheimVillages.Villages.Entity.VillagePrefabFactory.RegisterInZNetScene(__instance);
            // VirtualRecipeLoader.RegisterCookingRecipesIfNeeded now runs as a deferred
            // [RequireObjectDB] task (enqueued above), so it no longer silently no-ops when
            // ObjectDB lost the Awake race against ZNetScene.
            // Log available Dvergr prefabs for debugging (single spawn path: Villager)
            VillagerSpawner.LogAvailableDvergrPrefabs();
        }
    }
}