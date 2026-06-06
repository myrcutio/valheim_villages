using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.UI.Tabs.Registry
{
    /// <summary>
    ///     Lists the villagers enrolled in this village (alive records for the
    ///     registry's village key) and their state, read from the authoritative
    ///     <see cref="VillagerRecordTable" />.
    /// </summary>
    [RegisterRegistryTab("roster", Order = 0)]
    public class RosterTab : IRegistryTabUI
    {
        private List<VillagerRecord> m_alive = new();

        public string TabName => "Roster";

        public void OnSelected(RegistryContext context)
        {
            Refresh(context);
        }

        public void OnDeselected()
        {
            m_alive.Clear();
        }

        public void OnUpdate(RegistryContext context)
        {
            Refresh(context);
        }

        private void Refresh(RegistryContext context)
        {
            m_alive = context == null
                ? new List<VillagerRecord>()
                : VillagerRecordTable.QueryByStatus(context.VillageId, RecordStatus.Alive).ToList();
        }

        public List<TabListItemUI> GetListItems(RegistryContext context)
        {
            if (m_alive.Count == 0) Refresh(context);

            if (m_alive.Count == 0)
                return new List<TabListItemUI> { new() { TabName = "(no villagers yet)" } };

            return m_alive
                .Select(r => new TabListItemUI { TabName = $"{r.Name} — {r.Type}" })
                .ToList();
        }

        public TabDetailDataUI GetDetail(int selectedIndex, RegistryContext context)
        {
            if (selectedIndex < 0 || selectedIndex >= m_alive.Count)
                return new TabDetailDataUI
                {
                    Title = "Roster",
                    Description = "No villagers are enrolled in this village yet.\n" +
                                  "Use the Add tab to enroll one.",
                };

            var r = m_alive[selectedIndex];
            return new TabDetailDataUI
            {
                Title = r.Name,
                Description =
                    $"Type: <color=#FFA13C>{r.Type}</color>\n" +
                    $"Status: <color=#6FCF6F>Alive</color>",
            };
        }
    }
}
