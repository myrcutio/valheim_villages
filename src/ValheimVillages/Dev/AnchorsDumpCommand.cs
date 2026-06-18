using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic dump of a village's anchor triad: founder + triad0/1/2 positions
    ///     (full XYZ) and a pairwise slot-31 connectivity matrix over
    ///     [founder, triad0, triad1, triad2]. Resolves the village containing the player,
    ///     falling back to enumerating all registered villages when the player sits in none.
    /// </summary>
    public static class AnchorsDumpCommand
    {
        // Row/column order for the connectivity matrix and the position dump.
        private static readonly string[] Names = { VillageAnchor.Founder, VillageAnchor.Triad[0], VillageAnchor.Triad[1], VillageAnchor.Triad[2] };

        [DevCommand("Dump the player's village anchor triad (founder + triad0/1/2) full XYZ + pairwise connectivity matrix",
            Name = "vv_anchors")]
        public static void DumpAnchors(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();

            var player = Player.m_localPlayer;
            var village = player != null ? VillageRegistry.GetVillageAt(player.transform.position) : null;

            if (village != null)
            {
                DumpVillage(sb, village);
            }
            else
            {
                sb.AppendLine("[vv_anchors] no village at player position; enumerating all villages");
                var any = false;
                foreach (var v in VillageRegistry.EnumerateAll())
                {
                    any = true;
                    DumpVillage(sb, v);
                }

                if (!any) sb.AppendLine("[vv_anchors] no registered villages");
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        private static void DumpVillage(StringBuilder sb, Village village)
        {
            sb.AppendLine($"[vv_anchors] village {village.VillageId} invalid={village.IsInvalid}");

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

            // Pairwise connectivity matrix over [founder, triad0, triad1, triad2].
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
