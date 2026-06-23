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
        private const string ResultRpcName = "VV_SpawnResult";

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
                rpc.Register<string, string, Vector3, string, bool>(RpcName, OnSpawn);
                rpc.Register<bool, bool, string>(ResultRpcName, OnSpawnResult);
                Plugin.Log?.LogInfo("[RecruitRpc] registered VV_SpawnVillager + VV_SpawnResult handlers");
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
        public static void RequestSpawn(string villagerType, string villageId, Vector3 anchorPos, string recordId,
            bool paid)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[RecruitRpc] no ZRoutedRpc available; cannot request spawn");
                return;
            }

            // No explicit target → routed to the server; on a host this dispatches locally.
            // paid = the caller already spent a Lode Core (UI recruit/revive) and must be
            // refunded if the host rejects the spawn; dev commands pass false.
            rpc.InvokeRoutedRPC(RpcName, villagerType, villageId, anchorPos, recordId ?? "", paid);
        }

        private static void OnSpawn(long sender, string villagerType, string villageId, Vector3 anchorPos,
            string recordId, bool paid)
        {
            // Authoritative spawn — host only. (The RPC targets the server, but guard anyway
            // so a stray/broadcast delivery on a client can never spawn a client-owned NPC.)
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var def = VillagerRegistry.Get(villagerType);
            if (def == null)
            {
                var reason = $"unknown villager type '{villagerType}'";
                Plugin.Log?.LogError($"[RecruitRpc] {reason}; aborting spawn.");
                SendResult(sender, false, paid, reason);
                return;
            }

            var village = Villages.Entity.VillageRegistry.FindById(villageId);
            if (village == null || village.IsInvalid)
            {
                var reason = $"village '{villageId}' missing/invalid";
                Plugin.Log?.LogError($"[RecruitRpc] {reason}; aborting spawn of {villagerType}.");
                SendResult(sender, false, paid, reason);
                return;
            }

            if (!Villages.Entity.VillageRegistry.TryResolveVillagerSeed(village, anchorPos, out var spawnPos))
            {
                var reason = $"no reachable spawn point on the host navmesh near ({anchorPos.x:F1},{anchorPos.z:F1})";
                Plugin.Log?.LogError(
                    $"[RecruitRpc] {reason} for village {villageId}; aborting spawn of {villagerType}.");
                SendResult(sender, false, paid, reason);
                return;
            }

            var prefab = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : "DvergerMage";
            var record = string.IsNullOrEmpty(recordId) ? null : VillagerRecordTable.FindById(recordId);
            var npc = VillagerSpawner.SpawnVillagerNpc(def, def.type, prefab, spawnPos, ref record, village.VillageId);
            if (npc != null)
            {
                Plugin.Log?.LogInfo(
                    $"[RecruitRpc] spawned {def.type} (server-owned) at " +
                    $"({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1}) in village {villageId} " +
                    $"(record {(record != null ? record.RecordId : "?")}, requestedBy={sender})");
                SendResult(sender, true, paid, "");
            }
            else
            {
                Plugin.Log?.LogError($"[RecruitRpc] spawn FAILED for {villagerType} in village {villageId}.");
                SendResult(sender, false, paid, "spawn failed on the host");
            }
        }

        /// <summary>
        ///     Tell the requesting client how the spawn turned out. Targeted at the original
        ///     sender; on a listen-host that's our own id and ZRoutedRpc dispatches it locally.
        /// </summary>
        private static void SendResult(long target, bool success, bool paid, string reason)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, ResultRpcName, success, paid, reason ?? "");
        }

        /// <summary>
        ///     Client-side result handler — runs on the peer that requested the spawn. The Lode
        ///     Core was spent up front on that client; if the host rejected the spawn and the
        ///     request was paid, give the core back, closing the loss window.
        /// </summary>
        private static void OnSpawnResult(long sender, bool success, bool paid, string reason)
        {
            if (success) return; // core was well spent — the villager exists

            Plugin.Log?.LogWarning($"[RecruitRpc] host rejected spawn: {reason}");
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (paid)
            {
                RecruitCost.Refund(player);
                player.Message(MessageHud.MessageType.Center, $"Spawn failed — Lode Core returned ({reason}).");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, $"Spawn failed ({reason}).");
            }
        }
    }
}
