using System;
using System.Collections.Generic;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Reporting for the handful of places that genuinely must not propagate an
    ///     exception — the screenshot/incident capture pipeline, whose stated contract is
    ///     "capture must never break the mod".
    ///     <para>
    ///     Those catches stay. What changes is that they are no longer SILENT: a diagnostic
    ///     that fails without a word is worse than no diagnostic, because it looks like it
    ///     ran. This is the narrow exception to the project's fail-fast rule, and it is
    ///     narrow on purpose — anything on a gameplay path that swallows an exception should
    ///     be fixed at the source instead (see <see cref="VanillaReflection" /> for the other
    ///     bounded case).
    ///     </para>
    /// </summary>
    internal static class SwallowedFailure
    {
        private static readonly HashSet<string> s_seen = new();

        /// <summary>
        ///     Log once per site. Debug level, not error: these are non-fatal by design, and
        ///     at error level they would cry wolf on a missing EnvMan during an early reload.
        /// </summary>
        internal static void Note(string site, Exception ex)
        {
            if (!s_seen.Add($"{site}|{ex.GetType().Name}")) return;
            Plugin.Log?.LogDebug($"[Swallowed] {site}: {ex.GetType().Name}: {ex.Message}");
        }

        [RegisterCleanup]
        public static void Reset()
        {
            s_seen.Clear();
        }
    }
}
