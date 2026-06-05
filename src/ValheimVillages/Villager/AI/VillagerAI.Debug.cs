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
            var navHold = Navigation.VillageNavLock.IsHeld
                ? $" [nav hold {Navigation.VillageNavLock.SecondsRemaining:F1}s — rebuild settle]"
                : "";
            sb.AppendLine($"[vv_get_villagers] {villagers.Count} active villager(s):{navHold}");
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

        [DevCommand("Reset patrol discovery for all patrollers, forcing a fresh route rebuild from the region graph",
            Name = "vv_patrol_reset")]
        public static void ResetPatrols()
        {
            var count = 0;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var patrol = kv.Value?.GetBehavior<Behaviors.Patrol.PerimeterPatrolBehavior>();
                if (patrol == null) continue;
                patrol.ResetDiscovery();
                count++;
            }

            var msg = $"[vv_patrol_reset] Reset discovery for {count} patroller(s)";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
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

            // Report the path state of the mover the villager ACTUALLY uses.
            // With NavMeshAgentMover, movement is driven by the advisory
            // NavMeshAgent's own internal path + desiredVelocity — BaseAI.m_path
            // and the HNA corridor planner are NOT consulted, so reporting them
            // is misleading (corridor_complete can read true while the agent is
            // stuck on its own partial path). Surface the agent's real state;
            // fall back to the m_path + corridor view only for the legacy custom
            // corner-walker mover.
            AppendAgentPathState(sb);


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
                    if (b is ValheimVillages.Behaviors.Work.CraftingBehaviorAdapter cba
                        && cba.Crafting != null)
                        sb.AppendLine($"    note[{b.Tag}]: {cba.Crafting.LastWorkNote}");
                    if (b is ValheimVillages.Behaviors.Patrol.PerimeterPatrolBehavior patrol
                        && patrol.HelpWaypointIndex >= 0)
                    {
                        var hp = patrol.HelpPosition;
                        sb.AppendLine(
                            $"    patrol-help: W{patrol.HelpWaypointIndex} @ " +
                            $"({hp.x:F1},{hp.y:F1},{hp.z:F1})");
                    }
                }
            }
        }

        /// <summary>
        ///     Report the advisory NavMeshAgent's real path state — the actual
        ///     information UpdateAgentMovement steers on. Replaces the old
        ///     corridor-planner readout, which described a planner the agent
        ///     mover never consults.
        /// </summary>
        private void AppendAgentPathState(StringBuilder sb)
        {
            if (m_navAgent == null)
            {
                sb.AppendLine("    agent: <not created yet>");
                return;
            }

            if (!m_navAgent.isOnNavMesh)
            {
                sb.AppendLine(
                    "    agent: OFF-MESH — agent position not on the slot-31 navmesh; cannot path");
                return;
            }

            var pending = m_navAgent.pathPending;
            var rd = m_navAgent.remainingDistance;
            var rdStr = pending ? "pending" : float.IsInfinity(rd) ? "∞" : $"{rd:F1}m";
            var dv = m_navAgent.desiredVelocity.magnitude;
            var dest = m_navAgent.destination;

            sb.AppendLine(
                $"    agent: status={m_navAgent.pathStatus} hasPath={m_navAgent.hasPath} " +
                $"pending={pending} onLink={m_navAgent.isOnOffMeshLink} " +
                $"corners={m_navAgent.path.corners.Length}");
            sb.AppendLine(
                $"    agent: remaining={rdStr} stop={m_navAgent.stoppingDistance:F1}m " +
                $"desiredVel={dv:F2} dest=({dest.x:F1},{dest.y:F1},{dest.z:F1})");

            // Silent-stall signature: a path the agent isn't moving along (zero
            // desired velocity, not yet at target) — the exact case
            // UpdateAgentMovement's bare StopMoving() otherwise hides.
            if (m_navAgent.hasPath && !pending && dv < 0.01f
                && !float.IsInfinity(rd) && rd > m_navAgent.stoppingDistance + 0.25f)
                sb.AppendLine("    agent: ⚠ STALLED — has path, off target, zero desired velocity");

            // Partial/invalid path = the agent can't actually reach the target on
            // its navmesh (mover-relevant analogue of the old 'corridor incomplete').
            if (!pending && m_navAgent.pathStatus != UnityEngine.AI.NavMeshPathStatus.PathComplete)
                sb.AppendLine(
                    $"    agent: ⚠ path {m_navAgent.pathStatus} — target not fully reachable on the agent navmesh");
        }

        /// <summary>
        ///     Read the villager's live NavMeshAgent route for debug visualization
        ///     (<see cref="Pathfinding.PathDebugRenderer" />). Corners come straight
        ///     from the agent the mover actually follows — NOT the legacy
        ///     <c>BaseAI.m_path</c>. Returns false when the agent isn't created,
        ///     is off-mesh, or has no path.
        /// </summary>
        public bool TryGetAgentPath(out Vector3[] corners, out UnityEngine.AI.NavMeshPathStatus status)
        {
            if (m_navAgent != null && m_navAgent.isOnNavMesh && m_navAgent.hasPath)
            {
                corners = m_navAgent.path.corners;
                status = m_navAgent.pathStatus;
                return true;
            }

            corners = System.Array.Empty<Vector3>();
            status = UnityEngine.AI.NavMeshPathStatus.PathInvalid;
            return false;
        }
    }
}

