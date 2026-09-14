using System.Collections;
using HarmonyLib;

namespace ValheimVillages.Networking
{
    /// <summary>
    ///     Makes <see cref="ZRoutedRpc" /> handler registration survive a hot reload.
    ///
    ///     <para><c>ZRoutedRpc.Register</c> does <c>m_functions.Add(name.GetStableHashCode(), …)</c>
    ///     and there is no Unregister. On a hot reload the world — and therefore the live
    ///     ZRoutedRpc — is untouched, so the PREVIOUS assembly's handler still occupies the key
    ///     and every re-registration throws "An item with the same key has already been added".
    ///     Catching that and moving on looks harmless but is not: the surviving delegate belongs
    ///     to the old assembly and is bound to types that have been replaced, so the RPC quietly
    ///     stops doing anything. The only symptom is one warning line at load.</para>
    ///
    ///     <para>Observed with <c>VV_SetVillagerPaused</c>: villagers never paused when a player
    ///     opened their menu, and the pause RPC had simply never registered since the first hot
    ///     reload of the session. Every routed RPC in the mod shares the hazard, so they all
    ///     clear the stale key before registering.</para>
    /// </summary>
    public static class RoutedRpcRegistrar
    {
        /// <summary>
        ///     Drop any handler already registered under <paramref name="name" /> so the caller
        ///     can register a fresh one. No-op when nothing is registered (the cold-start case).
        /// </summary>
        public static void ClearStale(ZRoutedRpc rpc, string name)
        {
            if (rpc == null || string.IsNullOrEmpty(name)) return;

            // m_functions is private; reached the same way the rest of the mod reaches engine
            // internals. Typed as IDictionary so we never need ZRoutedRpc's nested value type.
            var functions = Traverse.Create(rpc).Field("m_functions").GetValue() as IDictionary;
            if (functions == null)
            {
                Plugin.Log?.LogWarning(
                    $"[RoutedRpc] ZRoutedRpc.m_functions unavailable — cannot clear a stale " +
                    $"handler for '{name}'; registration may fail after a hot reload.");
                return;
            }

            var key = name.GetStableHashCode();
            if (!functions.Contains(key)) return;

            functions.Remove(key);
            Plugin.Log?.LogInfo($"[RoutedRpc] cleared stale '{name}' handler (hot reload)");
        }
    }
}
