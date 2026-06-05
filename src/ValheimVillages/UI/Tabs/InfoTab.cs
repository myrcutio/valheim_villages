using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Work;
using ValheimVillages.Interfaces;
using ValheimVillages.Items.WorkOrders;
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

        // The current-activity row is always the first list item.
        private const int CurrentTaskCount = 1;

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>
            {
                new() { TabName = "▶ " + CurrentActivityLabel(villager), Icon = null },
            };

            m_issueCount = m_issues.Count;
            foreach (var issue in m_issues)
                items.Add(new TabListItemUI
                {
                    TabName = AttentionPrefix + (issue.ItemPrefab ?? issue.TaskName ?? "(blocked)"),
                    Icon = ResolveItemIcon(issue.ItemPrefab),
                });

            AddAbilityItems(items, villager);
            return items;
        }

        /// <summary>
        ///     Short label for the current-activity row. Prefers the active
        ///     behavior's own status text (e.g. "Farming: Planting",
        ///     "Patrolling (8 waypoints)") and falls back to the raw behavior
        ///     state when no behavior is active or it reports nothing.
        /// </summary>
        private static string CurrentActivityLabel(VillagerBehaviorBridge villager)
        {
            var status = villager?.AI?.ActiveBehavior?.GetStatusText();
            if (!string.IsNullOrEmpty(status)) return status;
            return villager != null ? villager.CurrentState.ToString() : "Idle";
        }

        public TabDetailDataUI GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            if (index < CurrentTaskCount)
                return CurrentTaskDetail(villager);

            var issueIdx = index - CurrentTaskCount;
            if (issueIdx >= 0 && issueIdx < m_issueCount)
            {
                var issue = m_issues[issueIdx];

                var pins = new List<(Vector3 position, Color color)>();
                Vector3? focus = null;
                if (issue.WorkOrderPosX.HasValue && issue.WorkOrderPosY.HasValue && issue.WorkOrderPosZ.HasValue)
                    focus = new Vector3(issue.WorkOrderPosX.Value, issue.WorkOrderPosY.Value, issue.WorkOrderPosZ.Value);

                // Work-order blockers pin a chest; behavior blockers (e.g. patrol)
                // pin the spot that's the problem — label/colour it accordingly.
                var isStationBlocker = !string.IsNullOrEmpty(issue.StationName);
                var focusLabel = isStationBlocker ? "Chest" : "Problem";
                var focusColor = isStationBlocker ? ChestPinColor : ProblemPinColor;
                var legend = BuildPins(pins, focus, focusLabel, focusColor);
                var reason = issue.Reason ?? issue.Description ?? "";
                // Station line only when this blocker is tied to one (work orders);
                // behavior blockers like patrol have no station.
                var description = string.IsNullOrEmpty(issue.StationName)
                    ? reason
                    : $"Station: {StationDisplay.Pretty(issue.StationName)}\n{reason}";
                if (legend.Length > 0) description += $"\n\n{legend}";

                return new TabDetailDataUI
                {
                    Title = issue.ItemPrefab ?? issue.TaskName ?? "Blocked",
                    Icon = ResolveItemIcon(issue.ItemPrefab),
                    Description = description,
                    MapTexture = VillageMapPanel.RenderForTask(villager, pins),
                };
            }

            // Ability panels stored absolute list indices when added.
            return GetAbilityDetail(index, villager);
        }

        /// <summary>Detail for the "current activity" row (index 0).</summary>
        private TabDetailDataUI CurrentTaskDetail(VillagerBehaviorBridge villager)
        {
            var state = villager != null
                ? villager.CurrentState.ToString()
                : "Idle";
            var status = CurrentActivityLabel(villager);

            var pins = new List<(Vector3 position, Color color)>();
            var waypoint = villager?.CurrentWaypoint;
            var target = waypoint != null ? waypoint.Position : (Vector3?)null;
            var legend = BuildPins(pins, target, "Target", TargetPinColor);

            // Show the behavior status; append the raw state only when it adds
            // information (the status fell back to the state otherwise).
            var description = status == state ? $"State: {state}" : $"{status}\nState: {state}";
            if (legend.Length > 0) description += $"\n\n{legend}";

            // The native description layout collapses (and the map covers the
            // text) when the recipe icon is disabled, so always give it an icon:
            // the item being crafted if any, else a generic tool icon.
            var crafting = (villager?.AI?.ActiveBehavior as CraftingBehaviorAdapter)?.Crafting;
            var icon = ResolveItemIcon(crafting?.CurrentItemPrefab)
                       ?? ResolveItemIcon("Hammer");

            return new TabDetailDataUI
            {
                Title = "Current Activity",
                Icon = icon,
                Description = description,
                MapTexture = VillageMapPanel.RenderForTask(villager, pins),
            };
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
            int absoluteIndex, VillagerBehaviorBridge villager)
        {
            foreach (var (panel, startIdx, count) in m_panelRanges)
                if (absoluteIndex >= startIdx && absoluteIndex < startIdx + count)
                    return panel is IListPanelUI panelUI
                        ? panelUI.GetDetail(absoluteIndex - startIdx, villager)
                        : null;
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

        private static readonly Color ChestPinColor = new(1f, 0.55f, 0.15f);
        private static readonly Color TargetPinColor = new(0.3f, 0.9f, 0.3f);
        private static readonly Color PlayerPinColor = new(0.35f, 0.8f, 1f);
        private static readonly Color ProblemPinColor = new(0.95f, 0.25f, 0.2f);

        /// <summary>
        ///     Add the focus pin (chest/target) and the player's pin, returning a
        ///     colour-coded legend line for the description ("● Chest    ● You").
        /// </summary>
        private static string BuildPins(
            List<(Vector3 position, Color color)> pins,
            Vector3? focus, string focusLabel, Color focusColor)
        {
            var legend = "";
            if (focus.HasValue)
            {
                pins.Add((focus.Value, focusColor));
                legend = $"{Bullet(focusColor)} {focusLabel}";
            }

            var player = Player.m_localPlayer;
            if (player != null)
            {
                pins.Add((player.transform.position, PlayerPinColor));
                legend += (legend.Length > 0 ? "    " : "") +
                          $"{Bullet(PlayerPinColor)} You";
            }

            return legend;
        }

        private static string Bullet(Color c)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>●</color>";
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
