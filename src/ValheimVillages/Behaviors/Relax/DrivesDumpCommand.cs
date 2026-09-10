using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Relax
{
    /// <summary>
    ///     Diagnostic: print every live villager's drive pressures and the ranked idle spots
    ///     those drives currently produce. This is the counterpart to <c>vv_rerank_dump</c>
    ///     for the routine (idle) tier — it shows WHY a villager is heading for the fire
    ///     rather than the table, which is otherwise invisible.
    /// </summary>
    public static class DrivesDumpCommand
    {
        private const int TopSpots = 5;

        [DevCommand("Dump villager idle drives (warmth/social/rest/curiosity) + ranked leisure spots",
            Name = "vv_drives")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();
            var now = Time.time;

            sb.AppendLine($"[vv_drives] cold={VillagerDrives.IsCold()} " +
                          "(wet/cold/freezing/night → shelter preferred)");

            var any = false;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var ai = kv.Value;
                if (ai == null) continue;
                any = true;

                var drives = VillagerDrives.For(kv.Key);
                sb.AppendLine($"- {ai.NpcName} [{kv.Key}]");
                sb.AppendLine($"    drives: {drives}  dominant={VillagerDrives.Dominant(kv.Key)}");

                var pois = VillagePoiRegistry.GetPois(ai.HomeAnchor);
                if (pois == null || pois.Count == 0)
                {
                    sb.AppendLine("    spots: (no PoIs cached for this village)");
                    continue;
                }

                var scored = new List<(KnownLocation poi, float score, int company)>();
                foreach (var poi in pois)
                {
                    if (!LeisureAppeal.IsLeisureType(poi.Type)) continue;
                    var company = CountIdleNear(poi.Position, kv.Key);
                    var s = LeisureAppeal.Score(poi, drives, company, now);
                    if (s > 0f) scored.Add((poi, s, company));
                }

                if (scored.Count == 0)
                {
                    sb.AppendLine("    spots: (none scored > 0 — no leisure PoIs in hull)");
                    continue;
                }

                scored.Sort((a, b) => b.score.CompareTo(a.score));
                var shown = Mathf.Min(TopSpots, scored.Count);
                for (var i = 0; i < shown; i++)
                {
                    var (poi, score, company) = scored[i];
                    var dist = Vector3.Distance(ai.Position, poi.Position);
                    var visited = poi.LastVisitedAt > 0f
                        ? $" lastVisit={now - poi.LastVisitedAt:F0}s"
                        : "";
                    sb.AppendLine(
                        $"    [{i}] {poi.Type,-8} score={score:F3} d={dist:F1}m " +
                        $"comfort={poi.ComfortValue:F0} shelter={(poi.HasShelter ? "Y" : "N")} " +
                        $"company={company}{visited}");
                }
            }

            if (!any) sb.AppendLine("  (no live villagers on this peer)");
            Print(sb.ToString().TrimEnd());
        }

        private static int CountIdleNear(Vector3 spot, string excludeVillagerId)
        {
            var n = 0;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var other = kv.Value;
                if (other == null || kv.Key == excludeVillagerId) continue;
                if (other.CurrentState != Enums.BehaviorState.Idle) continue;
                if (Vector3.Distance(other.Position, spot) <= 4.5f) n++;
            }

            return n;
        }

        private static void Print(string text)
        {
            global::Console.instance?.Print(text);
            Plugin.Log?.LogInfo(text);
        }
    }
}
