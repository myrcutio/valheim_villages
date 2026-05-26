using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI;
using Object = UnityEngine.Object;

namespace ValheimVillages
{
    /// <summary>
    ///     Handles hot reload cleanup for existing game objects and static state.
    ///     When the mod is hot-reloaded via ScriptEngine, the old assembly stays
    ///     in memory and its MonoBehaviour instances keep running.  This helper
    ///     identifies stale objects by comparing their Type.Assembly against the
    ///     current (newly loaded) assembly, then destroys them.
    ///     IMPORTANT: Object.Destroy is deferred until end-of-frame, so old
    ///     components continue to execute Update/LateUpdate for the remainder
    ///     of the frame.  To prevent interference we DISABLE components and
    ///     DEACTIVATE GameObjects before calling Destroy.
    /// </summary>
    public static class HotReloadHelper
    {
        /// <summary>
        ///     Prefix used by mod-created GameObjects (station templates,
        ///     tab buttons, work order button, prefabs, etc.).
        /// </summary>
        private const string ModPrefix = "VV_";

        private const string ModPrefabPrefix = "vv_";

        /// <summary>
        ///     Station name prefix for CraftingStation components created by
        ///     our VillagerStation.  Used to identify and remove orphaned
        ///     Valheim CraftingStation components on NPC GameObjects.
        /// </summary>
        private const string VillagerStationPrefix = "$vv_";

        private static readonly Assembly CurrentAssembly = typeof(HotReloadHelper).Assembly;

        /// <summary>
        ///     Master cleanup entry point.  Call at the top of Plugin.Awake
        ///     during a hot reload (ObjectDB/ZNetScene already exist).
        /// </summary>
        public static void FullCleanup()
        {
            Plugin.Log?.LogInfo("[HotReload] Starting full cleanup...");

            ResetAllStaticState();

            var staleComponents = DestroyStaleComponents();
            var orphanedObjects = DestroyOrphanedModObjects();

            Plugin.Log?.LogInfo(
                "[HotReload] Cleanup complete: " +
                $"{staleComponents} stale component(s), " +
                $"{orphanedObjects} orphaned GameObject(s) destroyed.");
        }

        /// <summary>
        ///     d
        ///     Clear all static registries that hold per-session state.
        ///     Called before any object scanning so callbacks don't fire
        ///     on stale references during destruction.
        /// </summary>
        private static void ResetAllStaticState()
        {
            AttributeScanner.InvokeAllCleanup(CurrentAssembly);
            Plugin.Log?.LogDebug("[HotReload] All static registries cleared.");
        }

        /// <summary>
        ///     Scan every MonoBehaviour in the scene.  Any whose type lives
        ///     in our namespace but belongs to a different assembly (the old
        ///     one) is stale.  We disable it immediately (stopping its
        ///     Update/LateUpdate from running) and then Destroy it.
        /// </summary>
        private static int DestroyStaleComponents()
        {
            var destroyed = 0;

#pragma warning disable CS0618
            var allBehaviours = Object.FindObjectsOfType<MonoBehaviour>();
#pragma warning restore CS0618

            foreach (var mb in allBehaviours)
            {
                if (mb == null) continue;

                var type = mb.GetType();
                var fullName = type.FullName;
                if (fullName == null) continue;

                // Only touch our mod's types
                if (!fullName.StartsWith("ValheimVillages.")) continue;

                // Same assembly = current reload, leave it alone
                if (type.Assembly == CurrentAssembly) continue;

                Plugin.Log?.LogDebug(
                    $"[HotReload] Destroying stale component: {fullName} " +
                    $"on \"{mb.gameObject.name}\"");

                // Disable first so Update/LateUpdate stop running this frame
                mb.enabled = false;
                Object.Destroy(mb);
                destroyed++;
            }

            return destroyed;
        }

        /// <summary>
        ///     After stale components are destroyed, some GameObjects may
        ///     be orphaned — they were created solely by the mod (UI
        ///     singletons, station templates, cloned tab buttons, etc.)
        ///     and now have no current-assembly components driving them.
        ///     Destroy the entire GameObject (and its children) to prevent
        ///     ghost objects accumulating across reloads.
        ///     We identify mod objects by exact name match or name prefix.
        ///     We skip anything that still has a current-assembly component
        ///     (it was just re-created by the new code).
        ///     We also skip NPC GameObjects (they have ZNetView + ZDO data)
        ///     because those are handled separately by FixupExistingNPCs.
        /// </summary>
        private static int DestroyOrphanedModObjects()
        {
            var destroyed = 0;

#pragma warning disable CS0618
            var allTransforms = Object.FindObjectsOfType<Transform>();
#pragma warning restore CS0618

            // Collect candidates first to avoid mutating during iteration
            var toDestroy = new List<GameObject>();

            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                var go = t.gameObject;
                var name = go.name;

                var isModObject =
                    AttributeScanner.GetModObjectNames(CurrentAssembly).Contains(name) ||
                    name.StartsWith(ModPrefix) ||
                    name.StartsWith(ModPrefabPrefix);

                if (!isModObject) continue;

                // Skip NPC GameObjects — they're handled by FixupExistingNPCs
                if (go.GetComponent<ZNetView>() != null) continue;

                // Skip if any current-assembly component is present
                // (means it was just created by new code)
                if (HasCurrentAssemblyComponent(go)) continue;

                toDestroy.Add(go);
            }

