using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     The two-tower retrieve-then-rerank pipeline, and the single entry point the
    ///     dispatcher uses to choose a task for a villager:
    ///
    ///     <list type="number">
    ///       <item>Encode the villager query once (<see cref="VillagerEncoder" />).</item>
    ///       <item>For each capable, unclaimed candidate, encode it
    ///             (<see cref="TaskEncoder" />) and score by dot product — spatially-aware
    ///             recall via the triad-MIPS embedding.</item>
    ///       <item>Take the top-M by that score.</item>
    ///       <item>Hand the top-M to <see cref="TaskReranker" />, which applies the EXACT
    ///             region-hop distance + slack-gated utility (and the learned residual once
    ///             trained) and returns the argmax.</item>
    ///     </list>
    ///
    ///     Retrieval is approximate (triad-hop space); the rerank is exact. At current task
    ///     counts the retrieval is academic — the rerank would handle them all — but the
    ///     two-tower stage is what scales when the board grows, and exercising it now keeps
    ///     the structure honest.
    /// </summary>
    public static class DualEncoderScheduler
    {
        public static CandidateTask SelectBest(
            in VillagerQuery query, IReadOnlyList<CandidateTask> tasks, Mlp mlp, RerankSettings settings)
        {
            if (tasks == null || tasks.Count == 0) return null;

            var now = Time.time;
            var qEmb = VillagerEncoder.Encode(
                query.Graph, query.Triad, query.Position, SchedulerSettings.PriorityWeight);

            // Stage 1-2: capability + claim pre-filter, then embedding dot-product score.
            var scored = new List<(CandidateTask task, float dot)>();
            foreach (var t in tasks)
            {
                if (!IsCapable(query, t)) continue;
                if (TaskBoard.IsClaimedByOther(t.SourceId, query.VillagerId, now)) continue;

                var tEmb = TaskEncoder.Encode(query.Graph, query.Triad, t);
                scored.Add((t, Dot(qEmb, tEmb)));
            }

            if (scored.Count == 0) return null;

            // Stage 3: retrieve top-M by embedding score (descending).
            scored.Sort((a, b) => b.dot.CompareTo(a.dot));
            var m = Mathf.Min(SchedulerSettings.RetrieveTopM, scored.Count);
            var topM = new List<CandidateTask>(m);
            for (var i = 0; i < m; i++) topM.Add(scored[i].task);

            // Stage 4: exact rerank (region-hop distance + slack gate + learned residual).
            return TaskReranker.SelectBest(in query, topM, mlp, settings);
        }

        private static bool IsCapable(in VillagerQuery query, CandidateTask task)
        {
            if (string.IsNullOrEmpty(task.RequiredCapability)) return true;
            return query.Capabilities != null && query.Capabilities.Contains(task.RequiredCapability);
        }

        private static float Dot(float[] a, float[] b)
        {
            var s = 0f;
            for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }
    }
}
