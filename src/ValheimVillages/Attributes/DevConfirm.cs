using System;
using System.Linq;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Shared confirmation gate for dev commands that destroy or fabricate state.
    ///     Used by <see cref="AttributeScanner" /> for whole commands marked
    ///     <see cref="DevCommandAttribute.Destructive" />, and directly by subcommand
    ///     groups whose individual targets differ in blast radius (so that, say,
    ///     <c>vv_reset patrols</c> stays a one-liner while <c>vv_reset all</c> does not).
    /// </summary>
    internal static class DevConfirm
    {
        /// <summary>
        ///     True when the invocation carries <c>--yes</c>, or <c>--dry-run</c> — the
        ///     latter previews without mutating, and gating it would make the safe path
        ///     harder to reach than the destructive one.
        /// </summary>
        public static bool IsConfirmed(Terminal.ConsoleEventArgs args)
        {
            var argv = args?.Args;
            return argv != null && argv.Any(a =>
                string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Prints the standard refusal for an unconfirmed destructive invocation.</summary>
        public static void PrintRefusal(string invocation)
        {
            Console.instance?.Print(
                $"[{invocation}] refused — this command destroys or fabricates state.");
            Console.instance?.Print(
                $"  re-run as: {invocation} --yes      (or --dry-run where supported)");
        }
    }
}
