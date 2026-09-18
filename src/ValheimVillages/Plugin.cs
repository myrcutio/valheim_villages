using System;
using System.Collections.Generic;
using System.Globalization;
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
using ValheimVillages.Villages.Entity;

[assembly: AssemblyTitle("Valheim Villages")]
[assembly: AssemblyDescription("A village-building and NPC management mod for Valheim")]
[assembly: AssemblyCompany("Myrcutio")]
[assembly: AssemblyProduct("ValheimVillages")]
[assembly: AssemblyCopyright("Copyright © Myrcutio 2026")]
[assembly: AssemblyVersion("0.3.0")]
[assembly: AssemblyFileVersion("0.3.0")]
[assembly: InternalsVisibleTo("ValheimVillages.Tests")]

namespace ValheimVillages
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.valheimvillages.mod";
        public const string PluginName = "Valheim Villages";
        public const string PluginVersion = "0.3.0";

        private static bool _recipeRefreshEnqueued;
        private static bool _recordIndexEnqueued;
        private static bool _villageIndexEnqueued;

        /// <summary>Throttle (realtime seconds) for the per-village navmesh-bake sweep in Update.</summary>
        private static float _lastBakeSweep;

        /// <summary>
        ///     How far outside a village's published footprint a structural change still
        ///     counts as that village's. Matches RegionPartitionHandler.RegionBuildRadius:
        ///     the bake reaches that far past the anchors, so a piece placed within it is
        ///     one the next partition will absorb — an extension wing must dirty the village
        ///     it extends, and the footprint it will grow into does not exist yet.
        /// </summary>
        private const float StructureChangeMargin = 30f;

        /// <summary>villageId → realtime when its hna_partition was last enqueued by the bake
        /// sweep, so we don't spam the queue while a bake is in flight / settling.</summary>
        private static readonly Dictionary<string, float> _villageBakeEnqueuedAt = new();

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
        ///     Wall-clock time this assembly's <see cref="Plugin" /> type was firstw
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
                _villageBakeEnqueuedAt.Clear();
                _lastBakeSweep = 0f;
                _recordIndexEnqueued = false;
                _villageIndexEnqueued = false;
                Log.LogInfo("Hot reload — armed per-village navmesh bake sweep (will fire 5s after settle)");
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
            // Register the server-authoritative villager-spawn RPC handler on the current
            // ZRoutedRpc (recreated per world session). Cheap no-op once registered.
            Villager.VillagerRecruitRpc.EnsureRegistered();
            Villager.WorkOrderConfigRpc.EnsureRegistered();
            Villager.VillagerPauseRpc.EnsureRegistered();
            Villages.VillageCleanupRpc.EnsureRegistered();
            Items.Fragments.FragmentQuestRpc.EnsureRegistered();

            // Deliver surface rescue-quest rewards on arrival (interior dungeons spawn via the
            // EnvMan dungeon-entry hook instead). Throttled internally; no-op on the dedicated
            // server, which has no local player.
            Items.Fragments.RescueQuestTracker.TickArrival(Player.m_localPlayer);

            // Raise/clear the "!" over villagers with something the player must fix (storage
            // nearly full, an order missing an ingredient). Throttled internally; no-op on the
            // dedicated server, which has no local player to show it to.
            UI.Alerts.VillageAlertMonitor.Tick();

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
            // Host-only: minting/migrating is an authoritative write — a client minting a
            // record would create a client-owned (and possibly duplicate) carrier. Clients
            // receive records via ZDO replication.
            if (!_recordIndexEnqueued &&
                ZNet.instance != null && ZNet.instance.IsServer() &&
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

            // Proactive PER-VILLAGE navmesh bake sweep. The region GRAPH replicates via the
            // village ZDO blob, but the Unity navmesh is a per-peer runtime structure that is
            // NOT replicated — so a villager that already HAS a graph (hydrated from the blob)
            // still cannot path until its village has been baked on THIS peer. The patrol's
            // "graph missing -> request partition" trigger therefore never fires on a peer that
            // received the graph by replication; we must bake proactively here.
            //
            // Runs on BOTH peers, gated by zone-loaded: a dedicated server keeps every village
            // zone loaded (VillageZoneLoadingPatch) so it bakes them ALL (the multi-instance
            // holder keeps them coexisting); a client only has the player's village zone loaded
            // so it bakes just that one (which it needs for recruit seed resolution).
            if (ObjectDB.instance != null && ZNetScene.instance != null && ZoneSystem.instance != null &&
                Time.realtimeSinceStartup > _hotReloadAt + 5f &&
                Time.realtimeSinceStartup - _lastBakeSweep > 2f)
            {
                _lastBakeSweep = Time.realtimeSinceStartup;

                // A structural change (piece placed/removed, terrain edited) invalidates the
                // affected bake. Each change carries its own XZ footprint, so only villages
                // that change could have touched rebuild — a repartition costs ~700ms per
                // village (PartitionProfile), and this used to charge every loaded village
                // for every change anywhere in the world.
                var changesSettled = PieceChangePatch.IsDirty &&
                    Time.realtimeSinceStartup - PieceChangePatch.LastStructureChangeTime > 3f;
                var changesConsumed = true;
                if (changesSettled)
                    Log?.LogInfo(
                        $"[Valheim Villages] {PieceChangePatch.PendingRegions.Count} settled structure " +
                        "change(s); matching against village footprints");

                foreach (var village in VillageRegistry.EnumerateAll())
                {
                    var id = village.VillageId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var anchor = village.Anchor;
                    if (anchor == Vector3.zero) continue;
                    // An unloaded village needs no retention bookkeeping for changes it
                    // missed: a structural change is recorded by THIS peer's patches, which
                    // only fire for geometry this peer is simulating — so a pending change
                    // inside an unloaded village cannot exist here. Its cached verdicts stay
                    // valid across the unload and are reused when it comes back.
                    if (!ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(anchor))) continue;

                    // Does any settled change land in (or just outside) THIS village? A village
                    // that has never partitioned has no footprint yet; it is picked up by the
                    // un-baked branch below, which is what builds its first graph.
                    float fpMinX = 0f, fpMinZ = 0f, fpMaxX = 0f, fpMaxZ = 0f;
                    var structureDirty = false;
                    if (changesSettled && village.TryGetFootprint(
                            out fpMinX, out fpMinZ, out fpMaxX, out fpMaxZ))
                        structureDirty = PieceChangePatch.Affects(
                            fpMinX, fpMinZ, fpMaxX, fpMaxZ, StructureChangeMargin);

                    // Skip villages already baked on this peer unless a structure change
                    // invalidated them. (Re-bake retries every ~15s while a village stays
                    // un-baked, e.g. its zone is still streaming.)
                    if (!structureDirty)
                    {
                        if (NavMeshBakeManager.HasVillage(id)) continue;
                        if (_villageBakeEnqueuedAt.TryGetValue(id, out var last) &&
                            Time.realtimeSinceStartup - last < 15f) continue;
                    }

                    var attributes = new Dictionary<string, string>
                    {
                        { "village_id", id },
                        { "anchor_x", anchor.x.ToString("F2", CultureInfo.InvariantCulture) },
                        { "anchor_z", anchor.z.ToString("F2", CultureInfo.InvariantCulture) },
                    };

                    // An incremental partition carries the rect it must re-probe. A village
                    // dirtied for any other reason (never baked yet) gets no rect and so
                    // rebuilds in full — the cached triangle verdicts it does not have.
                    if (structureDirty)
                    {
                        var rect = PieceChangePatch.UnionAffecting(
                            fpMinX, fpMinZ, fpMaxX, fpMaxZ, StructureChangeMargin);
                        attributes["dirty_min_x"] = rect.MinX.ToString("R", CultureInfo.InvariantCulture);
                        attributes["dirty_min_z"] = rect.MinZ.ToString("R", CultureInfo.InvariantCulture);
                        attributes["dirty_max_x"] = rect.MaxX.ToString("R", CultureInfo.InvariantCulture);
                        attributes["dirty_max_z"] = rect.MaxZ.ToString("R", CultureInfo.InvariantCulture);
                    }

                    _villageBakeEnqueuedAt[id] = Time.realtimeSinceStartup;
                    var accepted = GlobalTaskQueue.Enqueue(new VillagerTask
                    {
                        Name = "hna_partition",
                        SourceId = "system",
                        Priority = TaskPriority.Low,
                        TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                        Attributes = attributes,
                    });

                    // A rejected enqueue means a partition for this village is already
                    // queued — carrying an OLDER rect that does not cover these changes.
                    // Keep them pending so the next sweep re-offers them; dropping them
                    // would leave this geometry never re-probed.
                    if (!accepted && structureDirty) changesConsumed = false;

                    Log?.LogInfo($"[Valheim Villages] Enqueued hna_partition for village {id} " +
                                 $"(baked={NavMeshBakeManager.HasVillage(id)}, " +
                                 $"structureDirty={structureDirty}, accepted={accepted})");
                }

                // Consume the changes only after EVERY village has been matched against
                // them, or the first village in the enumeration eats them all.
                if (changesSettled && changesConsumed) PieceChangePatch.ClearPending();
            }

            GlobalTaskQueue.ProcessBatch();

            PathDebugRenderer.AutoEnable();
            TaskQueue.PartitionRunner.EnsureHost();

            // Keep a non-carving NavMeshObstacle on EVERY player so villager RVO
            // steers around them (a player isn't a NavMeshAgent, so it's otherwise
            // invisible to their avoidance). Must cover all players, not just the
            // local one: villagers are server-simulated and a dedicated server has
            // no local player — the obstacle has to live on the connected players'
            // server-side ghosts, where the agents actually run.
            Villager.AI.Navigation.PlayerAvoidanceObstacle.Tick();

            // Tick Villager.AI.VillagerAI instances (BaseAI path; no MonsterAI)
            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
                if (ai != null)
                    ai.UpdateAI(Time.deltaTime);
        }
    }
}