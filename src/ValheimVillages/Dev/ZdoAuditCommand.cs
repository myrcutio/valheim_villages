using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Headless-safe, read-only ZDO + GameObject + collider audit at a world point.
    ///     Built to find a duplicate/orphan chest object that exists ONLY on the dedicated
    ///     server: a collider-bearing object that <c>FindObjectsOfType&lt;Container&gt;()</c>
    ///     (the scan) does NOT see, but <c>Physics.OverlapSphere</c> and the slot-31 navmesh
    ///     bake (<c>NavMeshBuilder.CollectSources</c>) DO — which is why it poisons the
    ///     server's bake near chests while the count-level scan reads clean.
    ///
    ///     <para>Unlike <c>printnetobj</c> (which filters <c>FindObjectsOfType&lt;ZNetView&gt;</c>
    ///     by <c>Player.m_localPlayer</c> distance and therefore returns nothing on a
    ///     headless server), this enumerates objects three independent ways and joins them:</para>
    ///     <list type="number">
    ///       <item><b>ZDOMan sector sweep</b> (<c>FindSectorObjects</c>) — the authoritative,
    ///         player-independent set of registered ZDOs around the point.</item>
    ///       <item><b>ZNetScene.m_instances values</b> (Traverse) — live ZNetViews, including
    ///         ghost/hollow instances whose ZDO is null/invalid and so never appear in the
    ///         sweep (the H2 candidate).</item>
    ///       <item><b>Physics.OverlapSphere</b> bridged to the owning ZNetView — colliders in
    ///         the physics world, including ones with NO live ZDO-backed ZNetView at all
    ///         (the true orphan that the scan can never see).</item>
    ///     </list>
    ///
    ///     <para>For every object it prints an authoritative Container verdict
    ///     (none/live/inactive/zdo-null) and <c>scanVisible</c> — exactly whether the chest
    ///     scan would count it — so the bad object is named, not merely implied.</para>
    ///
    ///     <para>Usage: <c>vv_zdo_audit &lt;x&gt; &lt;z&gt; [y] [radius]</c> (Valheim X,Z,[Y]
    ///     order; radius default 5m). No-arg falls back to the local player position ONLY if a
    ///     player exists, so it still works on the client. Run identically on host
    ///     (<c>valheim</c>) and client (<c>valheim-player</c>) and diff the chest-uid count and
    ///     the ORPHAN section. Mutates nothing — safe to run repeatedly.</para>
    /// </summary>
    public static class ZdoAuditCommand
    {
        private const float DefaultRadius = 5f;

        /// <summary>Sector half-span (zones) for FindSectorObjects. ±2 zones covers any
        /// radius up to one 64m zone with margin near a zone seam.</summary>
        private const int SectorArea = 2;

        /// <summary>Owning-ZNetView is attributed to a collider only if it sits within this
        /// distance; a farther ancestor ZNetView is treated as a mis-attribution (and the
        /// collider counted as a potential orphan), per the spec's far-parent guard.</summary>
        private const float AttributionTolerance = 3f;

        /// <summary>A bake source counts as "at" an object if within this distance.</summary>
        private const float BakeMatchTolerance = 0.75f;

        private static readonly string[] FallbackChestPrefabs =
        {
            "piece_chest_wood", "piece_chest", "piece_chest_private",
            "piece_chest_blackmetal", "piece_chest_treasure", "wood_chest",
        };

        [DevCommand("Read-only headless ZDO/collider audit at a point: finds scan-invisible orphan colliders. vv_zdo_audit <x> <z> [y] [radius]",
            Name = "vv_zdo_audit")]
        public static void Audit(Terminal.ConsoleEventArgs args)
        {
            var inv = CultureInfo.InvariantCulture;

            Vector3 pos;
            var radius = DefaultRadius;
            bool fromPlayer;
            if (args?.Args != null && args.Args.Length >= 3)
            {
                if (!float.TryParse(args.Args[1], NumberStyles.Float, inv, out var x)
                    || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var z))
                {
                    Print("Usage: vv_zdo_audit <x> <z> [y] [radius]   (X,Z,[Y] order)");
                    return;
                }

                pos = new Vector3(x, MeshProbe.ResolveY(x, z, args, 3, inv), z);
                // radius is the 4th positional arg (after optional Y). Only treat args[4]
                // as radius; if Y was omitted the caller can still pass radius at index 3
                // only when it does not parse as a plausible Y — keep it simple: radius at 4.
                if (args.Args.Length > 4
                    && float.TryParse(args.Args[4], NumberStyles.Float, inv, out var r) && r > 0f)
                    radius = r;
                fromPlayer = false;
            }
            else
            {
                var p = Player.m_localPlayer;
                if (p == null || p.transform == null)
                {
                    Print("No local player (headless?) — pass coords: vv_zdo_audit <x> <z> [y] [radius]");
                    return;
                }

                pos = p.transform.position;
                fromPlayer = true;
            }

            var zdoMan = ZDOMan.instance;
            var scene = ZNetScene.instance;
            if (zdoMan == null || scene == null)
            {
                Print("[vv_zdo_audit] ZDOMan/ZNetScene not ready — aborting");
                return;
            }

            var isServer = ZNet.instance != null && ZNet.instance.IsServer();
            var localSession = ZDOMan.GetSessionID();

            var sb = new StringBuilder();
            sb.AppendLine(
                $"[vv_zdo_audit] pos=({pos.x:F2}, {pos.y:F2}, {pos.z:F2}) radius={radius:F1} " +
                $"isServer={isServer} session={localSession} " +
                $"localPlayer={(Player.m_localPlayer != null ? "present" : "null")} " +
                $"source={(fromPlayer ? "player" : "args")}");
            sb.AppendLine(
                "  NOTE: a client sees only server-sent ZDOs; a duplicate present on the host " +
                "but absent here is the expected host-vs-client asymmetry, not a command bug.");

            var sweep = ReportSectorSweep(sb, zdoMan, scene, pos, radius, localSession,
                out var chestUids, out var liveContainers);
            var ghostInstances = ReportGhostInstances(sb, scene, pos, radius);
            var orphanColliders = ReportColliderBridge(sb, pos, radius);
            ReportPrefabCrossCheck(sb, zdoMan, scene, pos, radius, sweep);
            var bakeSources = ReportBakeSources(sb, pos, radius);

            sb.AppendLine("--- SUMMARY ---");
            sb.AppendLine(
                $"  zdos={sweep.Count} chestUids={chestUids.Count} liveContainers={liveContainers} " +
                $"orphanColliders={orphanColliders} ghostInstances={ghostInstances} bakeSources={bakeSources}");
            sb.AppendLine(DecisionHint(chestUids.Count, orphanColliders, ghostInstances));

            Print(sb.ToString());
        }

        /// <summary>Per-ZDO table from the authoritative ZDOMan sector sweep.</summary>
        private static List<ZDO> ReportSectorSweep(StringBuilder sb, ZDOMan zdoMan, ZNetScene scene,
            Vector3 pos, float radius, long localSession, out HashSet<ZDOID> chestUids, out int liveContainers)
        {
            chestUids = new HashSet<ZDOID>();
            liveContainers = 0;

            var sector = ZoneSystem.GetZone(pos);
            var near = new List<ZDO>();
            var distant = new List<ZDO>();
            zdoMan.FindSectorObjects(sector, SectorArea, SectorArea, near, distant);

            var all = near.Concat(distant)
                .Where(z => z != null && Vector3.Distance(z.GetPosition(), pos) <= radius)
                .OrderBy(z => Vector3.Distance(z.GetPosition(), pos))
                .ToList();

            sb.AppendLine($"--- ZDOMan sector sweep within {radius:F1}m (count={all.Count}) ---");
            sb.AppendLine(
                "  uid | prefab | dist | owner(class) | P D | dataRev/ownRev | inst active | " +
                "zVld zdoNull ghost | Container | scanVisible");

            foreach (var zdo in all)
            {
                var prefabHash = zdo.GetPrefab();
                var prefabName = scene.GetPrefab(prefabHash)?.name ?? $"#{prefabHash}";
                var isChest = IsChestName(prefabName);
                if (isChest) chestUids.Add(zdo.m_uid);

                var owner = zdo.GetOwner();
                var ownerClass = !zdo.HasOwner() ? "none" : owner == localSession ? "local" : "remote";

                var nview = scene.FindInstance(zdo);
                var go = nview != null ? nview.gameObject : null;
                var hasInst = nview != null;
                var active = go != null && go.activeInHierarchy;
                var zValid = nview != null && nview.IsValid();
                var zdoNull = nview != null && nview.GetZDO() == null;
                var ghost = nview != null ? TryGhost(nview) : (bool?)null;

                var container = go != null ? go.GetComponentInChildren<Container>(true) : null;
                var containerState = ClassifyContainer(container);
                // The chest scan = FindObjectsOfType<Container>() (active GameObjects) + ContainerScanner's
                // zdo!=null filter. So scanVisible iff a live, active, ZDO-bound Container exists.
                var scanVisible = container != null && active && !zdoNull
                                  && container.GetComponent<ZNetView>()?.GetZDO() != null;
                if (scanVisible) liveContainers++;

                var dist = Vector3.Distance(zdo.GetPosition(), pos);
                sb.AppendLine(
                    $"  {zdo.m_uid} | {prefabName}{(isChest ? "*CHEST*" : "")} | {dist:F2}m | " +
                    $"{owner}({ownerClass}) | {(zdo.Persistent ? "P" : "-")}{(zdo.Distant ? "D" : "-")} | " +
                    $"{zdo.DataRevision}/{zdo.OwnerRevision} | " +
                    $"inst={(hasInst ? "y" : "n")} act={(active ? "T" : "F")} | " +
                    $"zVld={(zValid ? "T" : "F")} zdoNull={(zdoNull ? "T" : "F")} ghost={GhostStr(ghost)} | " +
                    $"{containerState} | scanVisible={(scanVisible ? "Y" : "N")}");
            }

            return all;
        }

        /// <summary>
        ///     ZNetScene.m_instances VALUES scan (mandatory, per adversary fix): surfaces live
        ///     ZNetViews whose ZDO is null/invalid/ghost. These never appear in the ZDOMan sweep
        ///     (no registered ZDO) yet keep a collider alive — the exact "hollow instance" the
        ///     scan misses but the bake sees.
        /// </summary>
        private static int ReportGhostInstances(StringBuilder sb, ZNetScene scene, Vector3 pos, float radius)
        {
            sb.AppendLine($"--- ghost/hollow instances (m_instances values, null/invalid ZDO) within {radius:F1}m ---");

            Dictionary<ZDO, ZNetView> instances;
            try
            {
                instances = Traverse.Create(scene)
                    .Field<Dictionary<ZDO, ZNetView>>("m_instances").Value;
            }
            catch (Exception e)
            {
                sb.AppendLine($"  (m_instances scan unavailable: {e.GetType().Name})");
                return 0;
            }

            if (instances == null)
            {
                sb.AppendLine("  (m_instances null)");
                return 0;
            }

            var count = 0;
            foreach (var nview in instances.Values)
            {
                if (nview == null || nview.gameObject == null) continue;
                var go = nview.gameObject;
                if (Vector3.Distance(go.transform.position, pos) > radius) continue;

                var zdoNull = nview.GetZDO() == null;
                var valid = nview.IsValid();
                var ghost = TryGhost(nview);
                // Only the hollow ones are interesting here; healthy instances are already in the sweep.
                if (!zdoNull && valid && ghost != true) continue;

                count++;
                var container = go.GetComponentInChildren<Container>(true);
                sb.AppendLine(
                    $"  go={go.name} dist={Vector3.Distance(go.transform.position, pos):F2}m " +
                    $"valid={(valid ? "T" : "F")} zdoNull={(zdoNull ? "T" : "F")} ghost={GhostStr(ghost)} " +
                    $"active={(go.activeInHierarchy ? "T" : "F")} Container={ClassifyContainer(container)}");
            }

            if (count == 0) sb.AppendLine("  (none)");
            return count;
        }

        /// <summary>
        ///     OverlapSphere → owning-ZNetView bridge. Buckets every collider by how it
        ///     attributes, and lists ORPHAN rows: colliders that are NOT a scan-visible live
        ///     Container (no ZNetView, ZNetView with null ZDO, far-parent attribution, or a
        ///     real object whose Container is missing/inactive). Returns the orphan count.
        /// </summary>
        private static int ReportColliderBridge(StringBuilder sb, Vector3 pos, float radius)
        {
            var cols = Physics.OverlapSphere(pos, radius, ~0, QueryTriggerInteraction.Ignore);
            sb.AppendLine($"--- OverlapSphere colliders within {radius:F1}m (count={cols?.Length ?? 0}) ---");
            if (cols == null || cols.Length == 0)
            {
                sb.AppendLine("  (none)");
                return 0;
            }

            int liveContainer = 0, zdoButNoLiveContainer = 0, znetviewNoZdo = 0, noZnetview = 0, farParent = 0;
            var orphanRows = new List<string>();

            Array.Sort(cols, (a, b) =>
                (a.ClosestPoint(pos) - pos).sqrMagnitude.CompareTo((b.ClosestPoint(pos) - pos).sqrMagnitude));

            foreach (var c in cols)
            {
                if (c == null) continue;
                var nv = c.GetComponentInParent<ZNetView>();
                var attributed = nv != null
                                 && Vector3.Distance(nv.transform.position, c.transform.position) <= AttributionTolerance;

                var container = c.GetComponentInParent<Container>();
                var containerLive = container != null
                                    && container.gameObject.activeInHierarchy
                                    && container.GetComponent<ZNetView>()?.GetZDO() != null;

                bool isOrphan;
                string kind;
                if (nv == null) { noZnetview++; kind = "no-ZNetView"; isOrphan = true; }
                else if (!attributed) { farParent++; kind = "far-parent(suspect)"; isOrphan = true; }
                else if (nv.GetZDO() == null) { znetviewNoZdo++; kind = "ZNetView-no-ZDO"; isOrphan = true; }
                else if (containerLive) { liveContainer++; kind = "live-Container"; isOrphan = false; }
                else { zdoButNoLiveContainer++; kind = "ZDO-but-no-live-Container"; isOrphan = true; }

                if (!isOrphan) continue;

                var piece = c.GetComponentInParent<Piece>();
                var b = c.bounds;
                var onBake = IsOnBake(c.transform.position);
                orphanRows.Add(
                    $"    [{kind}] go={c.gameObject.name} piece={(piece != null ? piece.gameObject.name : "none")} " +
                    $"layer={c.gameObject.layer}:{SafeLayer(c.gameObject.layer)} type={c.GetType().Name} " +
                    $"dist={Vector3.Distance(c.ClosestPoint(pos), pos):F2}m " +
                    $"nview={(nv != null ? nv.name : "none")} Container={ClassifyContainer(container)} " +
                    $"bounds_y=[{b.min.y:F2}..{b.max.y:F2}] onBake={(onBake ? "yes" : "no")}");
            }

            sb.AppendLine(
                $"  bucket: live-Container={liveContainer} ZDO-but-no-live-Container={zdoButNoLiveContainer} " +
                $"ZNetView-no-ZDO={znetviewNoZdo} no-ZNetView={noZnetview} far-parent={farParent}");
            sb.AppendLine($"  ORPHAN colliders (collider present but NOT a scan-visible live Container): {orphanRows.Count}");
            foreach (var r in orphanRows) sb.AppendLine(r);

            return orphanRows.Count;
        }

        /// <summary>
        ///     Independent cross-check via the prefab-keyed iterative enumeration. Confirms the
        ///     sweep's chest-uid count is not a sector-filter artifact. Cannot see H4 (non-chest
        ///     collider) — that is the sweep/collider bridge's job — so this is a confirmation lens.
        /// </summary>
        private static void ReportPrefabCrossCheck(StringBuilder sb, ZDOMan zdoMan, ZNetScene scene,
            Vector3 pos, float radius, List<ZDO> sweep)
        {
            var names = new HashSet<string>(FallbackChestPrefabs, StringComparer.Ordinal);
            foreach (var z in sweep)
            {
                var n = scene.GetPrefab(z.GetPrefab())?.name;
                if (IsChestName(n)) names.Add(n);
            }

            sb.AppendLine($"--- prefab cross-check (GetAllZDOsWithPrefabIterative) within {radius:F1}m ---");
            foreach (var name in names.OrderBy(n => n))
            {
                var list = new List<ZDO>();
                var index = 0;
                // Pages until it returns true. Read-only.
                while (!zdoMan.GetAllZDOsWithPrefabIterative(name, list, ref index)) { }
                var uids = new HashSet<ZDOID>(
                    list.Where(z => z != null && Vector3.Distance(z.GetPosition(), pos) <= radius)
                        .Select(z => z.m_uid));
                if (uids.Count > 0)
                    sb.AppendLine($"  {name}: {uids.Count} distinct uid(s) near point");
            }
        }

        /// <summary>Count slot-31 bake sources near the point (the 42-vs-21 fact lives here).</summary>
        private static int ReportBakeSources(StringBuilder sb, Vector3 pos, float radius)
        {
            var sources = NavMeshBakeManager.LastSources;
            sb.AppendLine($"--- NavMesh bake sources within {radius:F1}m ---");
            if (sources == null || sources.Count == 0)
            {
                sb.AppendLine("  (no baked sources cached)");
                return 0;
            }

            int near = 0, box = 0, mesh = 0, modBox = 0, other = 0;
            foreach (var s in sources)
            {
                var srcPos = s.transform.MultiplyPoint3x4(Vector3.zero);
                if (Vector3.Distance(srcPos, pos) > radius) continue;
                near++;
                switch (s.shape)
                {
                    case NavMeshBuildSourceShape.Box: box++; break;
                    case NavMeshBuildSourceShape.Mesh: mesh++; break;
                    case NavMeshBuildSourceShape.ModifierBox: modBox++; break;
                    default: other++; break;
                }
            }

            sb.AppendLine($"  {near} source(s) (Mesh={mesh} Box={box} ModBox={modBox} Other={other})");
            return near;
        }

        private static bool IsOnBake(Vector3 worldPos)
        {
            var sources = NavMeshBakeManager.LastSources;
            if (sources == null) return false;
            foreach (var s in sources)
            {
                var srcPos = s.transform.MultiplyPoint3x4(Vector3.zero);
                if (Vector3.Distance(srcPos, worldPos) <= BakeMatchTolerance) return true;
            }

            return false;
        }

        private static string ClassifyContainer(Container container)
        {
            if (container == null) return "none";
            var nv = container.GetComponent<ZNetView>();
            if (nv == null || nv.GetZDO() == null) return "zdo-null";
            if (!container.gameObject.activeInHierarchy) return "inactive";
            if (!container.enabled) return "disabled";
            return "live";
        }

        private static bool IsChestName(string name)
        {
            return !string.IsNullOrEmpty(name)
                   && name.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Read the private ZNetView.m_ghost flag; null if reflection fails.</summary>
        private static bool? TryGhost(ZNetView nview)
        {
            try { return Traverse.Create(nview).Field<bool>("m_ghost").Value; }
            catch { return null; }
        }

        private static string GhostStr(bool? g)
        {
            return g == null ? "?" : g.Value ? "T" : "F";
        }

        private static string SafeLayer(int layer)
        {
            var n = LayerMask.LayerToName(layer);
            return string.IsNullOrEmpty(n) ? "(unnamed)" : n;
        }

        private static string DecisionHint(int chestUids, int orphanColliders, int ghostInstances)
        {
            if (chestUids >= 2)
                return "  DECISION: ≥2 distinct chest ZDO uids → H1 (a second persistent chest ZDO). " +
                       "Confirm host=2 vs client=1; the supernumerary uid is the corruptor.";
            if (chestUids == 1 && (orphanColliders > 0 || ghostInstances > 0))
                return "  DECISION: 1 chest uid + orphan/ghost present → H2/H3 (hollow/preserved instance) " +
                       "or H4 (non-chest collider). Inspect the ORPHAN row's prefab/Container.";
            return "  DECISION: no duplicate signature at this point/radius. Re-run on host AND client and " +
                   "diff chestUids + orphanColliders; widen radius if the chest sits >5m away.";
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
