using System.Collections.Generic;
using HarmonyLib;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     TELEMETRY ONLY — changes nothing. Reports when a network destroy reaches an object
    ///     whose registered instance is already a dead Unity object.
    ///
    ///     <para><c>ZNetScene.OnZDODestroyed</c> reads <c>value.gameObject</c> on the
    ///     registered <see cref="ZNetView" />. <c>ZNetView.OnDestroy</c> does not unregister
    ///     itself, so any code that destroys a networked GameObject with a bare
    ///     <c>Object.Destroy</c> (bypassing <c>ZNetScene.Destroy</c>) leaves a dead entry in
    ///     <c>m_instances</c>; the next destroy for that ZDO then throws, and
    ///     <c>ZDOMan.HandleDestroyedZDO</c> aborts before <c>RemoveFromSector</c> — the ZDO
    ///     survives on this peer as a ghost. This names the prefab and sender so the source
    ///     can be traced.</para>
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "OnZDODestroyed")]
    public static class ZdoDestroyTelemetryPatch
    {
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        [HarmonyPrefix]
        private static void Prefix(ZNetScene __instance, ZDO zdo)
        {
            if (zdo == null) return;
            if (!Instances(__instance).TryGetValue(zdo, out var view)) return;
            if (view != null) return; // live instance — the engine handles it

            var prefab = __instance.GetPrefab(zdo.GetPrefab());
            DebugLog.Event("ZdoDestroy", "dead_registered_instance",
                ("prefab", prefab != null ? prefab.name : zdo.GetPrefab().ToString()),
                ("uid", zdo.m_uid), ("zdo_pos", zdo.GetPosition()),
                ("owner", zdo.GetOwner()), ("is_owner", zdo.IsOwner()),
                ("view_is_csharp_null", ReferenceEquals(view, null)),
                ("note", "engine will throw here and leave this ZDO as a ghost"));
        }
    }
}
