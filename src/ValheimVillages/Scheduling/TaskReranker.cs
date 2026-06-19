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
    ///     <paramref name="mlp" /> (zero while untrained). Capability mismatch and
    ///     unreachable tasks are hard-filtered before scoring.
    /// </summary>
    public static class TaskReranker
    {
        public const int FeatureCount = 6;

        public static CandidateTask SelectBest(
            in VillagerQuery query,
            IReadOnlyList<CandidateTask> tasks,
            Mlp mlp,
            RerankSettings settings)
        {
            if (tasks == null || tasks.Count == 0) return null;
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (mlp != null && mlp.InputCount != FeatureCount)
                throw new ArgumentException(
                    $"MLP expects {mlp.InputCount} inputs, reranker emits {FeatureCount}");

            var now = Time.time;
            CandidateTask best = null;
            var bestScore = float.NegativeInfinity;
            var features = new float[FeatureCount];

            foreach (var task in tasks)
            {
                // Hard filter: villager must have the required capability.
                if (!string.IsNullOrEmpty(task.RequiredCapability) &&
                    (query.Capabilities == null || !query.Capabilities.Contains(task.RequiredCapability)))
                    continue;

                var hops = RegionHopDistance.Hops(query.Graph, query.Position, task.Position);
                if (hops < 0) continue; // unreachable — never dispatch

                var eta = hops * settings.PerHopSeconds;

                // No deadline → always feasible (large positive slack).
                var slack = task.ExpiresAt > 0f
                    ? task.ExpiresAt - now - eta
                    : settings.SlackNorm * 4f;

                var closed = task.Priority * Sigmoid(settings.SlackSharpness * slack);

                var residual = 0f;
                if (mlp != null)
                {
                    BuildFeatures(features, task, hops, eta, slack, in query, settings);
                    residual = mlp.Forward(features);
                }

                var score = closed + residual;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = task;
                }
            }

            return best;
        }

        private static void BuildFeatures(
            float[] f, CandidateTask task, int hops, float eta, float slack,
            in VillagerQuery query, RerankSettings s)
        {
            f[0] = task.Priority;
            f[1] = hops; // raw hop count
            f[2] = eta / s.EtaNorm; // normalized ETA
            f[3] = task.ExpiresAt > 0f ? slack / s.SlackNorm : 1f; // normalized slack
            f[4] = task.ExpiresAt > 0f ? 1f : 0f; // has-deadline flag
            f[5] = query.LastTaskKind == task.Kind ? 1f : 0f; // continuity / inertia
        }

        private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));
    }
}
