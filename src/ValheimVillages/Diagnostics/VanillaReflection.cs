using System;
using System.Collections.Generic;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Reporting for calls that reach into Valheim's private members by reflection.
    ///     <para>
    ///     Those call sites used to be <c>try { ... } catch { return default; }</c> — the
    ///     exact silent-substitute shape this project forbids. A reflection failure means a
    ///     Valheim update moved or renamed the member, and the substituted default is a lie
    ///     with consequences: "fire is not lit", "fuel is 0", "queue is empty". Villagers
    ///     then behave correctly for a world that isn't the real one, with nothing in the log.
    ///     </para>
    ///     <para>
    ///     These do not throw. The member is resolved once into a static <c>MethodInfo</c>
    ///     and the null case is already guarded, so reaching here means the game itself threw
    ///     inside the call — something this mod cannot fix at runtime, and which should not
    ///     take a player's session down mid-frame. So: report it loudly, once per member, and
    ///     return the documented default. Loud in the log is the part that was missing.
    ///     </para>
    /// </summary>
    internal static class VanillaReflection
    {
        private static readonly HashSet<string> s_reported = new();

        /// <summary>
        ///     Log a reflection failure once per <paramref name="member" />. Repeats are
        ///     dropped: these sit in per-frame AI paths and would otherwise flood the log and
        ///     bury the first occurrence, which is the one that carries the real stack.
        /// </summary>
        internal static void ReportFailure(string member, Exception ex)
        {
            if (!s_reported.Add(member)) return;
            Plugin.Log?.LogError(
                $"[VanillaReflection] {member} threw — a Valheim update likely changed it. " +
                $"Callers are now reading a substituted default, so villager behaviour here " +
                $"is unreliable until this is fixed: {ex}");
        }

        [RegisterCleanup]
        public static void Reset()
        {
            s_reported.Clear();
        }
    }
}
