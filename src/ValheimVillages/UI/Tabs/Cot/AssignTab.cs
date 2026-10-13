using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.Items;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager.Levels;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.UI.Tabs.Cot
{
    /// <summary>
    ///     Pick which villager a cot belongs to. Modelled on the registry's Roster tab — the same
    ///     list of enrolled villagers — but its action assigns the cot instead of recalling.
    ///
    ///     <para>Only ALIVE villagers are listed. An egg has no one to sleep in a bed yet, and a
    ///     dead villager's cot should be freed rather than kept made up for them.</para>
    /// </summary>
    [RegisterCotTab("assign", Order = 0)]
    public class AssignTab : ICotTabUI, IInitialRowTab<CotContext>
    {
        private List<VillagerRecord> m_roster = new();

        public string TabName => "Assign";

        public void OnSelected(CotContext context) => Refresh(context);

        public void OnDeselected() => m_roster.Clear();

        public void OnUpdate(CotContext context) => Refresh(context);

        private void Refresh(CotContext context)
        {
            m_roster = context == null || string.IsNullOrEmpty(context.VillageId)
                ? new List<VillagerRecord>()
                : VillagerRecordTable.QueryByVillage(context.VillageId)
                    .Where(r => r.Status == RecordStatus.Alive)
                    .OrderBy(r => r.Name)
                    .ToList();
        }

        /// <summary>
        ///     Open on the villager who already sleeps here, rather than the unassign row.
        ///     "Whose bed is this?" is what the player wants when they walk up to a made cot;
        ///     clearing it is the rarer action, so it should not be what the cursor starts on.
        /// </summary>
        public int FindInitialRow(CotContext context)
        {
            var recordId = VillagerCotOccupant.GetRecordId(context?.Cot);

            // An unassigned cot has no better answer than row 0 — which IS the unassign row,
            // and reads correctly as "nobody".
            if (string.IsNullOrEmpty(recordId)) return -1;

            if (m_roster.Count == 0) Refresh(context);

            for (var i = 0; i < m_roster.Count; i++)
                if (m_roster[i].RecordId == recordId)
                    return i + 1; // row 0 is the unassign row, so the roster starts at 1

            // Assigned to someone absent from the roster (dead, or moved village). Falling back
            // to the default row is right: there is no row to point at.
            return -1;
        }

        public List<TabListItemUI> GetListItems(CotContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.VillageId))
                return new List<TabListItemUI> { new() { TabName = "(not in a village)" } };

            if (m_roster.Count == 0) Refresh(context);

            // "Unassigned" is a real row, not a button hidden in the detail pane: freeing a cot
            // is as ordinary an action as claiming one, and it reads naturally as picking a
            // different occupant.
            var rows = new List<TabListItemUI> { new() { TabName = UnassignLabel(context) } };
            rows.AddRange(m_roster.Select(r => new TabListItemUI { TabName = RowLabel(r, context) }));
            return rows;
        }

        private static string UnassignLabel(CotContext context)
        {
            return string.IsNullOrEmpty(VillagerCotOccupant.GetRecordId(context.Cot))
                ? "(unassigned)  ✓"
                : "(unassigned)";
        }

        /// <summary>
        ///     Row text. The current occupant is ticked, so the cot's state is readable from the
        ///     list without selecting anything. Including the tick in the label also changes the
        ///     row's refresh signature, so it redraws the moment the assignment changes.
        /// </summary>
        private static string RowLabel(VillagerRecord record, CotContext context)
        {
            var current = VillagerCotOccupant.GetRecordId(context.Cot);
            var mine = !string.IsNullOrEmpty(current) && current == record.RecordId;
            return mine
                ? $"{record.Name} — {record.Type}  ✓"
                : $"{record.Name} — {record.Type}";
        }

        public TabDetailDataUI GetDetail(int selectedIndex, CotContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.VillageId))
                return new TabDetailDataUI
                {
                    Title = "Villager Cot",
                    Description =
                        "This cot is not inside a village.\n" +
                        "Place it within a village's bounds to assign it to a villager.",
                };

            var occupant = VillagerCotOccupant.ResolveRecord(context.Cot);
            var occupantLine = occupant != null
                ? $"Occupant: <color=#FFA13C>{occupant.Name}</color> ({occupant.Type})"
                : "Occupant: <color=#9FB4C7>none</color>";

            // The room's own report, shown whoever is selected: the player is usually standing
            // in the room deciding what to build next, and that answer should not depend on
            // which row happens to be highlighted.
            var quarters = VillagerQuarters.ForCot(context.Cot,
                occupant != null ? Villager.Registry.VillagerRegistry.Get(occupant.Type) : null);
            var roomLines =
                $"\n\nRoom: {ShelterLine(quarters)}\n" +
                $"Comfort: {quarters.ComfortBar()}" +
                (string.IsNullOrEmpty(quarters.Blocker)
                    ? ""
                    : $"\n<color=#FFE300>{quarters.Blocker}</color>");

            // Row 0 is the unassign row; villagers start at 1.
            if (selectedIndex <= 0)
            {
                var detail = new TabDetailDataUI
                {
                    Title = "Unassigned",
                    Description =
                        occupantLine + "\n\n" +
                        "An unassigned cot still counts toward your villagers' comfort — it just " +
                        "belongs to nobody in particular, and its blanket stays plain." + roomLines,
                };

                if (occupant == null) return detail; // already unassigned; no action to offer

                detail.ActionText = "Clear";
                var cot = context.Cot;
                detail.OnAction = () => Apply(cot, "");
                return detail;
            }

            var index = selectedIndex - 1;
            if (index >= m_roster.Count)
                return new TabDetailDataUI
                {
                    Title = "Villager Cot", Description = occupantLine + roomLines,
                };

            var record = m_roster[index];
            var alreadyMine = occupant != null && occupant.RecordId == record.RecordId;

            var result = new TabDetailDataUI
            {
                Title = record.Name,
                Description =
                    $"Type: <color=#FFA13C>{record.Type}</color>\n" +
                    occupantLine + "\n\n" +
                    (alreadyMine
                        ? "This cot already belongs to them."
                        : "Assigning marks the blanket with their trade's colours.") + roomLines,
            };

            if (alreadyMine) return result;

            result.ActionText = "Assign Cot";
            var target = context.Cot;
            var recordId = record.RecordId;
            result.OnAction = () => Apply(target, recordId);
            return result;
        }

        /// <summary>Shelter in one line — the workbench test, in the words a player can act on.</summary>
        private static string ShelterLine(QuartersState quarters)
        {
            if (!quarters.HasCot) return "<color=#9FB4C7>unknown</color>";

            return quarters.Sheltered
                ? $"<color=#6FCF6F>sheltered</color> <color=#9FB4C7>({quarters.Coverage:P0} covered)</color>"
                : $"<color=#FF8A6F>exposed</color> <color=#9FB4C7>({quarters.Coverage:P0} covered, " +
                  $"needs {VillagerQuarters.RequiredCoverage:P0})</color>";
        }

        /// <summary>
        ///     Write the assignment and repaint. Failure is reported to the player rather than
        ///     swallowed: a write can be dropped when the cot's ZDO cannot be claimed, and a
        ///     button that silently does nothing is worse than one that says why.
        /// </summary>
        private static void Apply(UnityEngine.GameObject cot, string recordId)
        {
            if (VillagerCotOccupant.Assign(cot, recordId)) return;

            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center, "Could not update this cot");
        }
    }
}