            foreach (var go in toDestroy)
            {
                if (go == null) continue;

                Plugin.Log?.LogDebug(
                    $"[HotReload] Destroying orphaned mod object: \"{go.name}\"");

                // Deactivate immediately so child components stop running
                go.SetActive(false);
                Object.Destroy(go);
                destroyed++;
            }

            return destroyed;
        }

        /// <summary>
        ///     Check whether a GameObject has any MonoBehaviour from the
        ///     current (newly loaded) assembly.
        /// </summary>
        private static bool HasCurrentAssemblyComponent(GameObject go)
        {
            var components = go.GetComponents<MonoBehaviour>();
            foreach (var c in components)
            {
                if (c == null) continue;
                var type = c.GetType();
                if (type.FullName != null &&
                    type.FullName.StartsWith("ValheimVillages.") &&
                    type.Assembly == CurrentAssembly)
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     Find all existing villager NPCs and replace their mod components
        ///     with fresh ones from the new assembly.  Called after FullCleanup
        ///     so stale components are already gone; this re-attaches new ones.
        ///     Also cleans up orphaned CraftingStation components that were
        ///     added by our VillagerStation in previous reloads.  Those are
        ///     Valheim types (not in our namespace), so the stale component
        ///     sweep doesn't catch them — they accumulate silently.
        /// </summary>
        public static void FixupExistingNPCs()
        {
            var fixedCount = 0;

#pragma warning disable CS0618
            var znetViews = Object.FindObjectsOfType<ZNetView>();
#pragma warning restore CS0618

            foreach (var nview in znetViews)
            {
                if (nview == null) continue;

                var zdo = nview.GetZDO();
                if (zdo == null) continue;

                // Check if this is one of our NPCs
                var villagerType = zdo.GetString("vv_villager_type");
                if (string.IsNullOrEmpty(villagerType)) continue;

                // Get bed position from ZDO
                var bedPos = zdo.GetVec3("vv_bed_position", Vector3.zero);
                if (bedPos == Vector3.zero)
                {
                    Plugin.Log?.LogWarning(
                        $"[HotReload] NPC at {nview.transform.position} " +
                        "has no bed position stored, skipping");
                    continue;
                }

                // Get or create unique ID
                var uniqueId = zdo.GetString("vv_villager_id");
                if (string.IsNullOrEmpty(uniqueId))
                {
                    uniqueId = Guid.NewGuid().ToString();
                    zdo.Set("vv_villager_id", uniqueId);
                }

                // Remove orphaned CraftingStation components from previous
                // reloads before restoring.  VillagerStation.Initialize adds
                // a CraftingStation (Valheim type), which the stale-component
                // sweep can't detect.  We identify ours by station name prefix.
                RemoveOrphanedCraftingStations(nview.gameObject);

                // Restore NPC state: strips native components, adds mod components
                VillagerRestoration.Restore(nview.gameObject, zdo);

                // Register with Villager.AI manager (pending; active when VillagerAI component exists)
                VillagerAIManager.Register(uniqueId, bedPos);

                fixedCount++;
                Plugin.Log?.LogDebug(
                    $"[HotReload] Fixed up NPC at {nview.transform.position}");
            }

            if (fixedCount > 0)
                Plugin.Log?.LogInfo(
                    $"[HotReload] Fixed up {fixedCount} existing NPC(s)");
        }

        /// <summary>
        ///     Remove CraftingStation components that were created by our
        ///     VillagerStation in previous assembly loads.  We identify them
        ///     by checking if their m_name starts with the "$vv_" prefix.
        /// </summary>
        private static void RemoveOrphanedCraftingStations(GameObject go)
        {
            var stations = go.GetComponents<CraftingStation>();
            foreach (var station in stations)
            {
                if (station == null) continue;
                if (station.m_name != null &&
                    station.m_name.StartsWith(VillagerStationPrefix))
                {
                    Plugin.Log?.LogDebug(
                        "[HotReload] Removing orphaned CraftingStation " +
                        $"'{station.m_name}' on \"{go.name}\"");
                    station.enabled = false;
                    Object.Destroy(station);
                }
            }
        }
    }
}