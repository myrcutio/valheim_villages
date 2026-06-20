using System;
using UnityEngine;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Server-authoritative villager spawn. Recruiting/reviving is triggered by a
    ///     player on the CLIENT, but a villager created client-side and then handed to the
    ///     server (via ownership claim) never stabilizes — it's flung off-graph, loses
    ///     ownership, and despawns. So instead the client sends a routed RPC and the HOST
    ///     runs <see cref="VillagerSpawner.SpawnVillagerNpc" />: the NPC (and its record)
    ///     are created on the server via <c>ZDOMan.CreateNewZDO</c> → owned by the server
    ///     from birth, no handoff. On a listen-host / singleplayer the RPC resolves to the
    ///     local host and runs in-process, so this path is correct in every topology.
    /// </summary>
    public static class VillagerRecruitRpc
    {
        private const string RpcName = "VV_SpawnVillager";

        // The ZRoutedRpc we registered our handler on. ZRoutedRpc is recreated per ZNet
        // session (world join/host), so we (re)register whenever the instance changes.
        private static object s_registeredInstance;

        /// <summary>
        ///     Register the spawn handler on the current <see cref="ZRoutedRpc" /> if not
        ///     already done. Cheap to call every frame; pumped from <c>Plugin.Update</c>.
        /// </summary>
        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredInstance)) return;
            s_registeredInstance = rpc;
            try
            {
                rpc.Register<string, string, Vector3, string>(RpcName, OnSpawn);
                Plugin.Log?.LogInfo("[RecruitRpc] registered VV_SpawnVillager handler");
            }
            catch (Exception ex)
            {
                // ZRoutedRpc has no Unregister; re-registering on the SAME instance (only
                // possible after a hot reload, which the dedicated server never does — it
                // restarts) throws on the duplicate key. Harmless; the prior handler stands.
                Plugin.Log?.LogWarning($"[RecruitRpc] register skipped: {ex.Message}");
            }
        }

        /// <summary>
        ///     Ask the host to spawn a villager. <paramref name="recordId" /> empty = fresh
        ///     recruit (mint a new record); set = revive (reuse that record). The host
        ///     resolves the walkable seed against ITS navmesh near <paramref name="anchorPos" />
        ///     so the villager lands on a surface the host can actually simulate.
        /// </summary>
        public static void RequestSpawn(string villagerType, string villageId, Vector3 anchorPos, string recordId)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[RecruitRpc] no ZRoutedRpc available; cannot request spawn");
                return;
            }

            // No explicit target → routed to the server; on a host this dispatches locally.
            rpc.InvokeRoutedRPC(RpcName, villagerType, villageId, anchorPos, recordId ?? "");
        }

        private static void OnSpawn(long sender, string villagerType, string villageId, Vector3 anchorPos, string recordId)
        {
            // Authoritative spawn — host only. (The RPC targets the server, but guard anyway
            // so a stray/broadcast delivery on a client can never spawn a client-owned NPC.)
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var def = VillagerRegistry.Get(villagerType);
            if (def == null)
            {
                Plugin.Log?.LogError($"[RecruitRpc] unknown villager type '{villagerType}'; aborting spawn.");
                return;
            }

            var village = Villages.Entity.VillageRegistry.FindById(villageId);
            if (village == null || village.IsInvalid)
            {
                Plugin.Log?.LogError(
                    $"[RecruitRpc] village '{villageId}' missing/invalid; aborting spawn of {villagerType}.");
                return;
            }

            if (!Villages.Entity.VillageRegistry.TryResolveVillagerSeed(village, anchorPos, out var spawnPos))
            {
                Plugin.Log?.LogError(
                    $"[RecruitRpc] no reachable seed near ({anchorPos.x:F1},{anchorPos.z:F1}) on the host " +
                    $"navmesh for village {villageId}; aborting spawn of {villagerType}.");
                return;
            }

            var prefab = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : "DvergerMage";
            var record = string.IsNullOrEmpty(recordId) ? null : VillagerRecordTable.FindById(recordId);
            var npc = VillagerSpawner.SpawnVillagerNpc(def, def.type, prefab, spawnPos, ref record, village.VillageId);
            Plugin.Log?.LogInfo(npc != null
                ? $"[RecruitRpc] spawned {def.type} (server-owned) at " +
                  $"({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}) in village {villageId} " +
                  $"(record {(record != null ? record.RecordId : "?")}, requestedBy={sender})"
                : $"[RecruitRpc] spawn FAILED for {villagerType} in village {villageId}.");
        }
    }
}
