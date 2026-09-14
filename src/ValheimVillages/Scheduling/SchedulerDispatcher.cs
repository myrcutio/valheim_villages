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
    ///     THE work selector. For an idle villager it refreshes the village board
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

        /// <summary>Seconds between repeats of an UNCHANGED dispatch-bail reason.</summary>
        private const float DiagHeartbeatSeconds = 30f;

        // DIAGNOSTIC: throttle per-villager bail logging so the decision path is visible
        // without flooding the log every reselect tick.
        private static readonly Dictionary<string, (float at, string msg)> s_lastDiag = new();

        public static IDirectedBehavior AssignIfIdle(VillagerAI ai)
        {
            if (ai == null) return null;
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
                    CraftWorkProducer.Scan(village, village.Anchor, now);
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

                var pick = DualEncoderScheduler.SelectBestExplained(
                    in query, tasks, model.mlp, model.settings, now);
                var best = pick.Task;
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
                var accepted = beh.BeginAssignment(best);

                // One training sample per dispatch: did this pick convert into work? Recorded for
                // BOTH outcomes — learning only from successes would teach the model nothing about
                // what to avoid.
                SchedulerTrainer.Learn(village, model.mlp, model.settings, in pick, accepted ? 1f : 0f);

                if (!accepted)
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

        /// <summary>
        ///     Log why a villager got no assignment. "Nothing to dispatch" is a NORMAL steady
        ///     state now that the scheduler is the only work selector (a villager with a full
        ///     board of satisfied orders relaxes), so a fixed short throttle would spam the log
        ///     forever. Log immediately whenever the REASON changes — that is the interesting
        ///     event — and otherwise only as an occasional heartbeat.
        /// </summary>
        private static void Diag(VillagerAI ai, string msg)
        {
            var now = Time.time;
            var id = ai.UniqueId ?? ai.NpcName;
            if (id != null && s_lastDiag.TryGetValue(id, out var prev)
                           && prev.msg == msg && now - prev.at < DiagHeartbeatSeconds)
                return;
            if (id != null) s_lastDiag[id] = (now, msg);
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
