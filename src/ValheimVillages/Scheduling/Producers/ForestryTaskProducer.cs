using UnityEngine;
using ValheimVillages.Behaviors.Forestry;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling.Producers
{
    /// <summary>
    ///     Produces a <see cref="TaskKind.Forestry" /> row when a village has a Forester's
    ///     Post. One row per village, positioned at the post, so the reranker weighs "go work
    ///     the woodlot" against the other jobs by the usual distance/priority terms.
    ///
    ///     <para>Like <c>CraftWorkProducer</c> this does not decide WHICH bit of forestry is
    ///     due — the woodlot state (drops, logs, grown trees, gaps) is read by the behavior at
    ///     <c>BeginAssignment</c>, which is also the only place it can be read without
    ///     duplicating the scan. The producer's job is to say a woodlot exists and roughly how
    ///     badly it wants attention.</para>
    /// </summary>
    public static class ForestryTaskProducer
    {
        private const string Capability = "forestry";

        public static void Scan(Village village, Vector3 center, float now)
        {
            if (village == null) return;
            var villageId = village.VillageId;
            if (string.IsNullOrEmpty(villageId)) return;

            var sourceId = "forestry:" + villageId;

            if (!village.TryGetAnchor(ForesterPost.AnchorName, out var post))
            {
                // No post (or it was removed) — drop any stale row so a Lumberjack is not
                // offered a woodlot that no longer exists.
                TaskBoard.Remove(villageId, sourceId);
                return;
            }

            // Wood already on the ground is the most urgent state: it is finished work
            // sitting outside, and more felling only adds to the pile. Everything else is
            // steady upkeep.
            var priority = HasLooseTimber(post) ? 0.8f : 0.4f;

            TaskBoard.Upsert(villageId, new CandidateTask
            {
                SourceId = sourceId,
                Kind = TaskKind.Forestry,
                Position = post,
                Priority = priority,
                ExpiresAt = 0f, // trees are not in a hurry
                RequiredCapability = Capability,
            });
        }

        /// <summary>Cheap "is there output waiting" check — drops or logs in the woodlot.</summary>
        private static bool HasLooseTimber(Vector3 post)
        {
            if (TreeFelling.FindNearestLog(post, ForesterPost.WorkRadius) != null) return true;

            foreach (var drop in PhysicsHelper.GetAllInRadius<ItemDrop>(post, ForesterPost.WorkRadius))
            {
                if (drop == null || drop.m_itemData?.m_shared == null) continue;
                var n = drop.m_itemData.m_shared.m_name ?? "";
                if (n.IndexOf("wood", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("log", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
    }
}
