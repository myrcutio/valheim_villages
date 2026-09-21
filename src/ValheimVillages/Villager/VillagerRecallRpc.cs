using System;
using UnityEngine;
using ValheimVillages.Networking;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Server-authoritative villager recall.
    ///
    ///     <para><b>Recall is the failsafe</b> — the one control a player has for putting a
    ///     villager that has got itself into a bad state back somewhere sane. So it has to work
    ///     from wherever the player is pressing the button, which on a dedicated server is a
    ///     client. It did not: recall resolved entirely client-side, and hit two walls there.
    ///     A villager too far away to be replicated reads as <c>Unknown</c> on a client (only
    ///     the host can tell <c>Away</c> from <c>Missing</c>), and the roster only attempted an
    ///     unloaded recall for <c>Away</c> — so it fell through to "Cannot recall X (?)".
    ///     Even past that, <c>VillagerRecall.RecallUnloaded</c> refuses off-host, because
    ///     villager ZDOs are host-authoritative and a client's write would not stick. Both
    ///     walls are on the client, and the player has no way over either.</para>
    ///
    ///     <para>So the client stops deciding. It sends the record id and the destination; the
    ///     HOST — which can see the ZDO, owns it, and knows whether the villager is loaded —
    ///     does the work and reports back. Same shape as
    ///     <see cref="VillagerRecruitRpc" />, and for the same reason: on a listen-host or in
    ///     singleplayer the RPC resolves locally, so one path is correct in every topology.</para>
    /// </summary>
    public static class VillagerRecallRpc
    {
        private const string RpcName = "VV_RecallVillager";
        private const string ResultRpcName = "VV_RecallResult";

        private static object s_registeredInstance;

        /// <summary>Register handlers on the current <see cref="ZRoutedRpc" />. Cheap per frame.</summary>
        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredInstance)) return;
            s_registeredInstance = rpc;
            try
            {
                RoutedRpcRegistrar.ClearStale(rpc, RpcName);
                RoutedRpcRegistrar.ClearStale(rpc, ResultRpcName);
                rpc.Register<string, Vector3>(RpcName, OnRecall);
                rpc.Register<bool, string, string>(ResultRpcName, OnRecallResult);
                Plugin.Log?.LogInfo("[RecallRpc] registered VV_RecallVillager + VV_RecallResult handlers");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RecallRpc] register skipped: {ex.Message}");
            }
        }

        /// <summary>Ask the host to bring this villager to <paramref name="destination" />.</summary>
        public static void RequestRecall(string recordId, Vector3 destination)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[RecallRpc] no ZRoutedRpc available; cannot request recall");
                return;
            }

            rpc.InvokeRoutedRPC(RpcName, recordId ?? "", destination);
        }

        private static void OnRecall(long sender, string recordId, Vector3 destination)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var record = VillagerRecordTable.FindById(recordId);
            if (record == null)
            {
                SendResult(sender, false, "", "no record with that id");
                return;
            }

            var ok = RecallOnHost(record, destination, out var report);
            SendResult(sender, ok, record.Name, report);
        }

        /// <summary>
        ///     The host-side recall itself, shared by the RPC handler and <c>vv_recall</c> so
        ///     the console command exercises the real thing rather than a second copy of it.
        ///     Returns false only when the villager could not be brought back at all.
        /// </summary>
        public static bool RecallOnHost(VillagerRecord record, Vector3 destination, out string report)
        {
            report = "";
            if (record == null)
            {
                report = "no record";
                return false;
            }

            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                report = "not the host — villager ZDOs are host-authoritative";
                return false;
            }

            // Loaded here: move the live instance, which also resets its navigation.
            if (VillagerAIManager.ActiveVillagers.TryGetValue(record.RecordId, out var ai) && ai != null)
            {
                ai.Recall(destination);
                report = $"recalled '{record.Name}' (live) to " +
                         $"({destination.x:F1},{destination.y:F1},{destination.z:F1})";
                Plugin.Log?.LogInfo($"[RecallRpc] {report}");
                return true;
            }

            // Not loaded. Its stored position is still writable, and the host is the only peer
            // that can write it — which is the whole reason this runs here.
            if (VillagerRecall.RecallUnloaded(record, destination))
            {
                report = $"recalled '{record.Name}' (was away) to " +
                         $"({destination.x:F1},{destination.y:F1},{destination.z:F1}); " +
                         "it will be there when its zone next loads";
                return true;
            }

            // The host cannot find the NPC at all. That IS the authoritative answer — on the
            // host, and only on the host, "no ZDO" means gone rather than merely "not
            // replicated to me". The roster used to draw this conclusion client-side, where it
            // could not actually be drawn; drawing it here is the same behaviour on the one
            // peer entitled to it. Recall stays a failsafe either way: the villager comes back,
            // or it is marked fallen so Revive can restore it.
            Plugin.Log?.LogWarning(
                $"[RecallRpc] '{record.Name}' ({record.RecordId}) has no NPC the host can find — " +
                "marking the record fallen so it can be revived.");
            VillagerRecordTable.SetStatus(record.RecordId, RecordStatus.Dead);
            report = "the host cannot find it, so it is marked fallen. Use Revive to restore it";
            return false;
        }

        private static void SendResult(long target, bool success, string name, string detail)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(
                target, ResultRpcName, success, name ?? "", detail ?? "");
        }

        private static void OnRecallResult(long sender, bool success, string name, string detail)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var who = string.IsNullOrEmpty(name) ? "Villager" : name;
            if (success)
            {
                player.Message(MessageHud.MessageType.Center,
                    string.IsNullOrEmpty(detail) ? $"{who} recalled" : $"{who} recalled ({detail})");
                return;
            }

            Plugin.Log?.LogWarning($"[RecallRpc] host could not recall {who}: {detail}");
            player.Message(MessageHud.MessageType.Center, $"Cannot recall {who} — {detail}.");
        }
    }
}
