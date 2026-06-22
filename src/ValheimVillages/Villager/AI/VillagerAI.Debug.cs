using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;

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
        [DevCommand(
            "List villagers from the authoritative record table (all statuses), annotated " +
            "with live-instance presence; full AI/path/region detail under each loaded instance",
            Name = "vv_get_villagers")]
        public static void DumpVillagers()
        {
            // Make the in-memory instance count honest before we report it.
            var pruned = VillagerAIManager.PruneTombstones();

            // The record table — NOT the in-memory AI dict — is the row set, so this now
            // agrees with vv_records (same store) and includes Dead/Egg/away villagers,
            // each annotated with where its NPC actually is right now.
            var records = VillagerRecordTable.EnumerateAll()
                .OrderBy(r => r.Village)
                .ThenBy(r => r.Name)
                .ToList();

            var sb = new StringBuilder();
            var navHold = Navigation.VillageNavLock.IsHeld
                ? $" [nav hold {Navigation.VillageNavLock.SecondsRemaining:F1}s — rebuild settle]"
                : "";
            sb.AppendLine(
                $"[vv_get_villagers] {VillagerLiveness.PeerLabel()} {records.Count} villager record(s); " +
                $"live-instance annotated{navHold}");

            foreach (var r in records)
            {
                var presence = VillagerLiveness.Resolve(r);
                var warn = presence == LivePresence.Missing ? " ⚠ORPHAN" : "";
                sb.AppendLine(
                    $"- {r.Name} [{r.Type}] status={r.Status} " +
                    $"presence={VillagerLiveness.Tag(presence)}{warn} id={r.RecordId}");

                // Only a record with a live local instance gets the full runtime block.
                if (presence == LivePresence.Live
                    && VillagerAIManager.ActiveVillagers.TryGetValue(r.RecordId, out var ai)
                    && ai != null)
                    ai.AppendDebug(sb);
            }

            sb.AppendLine(
                $"  in-memory AI instances on this peer: {VillagerAIManager.ActiveVillagers.Count}" +
                (pruned > 0 ? $" ({pruned} null tombstone(s) pruned)" : ""));

            Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        /// <summary>
        ///     Runtime toggle for the off-mesh rescue (strand detection → walk-home →
        ///     teleport escalation) in <see cref="TryOffMeshRescue" />. Defaults OFF for the
        ///     current experiment (isolate whether the rescue itself contributes to villager
        ///     displacement). The anchor LEASH is a separate path and stays active.
        ///     Static field, so it resets to this default on each reload.
        /// </summary>
        public static bool OffMeshRescueEnabled = false;

        [DevCommand(
            "Toggle the villager off-mesh rescue (strand→walk-home→teleport). Usage: vv_rescue [on|off]",
            Name = "vv_rescue")]
        public static void ToggleRescue(Terminal.ConsoleEventArgs args)
        {
            var arg = args.Length > 1 ? args[1].ToLowerInvariant() : null;
            if (arg == "on") OffMeshRescueEnabled = true;
            else if (arg == "off") OffMeshRescueEnabled = false;
            else OffMeshRescueEnabled = !OffMeshRescueEnabled;

            var msg = $"[vv_rescue] off-mesh rescue {(OffMeshRescueEnabled ? "ENABLED" : "DISABLED")} " +
                      "(anchor leash unaffected)";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        [DevCommand("Dump each villager's AI event ring — state changes / target sets / path recomputes with timestamps. The transition timeline that polling vv_get_villagers aliases past.",
            Name = "vv_ai_events")]
        public static void DumpAiEvents()
        {
            const int MaxPerVillager = 40;
            var now = Time.time;
            var sb = new StringBuilder();
            sb.AppendLine($"[vv_ai_events] now={now:F1}s — last {MaxPerVillager} events/villager (oldest first, time shown as age):");

            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var ai = kv.Value;
                if (ai == null) continue;

                sb.AppendLine($"- {ai.NpcName} [{ai.VillagerType}] state={ai.CurrentState}");

                // Large lookback → the whole buffer; trim to the most recent N below.
                var events = ai.EventRing.Snapshot(now, 100000f);
                if (events.Count == 0)
                {
                    sb.AppendLine("    (no events recorded)");
                    continue;
                }

                for (var i = System.Math.Max(0, events.Count - MaxPerVillager); i < events.Count; i++)
                {
                    var ev = events[i];
                    var age = now - ev.TimeSec;
                    switch (ev.Kind)
                    {
                        case Diagnostics.AiEventRing.EventKind.StateChange:
                            sb.AppendLine($"    -{age,6:F1}s  STATE   {ev.Detail}");
                            break;
                        case Diagnostics.AiEventRing.EventKind.TargetSet:
                            sb.AppendLine(
                                $"    -{age,6:F1}s  TARGET  {ev.Detail} -> " +
                                $"({ev.PosA.x:F1},{ev.PosA.y:F1},{ev.PosA.z:F1})");
                            break;
                        case Diagnostics.AiEventRing.EventKind.PathRecompute:
                            sb.AppendLine(
                                $"    -{age,6:F1}s  PATH    {ev.Detail} corners={ev.IntA}");
                            break;
                    }
                }
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

                var anchor = ai.m_homeAnchor;
                var containers = Work.ContainerScanner.FindNearbyContainers(
                    anchor, Settings.WorkSettings.ChestScanRadius);
                var village = Villages.Entity.VillageRegistry.GetVillageAt(anchor);
                var orders = Work.ContainerScanner.FindAllWorkOrders(village, ai.VillagerType);
                var vid = village != null ? village.VillageId : "(none)";
                var owns = village?.Zdo != null && village.Zdo.IsOwner();
                sb.AppendLine(
                    $"- {ai.NpcName} [{ai.VillagerType}] anchor=({anchor.x:F0},{anchor.z:F0}) " +
                    $"village={vid} owns={owns} containers={containers.Count} orders={orders.Count}");
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

        /// <summary>
        ///     Migrate legacy in-chest work-order tokens into the host-owned village record
        ///     (Fix C seed). Host-only — the village carrier is host-owned so UpsertWorkOrder
        ///     no-ops on a client. Fail-loud per token whose village can't be resolved (no
        ///     auto-create). Idempotent: skips any (station,item) already present in the record,
        ///     so a re-run never resets a player's edited quota back to the token's original.
        /// </summary>
        [DevCommand("Migrate legacy in-chest work-order tokens into the host-owned village record (Fix C). Host-only.",
            Name = "vv_migrate_workorders")]
        public static void MigrateWorkOrders()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Console.instance?.Print(
                    "[vv_migrate_workorders] Run on the SERVER — the host owns the village record.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[vv_migrate_workorders]");
            int tokens = 0, migrated = 0, skipped = 0, unresolved = 0;

            foreach (var c in Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                if (c == null) continue;
                var inv = c.GetInventory();
                if (inv == null) continue;

                foreach (var item in inv.GetAllItems())
                {
                    if (!Work.ContainerScanner.IsWorkOrderItem(item)) continue;
                    if (item.m_customData == null) continue;
                    tokens++;

                    item.m_customData.TryGetValue("wo_station", out var station);
                    item.m_customData.TryGetValue("wo_item", out var orderItem);
                    item.m_customData.TryGetValue("wo_item_name", out var itemName);
                    var p = c.transform.position;

                    if (string.IsNullOrEmpty(station) || string.IsNullOrEmpty(orderItem))
                    {
                        sb.AppendLine($"  SKIP token with empty station/item @ ({p.x:F0},{p.z:F0})");
                        unresolved++;
                        continue;
                    }

                    int min = 1, max = 10;
                    if (item.m_customData.TryGetValue("wo_min", out var minStr)) int.TryParse(minStr, out min);
                    if (item.m_customData.TryGetValue("wo_max", out var maxStr)) int.TryParse(maxStr, out max);

                    var village = Villages.Entity.VillageRegistry.GetVillageAt(p);
                    if (village == null)
                    {
                        sb.AppendLine(
                            $"  UNRESOLVED village for {orderItem}@{station} @ ({p.x:F0},{p.z:F0}) — skipped (no auto-create)");
                        unresolved++;
                        continue;
                    }

                    // Idempotent: never overwrite an existing record entry — a re-run must not
                    // reset a player's edited quota back to the token's stale original value.
                    if (village.TryGetWorkOrder(station, orderItem, out _))
                    {
                        sb.AppendLine($"  skip {orderItem}@{station} — already in record (village {village.VillageId})");
                        skipped++;
                        continue;
                    }

                    village.UpsertWorkOrder(
                        new Villages.Entity.WorkOrderEntry(station, orderItem, itemName ?? "", min, max));
                    migrated++;
                    sb.AppendLine(
                        $"  migrated {orderItem} x[{min}-{max}] station={station} -> village {village.VillageId}");
                }
            }

            sb.AppendLine(
                $"  => {tokens} token(s), {migrated} migrated, {skipped} already-present, {unresolved} unresolved.");
            Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        /// <summary>Append this villager's diagnostic block to <paramref name="sb" />.</summary>
        private void AppendDebug(StringBuilder sb)
        {
            var pos = transform != null ? transform.position : Vector3.zero;

            string region = null;
            try { region = Villages.Entity.VillageRegistry.GraphAt(pos)?.PointToRegionId(pos); }
            catch { /* graph not built / not ready */ }

            // Header line ("- Name [Type] status= presence= id=") is printed by the caller
            // (DumpVillagers) from the record; this block is the live runtime detail under it.
            sb.AppendLine($"    pos=({pos.x:F1},{pos.y:F1},{pos.z:F1})  state={CurrentState}  " +
                          $"paused={IsPaused} lingering={IsLingering} casual={IsCasualTravel}");
            sb.AppendLine($"    region@pos={region ?? "UNRESOLVED"}  " +
                          $"anchor=({m_homeAnchor.x:F1},{m_homeAnchor.y:F1},{m_homeAnchor.z:F1})");

            if (!IsSettled)
                sb.AppendLine(
                    "    settling: holding for spawn preconditions (on village graph + ready agent)");

            // ZDO ownership: BaseAI.UpdateAI bails (returns false → whole tick,
            // including the movement step, is skipped) when this instance is NOT
            // the owner. On the dedicated server, isOwner=False means the server
            // handed the villager to the player's client and can't simulate it.
            var znv = GetComponent<ZNetView>();
            var zdo = znv != null ? znv.GetZDO() : null;
            if (zdo != null)
                sb.AppendLine($"    zdo: owner={zdo.GetOwner()} session={ZDOMan.GetSessionID()} " +
                              $"isOwner={zdo.IsOwner()} valid={znv.IsValid()}");
            else
                sb.AppendLine("    zdo: <no zdo>");

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

            // What the mover is ACTUALLY commanding the character vs how fast it's
            // really going — distinguishes "behavior isn't driving movement"
            // (moveDir~0) from "driving but physically blocked / rotating"
            // (moveDir set, vel~0).
            if (m_character == null)
            {
                // A registered villager with a null m_character is anomalous (stripped
                // native AI / missing component). Surface it loudly instead of printing
                // valid-looking zero movement that masks the real state.
                sb.AppendLine("    char: m_character=NULL (anomalous — no movement data)");
            }
            else
            {
                var mv = m_character.GetMoveDir();
                var vel = m_character.GetVelocity();
                sb.AppendLine(
                    $"    char: moveDir=({mv.x:F2},{mv.z:F2}) |{new Vector3(mv.x, 0f, mv.z).magnitude:F2}| " +
                    $"vel={new Vector3(vel.x, 0f, vel.z).magnitude:F2} " +
                    $"needsMove={NeedsMovement(CurrentState)} hasWaypoint={m_currentWaypoint != null}");
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

