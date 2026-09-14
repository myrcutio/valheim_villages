using System;
using ValheimVillages.Villager.AI;
using ValheimVillages.Networking;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Host-authoritative "hold still, someone is talking to you". A player with a
    ///     villager's craft menu open must not watch it walk away mid-conversation, but
    ///     villagers are simulated on the HOST (spawned server-side, host-owned from birth —
    ///     see <see cref="VillagerRecruitRpc" />), so pausing the client's local
    ///     <see cref="VillagerAI" /> does nothing to the instance that is actually moving.
    ///     The client sends this routed RPC instead and the host applies the pause.
    ///
    ///     <para>The pause is a LEASE, not a latch. A client that disconnects, crashes, or
    ///     alt-F4s with the menu open never sends the release, and a latched pause would
    ///     freeze that villager permanently with nothing in the game able to clear it. The
    ///     holder renews every <see cref="HeartbeatSeconds" /> while the menu is open and the
    ///     host drops the pause once <see cref="LeaseSeconds" /> elapses without a renewal.</para>
    ///
    ///     <para>Registration mirrors the other villager RPCs (register on ZRoutedRpc-instance
    ///     change, InvokeRoutedRPC with no target → server, handler applies host-side). On a
    ///     listen-host the routed call dispatches locally, so there is one code path for both
    ///     dedicated and single-player.</para>
    /// </summary>
    public static class VillagerPauseRpc
    {
        private const string PauseRpc = "VV_SetVillagerPaused";

        /// <summary>
        ///     How long a pause survives on the host without renewal. Must comfortably exceed
        ///     <see cref="HeartbeatSeconds" /> so ordinary network jitter never un-pauses a
        ///     villager whose menu is still open.
        /// </summary>
        public const float LeaseSeconds = 6f;

        /// <summary>How often the client re-asserts the pause while the menu is open.</summary>
        public const float HeartbeatSeconds = 2f;

        private static object s_registeredInstance;

        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredInstance)) return;
            s_registeredInstance = rpc;
            try
            {
                RoutedRpcRegistrar.ClearStale(rpc, PauseRpc);
                rpc.Register<string, bool>(PauseRpc, OnSetPaused);
                Plugin.Log?.LogInfo("[VillagerPauseRpc] registered VV_SetVillagerPaused");
            }
            catch (Exception ex)
            {
                // Should not happen now that ClearStale drops the previous assembly's handler
                // first, but keep reporting it: a swallowed registration failure means the RPC
                // silently does nothing (see RoutedRpcRegistrar).
                Plugin.Log?.LogWarning($"[VillagerPauseRpc] register skipped: {ex.Message}");
            }
        }

        /// <summary>
        ///     Ask the host to pause (or release) a villager. Safe to call repeatedly — that
        ///     is exactly how the lease is renewed.
        /// </summary>
        public static void Request(string villagerId, bool paused)
        {
            if (string.IsNullOrEmpty(villagerId)) return;
            EnsureRegistered();
            var rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                Plugin.Log?.LogError("[VillagerPauseRpc] no ZRoutedRpc available; cannot set pause");
                return;
            }

            rpc.InvokeRoutedRPC(PauseRpc, villagerId, paused);
        }

        private static void OnSetPaused(long sender, string villagerId, bool paused)
        {
            // Authoritative — host only. The villager's AI ticks here.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (string.IsNullOrEmpty(villagerId)) return;

            if (!VillagerAIManager.ActiveVillagers.TryGetValue(villagerId, out var ai) || ai == null)
            {
                // Not loaded on this peer: nothing is simulating it, so there is nothing to
                // hold still. Not an error — the villager's zone may simply be unloaded.
                return;
            }

            ai.SetPaused(paused, LeaseSeconds);
        }
    }
}
