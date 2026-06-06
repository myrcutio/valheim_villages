using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.UI.Tabs.Registry
{
    /// <summary>
    ///     Lists fallen (dead) villagers for this village, read from the
    ///     <see cref="VillagerRecordTable" />. The revive action itself is deferred to
    ///     the next phase (it re-spawns an NPC from the record); this tab is read-only.
    /// </summary>
    [RegisterRegistryTab("revive", Order = 2)]
    public class ReviveTab : IRegistryTabUI
    {
        private List<VillagerRecord> m_dead = new();

        public string TabName => "Revive";

        public void OnSelected(RegistryContext context)
        {
            Refresh(context);
        }

        public void OnDeselected()
        {
            m_dead.Clear();
        }

        public void OnUpdate(RegistryContext context)
        {
            Refresh(context);
        }

        private void Refresh(RegistryContext context)
        {
            m_dead = context == null
                ? new List<VillagerRecord>()
                : VillagerRecordTable.QueryByStatus(context.VillageKey, RecordStatus.Dead).ToList();
        }

        public List<TabListItemUI> GetListItems(RegistryContext context)
        {
            if (m_dead.Count == 0) Refresh(context);

            if (m_dead.Count == 0)
                return new List<TabListItemUI> { new() { TabName = "(no fallen villagers)" } };

            return m_dead
                .Select(r => new TabListItemUI { TabName = $"{r.Name} — {r.Type}" })
                .ToList();
        }

        public TabDetailDataUI GetDetail(int selectedIndex, RegistryContext context)
        {
            if (selectedIndex < 0 || selectedIndex >= m_dead.Count)
                return new TabDetailDataUI
                {
                    Title = "Revive",
                    Description = "Fallen villagers will appear here.\n" +
                                  "Revive them to return them to the village.",
                };

            var r = m_dead[selectedIndex];
            var detail = new TabDetailDataUI
            {
                Title = r.Name,
                Description =
                    $"Type: <color=#FFA13C>{r.Type}</color>\n" +
                    "Status: <color=#E8643C>Fallen</color>",
            };

            if (VillagerReviveService.IsOnCooldown)
            {
                // Gate the action behind the cooldown (no material cost for now).
                detail.Description +=
                    $"\n\n<color=#FFE300>Revive cooldown: {VillagerReviveService.CooldownRemaining:F0}s</color>";
            }
            else
            {
                detail.ActionText = "Revive";
                var recordId = r.RecordId;
                var name = r.Name;
                detail.OnAction = () =>
                {
                    if (VillagerReviveService.Revive(VillagerRecordTable.FindById(recordId), out var err))
                        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"{name} revived");
                    else
                        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Cannot revive: {err}");
                };
            }

            return detail;
        }
    }
}
