using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;

namespace ValheimVillages.UI.Tabs
{
    /// <summary>
    ///     Tab showing debug commands and registered panels for villager NPCs.
    ///     Provides commands as list items with action buttons, plus panel-driven
    ///     items (e.g. Village Map for patrollers) via [RegisterListPanel].
    /// </summary>
    [RegisterTab("debug", Order = 1)]
    public class DebugTab : IVillagerTabUI
    {
        private readonly List<DebugCommand> m_commands = new();
        public string TabName => "Debug";

        public void OnSelected(VillagerBehaviorBridge villager)
        {
            BuildCommands(villager);
        }

        public void OnDeselected()
        {
            m_commands.Clear();
            m_panelRanges.Clear();
        }

        public void OnUpdate(VillagerBehaviorBridge villager)
        {
            BuildCommands(villager);
        }

        private class DebugCommand
        {
            public string ActionText;
            public string CommandName;
            public string Description;
            public Action OnAction;
        }

        #region IVillagerTabUI — List + Detail

        public List<TabListItemUI> GetListItems(
            VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>();
            foreach (var cmd in m_commands)
                items.Add(new TabListItemUI { TabName = cmd.CommandName });

            AddPanelItems(items, villager);
            return items;
        }

        public TabDetailDataUI GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            // Command items
            if (index >= 0 && index < m_commands.Count)
            {
                var cmd = m_commands[index];
                return new TabDetailDataUI
                {
                    Title = cmd.CommandName,
                    Description = cmd.Description,
                    ActionText = cmd.ActionText,
                    OnAction = cmd.OnAction,
                };
            }

            // Panel items (after commands)
            return GetPanelDetail(index, villager);
        }

        #endregion

        #region Panel Integration

        private static readonly List<IListPanel> s_panels = new();
        private readonly List<(IListPanel panel, int startIdx, int count)> m_panelRanges = new();

        /// <summary>Register an IListPanel for this tab. Called from AttributeScanner.</summary>
        public static void RegisterPanel(IListPanel panel)
        {
            if (panel.ParentTab == "debug" && !s_panels.Contains(panel))
                s_panels.Add(panel);
        }

        private void AddPanelItems(
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

        private TabDetailDataUI GetPanelDetail(
            int index, VillagerBehaviorBridge villager)
        {
            foreach (var (panel, startIdx, count) in m_panelRanges)
                if (index >= startIdx && index < startIdx + count)
                    return panel is IListPanelUI panelUI
                        ? panelUI.GetDetail(index - startIdx, villager)
                        : null;
            return null;
        }

        #endregion

        #region Command Building

        private void BuildCommands(VillagerBehaviorBridge villager)
        {
            m_commands.Clear();
            if (villager == null) return;

            AddStateInfo(villager);
            AddRecentTasks(villager);
            AddNavigationCommands(villager);
        }

        private void AddStateInfo(VillagerBehaviorBridge villager)
        {
            var info = $"State: {villager.CurrentState}";
            var waypoint = villager.villagerInstance?.villagerAI?.GetCurrentWaypoint();
            if (waypoint != null)
            {
                var dist = Vector3.Distance(
                    villager.transform.position, waypoint.Position);
                info += $"\nTarget: {dist:F0}m away";
            }

            info += $"\nBest comfort: {villager.Memory?.BestComfortLevel ?? 0f:F1}";

            m_commands.Add(new DebugCommand
            {
                CommandName = "Current State",
                Description = info,
                ActionText = null,
                OnAction = null,
            });
        }

        private void AddRecentTasks(VillagerBehaviorBridge villager)
        {
            var entries = VillagerActivityLog.Instance.GetEntries(villager.UniqueId);
            var take = Math.Min(10, entries.Count);
            var recent = take == 0
                ? new List<ActivityLogEntry>()
                : entries.Skip(entries.Count - take).ToList();
            var lines = new List<string>();
            for (var i = recent.Count - 1; i >= 0; i--)
            {
                var e = recent[i];
                lines.Add($"{e.TaskName} / {e.Action}: {e.Description}");
            }

            var description = lines.Count == 0
                ? "No recent tasks recorded."
                : string.Join("\n", lines);

            m_commands.Add(new DebugCommand
            {
                CommandName = "Recent tasks",
                Description = description,
                ActionText = null,
                OnAction = null,
            });
        }

        private void AddNavigationCommands(VillagerBehaviorBridge villager)
        {
            var v = villager;
            AddNavCommand(v, "Go to Bed", LocationType.Bed);
            AddNavCommand(v, "Find Fire", LocationType.Fire);
        }

        private void AddNavCommand(
            VillagerBehaviorBridge v, string name, LocationType type)
        {
            m_commands.Add(new DebugCommand
            {
                CommandName = name,
                Description = $"Send villager to the nearest known {type}.",
                ActionText = "Go",
                OnAction = () =>
                {
                    var ai = v.villagerInstance?.villagerAI;
                    if (ai == null)
                    {
                        Msg("No villager available.");
                        return;
                    }

                    var bedPos = ai.GetMemory()?.BedPosition ?? ai.Position;

                    // Bed is per-villager; everything else comes from the
                    // village PoI registry (nearest of the requested type).
                    Vector3 dest;
                    if (type == LocationType.Bed)
                    {
                        dest = bedPos;
                    }
                    else
                    {
                        var nearest = VillagePoiRegistry.GetPois(bedPos, type)
                            .OrderBy(l => Vector3.Distance(bedPos, l.Position))
                            .FirstOrDefault();
                        if (nearest == null)
                        {
                            Msg($"No {type} location known.");
                            return;
                        }

                        dest = nearest.Position;
                    }

                    // Funnel through the single NavTo entry point: it snaps to a
                    // reachable approach, clears any stale path, sets state, and
                    // resets the agent. (Was ai.FindPath → native BaseAI.FindPath,
                    // which left a stale path the agent mover ignored and stranded
                    // the villager.)
                    // directOrder: outranks task pickup / idling until arrival,
                    // so the behavior loop's idle fallback can't reset it.
                    if (ai.NavTo(dest, BehaviorState.Traveling, $"{name} destination",
                            directOrder: true))
                        Msg($"Going to {type}");
                    else
                        Msg($"Can't reach {type} from here");
                    InventoryGui.instance?.Hide();
                },
            });
        }

        private static void Msg(string text)
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.TopLeft, text);
        }

        #endregion
    }
}