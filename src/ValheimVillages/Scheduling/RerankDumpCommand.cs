using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Scheduling.Producers;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Diagnostic: for every loaded villager (or one named by argument), refresh its
    ///     village task board and print each candidate with the reranker's full breakdown —
    ///     hops / ETA / slack / closed-form utility / learned residual — scored FROM THAT
    ///     VILLAGER'S POSITION, plus the row's claim state and the pick that wins.
    ///
    ///     <para>Villager-centric on purpose. The scheduler is the only thing that decides
    ///     what a villager does, so "why is this villager idle?" is the question this has to
    ///     answer, and it must answer it on a dedicated server where there is no local
    ///     player to score from. Scoring from the player's position instead (as this used to)
    ///     described a trip nobody was taking.</para>
    ///
    ///     <para><b>Summarised, not dumped.</b> A village routinely carries a hundred-plus
    ///     rows (one per damaged piece), and printing every one of them for every villager
    ///     produced a single ~30 KB write that blocked the main thread on a dedicated server's
    ///     stdout pipe and left it unresponsive — see <see cref="Dev.ConsoleReport" />. The
    ///     rows that lose on SCORE are the ones worth seeing individually; the rows dropped by
    ///     the pre-filter are counted by reason instead, which is the same answer in one line.
    ///     <c>full</c> as a second argument prints every row (still capped).</para>
    /// </summary>
    public static class RerankDumpCommand
    {
        [DevCommand("Dump task-board candidates + reranker score breakdown per villager. " +
                    "Usage: vv_rerank_dump [villagerName] [full]",
            Name = "vv_rerank_dump")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var filter = args.Length >= 2 && !IsFull(args[1]) ? args[1] : null;
            var full = (args.Length >= 2 && IsFull(args[1])) || (args.Length >= 3 && IsFull(args[2]));
            // Top scorers only, unless asked for everything: the pick is decided at the head
            // of the ranking, so that is where the explanation lives.
            const int shown = 8;
            var sb = new StringBuilder();
            var now = Time.time;
            var scanned = new HashSet<string>();
            var found = 0;

            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai == null) continue;
                if (filter != null &&
                    !string.Equals(ai.NpcName, filter, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                found++;
                var village = VillageRegistry.GetVillageAt(ai.HomeAnchor);
                if (village == null || !village.HasGraph)
                {
                    sb.AppendLine($"- {ai.NpcName}: no village/graph at anchor {ai.HomeAnchor}");
                    continue;
                }

                // Refresh each village's board once, exactly as the dispatcher would.
                if (scanned.Add(village.VillageId))
                {
                    CookRescueProducer.Scan(village, village.Anchor, now);
                    RepairTaskProducer.Scan(village, village.Anchor, now);
                    CraftWorkProducer.Scan(village, village.Anchor, now);
                }

                // AllTasks, not UnblockedTasks: a row blocked as unreachable is exactly what
                // someone runs this dump to find, so show it and label it rather than hiding it.
                var graphGeneration = village.Graph?.Generation ?? 0u;
                var tasks = TaskBoard.AllTasks(village.VillageId, now);
                var (mlp, settings) = SchedulerModelPersistence.LoadOrCreate(village);
                var from = ai.Position;
                var caps = new HashSet<string>(ai.BehaviorTags);

                var regionId = village.Graph?.PointToRegionId(from);
                sb.AppendLine(
                    $"- {ai.NpcName} @({from.x:F0},{from.z:F0}) region={regionId ?? "UNRESOLVED"} " +
                    $"village={village.VillageId}: {tasks.Count} candidate(s), caps={string.Join(",", caps)}");

                var query = new VillagerQuery
                {
                    VillagerId = ai.UniqueId,
                    Position = from,
                    Graph = village.Graph,
                    Triad = village.TriadAnchors,
                    Capabilities = caps,
                    LastTaskKind = null,
                };

                var features = new float[TaskReranker.FeatureCount];
                var skips = new Dictionary<string, int>();
                var scored = new List<(float total, string line)>();
                foreach (var t in tasks)
                {
                    var why = Ineligible(in query, t, now, village.VillageId, graphGeneration);
                    if (why != null && !full)
                    {
                        // Counted, not printed. One "missing capability 'repair'" line is the
                        // whole story; a hundred of them is just the village's repair list.
                        skips.TryGetValue(why, out var had);
                        skips[why] = had + 1;
                        continue;
                    }

                    var hops = RegionHopDistance.Hops(village.Graph, from, t.Position);
                    var hasDeadline = t.ExpiresAt > 0f;
                    var eta = hops * settings.PerHopSeconds;
                    var slack = hasDeadline
                        ? t.ExpiresAt - now - eta
                        : settings.SlackNorm * 4f;
                    var closed = t.Priority * Sigmoid(settings.SlackSharpness * slack);

                    TaskReranker.BuildFeatures(features, t, hops, eta, slack, in query, settings, now);
                    var residual = mlp != null ? mlp.Forward(features) : 0f;

                    var line =
                        $"    {t.Kind,-11} {t.TargetItemPrefab ?? "(self-pick)",-16} " +
                        $"pri={t.Priority:F2} hops={hops,3} eta={eta,5:F1}s " +
                        $"slack={(hasDeadline ? slack.ToString("F1") : "--"),-7} " +
                        $"U={closed:F3}{(residual != 0f ? $"{residual:+0.000;-0.000}" : "")} " +
                        $"=> {closed + residual:F3}  @({t.Position.x:F0},{t.Position.z:F0})" +
                        (why != null ? $"  [SKIPPED: {why}]" : "");
                    scored.Add((closed + residual, line));
                }

                scored.Sort((a, b) => b.total.CompareTo(a.total));
                for (var i = 0; i < scored.Count && (full || i < shown); i++)
                    sb.AppendLine(scored[i].line);
                if (!full && scored.Count > shown)
                    sb.AppendLine($"    … and {scored.Count - shown} more scored lower");
                if (skips.Count > 0)
                    sb.AppendLine($"    not eligible: {Histogram(skips)}");

                var pick = DualEncoderScheduler.SelectBestExplained(in query, tasks, mlp, settings, now);
                sb.AppendLine(pick.Task != null
                    ? $"    => PICK {pick.Task.Kind} {pick.Task.TargetItemPrefab ?? "(self-pick)"} " +
                      $"@({pick.Task.Position.x:F0},{pick.Task.Position.z:F0})"
                    : "    => PICK none");
            }

            if (found == 0)
                sb.AppendLine(filter != null
                    ? $"[vv_rerank_dump] no loaded villager named '{filter}'"
                    : "[vv_rerank_dump] no villagers loaded on this peer");

            Dev.ConsoleReport.Emit("[vv_rerank_dump]", sb);
        }

        private static bool IsFull(string arg)
        {
            return string.Equals(arg, "full", System.StringComparison.OrdinalIgnoreCase);
        }

        private static string Histogram(Dictionary<string, int> counts)
        {
            var sb = new StringBuilder();
            foreach (var kv in counts)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{kv.Key} ×{kv.Value}");
            }

            return sb.ToString();
        }

        /// <summary>
        ///     Why the dual-encoder's pre-filter would drop this row, or null if it survives.
        ///     Mirrors <see cref="DualEncoderScheduler" />'s capability/claim checks — the
        ///     point of the dump is to show the rows that never reach the rerank at all.
        /// </summary>
        private static string Ineligible(
            in VillagerQuery query, CandidateTask task, float now, string villageId, uint graphGeneration)
        {
            if (TaskBoard.IsBlocked(villageId, task.SourceId, graphGeneration))
                return $"BLOCKED unreachable (until repartition; graph gen {graphGeneration})";
            if (!string.IsNullOrEmpty(task.OwnerVillagerId) && task.OwnerVillagerId != query.VillagerId)
                return "owned by another villager";
            if (!string.IsNullOrEmpty(task.RequiredCapability) &&
                (query.Capabilities == null || !query.Capabilities.Contains(task.RequiredCapability)))
                return $"missing capability '{task.RequiredCapability}'";
            if (TaskBoard.IsClaimedByOther(task.SourceId, query.VillagerId, now))
                return "claimed";
            return null;
        }

        private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));
    }
}
