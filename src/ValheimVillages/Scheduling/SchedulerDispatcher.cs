using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;
using ValheimVillages.Scheduling.Producers;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Primary-mode work selector. For an idle villager it refreshes the village board
    ///     (throttled), runs the dual-encoder scheduler, claims the chosen task, and hands
    ///     it to the matching directed behavior. Returns the directed behavior currently
    ///     executing an assignment — which the <see cref="VillagerAI" /> selection loop then
    ///     runs — or null if there's nothing to do.
    ///
    ///     <para>Claim lifecycle lives here, not in the behaviors: a claim is taken on
    ///     assignment and released when the behavior's <see cref="IDirectedBehavior.AssignmentActive" />
    ///     flips false (task done or abandoned).</para>
    /// </summary>
    public static class SchedulerDispatcher
    {
        private static readonly Dictionary<string, float> s_lastScan = new();
        private static readonly Dictionary<string, (Mlp mlp, RerankSettings settings)> s_models = new();
        private static readonly Dictionary<string, (string sourceId, IDirectedBehavior beh)> s_assigned = new();

        // DIAGNOSTIC: throttle per-villager bail logging so the decision path is visible
        // without flooding the log every reselect tick.
        private static readonly Dictionary<string, float> s_lastDiag = new();

        public static IDirectedBehavior AssignIfIdle(VillagerAI ai)
        {
            if (!SchedulerSettings.Enabled || !SchedulerSettings.PrimaryMode || ai == null) return null;
            try
            {
                var villagerId = ai.UniqueId;
                if (string.IsNullOrEmpty(villagerId)) return null;

                // Keep an in-progress assignment running; release a completed/abandoned one.
                if (s_assigned.TryGetValue(villagerId, out var cur))
                {
                    if (cur.beh != null && cur.beh.AssignmentActive) return cur.beh;
                    TaskBoard.Release(cur.sourceId);
                    s_assigned.Remove(villagerId);
                }

                var village = VillageRegistry.GetVillageAt(ai.HomeAnchor);
                if (village == null || !village.HasGraph)
                {
                    Diag(ai, "no village/graph at anchor");
                    return null;
                }

                var villageId = village.VillageId;
                var now = Time.time;

                if (!s_lastScan.TryGetValue(villageId, out var last) ||
                    now - last >= SchedulerSettings.ScanInterval)
                {
                    s_lastScan[villageId] = now;
                    CookRescueProducer.Scan(village, village.Anchor, now);
                    RepairTaskProducer.Scan(village, village.Anchor, now);
                }

                var tasks = TaskBoard.Tasks(villageId, now);
                if (tasks.Count == 0)
                {
                    Diag(ai, "0 tasks on board");
                    return null;
                }

                if (!s_models.TryGetValue(villageId, out var model))
                {
                    model = SchedulerModelPersistence.LoadOrCreate(village);
                    s_models[villageId] = model;
                }

                var query = new VillagerQuery
                {
                    VillagerId = villagerId,
                    Position = ai.Position,
                    Graph = village.Graph,
                    Triad = village.TriadAnchors,
                    Capabilities = new HashSet<string>(ai.BehaviorTags),
                    LastTaskKind = null,
                };

                var best = DualEncoderScheduler.SelectBest(in query, tasks, model.mlp, model.settings);
                if (best == null)
                {
                    Diag(ai, $"SelectBest=null over {tasks.Count} tasks (caps={string.Join(",", ai.BehaviorTags)})");
                    return null;
                }

                var beh = ai.FindDirectedBehavior(best.Kind);
                if (beh == null)
                {
                    Diag(ai, $"no directed behavior for {best.Kind}");
                    return null;
                }

                TaskBoard.Claim(best.SourceId, villagerId, now);
                if (!beh.BeginAssignment(best))
                {
                    // No walkable approach right now. Reserve to a sentinel owner so the
                    // dispatcher rotates to a different piece next tick instead of looping
                    // on this one; the claim expires after ClaimTtl and it's retried.
                    TaskBoard.Claim(best.SourceId, "(approach-failed)", now);
                    Diag(ai, $"BeginAssignment FAILED {best.Kind}@({best.Position.x:F0},{best.Position.z:F0})");
                    return null;
                }

                s_assigned[villagerId] = (best.SourceId, beh);
                Plugin.Log?.LogInfo(
                    $"[Scheduler:{ai.NpcName}] assigned {best.Kind}@({best.Position.x:F0},{best.Position.z:F0}) " +
                    $"cap={best.RequiredCapability}");
                return beh;
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogError($"[Scheduler] AssignIfIdle threw for {ai?.NpcName}: {ex}");
                return null;
            }
        }

        private static void Diag(VillagerAI ai, string msg)
        {
            var now = Time.time;
            var id = ai.UniqueId ?? ai.NpcName;
            if (id != null && s_lastDiag.TryGetValue(id, out var t) && now - t < 2f) return;
            if (id != null) s_lastDiag[id] = now;
            Plugin.Log?.LogInfo($"[SchedDiag:{ai.NpcName}] {msg}");
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_lastScan.Clear();
            s_models.Clear();
            s_assigned.Clear();
            s_lastDiag.Clear();
        }
    }
}
