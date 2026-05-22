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
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

[assembly: AssemblyTitle("Valheim Villages")]
[assembly: AssemblyDescription("A village-building and NPC management mod for Valheim")]
[assembly: AssemblyCompany("Myrcutio")]
[assembly: AssemblyProduct("ValheimVillages")]
[assembly: AssemblyCopyright("Copyright © Myrcutio 2026")]
[assembly: AssemblyVersion("0.1.0")]
[assembly: AssemblyFileVersion("0.1.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ValheimVillages.Tests")]

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
        private static bool _regionPartitionEnqueued;
        /// <summary>
        /// <see cref="Time.realtimeSinceStartup"/> at the moment a hot reload
        /// was detected, or 0 on cold start. Used by the Update-loop's
        /// hna_partition gate so the 5-second settling delay restarts after
        /// every hot reload (otherwise the partition would fire instantly
        /// against half-reregistered world state).
        /// </summary>
        private static float _hotReloadAt;
        // #endregion

        public static ManualLogSource Log => _logger;
        public static Plugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            _logger = Logger;
            bool isHotReload = ObjectDB.instance != null &&
                               ObjectDB.instance.m_items.Count > 0;
            DebugLog.BeginCycle(isHotReload);
            RegionGraphPersistence.LogAction = msg => _logger.LogInfo(msg);
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

                // Hot-reload iteration QoL: arm the Update-loop's hna_partition
                // auto-enqueue to fire again. The 5-second settling delay
                // restarts from _hotReloadAt so prefab re-registration and
                // polygon scope have time to establish before we sample world
                // state.
                _hotReloadAt = Time.realtimeSinceStartup;
                _regionPartitionEnqueued = false;
                _logger.LogInfo("Hot reload — armed hna_partition (will fire 5s after settle)");
            }

            _logger.LogInfo($"{PluginName} loaded successfully!");

            if (isHotReload)
            {
                DebugLog.Capture("hot-reload");
            }

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
            if (!_regionPartitionEnqueued &&
                ObjectDB.instance != null &&
                ZNetScene.instance != null &&
                Time.realtimeSinceStartup > _hotReloadAt + 5f)
            {
                _regionPartitionEnqueued = true;
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

            // Debounced HNA rebuild on structural changes (piece placed/removed)
            const float hnaRebuildDebounce = 10f;
            if (Patches.PieceChangePatch.IsDirty &&
                Time.realtimeSinceStartup - Patches.PieceChangePatch.LastStructureChangeTime > hnaRebuildDebounce)
            {
                Patches.PieceChangePatch.IsDirty = false;
                _regionPartitionEnqueued = false;
                _logger?.LogInfo("[Valheim Villages] Structure change detected, will re-enqueue hna_partition");
            }

            GlobalTaskQueue.ProcessBatch();

            PathDebugRenderer.AutoEnable();

            if (RegionGraph.IsAnyAvailable && !NavMeshLinkPlacer.LinkCandidatesReady)
                NavMeshLinkPlacer.ComputeLinkCandidates();

            if (RegionGraph.IsAnyAvailable && !NavMeshLinkPlacer.HasLinks)
                NavMeshLinkPlacer.PlaceLinks();

            // Tick Villager.AI.VillagerAI instances (BaseAI path; no MonsterAI)
            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai != null)
                    ai.UpdateAI(Time.deltaTime);
            }
        }
    }
}
