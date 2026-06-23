using HarmonyLib;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Prevents ItemDrop and ZNetView Awake from running on our template prefabs.
    ///     Templates have names like "vv_lode_core", spawned clones have "vv_lode_core(Clone)".
    /// </summary>
    [HarmonyPatch(typeof(ItemDrop), "Awake")]
    public static class ItemDropAwakeProtectionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ItemDrop __instance)
        {
            var name = __instance.gameObject.name;

            // Skip Awake for our template prefabs (not clones)
            if (name.StartsWith("vv_") && !name.Contains("(Clone)"))
            {
                Plugin.Log?.LogInfo($"[ItemDrop.Awake] Skipping for template: {name}");
                return false; // Skip original Awake
            }

            return true; // Run original Awake for clones and other items
        }
    }

    [HarmonyPatch(typeof(ItemDrop), "OnDestroy")]
    public static class ItemDropOnDestroyProtectionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ItemDrop __instance)
        {
            var name = __instance.gameObject.name;

            // Skip OnDestroy for our template prefabs that never had Awake run,
            // otherwise the swap-and-remove from s_instances uses an invalid m_myIndex
            if (name.StartsWith("vv_") && !name.Contains("(Clone)")) return false; // Skip original OnDestroy

            return true;
        }
    }

    [HarmonyPatch(typeof(ZNetView), "Awake")]
    public static class ZNetViewAwakeProtectionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZNetView __instance)
        {
            var name = __instance.gameObject.name;

            // Skip Awake for our template prefabs (not clones)
            if (name.StartsWith("vv_") && !name.Contains("(Clone)"))
            {
                Plugin.Log?.LogInfo($"[ZNetView.Awake] Skipping for template: {name}");
                return false; // Skip original Awake
            }

            return true; // Run original Awake for clones and other items
        }
    }
}