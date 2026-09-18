using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic dump of a village's anchor triad: founder + triad0/1/2 positions
    ///     (full XYZ) and a pairwise slot-31 connectivity matrix over
    ///     [founder, triad0, triad1, triad2]. Reports <paramref name="target" /> when the
    ///     caller named a village, else the village containing the player, falling back to
    ///     enumerating all registered villages when the player sits in none.
    /// </summary>
    public static class AnchorsDumpCommand
    {
        // Row/column order for the connectivity matrix and the position dump.
        // Registry first: it is THE villager anchor (the founder/triad hang off it), and
        // leaving it out made this dump quietly disagree with every other view of a village.
        private static readonly string[] Names =
        {
            VillageAnchor.Registry, VillageAnchor.Founder,
            VillageAnchor.Triad[0], VillageAnchor.Triad[1], VillageAnchor.Triad[2],
        };

        /// <param name="target">
        ///     The village to report on, or null to resolve it from the player's position.
        ///     In a multi-village world the positional resolve is ambiguous, so
        ///     <c>vv_village anchors &lt;id&gt;</c> passes an explicit village through here.
        /// </param>
        public static void DumpAnchors(Village target)
        {
            var sb = new StringBuilder();

            if (target != null)
            {
                DumpVillage(sb, target);
                global::Console.instance?.Print(sb.ToString());
                Plugin.Log?.LogInfo(sb.ToString());
                return;
            }

            var player = Player.m_localPlayer;
            var village = player != null ? VillageRegistry.GetVillageAt(player.transform.position) : null;

            if (village != null)
            {
                DumpVillage(sb, village);
            }
            else
            {
                sb.AppendLine("[vv_village anchors] no village at player position; enumerating all villages");
                var any = false;
                foreach (var v in VillageRegistry.EnumerateAll())
                {
                    any = true;
                    DumpVillage(sb, v);
                }

                if (!any) sb.AppendLine("[vv_village anchors] no registered villages");
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        private static void DumpVillage(StringBuilder sb, Village village)
        {
            sb.AppendLine(
                $"[vv_village anchors] village {village.VillageId} invalid={village.IsInvalid} " +
                $"needsWall={village.NeedsPerimeterWall}");

            // Resolve the four anchors in fixed order; missing ones print as "(unset)".
            var positions = new Vector3[Names.Length];
            var present = new bool[Names.Length];
            for (var i = 0; i < Names.Length; i++)
            {
                present[i] = village.TryGetAnchor(Names[i], out positions[i]);
                sb.AppendLine(present[i]
                    ? $"  {Names[i],-8} = ({positions[i].x:R}, {positions[i].y:R}, {positions[i].z:R})"
                    : $"  {Names[i],-8} = (unset)");
            }

            // Pairwise connectivity matrix over [registry, founder, triad0, triad1, triad2].
            //
            // AnchorsConnected queries the LIVE slot-31 navmesh, so a reading taken while a
            // partition is rebaking is meaningless — UpdateNavMeshDataAsync is rewriting the
            // data underneath it. That used to be a one-frame window; now that a partition is
            // spread across ~100 frames to keep it off the player's frame, it is wide enough
            // to hit by hand. Observed: an all-N matrix on a healthy village whose triad
            // EnsureAnchorTriad had just validated, which re-read all-Y once the bake settled.
            if (TaskQueue.PartitionRunner.IsAnyRunning)
                sb.AppendLine(
                    $"  !! a partition is rebaking ({TaskQueue.PartitionRunner.RunningVillage}); " +
                    "the matrix below reads a navmesh mid-rewrite and will show false N's. " +
                    "Re-run once it completes.");

            sb.AppendLine("  connectivity (Y/N; '-' if an endpoint is unset):");
            sb.Append("           ");
            for (var c = 0; c < Names.Length; c++) sb.Append($"{Names[c],-8} ");
            sb.AppendLine();

            for (var r = 0; r < Names.Length; r++)
            {
                sb.Append($"  {Names[r],-8} ");
                for (var c = 0; c < Names.Length; c++)
                {
                    string cell;
                    if (r == c) cell = "self";
                    else if (!present[r] || !present[c]) cell = "-";
                    else cell = VillageRegistry.AnchorsConnected(positions[r], positions[c]) ? "Y" : "N";
                    sb.Append($"{cell,-8} ");
                }

                sb.AppendLine();
            }
        }
    }
}
