using System;
using System.Diagnostics;
using System.Globalization;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        // Anchors at the time the mod assembly loads. Survives hot reload only
        // partially — on hot reload the type stays loaded so this clock keeps
        // ticking; that's intentional, so events across cycles share a monotonic
        // timeline. The cycle banner from BeginCycle() carries the wall-clock
        // anchor for any reader who needs absolute time.
        private static readonly Stopwatch _sessionClock = Stopwatch.StartNew();

        /// <summary>
        /// Session-relative elapsed time. Used internally by Throttled() to
        /// schedule re-emits; exposed in case other code wants the same clock.
        /// </summary>
        public static TimeSpan Elapsed() => _sessionClock.Elapsed;

        /// <summary>
        /// Formatted timestamp token: "t=+12.34s". Append to structured event
        /// lines so a reader can see elapsed time at a glance without parsing
        /// wall-clock dates out of mixed BepInEx output.
        /// </summary>
        public static string T()
        {
            return "t=+"
                + _sessionClock.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + "s";
        }
    }
}
