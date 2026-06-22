using System;
using UnityEngine;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Host-authoritative work-order config edits (Fix C P2). The quota config lives on
    ///     the host-owned <c>vv_village</c> carrier ZDO, so a client must NOT write it directly
    ///     (a client write loses the ownership race against the host — the original clobber).
    ///     Instead the client resolves its village id (graph-independent
    ///     <see cref="Villages.Entity.VillageRegistry.FindNearAnchor" />) and sends a routed RPC;
    ///     the HOST applies the change via <c>Village.UpsertWorkOrder</c>/<c>RemoveWorkOrder</c>.
    ///     Mirrors <see cref="VillagerRecruitRpc" /> exactly (register on ZRoutedRpc-instance
    ///     change, InvokeRoutedRPC with no target → server, IsServer-guarded handler). Handlers
    ///     are synchronous, so ZRoutedRpc's main-thread dispatch serializes concurrent edits.
    /// </summary>
    public static class WorkOrderConfigRpc
    {
        private const string SetRpc = "VV_SetWorkOrder";
        private const string DeleteRpc = "VV_DeleteWorkOrder";

        private static object s_registeredInstance;

        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredInstance)) return;
            s_registeredInstance = rpc;
            try
            {
                rpc.Register<string, string, string, string, int, int>(SetRpc, OnSet);
                rpc.Register<string, string, string>(DeleteRpc, OnDelete);
                Plugin.Log?.LogInfo("[WorkOrderRpc] registered VV_SetWorkOrder/VV_DeleteWorkOrder");
            }
            catch (Exception ex)
            {
                // ZRoutedRpc has no Unregister; re-registering the same instance throws on the
                // duplicate key (only possible after a hot reload, which the server never does).
                Plugin.Log?.LogWarning($"[WorkOrderRpc] register skipped: {ex.Message}");
            }
        }

        /// <summary>Create or update a work order's quota on the owning village (host applies).</summary>
        public static void RequestSet(string villageId, string station, string item, string itemDisplay, int min, int max)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[WorkOrderRpc] no ZRoutedRpc available; cannot set work order");
                return;
            }

            rpc.InvokeRoutedRPC(SetRpc, villageId ?? "", station ?? "", item ?? "", itemDisplay ?? "", min, max);
        }

        /// <summary>Delete a work order from the owning village (host applies).</summary>
        public static void RequestDelete(string villageId, string station, string item)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[WorkOrderRpc] no ZRoutedRpc available; cannot delete work order");
                return;
            }

            rpc.InvokeRoutedRPC(DeleteRpc, villageId ?? "", station ?? "", item ?? "");
        }

        private static void OnSet(long sender, string villageId, string station, string item, string itemDisplay,
            int min, int max)
        {
            // Host only — the village carrier is host-owned, so SetBlob no-ops on a client anyway,
            // but guard so a stray delivery can never act on a client.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            if (string.IsNullOrEmpty(station) || string.IsNullOrEmpty(item))
            {
                Plugin.Log?.LogError("[WorkOrderRpc] set with empty station/item; aborting.");
                return;
            }

            if (min < 0 || max < min)
            {
                Plugin.Log?.LogError($"[WorkOrderRpc] invalid quota min={min} max={max} for {item}@{station}; aborting.");
                return;
            }

            var village = Villages.Entity.VillageRegistry.FindById(villageId);
            if (village == null)
            {
                // Fail loud — never auto-create a village.
                Plugin.Log?.LogError(
                    $"[WorkOrderRpc] village '{villageId}' missing; aborting set of {item}@{station} (requestedBy={sender}).");
                return;
            }

            village.UpsertWorkOrder(new Villages.Entity.WorkOrderEntry(station, item, itemDisplay ?? "", min, max));
            Plugin.Log?.LogInfo(
                $"[WorkOrderRpc] set {item}@{station} [{min}-{max}] in village {villageId} (requestedBy={sender})");
        }

        private static void OnDelete(long sender, string villageId, string station, string item)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var village = Villages.Entity.VillageRegistry.FindById(villageId);
            if (village == null)
            {
                Plugin.Log?.LogError(
                    $"[WorkOrderRpc] village '{villageId}' missing; aborting delete of {item}@{station}.");
                return;
            }

            if (village.RemoveWorkOrder(station, item))
                Plugin.Log?.LogInfo(
                    $"[WorkOrderRpc] deleted {item}@{station} from village {villageId} (requestedBy={sender})");
            else
                Plugin.Log?.LogWarning(
                    $"[WorkOrderRpc] delete {item}@{station}: no such order in village {villageId}.");
        }
    }
}
