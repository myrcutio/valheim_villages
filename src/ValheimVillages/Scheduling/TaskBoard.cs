using System.Collections.Generic;
using ValheimVillages.Attributes;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Per-village in-memory table of <see cref="CandidateTask" /> rows. Producers
    ///     upsert by <see cref="CandidateTask.SourceId" />; expired deadline-bearing rows
    ///     are evicted on access.
    ///
    ///     <para>
    ///     The board is <i>derived</i> state — rebuilt from live world signals by the
    ///     producers each scan — so it is intentionally NOT persisted. Only the scheduler
    ///     model is durable (see <see cref="SchedulerModelPersistence" />). If we later
    ///     decide the table itself should survive reload, serialize <see cref="Tasks" />
    ///     to the village ZDO blob the same way.
    ///     </para>
    /// </summary>
    public static class TaskBoard
    {
        private static readonly Dictionary<string, Dictionary<string, CandidateTask>> s_byVillage = new();

        public static void Upsert(string villageId, CandidateTask task)
        {
            if (string.IsNullOrEmpty(villageId) || task == null || string.IsNullOrEmpty(task.SourceId)) return;
            if (!s_byVillage.TryGetValue(villageId, out var table))
            {
                table = new Dictionary<string, CandidateTask>();
                s_byVillage[villageId] = table;
            }

            table[task.SourceId] = task;
        }

        public static void Remove(string villageId, string sourceId)
        {
            if (s_byVillage.TryGetValue(villageId, out var table))
                table.Remove(sourceId);
            // A row that no longer exists can't stay blocked — the next incarnation of the
            // same SourceId (a piece damaged again after being repaired) must start clean.
            if (s_blocked.TryGetValue(villageId, out var blocked))
                blocked.Remove(sourceId);
        }

        /// <summary>
        ///     Every live task for a village, blocked ones included, evicting expired
        ///     deadline-bearing rows. For diagnostics (<c>vv_rerank_dump</c>) — the
        ///     selection path uses <see cref="UnblockedTasks" />.
        /// </summary>
        public static List<CandidateTask> AllTasks(string villageId, float now)
        {
            var result = new List<CandidateTask>();
            if (!s_byVillage.TryGetValue(villageId, out var table)) return result;

            List<string> expired = null;
            foreach (var kv in table)
            {
                var t = kv.Value;
                if (t.ExpiresAt > 0f && t.ExpiresAt <= now)
                {
                    (expired ??= new List<string>()).Add(kv.Key);
                    continue;
                }

                result.Add(t);
            }

            if (expired != null)
                foreach (var id in expired)
                    table.Remove(id);

            return result;
        }

        // --- Blocked tasks: rows a behavior reported UNREACHABLE.
        //
        // Reachability is a property of (target, village region graph) — see
        // RepairBehavior.TryResolveReachableApproach, which never looks at the villager
        // asking. So a task that couldn't be reached cannot become reachable until that
        // graph is rebuilt, and rebuilds only happen when the player changes the village
        // (a piece placed/removed, terrain reshaped) — see RegionGraph.Generation.
        //
        // Stored beside the rows rather than ON them because producers re-Upsert a fresh
        // CandidateTask every scan, which would wipe a flag held on the row itself.
        // villageId -> sourceId -> graph generation the block was recorded at.
        private static readonly Dictionary<string, Dictionary<string, uint>> s_blocked = new();

        /// <summary>
        ///     Record that <paramref name="sourceId" /> had no walkable approach under graph
        ///     <paramref name="graphGeneration" />. It stays out of selection until the
        ///     village's graph generation moves on.
        /// </summary>
        public static void MarkBlocked(string villageId, string sourceId, uint graphGeneration)
        {
            if (string.IsNullOrEmpty(villageId) || string.IsNullOrEmpty(sourceId)) return;
            if (!s_blocked.TryGetValue(villageId, out var blocked))
            {
                blocked = new Dictionary<string, uint>();
                s_blocked[villageId] = blocked;
            }

            blocked[sourceId] = graphGeneration;
        }

        /// <summary>
        ///     True while this row is blocked under the CURRENT graph. A block recorded
        ///     against an older generation is stale — the village has been repartitioned
        ///     since, so the verdict may have changed — and is dropped here so the task is
        ///     offered again.
        /// </summary>
        public static bool IsBlocked(string villageId, string sourceId, uint graphGeneration)
        {
            if (!s_blocked.TryGetValue(villageId, out var blocked)) return false;
            if (!blocked.TryGetValue(sourceId, out var blockedAt)) return false;
            if (blockedAt == graphGeneration) return true;

            blocked.Remove(sourceId);
            return false;
        }

        /// <summary>
        ///     Tasks eligible for selection: <see cref="AllTasks" /> minus everything
        ///     currently blocked as unreachable under graph <paramref name="graphGeneration" />.
        /// </summary>
        public static List<CandidateTask> UnblockedTasks(string villageId, float now, uint graphGeneration)
        {
            var all = AllTasks(villageId, now);
            if (!s_blocked.TryGetValue(villageId, out var blocked) || blocked.Count == 0) return all;

            var result = new List<CandidateTask>(all.Count);
            foreach (var t in all)
                if (!IsBlocked(villageId, t.SourceId, graphGeneration))
                    result.Add(t);

            return result;
        }

        /// <summary>How many rows are blocked under the current graph (diagnostics).</summary>
        public static int BlockedCount(string villageId, uint graphGeneration)
        {
            if (!s_blocked.TryGetValue(villageId, out var blocked)) return 0;
            var n = 0;
            // Snapshot the keys: IsBlocked evicts stale entries as it goes.
            foreach (var sourceId in new List<string>(blocked.Keys))
                if (IsBlocked(villageId, sourceId, graphGeneration))
                    n++;

            return n;
        }

        // --- Task claiming: one villager reserves a task so others don't double-grab.
        // Keyed by task SourceId → (villagerId, claim time). Soft (TTL-expiring) so a
        // villager that dies / abandons mid-task doesn't lock the task forever.
        private static readonly Dictionary<string, (string villager, float at)> s_claims = new();

        /// <summary>True if another villager holds a live claim on this task.</summary>
        public static bool IsClaimedByOther(string sourceId, string villagerId, float now)
        {
            if (!s_claims.TryGetValue(sourceId, out var c)) return false;
            if (now - c.at > SchedulerSettings.ClaimTtl)
            {
                s_claims.Remove(sourceId); // stale claim expired
                return false;
            }

            return c.villager != villagerId;
        }

        public static void Claim(string sourceId, string villagerId, float now)
        {
            if (!string.IsNullOrEmpty(sourceId)) s_claims[sourceId] = (villagerId, now);
        }

        public static void Release(string sourceId)
        {
            if (!string.IsNullOrEmpty(sourceId)) s_claims.Remove(sourceId);
        }

        /// <summary>
        ///     Drop every claim this villager holds. Claims expire on their own after
        ///     <see cref="SchedulerSettings.ClaimTtl" />, so this is not about correctness — it
        ///     is about a recalled villager not leaving its work reserved behind it. Recall is
        ///     the player saying "stop what you are doing"; another villager should be able to
        ///     pick the task up at once rather than waiting out a lease nobody is using.
        /// </summary>
        public static int ReleaseAllHeldBy(string villagerId)
        {
            if (string.IsNullOrEmpty(villagerId)) return 0;

            var held = new List<string>();
            foreach (var kv in s_claims)
                if (kv.Value.villager == villagerId)
                    held.Add(kv.Key);

            foreach (var sourceId in held) s_claims.Remove(sourceId);
            return held.Count;
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_byVillage.Clear();
            s_claims.Clear();
            s_blocked.Clear();
        }
    }
}
