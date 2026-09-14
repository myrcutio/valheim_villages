using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Scheduling
{
    /// <summary>Per-villager state forming the reranker's query.</summary>
    public struct VillagerQuery
    {
        public string VillagerId; // stable id, for task claiming
        public Vector3 Position;
        public RegionGraph Graph; // the villager's village graph
        public IReadOnlyList<Vector3> Triad; // village triad anchors, for the spatial embedding
        public HashSet<string> Capabilities; // behavior tags the villager has
        public TaskKind? LastTaskKind; // most recently completed (feature only)
    }

    /// <summary>Tuning constants for the reranker. Persisted alongside the model.</summary>
    public sealed class RerankSettings
    {
        /// <summary>Average seconds to traverse one region hop (hops → ETA).</summary>
        public float PerHopSeconds = 3f;

        /// <summary>Slope of the feasibility sigmoid; larger = sharper "too far" cutoff.</summary>
        public float SlackSharpness = 0.4f;

        /// <summary>Normalizer for ETA in the MLP feature vector.</summary>
        public float EtaNorm = 30f;

        /// <summary>Normalizer for slack in the MLP feature vector.</summary>
        public float SlackNorm = 30f;
    }

    /// <summary>
    ///     Scores every eligible candidate task for one idle villager and returns the
    ///     best. v1 utility is the closed-form, feasibility-gated priority
    ///     <c>U = priority · σ(k·slack)</c> PLUS a learned residual from
    ///     <paramref name="mlp" /> (zero while untrained).
    ///
    ///     <para>Only two things hard-filter a candidate: a capability the villager lacks,
    ///     and a row minted for a different villager. Distance never does. Reachability is
    ///     decided later, by the behavior that actually pathfinds
    ///     (<see cref="Interfaces.IDirectedBehavior.BeginAssignment" /> returning false) —
    ///     because this is the only source of villager work, a distance-based filter here
    ///     doesn't degrade the pick, it removes the villager's entire job list.</para>
    /// </summary>
    public static class TaskReranker
    {
        /// <summary>
        ///     Width of the MLP input. Bumping this invalidates every persisted model — see the
        ///     version guard in <see cref="SchedulerModelPersistence" />, which discards an
        ///     incompatible blob loudly rather than loading weights of the wrong shape.
        /// </summary>
        public const int FeatureCount = 6 + 3 + ItemBuckets;

        /// <summary>
        ///     Hashed one-hot width for item identity. Without SOME identity signal a learner can
        ///     only ever learn "prefer a big deficit": every craft order shares
        ///     <see cref="TaskKind.CraftWork" />, so nothing else in the vector distinguishes
        ///     CarrotSeeds from CookedMeat. The hashing trick keeps the width fixed as the recipe
        ///     list grows; collisions make two items share a bucket, which blurs them together
        ///     rather than breaking anything.
        /// </summary>
        public const int ItemBuckets = 8;

        /// <summary>Seconds since last worked at which the staleness feature saturates.</summary>
        private const float StalenessNorm = 600f;

        /// <summary>
        ///     The chosen task plus the inputs that chose it, so a trainer can attribute an
        ///     outcome back to the exact feature vector that produced the pick. Without this the
        ///     learner would have to recompute features later, against state that has since moved.
        /// </summary>
        public readonly struct RerankPick
        {
            public readonly CandidateTask Task;
            public readonly float[] Features; // defensive copy of the winning vector
            public readonly float Closed; // closed-form component, the learner's baseline

            public RerankPick(CandidateTask task, float[] features, float closed)
            {
                Task = task;
                Features = features;
                Closed = closed;
            }
        }

        public static CandidateTask SelectBest(
            in VillagerQuery query,
            IReadOnlyList<CandidateTask> tasks,
            Mlp mlp,
            RerankSettings settings,
            float now)
            => SelectBestExplained(in query, tasks, mlp, settings, now).Task;

        /// <param name="now">
        ///     Current time, passed in rather than read from <c>Time.time</c> so the whole
        ///     selection path can be exercised headlessly. The scheduler is the only source of
        ///     villager work now, so "which task wins, and does anything win at all" has to be
        ///     testable without launching the game.
        /// </param>
        public static RerankPick SelectBestExplained(
            in VillagerQuery query,
            IReadOnlyList<CandidateTask> tasks,
            Mlp mlp,
            RerankSettings settings,
            float now)
        {
            if (tasks == null || tasks.Count == 0) return default;
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (mlp != null && mlp.InputCount != FeatureCount)
                throw new ArgumentException(
                    $"MLP expects {mlp.InputCount} inputs, reranker emits {FeatureCount}");

            CandidateTask best = null;
            var bestScore = float.NegativeInfinity;
            var bestClosed = 0f;
            float[] bestFeatures = null;
            var features = new float[FeatureCount];

            foreach (var task in tasks)
            {
                // Hard filter: a row minted for a specific villager is only ever that
                // villager's (see CandidateTask.OwnerVillagerId). Checked here as well as in
                // DualEncoderScheduler because the reranker is also called directly.
                if (!string.IsNullOrEmpty(task.OwnerVillagerId) &&
                    task.OwnerVillagerId != query.VillagerId)
                    continue;

                // Hard filter: villager must have the required capability.
                if (!string.IsNullOrEmpty(task.RequiredCapability) &&
                    (query.Capabilities == null || !query.Capabilities.Contains(task.RequiredCapability)))
                    continue;

                // -1 now means "no graph to measure against", not "unreachable": see
                // RegionHopDistance. A merely-unresolved endpoint must never filter a
                // candidate, or a villager standing in a lookup-grid hole starves.
                var hops = RegionHopDistance.Hops(query.Graph, query.Position, task.Position);
                if (hops < 0) continue;

                var eta = hops * settings.PerHopSeconds;

                // No deadline → always feasible (large positive slack).
                var slack = task.ExpiresAt > 0f
                    ? task.ExpiresAt - now - eta
                    : settings.SlackNorm * 4f;

                var closed = task.Priority * Sigmoid(settings.SlackSharpness * slack);

                // Always build features — the trainer needs them for the winner even while the
                // model is untrained (that is exactly when learning has to start).
                BuildFeatures(features, task, hops, eta, slack, in query, settings, now);
                var residual = mlp != null ? mlp.Forward(features) : 0f;

                var score = closed + residual;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = task;
                    bestClosed = closed;
                    bestFeatures = (float[])features.Clone();
                }
            }

            return new RerankPick(best, bestFeatures, bestClosed);
        }

        internal static void BuildFeatures(
            float[] f, CandidateTask task, int hops, float eta, float slack,
            in VillagerQuery query, RerankSettings s, float now)
        {
            Array.Clear(f, 0, f.Length);

            f[0] = task.Priority;
            f[1] = hops; // raw hop count
            f[2] = eta / s.EtaNorm; // normalized ETA
            f[3] = task.ExpiresAt > 0f ? slack / s.SlackNorm : 1f; // normalized slack
            f[4] = task.ExpiresAt > 0f ? 1f : 0f; // has-deadline flag
            f[5] = query.LastTaskKind == task.Kind ? 1f : 0f; // continuity / inertia

            // --- per-order state (zero for rows that are not a specific work order) ---
            f[6] = task.StockFraction;
            f[7] = task.MinShortfall;
            f[8] = task.LastWorkedAt > 0f
                ? Mathf.Clamp01((now - task.LastWorkedAt) / StalenessNorm)
                : 1f; // never worked = maximally stale

            // --- hashed one-hot item identity ---
            var bucket = ItemBucket(task.TargetItemPrefab);
            if (bucket >= 0) f[9 + bucket] = 1f;
        }

        /// <summary>
        ///     Stable bucket for an item prefab, or -1 when the row is not item-specific.
        ///     FNV-1a rather than <c>string.GetHashCode</c>: weights are PERSISTED, so the mapping
        ///     from item to bucket must be identical across sessions and runtimes, and
        ///     GetHashCode is explicitly not guaranteed to be.
        /// </summary>
        internal static int ItemBucket(string itemPrefab)
        {
            if (string.IsNullOrEmpty(itemPrefab)) return -1;
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                var h = offset;
                foreach (var c in itemPrefab)
                {
                    h ^= c;
                    h *= prime;
                }

                return (int)(h % ItemBuckets);
            }
        }

        private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));
    }
}
