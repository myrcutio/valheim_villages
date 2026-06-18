using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Items;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Patches;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.Testing;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

[assembly: AssemblyTitle("Valheim Villages")]
[assembly: AssemblyDescription("A village-building and NPC management mod for Valheim")]
[assembly: AssemblyCompany("Myrcutio")]
[assembly: AssemblyProduct("ValheimVillages")]
[assembly: AssemblyCopyright("Copyright © Myrcutio 2026")]
[assembly: AssemblyVersion("0.1.1")]
[assembly: AssemblyFileVersion("0.1.1")]
[assembly: InternalsVisibleTo("ValheimVillages.Tests")]

namespace ValheimVillages
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.valheimvillages.mod";
        public const string PluginName = "Valheim Villages";
        public const string PluginVersion = "0.1.1";

        private static bool _recipeRefreshEnqueued;
        private static bool _regionPartitionEnqueued;
        private static bool _recordIndexEnqueued;
        private static bool _villageIndexEnqueued;

        /// <summary>
        ///     <see cref="Time.realtimeSinceStartup" /> at the moment a hot reload
        ///     was detected, or 0 on cold start. Used by the Update-loop's
        ///     hna_partition gate so the 5-second settling delay restarts after
        ///     every hot reload (otherwise the partition would fire instantly
        ///     against half-reregistered world state).
        /// </summary>
        private static float _hotReloadAt;

        private Harmony _harmony;
        // #endregion

        public static ManualLogSource Log { get; private set; }

        public static Plugin Instance { get; private set; }

        /// <summary>
        ///     Wall-clock time this assembly's <see cref="Plugin" /> type was first
        ///     touched (≈ when ScriptEngine loaded this assembly). Because a hot
        ///     reload loads a brand-new assembly, this field is re-initialized on
        ///     every reload — so a dev command that prints it advancing confirms
        ///     the reload pipeline fired. See <c>vv_reloadinfo</c>. UTC so it's
        ///     comparable across processes/hosts in different local zones.
        /// </summary>
        public static readonly DateTime AssemblyLoadedAt = DateTime.UtcNow;

        /// <summary>True if the most recent load was a hot reload (world already up).</summary>
        public static bool LastLoadWasHotReload { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            var isHotReload = ObjectDB.instance != null &&
                              ObjectDB.instance.m_items.Count > 0;
            LastLoadWasHotReload = isHotReload;
            DebugLog.BeginCycle(isHotReload);
            RegionGraphPersistence.LogAction = msg => Log.LogInfo(msg);
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            // Clean up any previous patches with our GUID (hot reload support)
            Harmony.UnpatchID(PluginGUID);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            // Register custom localization tokens (SetupLanguage already ran)
            LocalizationPatch.RegisterTokens();

            // Hot reload support: if the game world is already loaded,
            // run full cleanup BEFORE re-registering anything.
            // FullCleanup invokes [RegisterCleanup] methods (e.g. TaskHandlerRegistry.Clear)
            // so it must run before ScanAndRegister to avoid wiping freshly-registered handlers.
            if (isHotReload)
            {
                Log.LogInfo("Hot reload detected — running full cleanup");
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
                Log.LogInfo("Hot reload — re-registering items in ObjectDB");
                ItemFactory.RegisterAll(ObjectDB.instance);
                VirtualRecipeLoader.RegisterAll(ObjectDB.instance);
                AttributeScanner.InvokeObjectDBRegistrations(typeof(Plugin).Assembly, ObjectDB.instance);
            }

            if (isHotReload && ZNetScene.instance != null)
            {
                Log.LogInfo("Hot reload — re-registering prefabs in ZNetScene");
                // ItemFactory + PieceFactory registration runs via deferred [RequireObjectDB]
                // tasks (ObjectDB is already alive on hot reload, so they execute next tick).
                AttributeScanner.EnqueueObjectDBDependentTasks();
                // [RequireAgent] setup runs once the slot-31 bake is installed; on hot reload
                // a prior bake is usually still live, so these execute promptly too.
                AttributeScanner.EnqueueAgentDependentTasks();
                Villager.Records.RecordPrefabFactory.RegisterInZNetScene(ZNetScene.instance);
                Villages.Entity.VillagePrefabFactory.RegisterInZNetScene(ZNetScene.instance);

                Log.LogInfo("Hot reload — fixing up existing NPC components");
                HotReloadHelper.FixupExistingNPCs();

                // Placed registry pieces lose their mod-owned RegistryInteract to the
                // stale-component sweep; re-attach it so E opens the tabbed registry UI
                // again instead of the vanilla CraftingStation's tab-less menu.
                Log.LogInfo("Hot reload — fixing up placed registry stations");
                HotReloadHelper.FixupExistingRegistries();

                // Hot-reload iteration QoL: arm the Update-loop's hna_partition
                // auto-enqueue to fire again. The 5-second settling delay
                // restarts from _hotReloadAt so prefab re-registration and
                // polygon scope have time to establish before we sample world
                // state.
                _hotReloadAt = Time.realtimeSinceStartup;
                _regionPartitionEnqueued = false;
                _recordIndexEnqueued = false;
                _villageIndexEnqueued = false;
                Log.LogInfo("Hot reload — armed hna_partition (will fire 5s after settle)");
            }

            Log.LogInfo($"{PluginName} loaded successfully!");

            // Wipe stale incident dumps from any prior session — world state
            // changed across hot reload, so past incidents are noise. Done
            // before any villager can trigger an incident write.
            Diagnostics.IncidentRecorder.ClearOnLoad();

            // Dev: freeze the day/night cycle at noon so screenshots and live
            // debugging happen under consistent lighting. Toggleable via
            // vv_freezetime. TODO: gate or remove before release — see
            // FreezeTime.AutoFreezeOnLoad.
            Diagnostics.FreezeTime.ApplyAutoFreeze();

            // No hot-reload capture: the auto-repartition that follows shortly
            // after rebuilds the region graph from scratch, so the post-reload
            // PNG would only ever show stale state. The repartition trigger
            // (orchestrated, below) captures the canonical post-rebuild view.

            // Schedule integration tests via task queue so they run after fixtures are ready
            if (isHotReload && ModTestRunner.AutoRunEnabled)
            {
                Log.LogInfo("Scheduling integration tests (will defer until fixtures are ready)...");
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = IntegrationTestHandler.TaskNameConst,
                    SourceId = "system",
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = 120f,
                    Attributes = new Dictionary<string, string>(),
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
                    Attributes = new Dictionary<string, string>(),
                });
                Log?.LogDebug("[Valheim Villages] Enqueued recipe_discovery_refresh (post–world load)");
            }

            // After world load, index/migrate villager records once (mints records for
            // legacy villagers that predate the record table so the roster + nav see them).
            if (!_recordIndexEnqueued &&
                ObjectDB.instance != null &&
                ZNetScene.instance != null &&
                Time.realtimeSinceStartup > 3f)
            {
                _recordIndexEnqueued = true;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "villager_record_index",
                    SourceId = "system",
                    Priority = TaskPriority.High,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>(),
                });
                Log?.LogDebug("[Valheim Villages] Enqueued villager_record_index (post–world load)");
            }

            // After world load, rebuild the live village cache and hydrate each
            // village's HNA graph from its durable ZDO blob (the load-time counterpart
            // to the partition's save).
            if (!_villageIndexEnqueued &&
                ObjectDB.instance != null &&
                ZNetScene.instance != null &&
                Time.realtimeSinceStartup > 3f)
            {
                _villageIndexEnqueued = true;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "village_index",
                    SourceId = "system",
                    Priority = TaskPriority.High,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>(),
                });
                Log?.LogDebug("[Valheim Villages] Enqueued village_index (post–world load)");
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
                    Attributes = new Dictionary<string, string>(),
                });
                Log?.LogInfo("[Valheim Villages] Enqueued hna_partition (low priority)");
            }

            // Debounced HNA rebuild on structural changes (piece placed/removed)
            const float hnaRebuildDebounce = 10f;
            if (PieceChangePatch.IsDirty &&
                Time.realtimeSinceStartup - PieceChangePatch.LastStructureChangeTime > hnaRebuildDebounce)
            {
                PieceChangePatch.IsDirty = false;
                _regionPartitionEnqueued = false;
                Log?.LogInfo("[Valheim Villages] Structure change detected, will re-enqueue hna_partition");
            }

            GlobalTaskQueue.ProcessBatch();

            PathDebugRenderer.AutoEnable();

            // Keep a non-carving NavMeshObstacle on the local player so villager
            // RVO steers around the player too (the player isn't a NavMeshAgent,
            // so it's otherwise invisible to their avoidance).
            Villager.AI.Navigation.PlayerAvoidanceObstacle.Tick();

            // Tick Villager.AI.VillagerAI instances (BaseAI path; no MonsterAI)
            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
                if (ai != null)
                    ai.UpdateAI(Time.deltaTime);
        }
    }
}