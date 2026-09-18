using HarmonyLib;
using ValheimVillages.Settings;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Logs ItemDrop.Awake calls for debugging spawned items.
    ///
    ///     <para>Off unless <see cref="LogSettings.VerboseItemSpawns" /> is set: this fires
    ///     for EVERY item that comes into existence anywhere in the loaded world, at
    ///     LogInfo, which made it both unbounded and impossible to filter by level.</para>
    /// </summary>
    [HarmonyPatch(typeof(ItemDrop), "Awake")]
    public static class ItemDropAwakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ItemDrop __instance)
        {
            if (!LogSettings.VerboseItemSpawns) return;
            var go = __instance.gameObject;
            Plugin.Log?.LogInfo($"[ItemDrop.Awake] {go?.name}");
        }
    }
}