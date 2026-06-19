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
        }

        /// <summary>Live tasks for a village, evicting expired deadline-bearing rows.</summary>
        public static List<CandidateTask> Tasks(string villageId, float now)
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

        [RegisterCleanup]
        public static void Clear()
        {
            s_byVillage.Clear();
            s_claims.Clear();
        }
    }
}
