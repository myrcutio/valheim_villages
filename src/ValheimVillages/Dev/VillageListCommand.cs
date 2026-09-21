using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Items;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     The two multi-village instruments behind <c>vv_village</c>.
    ///     <para>
    ///         <c>list</c> enumerates EVERY village ZDO in the world and cross-references the
    ///         three things that can disagree about which village is which: the registry
    ///         pieces that point at a village, the villager records that claim one, and the
    ///         work orders stored on one. Every other <c>vv_village</c> section reports "the
    ///         village at the player", which is blind exactly when a second village exists —
    ///         the 2026-09-12 split-brain (one village holding the registry and all anchors,
    ///         a second holding the villager record) was invisible to every command the mod
    ///         had.
    ///     </para>
    ///     <para>
    ///         <c>at</c> reports which village a POSITION resolves to AND by which branch.
    ///         <see cref="VillageRegistry.GetVillageAt" /> tries exact graph coverage and then
    ///         falls back to the nearest graph ORIGIN with no distance limit, and ~31 call
    ///         sites resolve a village from a position through it. In a two-village world any
    ///         coverage miss silently lands on the other village from arbitrarily far away.
    ///         This prints which branch answered, so that substitution stops being silent.
    ///     </para>
    /// </summary>
    public static class VillageListCommand
    {
        /// <summary>
        ///     Anchor separation under which two villages should have been ONE — this is
        ///     <see cref="VillageRegistry.GetOrCreateAt" />'s relink radius, so a pair closer
        ///     than this is by definition a mint that should have re-linked instead.
        /// </summary>
        private const float RelinkRadius = 30f;

        // -------------------------------------------------------------------------
        // vv_village list
        // -------------------------------------------------------------------------

        public static void List()
        {
            var villages = VillageRegistry.EnumerateAll().ToList();
            var registries = CollectRegistryPieces();
            var records = CollectRecordsByVillage();

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_village list] {villages.Count} village(s) in the world");

            foreach (var village in villages.OrderBy(v => v.VillageId))
                AppendVillage(sb, village, registries, records);

            AppendAnomalies(sb, villages, registries, records);
            Print(sb.ToString());
        }

        private static void AppendVillage(
            StringBuilder sb, Village village,
            Dictionary<string, List<Vector3>> registries,
            Dictionary<string, List<VillagerRecord>> records)
        {
            var id = village.VillageId;
            var anchor = village.Anchor;
            var zone = ZoneSystem.GetZone(anchor);
            var loaded = ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(zone);

            sb.AppendLine($"  {id}");
            sb.AppendLine(
                $"    anchor   = ({anchor.x:F1},{anchor.y:F1},{anchor.z:F1}) zone=({zone.x},{zone.y}) " +
                $"loaded={(loaded ? "Y" : "N")} owner={(village.Zdo != null && village.Zdo.IsOwner() ? "self" : "remote")}");

            var graph = village.Graph;
            if (graph == null)
                sb.AppendLine("    graph    = none (never hydrated)");
            else
                sb.AppendLine(
                    $"    graph    = {graph.RegionCount} region(s), {graph.LinkCount} link(s), " +
                    $"origin={(graph.GetOrigin(out var ox, out var oz) ? $"({ox:F0},{oz:F0})" : "(unset)")}, " +
                    $"available={graph.IsAvailable}, gen={graph.Generation}");

            sb.AppendLine(village.TryGetFootprint(out var minX, out var minZ, out var maxX, out var maxZ)
                ? $"    extent   = x[{minX:F0}..{maxX:F0}] z[{minZ:F0}..{maxZ:F0}]"
                : "    extent   = (unset)");

            sb.AppendLine($"    flags    = invalid={village.IsInvalid} needsWall={village.NeedsPerimeterWall}");

            var pieces = registries.TryGetValue(id, out var found) ? found : null;
            sb.AppendLine(pieces == null || pieces.Count == 0
                ? "    registry = NONE — no registry piece points at this village"
                : $"    registry = {pieces.Count} piece(s): " +
                  string.Join(", ", pieces.Select(p => $"({p.x:F1},{p.y:F1},{p.z:F1})")));

            var orders = village.WorkOrders;
            sb.AppendLine($"    orders   = {orders.Count}");
            foreach (var order in orders)
                sb.AppendLine($"       {order.Item} x[{order.Min}-{order.Max}] station={order.Station}");

            var owned = records.TryGetValue(id, out var list) ? list : null;
            if (owned == null || owned.Count == 0)
            {
                sb.AppendLine("    records  = 0");
                return;
            }

            var byStatus = string.Join(", ", owned
                .GroupBy(r => r.Status).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()}"));
            sb.AppendLine($"    records  = {owned.Count} ({byStatus})");
            foreach (var record in owned)
                sb.AppendLine(
                    $"       {record.Status} {record.Type} \"{record.Name}\" id={Short(record.RecordId)} " +
                    $"home=({record.HomeAnchor.x:F0},{record.HomeAnchor.z:F0})");
        }

        /// <summary>
        ///     Cross-references the three stores that can disagree about village identity.
        ///     Each entry here is a state the model says cannot exist, so they are reported
        ///     as faults rather than folded into the per-village blocks.
        /// </summary>
        private static void AppendAnomalies(
            StringBuilder sb, List<Village> villages,
            Dictionary<string, List<Vector3>> registries,
            Dictionary<string, List<VillagerRecord>> records)
        {
            var problems = new List<string>();
            var ids = new HashSet<string>(villages.Select(v => v.VillageId));

            // A village nothing points at cannot be re-linked to and cannot be reaped by
            // VillageCleanupRpc (which triggers off registry-piece removal).
            foreach (var village in villages)
                if (!registries.TryGetValue(village.VillageId, out var pieces) || pieces.Count == 0)
                    problems.Add($"village {Short(village.VillageId)} has NO registry piece — " +
                                 "orphan: nothing can re-link to it and its removal has no trigger");

            foreach (var kvp in registries)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                {
                    problems.Add($"{kvp.Value.Count} registry piece(s) carry NO village id — " +
                                 "the mint/link in StationBuildPatch never ran for them");
                    continue;
                }

                if (!ids.Contains(kvp.Key))
                    problems.Add($"{kvp.Value.Count} registry piece(s) link to village {Short(kvp.Key)}, " +
                                 "which has no ZDO");
            }

            foreach (var kvp in records)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                {
                    problems.Add($"{kvp.Value.Count} record(s) carry NO village id");
                    continue;
                }

                if (!ids.Contains(kvp.Key))
                    problems.Add($"{kvp.Value.Count} record(s) point at village {Short(kvp.Key)}, " +
                                 "which has no ZDO (dangling)");
            }

            // Two villages inside the relink radius should have been one: GetOrCreateAt
            // re-links within 30m, so a closer pair is a mint that should never have happened.
            for (var i = 0; i < villages.Count; i++)
            for (var j = i + 1; j < villages.Count; j++)
            {
                var dist = Vector3.Distance(villages[i].Anchor, villages[j].Anchor);
                if (dist > RelinkRadius) continue;
                problems.Add(
                    $"villages {Short(villages[i].VillageId)} and {Short(villages[j].VillageId)} are " +
                    $"{dist:F1}m apart — inside the {RelinkRadius:F0}m relink radius, so one is a duplicate mint");
            }

            if (problems.Count == 0)
            {
                sb.AppendLine("  anomalies: none");
                return;
            }

            sb.AppendLine($"  ANOMALIES ({problems.Count}):");
            foreach (var problem in problems) sb.AppendLine($"    ! {problem}");
        }

        // -------------------------------------------------------------------------
        // vv_village at
        // -------------------------------------------------------------------------

        public static void At(Terminal.ConsoleEventArgs args)
        {
            if (!TryResolveProbe(args, out var pos, out var how))
            {
                Print("[vv_village at] usage: vv_village at <x> <z> [y] (no args = player position). " +
                      "Pass y explicitly when the point has no solid ground under it.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_village at] probe ({pos.x:F1},{pos.y:F1},{pos.z:F1}) — {how}");

            // Mirror GetVillageAt's own two-branch walk so the printed verdict is an
            // explanation of the real resolution, not a parallel reimplementation of it.
            Village exact = null;
            Village nearestOrigin = null;
            var bestOriginDist = float.MaxValue;
            Village nearestAnchor = null;
            var bestAnchorDist = float.MaxValue;

            foreach (var village in VillageRegistry.EnumerateAll())
            {
                var anchorDist = Vector3.Distance(pos, village.Anchor);
                if (anchorDist < bestAnchorDist)
                {
                    bestAnchorDist = anchorDist;
                    nearestAnchor = village;
                }

                var graph = village.Graph;
                if (graph == null || !graph.IsAvailable)
                {
                    sb.AppendLine($"  {Short(village.VillageId)}  graph unavailable — GetVillageAt skips it " +
                                  $"(anchor {anchorDist:F0}m away)");
                    continue;
                }

                var region = graph.PointToRegionId(pos);
                var covered = !string.IsNullOrEmpty(region);
                if (covered && exact == null) exact = village;

                var originDist = float.NaN;
                if (graph.GetOrigin(out var ox, out var oz))
                {
                    var dx = pos.x - ox;
                    var dz = pos.z - oz;
                    originDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (!covered && originDist < bestOriginDist)
                    {
                        bestOriginDist = originDist;
                        nearestOrigin = village;
                    }
                }

                sb.AppendLine(
                    $"  {Short(village.VillageId)}  region={(covered ? region : "(none)")}" +
                    $"  origin={(float.IsNaN(originDist) ? "unset" : $"{originDist:F0}m")}" +
                    $"  anchor={anchorDist:F0}m{(covered ? "   <-- EXACT COVERAGE" : "")}");
            }

            if (exact != null)
            {
                sb.AppendLine($"  => GetVillageAt = {Short(exact.VillageId)} via exact graph coverage");
            }
            else if (nearestOrigin != null)
            {
                sb.AppendLine(
                    $"  => GetVillageAt = {Short(nearestOrigin.VillageId)} via NEAREST-ORIGIN FALLBACK " +
                    $"at {bestOriginDist:F0}m — no village covers this point");
                if (nearestAnchor != null && nearestAnchor.VillageId != nearestOrigin.VillageId)
                    sb.AppendLine(
                        $"     !! the fallback answered {Short(nearestOrigin.VillageId)} although the nearest " +
                        $"anchor is {Short(nearestAnchor.VillageId)} at {bestAnchorDist:F0}m");
            }
            else
            {
                sb.AppendLine("  => GetVillageAt = null (no village has an available graph)");
            }

            // Ground the verdict against the real call, so a divergence here is itself a finding.
            var actual = VillageRegistry.GetVillageAt(pos);
            var expected = exact ?? nearestOrigin;
            if (!ReferenceEquals(actual, expected))
                sb.AppendLine(
                    $"     !! live GetVillageAt returned {(actual == null ? "null" : Short(actual.VillageId))}, " +
                    $"which this walk did not predict");

            Print(sb.ToString());
        }

        /// <summary>
        ///     Resolves the probe point: explicit <c>x z [y]</c>, else the player. Y is taken
        ///     from solid ground when omitted and the command REFUSES rather than substituting
        ///     a stand-in height — region ids are height-bucketed, so a wrong Y silently
        ///     changes which region (and therefore which village) the point resolves to.
        /// </summary>
        private static bool TryResolveProbe(Terminal.ConsoleEventArgs args, out Vector3 pos, out string how)
        {
            pos = Vector3.zero;
            how = null;

            var argv = args?.Args;
            // argv is [vv_village, at, x, z, y] — the section token is still present here.
            if (argv == null || argv.Length < 4)
            {
                var player = Player.m_localPlayer;
                if (player == null) return false;
                pos = player.transform.position;
                how = "player position";
                return true;
            }

            if (!TryParse(argv[2], out var x) || !TryParse(argv[3], out var z)) return false;

            if (argv.Length >= 5 && TryParse(argv[4], out var explicitY))
            {
                pos = new Vector3(x, explicitY, z);
                how = "explicit x z y";
                return true;
            }

            if (!RegionGraph.GetSolidHeightAt(x, z, out var groundY)) return false;
            pos = new Vector3(x, groundY, z);
            how = $"y={groundY:F1} sampled from solid ground";
            return true;
        }

        // -------------------------------------------------------------------------

        private static Dictionary<string, List<Vector3>> CollectRegistryPieces()
        {
            var result = new Dictionary<string, List<Vector3>>();
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return result;

            var objectsByID = Traverse.Create(zdoMan)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null) return result;

            var registryHash = PieceFactory.RegistryPrefabName.GetStableHashCode();
            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null || zdo.GetPrefab() != registryHash) continue;
                var id = zdo.GetString(Village.IdKey) ?? "";
                if (!result.TryGetValue(id, out var list)) result[id] = list = new List<Vector3>();
                list.Add(zdo.GetPosition());
            }

            return result;
        }

        private static Dictionary<string, List<VillagerRecord>> CollectRecordsByVillage()
        {
            var result = new Dictionary<string, List<VillagerRecord>>();
            foreach (var record in VillagerRecordTable.EnumerateAll())
            {
                var key = record.Village ?? "";
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<VillagerRecord>();
                list.Add(record);
            }

            return result;
        }

        private static bool TryParse(string token, out float value)
        {
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>First GUID group — enough to tell villages apart without the 36-char noise.</summary>
        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(empty)";
            var dash = id.IndexOf('-');
            return dash > 0 ? id.Substring(0, dash) : id;
        }

        private static void Print(string text)
        {
            // Capped + chunked: a single oversized write to a headless server's
            // stdout pipe blocks the main thread. See ConsoleReport.
            ValheimVillages.Dev.ConsoleReport.Emit(text);
        }
    }
}
