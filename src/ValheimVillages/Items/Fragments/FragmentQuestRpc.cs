using System;
using UnityEngine;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Server-authoritative rescue-quest location lookup. Combining fragments is triggered
    ///     by a player on the CLIENT, but the candidate quest sites live in
    ///     <c>ZoneSystem.m_locationInstances</c>, which is populated only on the host (world
    ///     save load / location generation). A client connected to a dedicated server sees an
    ///     empty dictionary, so <see cref="FragmentCombiner.FindPositionInBiome" /> must run on
    ///     the host. The client sends a routed request; the host resolves a real location and
    ///     replies to the original sender, which finishes the combine in
    ///     <see cref="FragmentCombiner.CompleteQuest" />. On a listen-host / singleplayer the RPC
    ///     resolves to the local host and runs in-process, so this path is correct in every
    ///     topology. Mirrors <see cref="ValheimVillages.Villager.VillagerRecruitRpc" />.
    /// </summary>
    public static class FragmentQuestRpc
    {
        private const string RequestRpcName = "VV_FindQuestLocation";
        private const string ResultRpcName = "VV_QuestLocationResult";

        // The ZRoutedRpc we registered our handlers on. ZRoutedRpc is recreated per ZNet
        // session (world join/host), so we (re)register whenever the instance changes.
        private static object s_registeredInstance;

        /// <summary>
        ///     Register the lookup handlers on the current <see cref="ZRoutedRpc" /> if not
        ///     already done. Cheap to call every frame; pumped from <c>Plugin.Update</c>.
        /// </summary>
        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredInstance)) return;
            s_registeredInstance = rpc;
            try
            {
                rpc.Register<string, Vector3>(RequestRpcName, OnRequest);
                rpc.Register<bool, string, Vector3, string>(ResultRpcName, OnResult);
                Plugin.Log?.LogInfo(
                    "[FragmentQuestRpc] registered VV_FindQuestLocation + VV_QuestLocationResult handlers");
            }
            catch (Exception ex)
            {
                // ZRoutedRpc has no Unregister; re-registering on the SAME instance (only
                // possible after a hot reload, which the dedicated server never does — it
                // restarts) throws on the duplicate key. Harmless; the prior handler stands.
                Plugin.Log?.LogWarning($"[FragmentQuestRpc] register skipped: {ex.Message}");
            }
        }

        /// <summary>
        ///     Ask the host to resolve a rescue-quest location in <paramref name="biome" /> near
        ///     the local player. The host replies to this peer with the chosen position (or a
        ///     failure), handled in <see cref="OnResult" />.
        /// </summary>
        public static void RequestQuestLocation(string biome)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[FragmentQuestRpc] no ZRoutedRpc available; cannot request quest location");
                return;
            }

            var player = Player.m_localPlayer;
            var playerPos = player != null ? player.transform.position : Vector3.zero;

            // No explicit target → routed to the server; on a host this dispatches locally.
            rpc.InvokeRoutedRPC(RequestRpcName, biome ?? "", playerPos);
        }

        /// <summary>
        ///     Host handler — resolves a real location from ZoneSystem and replies to the sender.
        ///     Guarded to the server even though the RPC targets it, so a stray/broadcast delivery
        ///     on a client (whose location dictionary is empty) can never produce a bogus result.
        /// </summary>
        private static void OnRequest(long sender, string biome, Vector3 playerPos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var questPos = FragmentCombiner.FindPositionInBiome(biome, playerPos);
            if (questPos.HasValue)
                SendResult(sender, true, biome, questPos.Value, "");
            else
                SendResult(sender, false, biome, Vector3.zero,
                    $"no valid {biome} location found in the loaded world");
        }

        /// <summary>
        ///     Tell the requesting client how the lookup turned out. Targeted at the original
        ///     sender; on a listen-host that's our own id and ZRoutedRpc dispatches it locally.
        /// </summary>
        private static void SendResult(long target, bool success, string biome, Vector3 questPos, string reason)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, ResultRpcName, success, biome ?? "", questPos, reason ?? "");
        }

        /// <summary>
        ///     Client-side result handler — runs on the peer that requested the lookup. Finishes
        ///     the combine (consume fragments + place the quest) on success, or messages the
        ///     player on failure without touching their fragments.
        /// </summary>
        private static void OnResult(long sender, bool success, string biome, Vector3 questPos, string reason)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (success)
                FragmentCombiner.CompleteQuest(player, biome, questPos);
            else
                FragmentCombiner.OnQuestLocationUnavailable(player, biome, reason);
        }
    }
}
