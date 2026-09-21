using System.Globalization;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Forestry;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Exercises <see cref="TreeFelling" /> by hand, so the engine path can be proven on a
    ///     headless host before a behavior is built on top of it.
    ///
    ///     <para>The specific thing under test: <c>TreeBase.RPC_Damage</c> calls
    ///     <c>DamageText.instance.ShowText(...)</c> unconditionally, and <c>DamageText</c> sets
    ///     that instance from a GUI <c>Awake</c> which has no reason to run on a dedicated
    ///     server. If it is null there, felling NREs inside the engine and the tree never
    ///     drops its wood.</para>
    /// </summary>
    public static class FellTreeCommand
    {
        private const float DefaultRadius = 10f;

        [DevCommand("Fell the nearest tree to a point: vv_fell <x> <z> [y] [radius]  (X,Z,[Y] order)",
            Name = "vv_fell", Destructive = true)]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var inv = CultureInfo.InvariantCulture;

            Vector3 pos;
            var radius = DefaultRadius;
            if (args?.Args != null && args.Args.Length >= 3)
            {
                if (!float.TryParse(args.Args[1], NumberStyles.Float, inv, out var x)
                    || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var z))
                {
                    Print("Usage: vv_fell <x> <z> [y] [radius]   (X,Z,[Y] order)");
                    return;
                }

                pos = new Vector3(x, MeshProbe.ResolveY(x, z, args, 3, inv), z);
                if (args.Args.Length > 4
                    && float.TryParse(args.Args[4], NumberStyles.Float, inv, out var r) && r > 0f)
                    radius = r;
            }
            else
            {
                var p = Player.m_localPlayer;
                if (p == null || p.transform == null)
                {
                    Print("No local player (headless?) — pass coords: vv_fell <x> <z> [y] [radius]");
                    return;
                }

                pos = p.transform.position;
            }

            // `log` forces the log half of the job. Without it a standing tree almost always
            // wins on distance in a forest, so the log path could not be exercised by hand.
            var logOnly = args?.Args != null && System.Array.Exists(args.Args,
                a => string.Equals(a, "log", System.StringComparison.OrdinalIgnoreCase));

            // Nearest timber of EITHER kind otherwise; the behavior clears whatever is closest.
            var tree = logOnly ? null : TreeFelling.FindNearest(pos, radius);
            var nearLog = TreeFelling.FindNearestLog(pos, radius);
            var treeDist = tree != null ? Vector3.Distance(tree.transform.position, pos) : float.MaxValue;
            var logDist = nearLog != null ? Vector3.Distance(nearLog.transform.position, pos) : float.MaxValue;

            if (tree == null || logDist < treeDist)
            {
                var log = nearLog;
                if (log == null)
                {
                    Print($"[vv_fell] no tree or log within {radius:F1}m of " +
                          $"({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                    return;
                }

                var logPos = log.transform.position;
                Print($"[vv_fell] breaking log '{log.gameObject.name}' at " +
                      $"({logPos.x:F1}, {logPos.y:F1}, {logPos.z:F1}) " +
                      $"dist={Vector3.Distance(logPos, pos):F1}m minToolTier={log.m_minToolTier}");
                Print($"[vv_fell] TryBreakLog={TreeFelling.TryBreakLog(log, pos)}. " +
                      "Expect Wood drops (and sub-logs on a large log).");
                return;
            }

            var treePos = tree.transform.position;
            var name = tree.gameObject.name;
            Print($"[vv_fell] felling '{name}' at ({treePos.x:F1}, {treePos.y:F1}, {treePos.z:F1}) " +
                  $"dist={Vector3.Distance(treePos, pos):F1}m minToolTier={tree.m_minToolTier}");

            // Felling from `pos` means the trunk is pushed away from `pos`.
            var ok = TreeFelling.TryFell(tree, pos);
            Print($"[vv_fell] TryFell={ok}. Check for a log/stub and wood drops at the trunk.");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
