using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages.Entity;

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
        ///     Unregister a villager.
        /// </summary>
        public static void Unregister(string uniqueId)
        {
            ActiveVillagers.Remove(uniqueId);
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
        ///     Get unique anchor positions across the world from DURABLE truth — the
        ///     union of (a) every <see cref="Village.AnchorPositions" /> for each village
        ///     in <see cref="VillageRegistry.EnumerateAll" /> and (b) every alive
        ///     <see cref="VillagerRecord.HomeAnchor" /> in <see cref="VillagerRecordTable" />
        ///     (records whose <see cref="VillagerRecord.Status" /> is
        ///     <see cref="RecordStatus.Alive" />). Both sources hydrate from their backing
        ///     ZDOs, so the result is independent of whether any NPC GameObject is
        ///     instantiated/loaded — it survives villager unload (out-of-range, teleported)
        ///     and hot reloads (the in-memory <see cref="ActiveVillagers" /> dict is
        ///     cleared on reload).
        ///     Positions are deduplicated within ~1m and exact <see cref="Vector3.zero" />
        ///     is skipped.
        ///     Returns an empty list if <c>ZDOMan</c> isn't yet alive — both the village
        ///     registry and the record table treat "world not ready" as no entries, so
        ///     callers should treat an empty result as "world not ready" and either retry
        ///     or abort. No fallback to in-memory state.
        /// </summary>
        public static List<Vector3> GetAllAnchorPositions()
        {
            var list = new List<Vector3>();

            foreach (var village in VillageRegistry.EnumerateAll())
            {
                if (village == null) continue;
                foreach (var pos in village.AnchorPositions)
                    AddUnique(list, pos);
            }

            foreach (var record in VillagerRecordTable.EnumerateAll())
            {
                if (record == null || record.Status != RecordStatus.Alive) continue;
                AddUnique(list, record.HomeAnchor);
            }

            return list;
        }

        /// <summary>
        ///     Add <paramref name="pos" /> to <paramref name="list" /> unless it is exact
        ///     <see cref="Vector3.zero" /> or within ~1m of an existing entry.
        /// </summary>
        private static void AddUnique(List<Vector3> list, Vector3 pos)
        {
            if (pos == Vector3.zero) return;
            foreach (var existing in list)
                if ((existing - pos).sqrMagnitude < 1f)
                    return;
            list.Add(pos);
        }

        /// <summary>
        ///     Clear cached BaseAI paths on every active villager. Call after
        ///     a partition / NavMesh rebake so stale waypoints (now off-mesh
        ///     or routed through cleared NavMeshLinks) don't keep a villager
        ///     locked into a doomed path. Each villager re-pathfinds against
        ///     the fresh NavMesh on their next tick. Returns count for log.
        /// </summary>
        public static int InvalidatePathsAfterRebake()
        {
            var count = 0;
            foreach (var ai in ActiveVillagers.Values)
            {
                if (ai == null) continue;
                ai.ClearCachedPath();
                count++;
            }

            return count;
        }

        /// <summary>
        ///     Rebuild every patroller's route from the freshly repartitioned region
        ///     graph. Call after a partition completes: the boundary may have grown
        ///     or shrunk (e.g. the player walled off a section), but the patrol
        ///     waypoint list is cached at discovery time and is otherwise only
        ///     re-derived by <c>vv_patrol_reset</c> or the one-shot stuck-waypoint
        ///     auto-heal. Neither of those fires when the OLD waypoints stay
        ///     individually reachable on the new NavMesh, so without this the guard
        ///     keeps patrolling the pre-change route — straight into a now-sealed
        ///     area. <see cref="InvalidatePathsAfterRebake" /> only clears the
        ///     low-level NavMeshAgent path; it does not touch the waypoint list.
        ///     Returns count for log.
        /// </summary>
        public static int ResetPatrolRoutesAfterRepartition()
        {
            var count = 0;
            foreach (var ai in ActiveVillagers.Values)
            {
                var patrol = ai?.GetBehavior<Behaviors.Patrol.PerimeterPatrolBehavior>();
                if (patrol == null) continue;
                patrol.ResetDiscovery();
                count++;
            }

            return count;
        }

        /// <summary>
        ///     Clear all registrations (e.g. on world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            ActiveVillagers.Clear();
        }
    }
}