using HarmonyLib;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Logs ItemDrop.Awake calls for debugging spawned items.
    /// </summary>
    [HarmonyPatch(typeof(ItemDrop), "Awake")]
    public static class ItemDropAwakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ItemDrop __instance)
        {
            var go = __instance.gameObject;
            Plugin.Log?.LogInfo($"[ItemDrop.Awake] {go?.name}");
        }
    }
}
