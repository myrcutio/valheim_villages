using System.Linq;
using UnityEngine;
using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villager.Levels
{
    /// <summary>
    ///     Report every villager's quarters: cot, shelter, comfort and what each is waiting on.
    ///     The whole ladder is derived from live geometry, so the only honest way to check it is
    ///     to ask the running world.
    /// </summary>
    internal static class QuartersDumpCommand
    {
        [DevCommand("Report each villager's cot, shelter and comfort. Usage: vv_quarters",
            Name = "vv_quarters")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var records = VillagerRecordTable.EnumerateAll()
                .Where(r => r.Status == RecordStatus.Alive)
                .OrderBy(r => r.Name)
                .ToList();

            var sb = new StringBuilder("[vv_quarters]");
            if (records.Count == 0) sb.Append("\n  (no living villagers)");

            foreach (var record in records)
            {
                var q = VillagerQuarters.For(record);
                sb.Append($"\n  {record.Name,-12} {record.Type,-12} level={q.Level}/{q.Potential} " +
                          $"cot={(q.HasCot ? "yes" : "NO")} " +
                          $"roof={(q.Sheltered ? "yes" : "no")} " +
                          $"cover={q.Coverage:P0} " +
                          $"comfort={q.Comfort}/{q.ComfortTarget} " +
                          $"bar={q.ComfortProgress:P0} " +
                          $"work={(q.CanWork ? "yes" : "IDLE")}");
                // Distance from the villager to their own cot — the thing the night/day wander
                // preference is supposed to move, so it has to be readable to know it works.
                if (q.HasCot &&
                    VillagerAIManager.ActiveVillagers.TryGetValue(record.RecordId, out var ai) &&
                    ai != null)
                {
                    sb.Append($" dist={Vector3.Distance(ai.Position, q.CotPosition):F1}m");

                    // Region membership is the night rule's actual test, so report it rather
                    // than leaving distance to stand in for it.
                    var graph = Villages.Entity.VillageRegistry.GraphAt(q.CotPosition);
                    if (graph != null)
                    {
                        var here = graph.PointToRegionId(ai.Position) ?? "-";

                        // Same resolution the night rule uses: a cot's origin is inside its own
                        // collider and off the walkable surface, so it usually resolves to no
                        // region until the lookup grid is consulted.
                        var home = graph.PointToRegionId(q.CotPosition);
                        if (string.IsNullOrEmpty(home))
                            graph.TryFindNearestLookupCell(q.CotPosition, null, out _, out home, 4f);
                        if (string.IsNullOrEmpty(home)) home = "-";
                        sb.Append($" region={here}{(here == home ? "==" : "!=")}{home}");
                    }
                }

                if (q.MissingPeers.Count > 0)
                    sb.Append($" peers_missing={string.Join(",", q.MissingPeers.ToArray())}");
                if (!string.IsNullOrEmpty(q.Blocker)) sb.Append($"\n                 -> {q.Blocker}");
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }
    }
}
