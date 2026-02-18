using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ValheimVillages.Abilities;
using ValheimVillages.Behaviors;
using ValheimVillages.NPCs.AI;
using ValheimVillages.TaskQueue;
using ValheimVillages.UI.Core;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Scans the mod assembly for registration attributes and wires up
    /// console commands, task handlers, cleanup hooks, abilities, passives,
    /// UI tabs, list panels, context menus, and behaviors matching annotations.
    /// </summary>
    public static class AttributeScanner
    {
        private static readonly Dictionary<string, IAbility> s_abilities = new();
        private static readonly Dictionary<string, IPassiveEffect> s_passives = new();
        private static readonly List<IVillagerTab> s_tabs = new();
        private static readonly List<IListPanel> s_panels = new();
        private static readonly List<IContextMenu> s_contextMenus = new();
        private static readonly Dictionary<string, Func<VillagerAI, IBehavior>> s_behaviorCreators = new();
        private static readonly HashSet<string> s_modObjectNames = new();

        /// <summary>
        /// Master entry point. Scans the assembly for all registration attributes
        /// and wires up the discovered types/methods.
        /// </summary>
        public static void ScanAndRegister(Assembly assembly)
        {
            s_abilities.Clear();
            s_passives.Clear();
            s_tabs.Clear();
            s_panels.Clear();
            s_contextMenus.Clear();
            s_behaviorCreators.Clear();
            s_modObjectNames.Clear();

            RegisterDevCommands(assembly);
            RegisterTaskHandlers(assembly);
            RegisterAbilities(assembly);
            RegisterPassives(assembly);
            RegisterTabs(assembly);
            RegisterListPanels(assembly);
            RegisterContextMenus(assembly);
            RegisterBehaviors(assembly);
            CollectModObjectNames(assembly);

            Plugin.Log?.LogInfo(
                $"[AttributeScanner] Registered: " +
                $"{s_abilities.Count} abilities, {s_passives.Count} passives, " +
                $"{s_tabs.Count} tabs, {s_panels.Count} panels, " +
                $"{s_contextMenus.Count} context menus, " +
                $"{s_behaviorCreators.Count} behaviors, " +
                $"{s_modObjectNames.Count} mod objects");
        }

        #region DevCommands

        private static void RegisterDevCommands(Assembly assembly)
        {
            int count = 0;
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = method.GetCustomAttribute<DevCommandAttribute>();
                    if (attr == null) continue;

                    string name = attr.Name ?? DeriveCommandName(type, method);
                    var parameters = method.GetParameters();

                    Terminal.ConsoleEventFailable handler;
                    if (parameters.Length == 0)
                    {
                        var m = method;
                        handler = _ => { m.Invoke(null, null); return null; };
                    }
                    else if (parameters.Length == 1 &&
                             parameters[0].ParameterType == typeof(Terminal.ConsoleEventArgs))
                    {
                        var m = method;
                        handler = args => { m.Invoke(null, new object[] { args }); return null; };
                    }
                    else
                    {
                        Plugin.Log?.LogWarning(
                            $"[AttributeScanner] Skipping [DevCommand] on {type.Name}.{method.Name}: " +
                            "unsupported parameter signature");
                        continue;
                    }

                    new Terminal.ConsoleCommand(name, attr.Description, handler);
                    count++;
                }
            }
            Plugin.Log?.LogDebug($"[AttributeScanner] Registered {count} dev commands");
        }

        private static string DeriveCommandName(Type type, MethodInfo method)
        {
            return $"{type.Name}_{method.Name}".ToLowerInvariant();
        }

        #endregion

        #region ObjectDB

        /// <summary>
        /// Invoke all [RegisterObjectDB] methods. Called from ObjectDB.Awake patch
        /// and during hot-reload.
        /// </summary>
        public static void InvokeObjectDBRegistrations(Assembly assembly, ObjectDB db)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = method.GetCustomAttribute<RegisterObjectDBAttribute>();
                    if (attr == null) continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(ObjectDB))
                    {
                        method.Invoke(null, new object[] { db });
                    }
                    else
                    {
                        Plugin.Log?.LogWarning(
                            $"[AttributeScanner] Skipping [RegisterObjectDB] on " +
                            $"{type.Name}.{method.Name}: expected (ObjectDB) parameter");
                    }
                }
            }
        }

        #endregion

        #region TaskHandlers

        private static void RegisterTaskHandlers(Assembly assembly)
        {
            int count = 0;
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterTaskHandlerAttribute>();
                if (attr == null) continue;

                if (!typeof(ITaskHandler).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterTaskHandler] " +
                        "but does not implement ITaskHandler");
                    continue;
                }

                var handler = (ITaskHandler)Activator.CreateInstance(type);
                TaskHandlerRegistry.Register(handler);
                count++;
            }
            Plugin.Log?.LogDebug($"[AttributeScanner] Registered {count} task handlers");
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Invoke all [RegisterCleanup] methods. Called during hot-reload cleanup.
        /// </summary>
        public static void InvokeAllCleanup(Assembly assembly)
        {
            int count = 0;
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = method.GetCustomAttribute<RegisterCleanupAttribute>();
                    if (attr == null) continue;

                    if (method.GetParameters().Length == 0)
                    {
                        method.Invoke(null, null);
                        count++;
                    }
                    else
                    {
                        Plugin.Log?.LogWarning(
                            $"[AttributeScanner] Skipping [RegisterCleanup] on " +
                            $"{type.Name}.{method.Name}: must be parameterless");
                    }
                }
            }
            Plugin.Log?.LogDebug($"[AttributeScanner] Invoked {count} cleanup methods");
        }

        #endregion

        #region ModObjects

        private static void CollectModObjectNames(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterModObjectAttribute>();
                if (attr != null)
                    s_modObjectNames.Add(attr.GameObjectName);
            }
        }

        /// <summary>Returns all mod-created GameObject names for hot-reload detection.</summary>
        public static HashSet<string> GetModObjectNames(Assembly assembly)
        {
            if (s_modObjectNames.Count == 0)
                CollectModObjectNames(assembly);
            return s_modObjectNames;
        }

        #endregion

        #region Abilities

        private static void RegisterAbilities(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterAbilityAttribute>();
                if (attr == null) continue;

                if (!typeof(IAbility).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterAbility] " +
                        "but does not implement IAbility");
                    continue;
                }

                var ability = (IAbility)Activator.CreateInstance(type);
                s_abilities[attr.Id] = ability;
            }
        }

        /// <summary>Get a registered ability by id. Returns null if not found.</summary>
        public static IAbility GetAbility(string id)
        {
            s_abilities.TryGetValue(id, out var ability);
            return ability;
        }

        /// <summary>Get all registered abilities.</summary>
        public static IReadOnlyDictionary<string, IAbility> GetAllAbilities() => s_abilities;

        #endregion

        #region Passives

        private static void RegisterPassives(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterPassiveAttribute>();
                if (attr == null) continue;

                if (!typeof(IPassiveEffect).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterPassive] " +
                        "but does not implement IPassiveEffect");
                    continue;
                }

                var passive = (IPassiveEffect)Activator.CreateInstance(type);
                s_passives[attr.Id] = passive;
            }
        }

        /// <summary>Get a registered passive effect by id. Returns null if not found.</summary>
        public static IPassiveEffect GetPassive(string id)
        {
            s_passives.TryGetValue(id, out var passive);
            return passive;
        }

        /// <summary>Get all registered passive effects.</summary>
        public static IReadOnlyDictionary<string, IPassiveEffect> GetAllPassives() => s_passives;

        #endregion

        #region Tabs

        private static void RegisterTabs(Assembly assembly)
        {
            var tabTypes = new List<(Type type, RegisterTabAttribute attr)>();
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterTabAttribute>();
                if (attr == null) continue;

                if (!typeof(IVillagerTab).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterTab] " +
                        "but does not implement IVillagerTab");
                    continue;
                }

                tabTypes.Add((type, attr));
            }

            foreach (var (type, _) in tabTypes.OrderBy(t => t.attr.Order))
            {
                var tab = (IVillagerTab)Activator.CreateInstance(type);
                s_tabs.Add(tab);
                VillagerTabManager.RegisterTab(tab);
            }
        }

        /// <summary>Get all registered tabs (sorted by Order).</summary>
        public static IReadOnlyList<IVillagerTab> GetRegisteredTabs() => s_tabs;

        #endregion

        #region ListPanels

        private static void RegisterListPanels(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterListPanelAttribute>();
                if (attr == null) continue;

                if (!typeof(IListPanel).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterListPanel] " +
                        "but does not implement IListPanel");
                    continue;
                }

                var panel = (IListPanel)Activator.CreateInstance(type);
                s_panels.Add(panel);

                // Wire panel into its parent tab's static registration
                if (panel.ParentTab == "info")
                    UI.Tabs.InfoTab.RegisterPanel(panel);
                else if (panel.ParentTab == "debug")
                    UI.Tabs.DebugTab.RegisterPanel(panel);
            }
        }

        /// <summary>Get list panels matching a parent tab id.</summary>
        public static List<IListPanel> GetListPanels(string parentTab)
        {
            return s_panels.Where(p => p.ParentTab == parentTab).ToList();
        }

        #endregion

        #region ContextMenus

        private static void RegisterContextMenus(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterContextMenuAttribute>();
                if (attr == null) continue;

                if (!typeof(IContextMenu).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterContextMenu] " +
                        "but does not implement IContextMenu");
                    continue;
                }

                var menu = (IContextMenu)Activator.CreateInstance(type);
                s_contextMenus.Add(menu);
            }
        }

        /// <summary>Get all registered context menus.</summary>
        public static IReadOnlyList<IContextMenu> GetContextMenus() => s_contextMenus;

        #endregion

        #region Behaviors

        private static void RegisterBehaviors(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<RegisterBehaviorAttribute>();
                if (attr == null) continue;

                if (!typeof(IBehavior).IsAssignableFrom(type))
                {
                    Plugin.Log?.LogWarning(
                        $"[AttributeScanner] {type.Name} has [RegisterBehavior] " +
                        "but does not implement IBehavior");
                    continue;
                }

                var capturedType = type;
                s_behaviorCreators[attr.Tag] = ai =>
                    (IBehavior)Activator.CreateInstance(capturedType, ai);
            }
        }

        /// <summary>
        /// Create an IBehavior instance by tag, using the registered constructor.
        /// Returns null if the tag is not registered.
        /// </summary>
        public static IBehavior CreateBehavior(string tag, VillagerAI owner)
        {
            if (s_behaviorCreators.TryGetValue(tag, out var creator))
                return creator(owner);
            return null;
        }

        /// <summary>Check if a behavior tag is registered.</summary>
        public static bool HasBehavior(string tag) => s_behaviorCreators.ContainsKey(tag);

        #endregion
    }
}
