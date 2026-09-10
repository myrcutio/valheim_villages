using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Items;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: dump the Hammer's <see cref="PieceTable" /> and name every DESTROYED entry
    ///     hiding in it.
    ///
    ///     <para>Opening the build menu throws <c>NullReferenceException</c> from
    ///     <c>FavoritePieceList.IsFavorite</c> when any list holds a Piece whose GameObject is
    ///     gone. The engine gives no clue which one — <c>piece.gameObject</c> is exactly what
    ///     throws. But a destroyed MonoBehaviour keeps its MANAGED fields, so <c>m_name</c> and
    ///     <c>m_category</c> still read fine and identify the corpse.</para>
    /// </summary>
    public static class PieceTableProbeCommand
    {
        [DevCommand("Dump the Hammer PieceTable and name any destroyed entries: vv_piecetable",
            Name = "vv_piecetable")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[vv_piecetable]");

            var hammer = ObjectDB.instance?.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>();
            var table = hammer?.m_itemData?.m_shared?.m_buildPieces;
            if (table == null)
            {
                Print("[vv_piecetable] Hammer build PieceTable not available");
                return;
            }

            var player = Player.m_localPlayer;
            var tool = player != null ? player.GetBuildTool() : null;
            sb.AppendLine($"  hammer table: {table.GetInstanceID()}  " +
                          $"player build tool: {(tool == null ? "(none - not in place mode)" : tool.GetInstanceID().ToString())}  " +
                          $"same={(object)tool == (object)table}");

            // m_pieces holds GameObjects; a dead one is what Player.UpdateKnownRecipesList trips on.
            var deadPieces = 0;
            foreach (var go in table.m_pieces)
                if (go == null)
                    deadPieces++;
            sb.AppendLine($"  m_pieces: {table.m_pieces.Count} entries, {deadPieces} destroyed");

            ReportComponents(sb, "m_availablePieces", table.m_availablePieces);
            ReportComponents(sb, "m_enabledPieces", table.m_enabledPieces);
            ReportByCategory(sb, table);

            // Is the registry the engine hands out the same object the table holds? A mismatch
            // means two incarnations are live at once.
            var znetPrefab = ZNetScene.instance?.GetPrefab(PieceFactory.RegistryPrefabName);
            sb.AppendLine($"  ZNetScene '{PieceFactory.RegistryPrefabName}': " +
                          (znetPrefab == null ? "MISSING" : $"alive id={znetPrefab.GetInstanceID()}"));

            var inTable = 0;
            foreach (var go in table.m_pieces)
            {
                if (go == null) continue;
                if (go.name != PieceFactory.RegistryPrefabName) continue;
                inTable++;
                sb.AppendLine($"    in m_pieces: id={go.GetInstanceID()} " +
                              $"same-as-znetscene={(object)go == (object)znetPrefab}");
            }

            if (inTable == 0) sb.AppendLine("    in m_pieces: NOT PRESENT");

            ReportBuildUiScratch(sb);

            Print(sb.ToString().TrimEnd());
        }

        /// <summary>
        ///     Count and name the destroyed components in one of the table's derived caches.
        ///     Reads only managed fields off a corpse, never <c>gameObject</c>.
        /// </summary>
        private static void ReportComponents(StringBuilder sb, string label, HashSet<Piece> set)
        {
            if (set == null)
            {
                sb.AppendLine($"  {label}: (null)");
                return;
            }

            var dead = new List<Piece>();
            foreach (var p in set)
                if (p == null)
                    dead.Add(p);

            sb.AppendLine($"  {label}: {set.Count} entries, {dead.Count} destroyed");
            foreach (var p in dead)
            {
                if ((object)p == null)
                {
                    sb.AppendLine("    DEAD: (a real null reference, not a destroyed object)");
                    continue;
                }

                sb.AppendLine($"    DEAD: m_name='{Managed(p)}' category={p.m_category} " +
                              $"repair={p.m_repairPiece} remove={p.m_removePiece}");
            }
        }

        private static void ReportByCategory(StringBuilder sb, PieceTable table)
        {
            var field = typeof(PieceTable).GetField(
                "m_availablePiecesByCategory", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(table) is not List<List<Piece>> byCategory)
            {
                sb.AppendLine("  m_availablePiecesByCategory: (unreadable)");
                return;
            }

            var total = 0;
            var dead = 0;
            var names = new List<string>();
            foreach (var list in byCategory)
            foreach (var p in list)
            {
                total++;
                if (p != null) continue;
                dead++;
                names.Add(Managed(p));
            }

            sb.AppendLine($"  m_availablePiecesByCategory: {total} entries, {dead} destroyed");
            foreach (var n in names)
                sb.AppendLine($"    DEAD: m_name='{n}'");
        }

        /// <summary>
        ///     BuildUi.m_tempPieces is the list UpdatePieceButtons walks. It is cleared at the
        ///     END of that method, so a throw mid-walk leaves the corpse stranded there and every
        ///     later open re-throws on it even with a spotless PieceTable. A healthy menu leaves
        ///     this EMPTY between opens — a non-zero count here means the menu has thrown.
        /// </summary>
        private static void ReportBuildUiScratch(StringBuilder sb)
        {
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi == null)
            {
                sb.AppendLine("  BuildUi: (no Hud)");
                return;
            }

            var field = typeof(BuildUi).GetField(
                "m_tempPieces", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(buildUi) is not List<Piece> temp)
            {
                sb.AppendLine("  BuildUi.m_tempPieces: (unreadable)");
                return;
            }

            var dead = 0;
            foreach (var p in temp)
                if (p == null)
                    dead++;

            sb.AppendLine($"  BuildUi.m_tempPieces: {temp.Count} entries ({dead} destroyed) " +
                          "— should be 0/0 between opens");
            foreach (var p in temp)
                if (p == null)
                    sb.AppendLine($"    DEAD: m_name='{Managed(p)}'");
        }

        /// <summary>Managed name field, safe to read on a destroyed component.</summary>
        private static string Managed(Piece p)
        {
            if ((object)p == null) return "(real null reference)";
            return p.m_name ?? "(unnamed)";
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
