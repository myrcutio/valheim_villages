using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Scheduling.Producers;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Log-only bridge between the villager idle hook and the reranker. When a
    ///     villager has no behavior wanting control, this refreshes the village task
    ///     board from live signals (throttled per village), runs the reranker for that
    ///     villager, and LOGS the pick. It does not yet dispatch — this is the
    ///     observation phase, so we can compare picks against the behavior system before
    ///     letting the scheduler drive.
    /// </summary>
    public static class SchedulerObserver
    {
        private static readonly Dictionary<string, float> s_lastScan = new();
        private static readonly Dictionary<string, (Mlp mlp, RerankSettings settings)> s_models = new();

        public static void Observe(VillagerAI ai)
        {
            if (!SchedulerSettings.Enabled || ai == null) return;

            var village = VillageRegistry.GetVillageAt(ai.HomeAnchor);
            if (village == null || !village.HasGraph) return;

            var villageId = village.VillageId;
            var now = Time.time;

            // Throttle the (physics-overlap) producer scans per village.
            if (!s_lastScan.TryGetValue(villageId, out var last) ||
                now - last >= SchedulerSettings.ScanInterval)
            {
                s_lastScan[villageId] = now;
                CookRescueProducer.Scan(village, village.Anchor, now);
                RepairTaskProducer.Scan(village, village.Anchor, now);
            }

            var tasks = TaskBoard.Tasks(villageId, now);
            if (tasks.Count == 0) return;

            if (!s_models.TryGetValue(villageId, out var model))
            {
                model = SchedulerModelPersistence.LoadOrCreate(village);
                s_models[villageId] = model;
            }

            var query = new VillagerQuery
            {
                Position = ai.Position,
                Graph = village.Graph,
                Capabilities = new HashSet<string>(ai.BehaviorTags),
                LastTaskKind = null,
            };

            var best = TaskReranker.SelectBest(in query, tasks, model.mlp, model.settings);
            Plugin.Log?.LogInfo(
                $"[Scheduler:{ai.NpcName}] {tasks.Count} candidate(s); pick=" +
                (best != null
                    ? $"{best.Kind}@({best.Position.x:F0},{best.Position.z:F0}) cap={best.RequiredCapability}"
                    : "none (all filtered/unreachable)"));
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_lastScan.Clear();
            s_models.Clear();
        }
    }
}
