using HarmonyLib;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     TELEMETRY ONLY (vv_log itemdestroy) — changes nothing. Traces every network destroy of an item-drop ZDO on
    ///     this peer: whether the ZDO was known when the destroy arrived, and whether it was
    ///     actually gone afterwards. A drop the owner has destroyed but this peer still holds is
    ///     a ghost (seen on the dedicated server as haul targets the owning client no longer has).
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
    public static class ItemDropDestroyTelemetryPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ZDOMan __instance, ZDOID uid, out string __state)
        {
            __state = null;
            if (!Settings.LogSettings.VerboseItemDestroy) return;
            var zdo = __instance.GetZDO(uid);
            var prefabName = PrefabName(zdo);
            if (zdo != null && prefabName == null) return; // known ZDO, not an item drop

            __state = prefabName ?? "unknown(zdo_not_found)";
            DebugLog.Event("ItemDestroy", "received",
                ("uid", uid), ("prefab", __state), ("known", zdo != null),
                ("owner", zdo?.GetOwner()), ("zdo_pos", zdo?.GetPosition()));
        }

        [HarmonyPostfix]
        private static void Postfix(ZDOMan __instance, ZDOID uid, string __state)
        {
            if (__state == null) return;
            DebugLog.Event("ItemDestroy", "handled",
                ("uid", uid), ("prefab", __state), ("still_exists", __instance.GetZDO(uid) != null));
        }

        /// <summary>The ZDO's prefab name when it is an ItemDrop, else null.</summary>
        private static string PrefabName(ZDO zdo)
        {
            if (zdo == null || ZNetScene.instance == null) return null;
            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            return prefab != null && prefab.GetComponent<ItemDrop>() != null ? prefab.name : null;
        }
    }
}
