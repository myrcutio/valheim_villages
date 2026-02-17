using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.NPCs.AI.Guards;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Tab showing debug commands for villager NPCs.
    /// Provides commands as list items with action buttons.
    /// </summary>
    public partial class DebugTab : IVillagerTab
    {
        public string Name => "Debug";

        private readonly List<DebugCommand> m_commands = new();

        public void OnSelected(VillagerBehaviorBridge villager) =>
            BuildCommands(villager);

        public void OnDeselected()
        {
            m_commands.Clear();
            ClearMapCache();
        }

        public void OnUpdate(VillagerBehaviorBridge villager) =>
            BuildCommands(villager);

        #region IVillagerTab — List + Detail

        public List<TabListItem> GetListItems(
            VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItem>();
            foreach (var cmd in m_commands)
                items.Add(new TabListItem { Name = cmd.Name });
            return items;
        }

        public TabDetailData GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            if (index < 0 || index >= m_commands.Count) return null;
            var cmd = m_commands[index];

            if (cmd.IsMapCommand)
                return GetMapDetail(cmd);

            return new TabDetailData
            {
                Title = cmd.Name,
                Description = cmd.Description,
                ActionText = cmd.ActionText,
                OnAction = cmd.OnAction
            };
        }

        #endregion

        #region Command Building

        private void BuildCommands(VillagerBehaviorBridge villager)
        {
            m_commands.Clear();
            if (villager == null) return;

            AddStateInfo(villager);
            AddRecentTasks(villager);
            AddProblems(villager);
            AddMovementCommands(villager);
            AddNavigationCommands(villager);
            AddGuardCommands(villager);
        }

        private void AddStateInfo(VillagerBehaviorBridge villager)
        {
            string info = $"State: {villager.CurrentState}";
            if (villager.CurrentTarget.HasValue)
            {
                float dist = Vector3.Distance(
                    villager.transform.position,
                    villager.CurrentTarget.Value);
                info += $"\nTarget: {dist:F0}m away";
            }
            int variety = villager.Memory?.GetLocationTypeVariety() ?? 0;
            info += $"\nVariety: {variety} types";

            m_commands.Add(new DebugCommand
            {
                Name = "Current State",
                Description = info,
                ActionText = null,
                OnAction = null
            });
        }

        private void AddRecentTasks(VillagerBehaviorBridge villager)
        {
            var entries = VillagerActivityLog.Instance.GetEntries(villager.UniqueId);
            int take = Math.Min(10, entries.Count);
            var recent = take == 0
                ? new List<ActivityLogEntry>()
                : entries.Skip(entries.Count - take).ToList();
            var lines = new List<string>();
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var e = recent[i];
                lines.Add($"{e.TaskName} / {e.Action}: {e.Description}");
            }
            string description = lines.Count == 0
                ? "No recent tasks recorded."
                : string.Join("\n", lines);

            m_commands.Add(new DebugCommand
            {
                Name = "Recent tasks (10)",
                Description = description,
                ActionText = null,
                OnAction = null
            });
        }

        private void AddProblems(VillagerBehaviorBridge villager)
        {
            var entries = VillagerActivityLog.Instance.GetEntries(villager.UniqueId);
            var problems = entries.Where(e => e.Action == "abandon").ToList();
            var lastProblems = problems.Count <= 10 ? problems : problems.Skip(problems.Count - 10).ToList();
            var lines = lastProblems.Count == 0
                ? new List<string> { "No problems (no abandoned tasks)." }
                : lastProblems.Select(e => $"{e.TaskName}: {e.Description}").ToList();
            string description = string.Join("\n", lines);

            m_commands.Add(new DebugCommand
            {
                Name = "Problems (abandoned)",
                Description = description,
                ActionText = null,
                OnAction = null
            });
        }

        private void AddMovementCommands(VillagerBehaviorBridge villager)
        {
            var v = villager;
            m_commands.Add(new DebugCommand
            {
                Name = "Run Movement Tests",
                Description = "Execute a sequence of movement tests " +
                    "(~17 seconds). The UI will close.",
                ActionText = "Run",
                OnAction = () =>
                {
                    if (v.IsTestRunning)
                    {
                        Msg("Tests already running...");
                    }
                    else if (v.RunMovementTests())
                    {
                        Msg("Starting movement tests (~17s)...");
                        InventoryGui.instance?.Hide();
                    }
                    else
                    {
                        Msg("Could not start movement tests");
                    }
                }
            });
        }

        private void AddNavigationCommands(VillagerBehaviorBridge villager)
        {
            var v = villager;
            AddNavCommand(v, "Go to Bed", LocationType.Bed);
            AddNavCommand(v, "Find Fire", LocationType.Fire);
            AddNavCommand(v, "Find Chair", LocationType.Chair);

            m_commands.Add(new DebugCommand
            {
                Name = "Cancel Test",
                Description = "Cancel the currently running movement test.",
                ActionText = "Cancel",
                OnAction = () =>
                {
                    v.CancelMovementTest();
                    Msg("Movement test cancelled");
                }
            });
        }

        private void AddNavCommand(
            VillagerBehaviorBridge v, string name, LocationType type)
        {
            m_commands.Add(new DebugCommand
            {
                Name = name,
                Description = $"Send villager to the nearest known {type}.",
                ActionText = "Go",
                OnAction = () =>
                {
                    var t = v.DebugWanderToLocationType(type);
                    Msg(t.HasValue
                        ? $"Going to {type}"
                        : $"No {type} known");
                    InventoryGui.instance?.Hide();
                }
            });
        }

        private static void Msg(string text) =>
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.TopLeft, text);

        #endregion

        private class DebugCommand
        {
            public string Name;
            public string Description;
            public string ActionText;
            public Action OnAction;
            public bool IsMapCommand;
            public GuardBehavior Guard;
            public Vector3? GuardPosition;
        }
    }
}
