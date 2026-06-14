using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Repair;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: replays the carpenter's repair-target search and prints, for
    ///     each nearby damaged structure, whether it is detected, in scan range,
    ///     ground-snappable, and reachable by a complete path — so we can see exactly
    ///     why a visibly-damaged piece isn't being repaired.
    /// </summary>
    public static class RepairProbeCommand
    {
        [DevCommand("Probe carpenter repair targeting (why damaged structures aren't repaired)",
            Name = "vv_repair_probe")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            VillagerAI carp = null;
            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
                if (ai != null && ai.GetBehavior<RepairBehavior>() != null)
                {
                    carp = ai;
                    break;
                }

            if (carp == null)
            {
                Print("[vv_repair_probe] no repair-capable villager active");
                return;
            }

            var bed = carp.HomeAnchor;
            var pos = carp.Position;
            var radius = WorkSettings.RepairScanRadius;
            Print($"[vv_repair_probe] {carp.NpcName} bed=({bed.x:F1},{bed.z:F1}) pos=({pos.x:F1},{pos.z:F1}) " +
                  $"scanR={radius} agentReg={VillagerAgentType.IsRegistered}");

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            var graph = Villages.Entity.VillageRegistry.GraphAt(bed);
            Print($"  graph@bed={(graph == null ? "NULL" : "ok")} bedRegion={(graph == null ? "-" : graph.PointToRegionId(bed))}");

            var seen = new HashSet<WearNTear>();
            var damaged = new List<(WearNTear w, float dPosSq)>();
            var total = 0;
            foreach (var w in PhysicsHelper.GetAllInRadius<WearNTear>(bed, radius))
            {
                if (w == null || !seen.Add(w)) continue;
                var nv = w.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                total++;
                if (w.GetHealthPercentage() >= 0.99f) continue;
                damaged.Add((w, (w.transform.position - pos).sqrMagnitude));
            }

            damaged.Sort((a, b) => a.dPosSq.CompareTo(b.dPosSq));
            Print($"  {total} valid structures in scan range; {damaged.Count} damaged");

            var n = 0;
            foreach (var (w, dPosSq) in damaged)
            {
                if (n++ >= 15) break;
                var wp = w.transform.position;
                var snapOk = NavMesh.SamplePosition(wp, out var near, 6f, filter);
                var reach = "snap-fail";
                if (snapOk)
                {
                    var fromOk = NavMesh.SamplePosition(pos, out var f, 3f, filter);
                    var toOk = NavMesh.SamplePosition(near.position, out var t, 3f, filter);
                    if (fromOk && toOk)
                    {
                        var path = new NavMeshPath();
                        NavMesh.CalculatePath(f.position, t.position, filter, path);
                        reach = path.status.ToString();
                    }
                    else
                    {
                        reach = $"sample(from={fromOk},to={toOk})";
                    }
                }

                // Old raw-snap region check (what pinned the carpenter): nearest mesh cell
                // to the piece, then PointToRegionId — empty for perimeter pieces.
                var rawRegion = "snap-fail";
                if (snapOk && graph != null)
                {
                    var rid = graph.PointToRegionId(near.position);
                    rawRegion = string.IsNullOrEmpty(rid) ? "<none>" : rid;
                }

                // New resolution: nearest region-resident lookup cell within RepairRange
                // that the agent can stand on — mirrors TryResolveReachableApproach.
                var resolved = "<unreach>";
                if (graph != null && graph.TryFindNearestLookupCell(
                        wp,
                        p => NavMesh.SamplePosition(p, out _, 2f, filter),
                        out var cell, out var cellRid, 6f)) // 6f = RepairBehavior.RepairRange
                    resolved = $"{cellRid}@({cell.x:F1},{cell.z:F1})";

                var name = w.gameObject.name.Replace("(Clone)", "");
                Print($"  {name} @({wp.x:F1},{wp.y:F1},{wp.z:F1}) hp={w.GetHealthPercentage():P0} " +
                      $"d={Mathf.Sqrt(dPosSq):F1}m snap={snapOk} path={reach} rawRegion={rawRegion} resolved={resolved}");
            }
        }

        private static void Print(string msg)
        {
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
