using System;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     A freshly-spawned villager (NPC clone AND its <c>vv_villager_record</c> carrier)
    ///     was observed self-destructing ~seconds after spawn via
    ///     <c>TimedDestruction.DestroyNow</c> on the host — leaving the record <c>Alive</c>
    ///     with a missing NPC (an orphan), which reads in-game as "villagers only partially
    ///     spawn". The component is NOT on the base prefab (<c>DvergerMage</c>/<c>Dverger</c>
    ///     are absent from <c>search_component object TimedDestruction</c>) and is added by
    ///     no code in this mod (zero <c>TimedDestruction</c> references) — so it is attached
    ///     at runtime by something outside our spawn path, and ONLY to fresh spawns
    ///     (villagers loaded from save survive). A self-destruct timer on a persistent,
    ///     record-backed villager is always wrong.
    ///
    ///     <para>Two coordinated patches:</para>
    ///     <list type="number">
    ///       <item><b>DestroyNow prefix</b> — the safety net. For a village object, cancel the
    ///         repeating invoke and refuse the destroy, so a villager can never be culled by a
    ///         stray timer. Logged loudly (not silent).</item>
    ///       <item><b>Awake postfix</b> — the diagnostic. When a <c>TimedDestruction</c> Awakes
    ///         on a village object, log the attach call stack (capped) so the runtime source
    ///         that adds it can be identified and fixed at the root, after which this guard can
    ///         be removed.</item>
    ///     </list>
    /// </summary>
    [HarmonyPatch(typeof(TimedDestruction))]
    public static class VillagerTimedDestructionGuard
    {
        private const int MaxAttachLogs = 20;
        private static int s_attachLogs;

        /// <summary>
        ///     True when the timer's GameObject is a village entity we must never auto-destroy:
        ///     its ZDO is a village ZDO (village/record carrier or villager NPC — registry piece
        ///     excluded by <see cref="VillageOwnership.IsVillageZdo" />), or it already carries
        ///     our villager components (covers the window before the ZDO keys are stamped).
        /// </summary>
        private static bool IsVillagerObject(GameObject go)
        {
            if (go == null) return false;

            var nview = go.GetComponent<ZNetView>();
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null && VillageOwnership.IsVillageZdo(zdo)) return true;

            return go.GetComponent<VillagerAI>() != null
                   || go.GetComponent<global::ValheimVillages.Villager.Villager>() != null;
        }

        [HarmonyPatch("DestroyNow")]
        [HarmonyPrefix]
        private static bool DestroyNowPrefix(TimedDestruction __instance)
        {
            if (!IsVillagerObject(__instance.gameObject)) return true; // not ours — vanilla behavior

            // Stop the InvokeRepeating so this fires once, not every second, and the timer
            // can't keep trying. We deliberately leave the (now inert) component in place;
            // tearing it down here would race the Awake diagnostic below.
            __instance.CancelInvoke("DestroyNow");
            Plugin.Log?.LogWarning(
                $"[TimedDestructionGuard] Blocked TimedDestruction.DestroyNow on village object " +
                $"'{__instance.gameObject.name}' — a persistent villager must not be timer-culled. " +
                "Invoke cancelled.");
            return false; // veto the destroy
        }

        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        private static void AwakePostfix(TimedDestruction __instance)
        {
            if (!IsVillagerObject(__instance.gameObject)) return;
            if (s_attachLogs >= MaxAttachLogs) return;
            s_attachLogs++;

            Plugin.Log?.LogWarning(
                $"[TimedDestructionGuard] TimedDestruction attached to village object " +
                $"'{__instance.gameObject.name}' (timeout={__instance.m_timeout}s " +
                $"triggerOnAwake={__instance.m_triggerOnAwake}). Source stack:\n{Environment.StackTrace}");
        }
    }
}
