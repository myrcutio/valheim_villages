using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Keeps a villager from re-picking a work order it just failed to carry out.
    ///
    ///     <para>Abandoning mid-workflow drops the villager straight back to Idle, which
    ///     enqueues a fresh scan, which re-ranks the same orders and matches the same one —
    ///     with nothing changed since the last attempt. That is a hot loop, and it is not
    ///     theoretical: a Farmer ran scan → match → gather → abandon → rescan several times a
    ///     second, seven log lines an iteration, on an order it could never complete. On a
    ///     dedicated server a flood like that is not just noise, because the log is a pipe and
    ///     filling it stalls the main thread.</para>
    ///
    ///     <para>The underlying fault should always be fixed too — this is a brake, not a cure,
    ///     and it exists so that the NEXT such bug degrades into a slow retry with a legible
    ///     reason instead of a server-wide stall.</para>
    /// </summary>
    public static class WorkOrderCooldown
    {
        /// <summary>
        ///     How long a villager leaves an order alone after failing it. Long enough that a
        ///     genuinely broken order costs one line a minute, short enough that a transient
        ///     cause (someone else took the last turnip) resolves itself while you watch.
        /// </summary>
        private const float CooldownSeconds = 60f;

        private static readonly Dictionary<string, (float Until, string Reason)> s_cooling = new();

        [RegisterCleanup]
        public static void Clear()
        {
            s_cooling.Clear();
        }

        private static string Key(string villagerId, string itemPrefab) =>
            villagerId + "|" + itemPrefab;

        /// <summary>Record that this villager could not finish this order, and why.</summary>
        public static void NoteAbandoned(string villagerId, string itemPrefab, string reason)
        {
            if (string.IsNullOrEmpty(villagerId) || string.IsNullOrEmpty(itemPrefab)) return;
            s_cooling[Key(villagerId, itemPrefab)] = (Time.time + CooldownSeconds, reason);
        }

        /// <summary>
        ///     True while this villager should skip the order. Writes the reason it failed last
        ///     time and how long is left, so the skip can be reported rather than being another
        ///     silent "nothing to do".
        /// </summary>
        public static bool IsCoolingDown(
            string villagerId, string itemPrefab, out string reason, out float secondsLeft)
        {
            reason = null;
            secondsLeft = 0f;
            if (string.IsNullOrEmpty(villagerId) || string.IsNullOrEmpty(itemPrefab)) return false;

            var key = Key(villagerId, itemPrefab);
            if (!s_cooling.TryGetValue(key, out var entry)) return false;

            if (Time.time >= entry.Until)
            {
                s_cooling.Remove(key);
                return false;
            }

            reason = entry.Reason;
            secondsLeft = entry.Until - Time.time;
            return true;
        }
    }
}
