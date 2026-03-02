using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue;
using ValheimVillages.Villager.AI;

[assembly: AssemblyTitle("Valheim Villages")]
[assembly: AssemblyDescription("A village-building and NPC management mod for Valheim")]
[assembly: AssemblyCompany("Myrcutio")]
[assembly: AssemblyProduct("ValheimVillages")]
[assembly: AssemblyCopyright("Copyright © Myrcutio 2026")]
[assembly: AssemblyVersion("0.1.0")]
[assembly: AssemblyFileVersion("0.1.0")]

namespace ValheimVillages
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.valheimvillages.mod";
        public const string PluginName = "Valheim Villages";
        public const string PluginVersion = "0.1.0";

        private static ManualLogSource _logger;
        private Harmony _harmony;
        private static bool _recipeRefreshEnqueued;
        private static bool _hnaPartitionEnqueued;

        public static ManualLogSource Log => _logger;

        private void Awake()
        {
            _logger = Logger;
            _logger.LogInfo($"{PluginName} v{PluginVersion} loading...");
            
            // Clean up any previous patches with our GUID (hot reload support)
            Harmony.UnpatchID(PluginGUID);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            // Register custom localization tokens (SetupLanguage already ran)
            Patches.LocalizationPatch.RegisterTokens();

            // Hot reload support: if the game world is already loaded,
            // run full cleanup BEFORE re-registering anything.
            // FullCleanup invokes [RegisterCleanup] methods (e.g. TaskHandlerRegistry.Clear)
            // so it must run before ScanAndRegister to avoid wiping freshly-registered handlers.
            bool isHotReload = ObjectDB.instance != null &&
                               ObjectDB.instance.m_items.Count > 0;

            if (isHotReload)
            {
                _logger.LogInfo("Hot reload detected — running full cleanup");
                HotReloadHelper.FullCleanup();
            }

            // Clear any stale task handlers before scanning
            // (redundant after FullCleanup on hot reload, but needed on first load)
            TaskHandlerRegistry.Clear();

            // Scan assembly for all registration attributes
            // (registers dev commands, task handlers, tabs, panels, abilities, etc.)
            AttributeScanner.ScanAndRegister(typeof(Plugin).Assembly);

            // Tabs, list panels, and context menus are now auto-registered via
            // [RegisterTab], [RegisterListPanel], [RegisterContextMenu] attributes
            // in AttributeScanner.ScanAndRegister() above.

            // Hot reload support: re-register items and prefabs
            if (isHotReload)
            {
                _logger.LogInfo("Hot reload — re-registering items in ObjectDB");
                Items.ItemFactory.RegisterAll(ObjectDB.instance);
                Items.VirtualRecipes.VirtualRecipeLoader.RegisterAll(ObjectDB.instance);
                AttributeScanner.InvokeObjectDBRegistrations(typeof(Plugin).Assembly, ObjectDB.instance);
            }

            if (isHotReload && ZNetScene.instance != null)
            {
                _logger.LogInfo("Hot reload — re-registering prefabs in ZNetScene");
                Items.ItemFactory.RegisterAllInZNetScene(ZNetScene.instance);

                _logger.LogInfo("Hot reload — fixing up existing NPC components");
                HotReloadHelper.FixupExistingNPCs();
            }

            _logger.LogInfo($"{PluginName} loaded successfully!");

            // Schedule integration tests via task queue so they run after fixtures are ready
            if (isHotReload && Testing.ModTestRunner.AutoRunEnabled)
            {
                _logger.LogInfo("Scheduling integration tests (will defer until fixtures are ready)...");
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = TaskQueue.Handlers.IntegrationTestHandler.TaskNameConst,
                    SourceId = "system",
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = 120f,
                    Attributes = new System.Collections.Generic.Dictionary<string, string>()
                });
            }
        }

        private void Update()
        {
            // After world load, enqueue one low-priority recheck of discovered recipes (cultivator + cooking)
            if (!_recipeRefreshEnqueued &&
                ObjectDB.instance != null &&
                ZNetScene.instance != null &&
                Time.realtimeSinceStartup > 3f)
            {
                _recipeRefreshEnqueued = true;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "recipe_discovery_refresh",
                    SourceId = "system",
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>()
                });
                _logger?.LogDebug("[Valheim Villages] Enqueued recipe_discovery_refresh (post–world load)");
            }

            // Enqueue HNA partition (low priority) so village region graph is built without overwhelming other tasks
            if (!_hnaPartitionEnqueued &&
                ObjectDB.instance != null &&
                ZNetScene.instance != null &&
                Time.realtimeSinceStartup > 5f)
            {
                _hnaPartitionEnqueued = true;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "hna_partition",
                    SourceId = "system",
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>()
                });
                _logger?.LogInfo("[Valheim Villages] Enqueued hna_partition (low priority)");
            }

            GlobalTaskQueue.ProcessBatch();

            // Tick Villager.AI.VillagerAI instances (BaseAI path; no MonsterAI)
            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai != null)
                    ai.UpdateAI(Time.deltaTime);
            }
        }
    }
}
