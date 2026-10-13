using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Which villager a placed Villager Cot belongs to, stored on the cot's own ZDO.
    ///
    ///     <para>The assignment lives on the COT, not on the villager record and not in a static.
    ///     A static was the first attempt and it died on every hot reload — the mark simply
    ///     vanished and had to be re-set by hand. A ZDO survives reloads, world saves and
    ///     restarts, and replicates to every client for free.</para>
    ///
    ///     <para>It records the RECORD ID, not a villager type. The player assigns a person, and
    ///     a person's type is a property of their record — deriving the type at read time means a
    ///     cot cannot drift out of sync with who actually lives in it.</para>
    /// </summary>
    internal static class VillagerCotOccupant
    {
        /// <summary>ZDO key holding the occupant's villager record id. Empty/absent = unassigned.</summary>
        public const string OccupantKey = "vv_cot_occupant";

        /// <summary>The occupant's record id, or empty when the cot is unclaimed.</summary>
        public static string GetRecordId(GameObject cot)
        {
            var zdo = cot != null ? cot.GetComponent<ZNetView>()?.GetZDO() : null;
            return zdo != null ? zdo.GetString(OccupantKey, "") : "";
        }

        /// <summary>The occupant's record, or null when unassigned or the record is gone.</summary>
        public static VillagerRecord ResolveRecord(GameObject cot)
        {
            var recordId = GetRecordId(cot);
            return string.IsNullOrEmpty(recordId) ? null : VillagerRecordTable.FindById(recordId);
        }

        /// <summary>
        ///     Assign this cot to a villager, or pass an empty id to unassign it.
        ///
        ///     <para>Claims the ZDO first. A placed piece is usually owned by whoever loaded that
        ///     zone, which on a dedicated server is not the player pressing the button, and a
        ///     write without ownership is silently dropped — the same trap
        ///     <c>Village.SetAnchor</c> fell into. Returns false if the cot has no live ZDO,
        ///     so the caller can say so rather than reporting a success that never happened.</para>
        /// </summary>
        public static bool Assign(GameObject cot, string recordId)
        {
            var view = cot != null ? cot.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid())
            {
                Plugin.Log?.LogWarning("[VillagerCot] cannot assign: cot has no live ZNetView");
                return false;
            }

            view.ClaimOwnership();
            view.GetZDO().Set(OccupantKey, recordId ?? "");
            RefreshBlanket(cot);

            // The quarters index is keyed by occupant, so it is wrong the instant this changes.
            Villager.Levels.VillagerQuarters.Invalidate();
            return true;
        }

        /// <summary>
        ///     Repaint one cot's blanket to match its recorded occupant.
        ///
        ///     <para>Rebuilt from the cot itself rather than cloned from the template, because the
        ///     mark is per-occupant: two cots in the same village can and should differ.</para>
        /// </summary>
        public static void RefreshBlanket(GameObject cot)
        {
            var sheet = cot != null
                ? cot.transform.Find(VillagerBedPiece.SheetNode)?.GetComponent<MeshRenderer>()
                : null;
            if (sheet == null) return; // not grafted yet; the graft paints it on the way in

            var record = ResolveRecord(cot);
            sheet.sharedMaterial = VillagerBedPiece.BuildBlanket(cot, record?.Type ?? "");
        }

        /// <summary>
        ///     Assign the NEAREST cot from the console, the same write the Assign tab performs.
        ///     Exists because the UI path needs a key press and so cannot be driven from a test
        ///     harness; this exercises the ZDO write, the record lookup and the repaint together.
        /// </summary>
        [DevCommand("Assign the nearest Villager Cot: vv_bedassign <villagerName|none>",
            Name = "vv_bedassign")]
        public static void AssignCommand(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Print("[vv_bedassign] no local player");
                return;
            }

            var cot = VillagerBedPiece.PlacedCots()
                .OrderBy(c => Vector3.Distance(c.transform.position, player.transform.position))
                .FirstOrDefault();
            if (cot == null)
            {
                Print("[vv_bedassign] no placed cots");
                return;
            }

            if (args == null || args.Length < 2)
            {
                var current = ResolveRecord(cot);
                var sb = new StringBuilder(
                    $"[vv_bedassign] nearest cot at {cot.transform.position} " +
                    $"occupant={(current != null ? $"{current.Name} ({current.Type})" : "none")}");

                var village = Villages.Entity.VillageRegistry.GetVillageAt(cot.transform.position);
                sb.Append($"\n  village={(village != null ? village.VillageId : "NONE")}");
                if (village != null)
                    foreach (var r in VillagerRecordTable.QueryByVillage(village.VillageId)
                                 .Where(r => r.Status == RecordStatus.Alive))
                        sb.Append($"\n  - {r.Name} ({r.Type})");

                sb.Append("\n  usage: vv_bedassign <villagerName|none>");
                Print(sb.ToString());
                return;
            }

            if (string.Equals(args[1], "none", System.StringComparison.OrdinalIgnoreCase))
            {
                Print(Assign(cot, "")
                    ? "[vv_bedassign] cleared"
                    : "[vv_bedassign] FAILED to clear (no live ZDO)");
                return;
            }

            var match = VillagerRecordTable.EnumerateAll()
                .FirstOrDefault(r => string.Equals(r.Name, args[1],
                    System.StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Print($"[vv_bedassign] no villager named '{args[1]}'");
                return;
            }

            Print(Assign(cot, match.RecordId)
                ? $"[vv_bedassign] assigned to {match.Name} ({match.Type})"
                : "[vv_bedassign] FAILED to assign (no live ZDO)");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        /// <summary>
        ///     Repaint every placed cot from its ZDO.
        ///
        ///     <para>Found by PREFAB NAME, never by a marker component: hot reload strips mod
        ///     components off placed pieces, so a component-based sweep quietly skips every cot
        ///     that was already in the world.</para>
        /// </summary>
        public static int RefreshAll()
        {
            var count = 0;
            foreach (var cot in VillagerBedPiece.PlacedCots())
            {
                RefreshBlanket(cot);
                count++;
            }

            return count;
        }
    }
}
