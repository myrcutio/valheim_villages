using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Villager.AI
{
    /// <summary>
    ///     Debug introspection for villagers, surfaced as the <c>vv_get_villagers</c>
    ///     console command. Lives in a partial so it can read VillagerAI's private
    ///     state (waypoint, path, recovery) without widening its public surface.
    ///     Output goes to the console so it round-trips through ValheimMCP.
    /// </summary>
    public partial class VillagerAI
    {
        [DevCommand("List active villagers with AI state, target, path status, and resolved region",
            Name = "vv_get_villagers")]
        public static void DumpVillagers()
        {
            var villagers = VillagerAIManager.ActiveVillagers;
            var sb = new StringBuilder();
            sb.AppendLine($"[vv_get_villagers] {villagers.Count} active villager(s):");
            foreach (var kv in villagers)
            {
                if (kv.Value == null)
                {
                    sb.AppendLine($"- <null> id={kv.Key}");
                    continue;
                }

                kv.Value.AppendDebug(sb);
            }

            Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        [DevCommand("List work orders in chests near each villager + how many outputs exist in chests (the real completion metric)",
            Name = "vv_workorders")]
        public static void DumpWorkOrders()
        {
            var sb = new StringBuilder();
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var ai = kv.Value;
                if (ai == null) continue;

                var bed = ai.m_bedPosition;
                var containers = Work.ContainerScanner.FindNearbyContainers(
                    bed, Settings.WorkSettings.ChestScanRadius);
                var orders = Work.ContainerScanner.FindAllWorkOrders(containers, ai.VillagerType);
                sb.AppendLine(
                    $"- {ai.NpcName} [{ai.VillagerType}] bed=({bed.x:F0},{bed.z:F0}) " +
                    $"containers={containers.Count} orders={orders.Count}");
                foreach (var o in orders)
                {
                    var produced = Work.ContainerScanner.CountAcrossContainers(containers, o.ItemPrefabName);
                    sb.AppendLine(
                        $"    order: {o.ItemPrefabName} x[{o.MinQuantity}-{o.MaxQuantity}] " +
                        $"station={o.StationName} inChests={produced}");
                }
            }

            Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        /// <summary>Append this villager's diagnostic block to <paramref name="sb" />.</summary>
        private void AppendDebug(StringBuilder sb)
        {
            var pos = transform != null ? transform.position : Vector3.zero;

            string region = null;
            try { region = RegionGraph.GetNearest(pos)?.PointToRegionId(pos); }
            catch { /* graph not built / not ready */ }

            sb.AppendLine($"- {NpcName} [{VillagerType}] id={UniqueId}");
            sb.AppendLine($"    pos=({pos.x:F1},{pos.y:F1},{pos.z:F1})  state={CurrentState}  " +
                          $"paused={IsPaused} lingering={IsLingering} casual={IsCasualTravel}");
            sb.AppendLine($"    region@pos={region ?? "UNRESOLVED"}  " +
                          $"bed=({m_bedPosition.x:F1},{m_bedPosition.y:F1},{m_bedPosition.z:F1})");

            if (m_currentWaypoint != null)
            {
                var t = m_currentWaypoint.Position;
                var label = string.IsNullOrEmpty(m_currentWaypoint.Label) ? "" : $" \"{m_currentWaypoint.Label}\"";
                sb.AppendLine($"    target=({t.x:F1},{t.y:F1},{t.z:F1}) dist={Vector3.Distance(pos, t):F1}m{label}");
            }
            else
            {
                sb.AppendLine("    target=<none>");
            }

            // Read BaseAI.m_path (the list the movement loop actually follows)
            // via s_pathField — NOT m_waypointPath, which is vestigial and never
            // assigned (it always read as "<none>", masking real movement).
            var followPath = s_pathField?.GetValue(this) as List<Vector3>;
            var corners = followPath?.Count ?? 0;
            if (corners > 0)
            {
                var next = followPath[0];
                sb.AppendLine($"    path: {corners} corner(s), next=({next.x:F1},{next.y:F1},{next.z:F1})");
            }
            else
            {
                sb.AppendLine("    path: <none>");

                // Diagnose WHY there's no path to the current target: run the
                // corridor planner's A* stage and report its result. cells>0
                // means A* succeeded and the failure is in per-segment NavMesh
                // validation (likely an off-graph corner); cells=0 names the
                // A* failure mode (start/goal off-graph, no connection).
                if (m_currentWaypoint != null)
                {
                    var g = RegionGraph.GetNearest(transform.position);
                    if (g != null)
                    {
                        var planned = RegionGraphAStar.PlanCells(
                            g, transform.position, m_currentWaypoint.Position, out var reason);
                        var complete = VillagerMovement.TryFindCompletePath(
                            transform.position, m_currentWaypoint.Position, null);
                        sb.AppendLine(
                            $"    pathplan: astar_cells={planned?.Count ?? 0} reason={reason} " +
                            $"corridor_complete={complete}");
                    }
                }
            }

            if (m_recoveryAttempts > 0 || m_recoveryRetreating || m_consecutiveStucks > 0)
                sb.AppendLine($"    recovery: attempts={m_recoveryAttempts} retreating={m_recoveryRetreating} " +
                              $"consecutiveStucks={m_consecutiveStucks}");

            if (m_behaviors != null && m_behaviors.Count > 0)
            {
                sb.Append("    behaviors: ");
                for (var i = 0; i < m_behaviors.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(m_behaviors[i]?.Tag ?? "?");
                }

                sb.AppendLine();

                // Surface each behavior's status text (e.g. crafting sub-state)
                // so we can see WHERE in a workflow a "Working" villager sits.
                foreach (var b in m_behaviors)
                {
                    var status = b?.GetStatusText();
                    if (!string.IsNullOrEmpty(status))
                        sb.AppendLine($"    status[{b.Tag}]: {status}");
                }
            }
        }
    }
}
