using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI
{
    /// <summary>
    ///     Static registry for VillagerAI instances, keyed by unique ID.
    ///     Inspired by MobAILib's MobManager pattern.
    /// </summary>
    public static class VillagerAIManager
    {
        /// <summary>
        ///     All active villager AI instances, keyed by their unique ID.
        /// </summary>
        public static readonly Dictionary<string, VillagerAI> ActiveVillagers = new();

        /// <summary>
        ///     Pending registrations (legacy): uniqueId -> bed position for villagers registered
        ///     before their AI component exists. The primary path registers via RegisterActive
        ///     from VillagerAI.Awake (component lifecycle).
        /// </summary>
        private static readonly Dictionary<string, Vector3> s_pendingRegistrations = new();

        /// <summary>
        ///     Register an active VillagerAI instance. Called from VillagerAI.Awake when the
        ///     Villager component adds VillagerAI (component lifecycle; no MonsterAI).
        /// </summary>
        public static void RegisterActive(VillagerAI ai)
        {
            if (ai == null || string.IsNullOrEmpty(ai.UniqueId)) return;
            ActiveVillagers[ai.UniqueId] = ai;
            Plugin.Log?.LogInfo($"[VillagerAIManager] Registered villager {ai.UniqueId} (active)");
        }

        /// <summary>
        ///     Legacy: Register a villager by ID and bed position (e.g. before AI component exists).
        ///     The primary path uses Villager + VillagerAI components and RegisterActive from Awake.
        /// </summary>
        public static void Register(string uniqueId, Vector3 bedPosition)
        {
            if (string.IsNullOrEmpty(uniqueId)) return;

            s_pendingRegistrations[uniqueId] = bedPosition;
            Plugin.Log?.LogInfo($"[VillagerAIManager] Registered villager {uniqueId} (pending)");
        }

        /// <summary>
        ///     Unregister a villager.
        /// </summary>
        public static void Unregister(string uniqueId)
        {
            ActiveVillagers.Remove(uniqueId);
            s_pendingRegistrations.Remove(uniqueId);
        }

        /// <summary>
        ///     Unregister and destroy reference when a villager is destroyed.
        /// </summary>
        public static void Unregister(VillagerAI ai)
        {
            if (ai != null)
                Unregister(ai.UniqueId);
        }

        /// <summary>
        ///     Check if a unique ID is registered (either pending or active).
        /// </summary>
        public static bool IsRegistered(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return false;
            return ActiveVillagers.ContainsKey(uniqueId) || s_pendingRegistrations.ContainsKey(uniqueId);
        }

        /// <summary>
        ///     Get unique bed positions for every villager in the world. Reads
        ///     authoritatively from <c>ZDOMan.m_objectsByID</c>: each ZDO with the
        ///     <c>vv_villager_type</c> tag carries a persistent <c>vv_bed_position</c>.
        ///     Survives villager GameObject unload (out-of-range, teleported) and
        ///     hot reloads (the in-memory <see cref="ActiveVillagers" /> dict is
        ///     cleared on reload).
        ///     Returns an empty list if <c>ZDOMan</c> isn't yet alive — callers
        ///     should treat that as "world not ready" and either retry or abort.
        ///     No fallback to in-memory state: that path was masking missing-bed
        ///     bugs (e.g. villagers unloaded across reload) by silently using a
        ///     stale subset.
        /// </summary>
        public static List<Vector3> GetAllBedPositions()
        {
            var list = new List<Vector3>();
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return list;
            var objectsByID = Traverse.Create(zdoMan)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null) return list;
            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null) continue;
                var vtype = zdo.GetString("vv_villager_type");
                if (string.IsNullOrEmpty(vtype)) continue;
                var pos = zdo.GetVec3("vv_bed_position", Vector3.zero);
                if (pos == Vector3.zero) continue;
                var duplicate = false;
                foreach (var existing in list)
                    if ((existing - pos).sqrMagnitude < 1f)
                    {
                        duplicate = true;
                        break;
                    }

                if (!duplicate) list.Add(pos);
            }

            return list;
        }

        /// <summary>
        ///     Clear all registrations (e.g. on world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            ActiveVillagers.Clear();
            s_pendingRegistrations.Clear();
        }
    }
}