using System;
using System.Threading;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        private static int _cycleNumber;

        /// <summary>
        ///     Emits a high-visibility banner marking the start of a mod load cycle
        ///     (cold start or hot reload). Lets log readers anchor "what happened
        ///     in the most recent run" with a single grep.
        /// </summary>
        public static void BeginCycle(bool isHotReload)
        {
            var n = Interlocked.Increment(ref _cycleNumber);
            var hot = isHotReload ? "true" : "false";
            var utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            Plugin.Log.LogInfo($"===== VV CYCLE n={n} hot={hot} t={utc} =====");
        }
    }
}