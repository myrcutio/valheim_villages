using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.UI.Panels;

namespace ValheimVillages.UI.Tabs
{
    /// <summary>
    ///     Tab listing tasks for the villager — currently blocked work orders that need
    ///     player attention. Each blocked entry shows the item being crafted and the
    ///     reason it can't be fulfilled.
    /// </summary>
    [RegisterTab("info", Order = 0)]
    public class InfoTab : IVillagerTabUI
    {
        private const string AttentionPrefix = "⚠ ";

        private List<ActivityLogEntry> m_issues = new();
        private int m_issueCount;
        public string TabName => "Tasks";

        public void OnSelected(VillagerBehaviorBridge villager)
        {
            RefreshIssues(villager);
        }

        public void OnDeselected()
        {
            m_issues.Clear();
            m_issueCount = 0;
        }

        public void OnUpdate(VillagerBehaviorBridge villager)
        {
            RefreshIssues(villager);
        }

        #region IVillagerTab — List + Detail

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>();

            m_issueCount = m_issues.Count;
            foreach (var issue in m_issues)
                items.Add(new TabListItemUI
                {
                    TabName = AttentionPrefix + (issue.ItemPrefab ?? "(blocked)"),
                    Icon = ResolveItemIcon(issue.ItemPrefab),
                });

            AddAbilityItems(items, villager);
            return items;
        }

        public TabDetailDataUI GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            if (index >= 0 && index < m_issueCount)
            {
                var issue = m_issues[index];

                var pins = new List<(Vector3 position, Color color)>();
                if (issue.WorkOrderPosX.HasValue && issue.WorkOrderPosY.HasValue && issue.WorkOrderPosZ.HasValue)
                {
                    var chestPos = new Vector3(issue.WorkOrderPosX.Value, issue.WorkOrderPosY.Value, issue.WorkOrderPosZ.Value);
                    pins.Add((chestPos, new Color(1f, 0.5f, 0.1f, 1f)));
                }

                var mapTexture = VillageMapPanel.RenderForTask(villager, pins);

                return new TabDetailDataUI
                {
                    Title = issue.ItemPrefab ?? "Blocked",
                    Icon = ResolveItemIcon(issue.ItemPrefab),
                    Description = $"Station: {issue.StationName ?? "?"}\n{issue.Reason ?? issue.Description ?? ""}",
                    MapTexture = mapTexture,
                };
            }

            var abilityIdx = index - m_issueCount;
            return GetAbilityDetail(abilityIdx, villager);
        }

        #endregion

        #region Panel-Driven Ability Data

        private static readonly List<IListPanel> s_panels = new();

        /// <summary>Register an IListPanel for this tab. Called from Plugin startup.</summary>
        public static void RegisterPanel(IListPanel panel)
        {
            if (panel.ParentTab == "info" && !s_panels.Contains(panel))
                s_panels.Add(panel);
        }

        private readonly List<(IListPanel panel, int startIdx, int count)> m_panelRanges = new();

        private void AddAbilityItems(
            List<TabListItemUI> items, VillagerBehaviorBridge villager)
        {
            m_panelRanges.Clear();
            foreach (var panel in s_panels)
                if (panel is IListPanelUI panelUI)
                {
                    var panelItems = panelUI.GetListItems(villager);
                    if (panelItems.Count > 0)
                    {
                        m_panelRanges.Add((panel, items.Count, panelItems.Count));
                        foreach (var p in panelItems) items.Add(p);
                    }
                }
        }

        private TabDetailDataUI GetAbilityDetail(
            int abilityIdx, VillagerBehaviorBridge villager)
        {
            var globalIdx = m_issueCount + abilityIdx;
            foreach (var (panel, startIdx, count) in m_panelRanges)
                if (globalIdx >= startIdx && globalIdx < startIdx + count)
                    return panel is IListPanelUI panelUI ? panelUI.GetDetail(globalIdx - startIdx, villager) : null;
            return null;
        }

        #endregion

        #region Helpers

        private void RefreshIssues(VillagerBehaviorBridge villager)
        {
            m_issues.Clear();
            if (villager == null || string.IsNullOrEmpty(villager.UniqueId)) return;
            m_issues = VillagerActivityLog.Instance
                .GetEntries(villager.UniqueId)
                .Where(e => e.Action == "blocked")
                .ToList();
        }

        private static Sprite ResolveItemIcon(string itemPrefab)
        {
            if (string.IsNullOrEmpty(itemPrefab) || ObjectDB.instance == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab(itemPrefab);
            if (prefab == null) return null;
            var drop = prefab.GetComponent<ItemDrop>();
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }

        #endregion
    }
}
