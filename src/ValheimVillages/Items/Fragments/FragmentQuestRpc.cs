using System;
using UnityEngine;
using ValheimVillages.Networking;

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
        private const string ClaimRpcName = "VV_ClaimRescueQuest";
        private const string ClaimResultRpcName = "VV_RescueQuestClaimResult";

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
                RoutedRpcRegistrar.ClearStale(rpc, RequestRpcName);
                RoutedRpcRegistrar.ClearStale(rpc, ResultRpcName);
                RoutedRpcRegistrar.ClearStale(rpc, ClaimRpcName);
                RoutedRpcRegistrar.ClearStale(rpc, ClaimResultRpcName);
                rpc.Register<string, Vector3>(RequestRpcName, OnRequest);
                rpc.Register<bool, string, Vector3, string, string>(ResultRpcName, OnResult);
                rpc.Register<ZDOID>(ClaimRpcName, OnClaim);
                rpc.Register<bool, ZDOID, Vector3, bool>(ClaimResultRpcName, OnClaimResult);
                Plugin.Log?.LogInfo(
                    "[FragmentQuestRpc] registered quest location + rescue quest claim handlers");
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

            var questPos = FragmentCombiner.FindPositionInBiome(biome, playerPos, out var locationName);
            if (questPos.HasValue)
                SendResult(sender, true, biome, questPos.Value, locationName ?? "", "");
            else
                SendResult(sender, false, biome, Vector3.zero, "",
                    $"no valid {biome} location found in the loaded world");
        }

        /// <summary>
        ///     Tell the requesting client how the lookup turned out. Targeted at the original
        ///     sender; on a listen-host that's our own id and ZRoutedRpc dispatches it locally.
        /// </summary>
        private static void SendResult(long target, bool success, string biome, Vector3 questPos, string locationName,
            string reason)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, ResultRpcName, success, biome ?? "", questPos,
                locationName ?? "", reason ?? "");
        }

        /// <summary>
        ///     Client-side result handler — runs on the peer that requested the lookup. Finishes
        ///     the combine (consume fragments + place the quest) on success, or messages the
        ///     player on failure without touching their fragments.
        /// </summary>
        private static void OnResult(long sender, bool success, string biome, Vector3 questPos, string locationName,
            string reason)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (success)
                FragmentCombiner.CompleteQuest(player, biome, questPos, locationName);
            else
                FragmentCombiner.OnQuestLocationUnavailable(player, biome, reason);
        }

        /// <summary>
        ///     Ask the host for the reward of the rescue quest <paramref name="questId" />. The
        ///     host grants each quest to exactly one claimant, so two players reaching the site
        ///     together can't both collect it; the answer arrives in <see cref="OnClaimResult" />.
        /// </summary>
        public static void RequestClaim(ZDOID questId)
        {
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[FragmentQuestRpc] no ZRoutedRpc available; cannot claim rescue quest");
                return;
            }

            rpc.InvokeRoutedRPC(ClaimRpcName, questId);
        }

        /// <summary>Host handler — grants the quest to the first claimant and removes it.</summary>
        private static void OnClaim(long sender, ZDOID questId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var granted = RescueQuestStore.TryClaim(questId, out var quest);
            Plugin.Log?.LogInfo(granted
                ? $"[RescueQuest] granted {quest.VillagerType} rescue quest {questId} to peer {sender}"
                : $"[RescueQuest] refused claim of {questId} by peer {sender}: already claimed or gone");
            ZRoutedRpc.instance?.InvokeRoutedRPC(sender, ClaimResultRpcName, granted, questId, quest.Position,
                quest.IsInterior);
        }

        /// <summary>Client handler — places the reward when the host granted the claim.</summary>
        private static void OnClaimResult(long sender, bool granted, ZDOID questId, Vector3 questPos, bool isInterior)
        {
            RescueQuestTracker.OnClaimResult(granted, questId, questPos, isInterior);
        }
    }
}
