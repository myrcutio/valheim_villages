using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Scheduling.Producers;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Diagnostic: refresh the player's village task board and print every candidate
    ///     with its reranker breakdown (hops / ETA / slack / closed-form utility) scored
    ///     FROM THE PLAYER'S POSITION. Lets us watch the feasibility gate fire — e.g. a
    ///     burning-meat task that's high-U up close and collapses to ~0 when far.
    /// </summary>
    public static class RerankDumpCommand
    {
        [DevCommand("Dump task-board candidates + reranker score breakdown for the player's village",
            Name = "vv_rerank_dump")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();
            var player = Player.m_localPlayer;
            var village = player != null ? VillageRegistry.GetVillageAt(player.transform.position) : null;
            if (village == null || !village.HasGraph)
            {
                Print("[vv_rerank_dump] no village with a graph at player position");
                return;
            }

            var now = Time.time;
            CookRescueProducer.Scan(village, village.Anchor, now);
            RepairTaskProducer.Scan(village, village.Anchor, now);

            var tasks = TaskBoard.Tasks(village.VillageId, now);
            var (_, settings) = SchedulerModelPersistence.LoadOrCreate(village);
            var from = player.transform.position;

            sb.AppendLine($"[vv_rerank_dump] village {village.VillageId}: {tasks.Count} candidate(s) " +
                          "scored from player pos (closed-form only)");
            foreach (var t in tasks)
            {
                var hops = RegionHopDistance.Hops(village.Graph, from, t.Position);
                var hasDeadline = t.ExpiresAt > 0f;
                var eta = hops < 0 ? -1f : hops * settings.PerHopSeconds;
                var slack = hasDeadline ? t.ExpiresAt - now - eta : settings.SlackNorm * 4f;
                var u = hops < 0 ? 0f : t.Priority * Sigmoid(settings.SlackSharpness * slack);

                sb.AppendLine(
                    $"  {t.Kind,-11} pri={t.Priority:F2} cap={t.RequiredCapability,-7} " +
                    $"hops={hops,3} eta={(hops < 0 ? "--" : eta.ToString("F1") + "s"),-7} " +
                    $"slack={(hasDeadline ? slack.ToString("F1") : "--"),-7} U={u:F3} " +
                    $"@({t.Position.x:F0},{t.Position.z:F0})");
            }

            Print(sb.ToString());
        }

        private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
