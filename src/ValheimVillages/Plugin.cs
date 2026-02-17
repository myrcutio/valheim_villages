using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.Handlers;

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
        private static bool _navMeshRebakeEnqueued;
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

            // Register task queue handlers
            TaskHandlerRegistry.Clear();
            TaskHandlerRegistry.Register(new ContainerScanHandler());
            TaskHandlerRegistry.Register(new WorkOrderScanHandler());
            TaskHandlerRegistry.Register(new BreachCheckHandler());
            TaskHandlerRegistry.Register(new POIDiscoveryHandler());
            TaskHandlerRegistry.Register(new POIValidationHandler());
            TaskHandlerRegistry.Register(new RecipeDiscoveryRefreshHandler());
            TaskHandlerRegistry.Register(new HnaPartitionHandler());
            TaskHandlerRegistry.Register(new NavMeshRebakeHandler());

            // Register debug console commands
            Abilities.VillagerAbilityManager.RegisterConsoleCommands();

            // Hot reload support: if the game world is already loaded,
            // run full cleanup before re-registering anything.
            bool isHotReload = ObjectDB.instance != null &&
                               ObjectDB.instance.m_items.Count > 0;
            if (isHotReload)
            {
                _logger.LogInfo("Hot reload detected — running full cleanup");
                HotReloadHelper.FullCleanup();
            }

            // Register villager UI tabs (after cleanup so the old
            // VillagerTabManager singleton is gone)
            NPCs.AI.UI.Tabs.VillagerTabManager.RegisterTab(new NPCs.AI.UI.Tabs.InfoTab());
            NPCs.AI.UI.Tabs.VillagerTabManager.RegisterTab(new NPCs.AI.UI.Tabs.DebugTab());

            // Hot reload support: re-register items and prefabs
            if (isHotReload)
            {
                _logger.LogInfo("Hot reload — re-registering items in ObjectDB");
                Items.ItemFactory.RegisterAll(ObjectDB.instance);
                Items.VirtualRecipes.VirtualRecipeLoader.RegisterAll(ObjectDB.instance);
                Abilities.MountainStride.MountainStride_ObjectDB_Patch.RegisterStatusEffect(ObjectDB.instance);
            }

            if (isHotReload && ZNetScene.instance != null)
            {
                _logger.LogInfo("Hot reload — re-registering prefabs in ZNetScene");
                Items.ItemFactory.RegisterAllInZNetScene(ZNetScene.instance);

                _logger.LogInfo("Hot reload — fixing up existing NPC components");
                HotReloadHelper.FixupExistingNPCs();
            }

            _logger.LogInfo($"{PluginName} loaded successfully!");
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

            // NavMesh rebake (low priority) for village pathfinding using Unity's public APIs
            if (!_navMeshRebakeEnqueued &&
                ObjectDB.instance != null &&
                ZNetScene.instance != null &&
                Time.realtimeSinceStartup > 5f)
            {
                _navMeshRebakeEnqueued = true;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = NavMeshRebakeTaskContract.TaskName,
                    SourceId = "village",
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>()
                });
                _logger?.LogInfo("[Valheim Villages] Enqueued navmesh_rebake (low priority)");
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
        }
    }
}
