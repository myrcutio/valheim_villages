using System;
using ValheimVillages.Items;
using ValheimVillages.Villages.Entity;
using ValheimVillages.Networking;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Routes registry-removal cleanup to the host. Village ZDOs are host-authoritative
    ///     (<c>ZDOMan.DestroyZDO</c> requires ownership), but a registry piece is removed on the
    ///     player's CLIENT. When a registry whose village FAILED triad validation is removed, the
    ///     client asks the host to reap that invalid village so it doesn't linger and re-bind on
    ///     re-placement. Mirrors <c>VillagerRecruitRpc</c>: a routed RPC that self-dispatches on a
    ///     listen-host. Pumped from <c>Plugin.Update</c> via <see cref="EnsureRegistered" />.
    /// </summary>
    public static class VillageCleanupRpc
    {
        private const string RpcName = "VV_DeleteVillage";
        private static object s_registeredInstance;

        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredInstance)) return;
            s_registeredInstance = rpc;
            try
            {
                RoutedRpcRegistrar.ClearStale(rpc, RpcName);
                rpc.Register<string>(RpcName, OnDelete);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[VillageCleanup] register skipped: {ex.Message}");
            }
        }

        /// <summary>
        ///     Piece-removal hook entry. If the removed piece is a registry whose village failed
        ///     validation, ask the host to reap it. Runs on the removing peer (a client on a
        ///     dedicated server); cheap pre-check here, authoritative re-check on the host.
        /// </summary>
        public static void OnRegistryRemoved(WearNTear wnt)
        {
            var znv = wnt != null ? wnt.GetComponent<ZNetView>() : null;
            var zdo = znv != null ? znv.GetZDO() : null;
            if (zdo == null) return;
            if (zdo.GetPrefab() != PieceFactory.RegistryPrefabName.GetStableHashCode()) return;

            var villageId = zdo.GetString(Village.IdKey);
            if (string.IsNullOrEmpty(villageId)) return;

            // A village is reapable once nothing is left of it: no living villagers and no
            // registry piece. NOT "IsInvalid" — that flag means triad validation failed,
            // which a live, working village sets whenever its wall is breached, so reaping
            // on it deleted villages out from under their villagers.
            //
            // This runs as a WearNTear.Remove PREFIX, so THIS piece's ZDO still exists and
            // is discounted by count. The host re-checks once it is actually gone.
            if (!VillageRegistry.CanDelete(villageId, out _, 1)) return;

            RequestDelete(villageId);
        }

        private static void RequestDelete(string villageId)
        {
            EnsureRegistered();
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcName, villageId); // no target → routed to host
        }

        private static void OnDelete(long sender, string villageId)
        {
            // Authoritative reap — host only (the host owns the village ZDO).
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var village = VillageRegistry.FindById(villageId);
            if (village == null) return;

            // Authoritative re-check. Delete() enforces this too; asking first keeps the
            // reason in the log instead of only a refusal.
            if (!VillageRegistry.CanDelete(villageId, out var blockedBy))
            {
                Plugin.Log?.LogInfo(
                    $"[VillageCleanup] not reaping {villageId}: {blockedBy} (requestedBy={sender})");
                return;
            }

            VillageRegistry.Delete(villageId);
            Plugin.Log?.LogInfo(
                $"[VillageCleanup] reaped empty village {villageId} — no villagers, no registry " +
                $"(requestedBy={sender})");
        }
    }
}
