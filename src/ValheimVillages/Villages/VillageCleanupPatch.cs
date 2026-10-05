using HarmonyLib;
using ValheimVillages.Items;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Reaps a village when its last registry piece is destroyed and nobody lives there.
    ///     <para>Runs on the HOST, off the destroy itself. It used to be a client→host request
    ///     sent from a <c>WearNTear.Remove</c> prefix, which lost a race every time: the request
    ///     reached the host before the piece's destroy did, so the host's authoritative re-check
    ///     still counted the piece ("1 registry piece(s) still point at it") and refused.
    ///     Moving a registry therefore always left an orphan village behind — five of them in
    ///     one base on valheim-boo. <c>HandleDestroyedZDO</c> is where every peer's destroy
    ///     lands on the host, with the ZDO still readable, so the piece is known for certain to
    ///     be going and is simply excluded from the count.</para>
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
    public static class VillageCleanupPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ZDOMan __instance, ZDOID uid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var zdo = __instance.GetZDO(uid);
            if (zdo == null || zdo.GetPrefab() != PieceFactory.RegistryPrefabName.GetStableHashCode()) return;

            var villageId = zdo.GetString(Village.IdKey);
            if (string.IsNullOrEmpty(villageId) || VillageRegistry.FindById(villageId) == null) return;

            if (!VillageRegistry.CanDelete(villageId, out var blockedBy, uid))
            {
                Plugin.Log?.LogInfo($"[VillageCleanup] registry removed; keeping {villageId}: {blockedBy}");
                return;
            }

            VillageRegistry.Delete(villageId, uid);
            Plugin.Log?.LogInfo($"[VillageCleanup] reaped empty village {villageId} — no villagers, no registry");
        }
    }
}
