using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.UI.Tabs.Registry
{
    /// <summary>
    ///     Lists the villagers enrolled in this village — alive AND incubating (egg)
    ///     records for the registry's village key — read from the authoritative
    ///     <see cref="VillagerRecordTable" />. Each alive row is annotated with live
    ///     <see cref="VillagerLiveness">presence</see> (in world / away / missing) so the
    ///     player can tell an Alive record with no NPC in the world from one that's simply
    ///     loaded elsewhere. Dead villagers live on the Revive tab.
    /// </summary>
    [RegisterRegistryTab("roster", Order = 0)]
    public class RosterTab : IRegistryTabUI
    {
        private List<VillagerRecord> m_roster = new();

        public string TabName => "Roster";

        public void OnSelected(RegistryContext context)
        {
            Refresh(context);
        }

        public void OnDeselected()
        {
            m_roster.Clear();
        }

        public void OnUpdate(RegistryContext context)
        {
            Refresh(context);
        }

        private void Refresh(RegistryContext context)
        {
            // Alive + Egg = the village's current/incoming population. Eggs were previously
            // invisible in the entire Manage UI (Roster=Alive only, Revive=Dead only).
            m_roster = context == null
                ? new List<VillagerRecord>()
                : VillagerRecordTable.QueryByVillage(context.VillageId)
                    .Where(r => r.Status == RecordStatus.Alive || r.Status == RecordStatus.Egg)
                    .OrderByDescending(r => (int)r.Status) // Alive(1) before Egg(0)
                    .ThenBy(r => r.Name)
                    .ToList();
        }

        public List<TabListItemUI> GetListItems(RegistryContext context)
        {
            if (!RegistryTabLoading.VillageReady(context)) return RegistryTabLoading.ListItems();

            if (m_roster.Count == 0) Refresh(context);

            if (m_roster.Count == 0)
                return new List<TabListItemUI> { new() { TabName = "(no villagers yet)" } };

            return m_roster
                .Select(r => new TabListItemUI { TabName = RowLabel(r) })
                .ToList();
        }

        /// <summary>
        ///     Row text. Eggs read "(incubating)"; alive villagers get a presence badge only
        ///     when they aren't actively here, so a normal in-world roster stays clean.
        ///     Including the word in the label also makes the host's name/icon refresh
        ///     signature change when presence changes, so the row redraws.
        /// </summary>
        private static string RowLabel(VillagerRecord r)
        {
            if (r.Status == RecordStatus.Egg)
                return $"{r.Name} — {r.Type}  (incubating)";

            var presence = VillagerLiveness.Resolve(r);
            return presence == LivePresence.Live
                ? $"{r.Name} — {r.Type}"
                : $"{r.Name} — {r.Type}  ({VillagerLiveness.Label(presence)})";
        }

        public TabDetailDataUI GetDetail(int selectedIndex, RegistryContext context)
        {
            if (!RegistryTabLoading.VillageReady(context)) return RegistryTabLoading.Detail();

            if (selectedIndex < 0 || selectedIndex >= m_roster.Count)
                return new TabDetailDataUI
                {
                    Title = "Roster",
                    Description = "No villagers are enrolled in this village yet.\n" +
                                  "Use the Add tab to enroll one.",
                };

            var r = m_roster[selectedIndex];

            // Eggs are incubating records with no NPC to recall — show their state read-only.
            if (r.Status == RecordStatus.Egg)
                return new TabDetailDataUI
                {
                    Title = r.Name,
                    Description =
                        $"Type: <color=#FFA13C>{r.Type}</color>\n" +
                        "Status: <color=#FFE300>Incubating</color>",
                };

            var presence = VillagerLiveness.Resolve(r);
            var detail = new TabDetailDataUI
            {
                Title = r.Name,
                Description =
                    $"Type: <color=#FFA13C>{r.Type}</color>\n" +
                    $"Status: <color=#6FCF6F>Alive</color> " +
                    $"<color=#9FB4C7>({VillagerLiveness.Label(presence)})</color>",
            };

            // Recall: bring the villager back to this station. Record id == VillagerAI.UniqueId.
            detail.ActionText = "Recall";
            var recordId = r.RecordId;
            var name = r.Name;
            var stationPos = context.RegistryPosition;
            detail.OnAction = () =>
            {
                if (VillagerAIManager.ActiveVillagers.TryGetValue(recordId, out var ai) && ai != null)
                {
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.Center,
                        ai.Recall(stationPos)
                            ? $"{name} recalled"
                            : $"Cannot recall {name} (no reachable spot at the station)");
                    return;
                }

                // No local instance. Only mark fallen when the villager is host-confirmed
                // GONE (Missing) — never for one that's merely away (unloaded / loaded on
                // another peer), which would wrongly kill a perfectly fine villager. Real
                // in-world deaths are flipped to Dead automatically by VillagerDeathPatch.
                var current = VillagerLiveness.Resolve(VillagerRecordTable.FindById(recordId));
                if (current == LivePresence.Missing)
                {
                    VillagerRecordTable.SetStatus(recordId, RecordStatus.Dead);
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.Center,
                        $"{name} is gone — marked fallen. Use Revive to restore.");
                }
                else
                {
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.Center,
                        $"{name} is away (not loaded here) — try again near the village.");
                }
            };

            return detail;
        }
    }
}
