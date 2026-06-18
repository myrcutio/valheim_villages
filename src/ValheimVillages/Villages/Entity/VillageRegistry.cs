using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     Static facade over durable villages — THE public interface for the HNA
    ///     region graph. Villages live in free-standing <c>vv_village</c> ZDOs
    ///     (mirrors <c>VillagerRecordTable</c>); this enumerates them by scanning
    ///     <c>ZDOMan.m_objectsByID</c> filtered to the carrier prefab hash, and caches
    ///     the one live <see cref="Village" /> per id in <see cref="s_live" /> so the
    ///     hydrated graph survives across lookups. Nothing outside this namespace
    ///     should touch a <see cref="Villager.AI.Navigation.RegionGraph" /> static —
    ///     graph access goes through a <see cref="Village" /> obtained here.
    /// </summary>
    public static class VillageRegistry
    {
        /// <summary>villageId → live wrapper (authoritative in-memory store, holds the live graph).</summary>
        private static readonly Dictionary<string, Village> s_live = new();

        /// <summary>
        ///     Mint a new durable village anchored at <paramref name="anchor" /> (a registry
        ///     position). Founder position falls back to the local player's position read
        ///     INSIDE Create when the placer cannot be determined at the call site.
        /// </summary>
        public static Village Create(Vector3 anchor)
        {
            var founderPos = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : anchor;
            return Create(anchor, founderPos);
        }

        /// <summary>
        ///     Mint a new durable village. <paramref name="founderPos" /> is the PLACING
        ///     PLAYER's position captured ONCE at mint, threaded from the registry-placement
        ///     call site. Founder is captured only here (real mint), never on relink.
        /// </summary>
        public static Village Create(Vector3 anchor, Vector3 founderPos)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                Plugin.Log?.LogError("[VillageRegistry] Cannot create village: ZDOMan not ready");
                return null;
            }

            var zdo = zdoMan.CreateNewZDO(anchor, VillagePrefabFactory.VillagePrefabHash);
            // CreateNewZDO does NOT persist the prefab hash — set it so GetPrefab()
            // returns the carrier hash (how EnumerateAll finds villages).
            zdo.SetPrefab(VillagePrefabFactory.VillagePrefabHash);
            // ZDOMan.GetSaveClone writes every Persistent ZDO regardless of owner, so this
            // flag alone makes the village survive a world save/reload (ownership is irrelevant).
            zdo.Persistent = true;

            var id = Guid.NewGuid().ToString();
            zdo.Set(Village.IdKey, id);

            // Legacy single-anchor key kept for FindNearAnchor; new named anchors are the
            // durable model: registry = the placement point, founder = the placer at mint.
            var village = new Village(zdo) { Anchor = anchor };
            village.SetAnchor(VillageAnchor.Registry, anchor);
            village.SetAnchor(VillageAnchor.Founder, founderPos);
            s_live[id] = village;

            Plugin.Log?.LogInfo(
                $"[VillageRegistry] Created village {id} at ({anchor.x:F1},{anchor.y:F1},{anchor.z:F1}) " +
                $"founder=({founderPos.x:F1},{founderPos.y:F1},{founderPos.z:F1})");
            return village;
        }

        /// <summary>Every durable village in the world. Returns the cached live wrapper per id.</summary>
        public static IEnumerable<Village> EnumerateAll()
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) yield break;

            var objectsByID = Traverse.Create(zdoMan)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null) yield break;

            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null) continue;
                if (zdo.GetPrefab() != VillagePrefabFactory.VillagePrefabHash) continue;
                var id = zdo.GetString(Village.IdKey);
                if (string.IsNullOrEmpty(id)) continue;

                if (!s_live.TryGetValue(id, out var village))
                {
                    village = new Village(zdo);
                    s_live[id] = village;
                }

                yield return village;
            }
        }

        /// <summary>Villages that currently have a non-empty, available region graph.</summary>
        public static IEnumerable<Village> EnumerateWithGraph()
        {
            return EnumerateAll().Where(v => v.HasGraph);
        }

        public static bool IsAnyAvailable => EnumerateWithGraph().Any();

        /// <summary>Live graphs of all villages that have one — replaces RegionGraph.GetAll().</summary>
        public static IEnumerable<RegionGraph> AllGraphs() => EnumerateWithGraph().Select(v => v.Graph);

        /// <summary>The graph owning <paramref name="pos" />, or null — replaces RegionGraph.GetNearest().</summary>
        public static RegionGraph GraphAt(Vector3 pos) => GetVillageAt(pos)?.Graph;

        public static Village FindById(string villageId)
        {
            if (string.IsNullOrEmpty(villageId)) return null;
            if (s_live.TryGetValue(villageId, out var cached) && cached.IsValid) return cached;
            foreach (var v in EnumerateAll())
                if (v.VillageId == villageId)
                    return v;
            return null;
        }

        /// <summary>
        ///     Resolve the village that owns <paramref name="pos" />: first by exact
        ///     graph coverage (<see cref="Villager.AI.Navigation.RegionGraph.PointToRegionId" />),
        ///     else the nearest village whose graph origin is closest, else null. Only
        ///     villages with an available graph are considered — absence is reported,
        ///     never fabricated. Replaces VillageAreaManager.TryGetContainingVillageKey
        ///     and RegionGraph.GetNearest.
        /// </summary>
        public static Village GetVillageAt(Vector3 pos)
        {
            Village nearest = null;
            var bestDist = float.MaxValue;

            foreach (var v in EnumerateAll())
            {
                var graph = v.Graph;
                if (graph == null || !graph.IsAvailable) continue;

                if (!string.IsNullOrEmpty(graph.PointToRegionId(pos)))
                    return v; // exact coverage wins immediately

                if (!graph.GetOrigin(out var ox, out var oz)) continue;
                var dx = pos.x - ox;
                var dz = pos.z - oz;
                var dist = dx * dx + dz * dz;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = v;
                }
            }

            return nearest;
        }

        /// <summary>
        ///     The existing village whose anchor sits within <paramref name="radius" /> of
        ///     <paramref name="anchor" />, or null. Pure lookup — never mints. Used by
        ///     resolve-only callers (partition, recruit-at-registry) so they can find a
        ///     village before its graph is built without fabricating one.
        /// </summary>
        public static Village FindNearAnchor(Vector3 anchor, float radius = 30f)
        {
            Village nearest = null;
            var bestDist = radius * radius;

            foreach (var v in EnumerateAll())
            {
                var a = v.Anchor;
                var dx = anchor.x - a.x;
                var dz = anchor.z - a.z;
                var dist = dx * dx + dz * dz;
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    nearest = v;
                }
            }

            return nearest;
        }

        /// <summary>
        ///     The village whose graph actually CONTAINS <paramref name="pos" />
        ///     (<see cref="Villager.AI.Navigation.RegionGraph.PointToRegionId" /> resolves),
        ///     or null. No nearest fallback — used where "inside this village" must be exact,
        ///     e.g. the registry-placement decision of whether to link vs mint.
        /// </summary>
        public static Village GetVillageCovering(Vector3 pos)
        {
            foreach (var v in EnumerateAll())
            {
                var g = v.Graph;
                if (g != null && g.IsAvailable && !string.IsNullOrEmpty(g.PointToRegionId(pos)))
                    return v;
            }

            return null;
        }

        /// <summary>
        ///     Find an existing village near <paramref name="anchor" /> (re-link an orphaned
        ///     village when a registry is re-placed), else mint a new one. THIS IS THE ONLY
        ///     MINT PATH — call it only from the registry-placement flow. Every other caller
        ///     must use <see cref="FindNearAnchor" />/<see cref="GetVillageAt" />/<see cref="FindById" />
        ///     and fail rather than fabricate a village.
        /// </summary>
        public static Village GetOrCreateAt(Vector3 anchor, float relinkRadius = 30f)
        {
            var founderPos = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : anchor;
            return GetOrCreateAt(anchor, founderPos, relinkRadius);
        }

        /// <summary>
        ///     <see cref="GetOrCreateAt(Vector3,float)" /> with an explicit founder position
        ///     threaded from the placement site. Founder is only consumed on a real mint
        ///     (<see cref="Create(Vector3,Vector3)" />); a relink ignores it.
        /// </summary>
        public static Village GetOrCreateAt(Vector3 anchor, Vector3 founderPos, float relinkRadius = 30f)
        {
            return FindNearAnchor(anchor, relinkRadius) ?? Create(anchor, founderPos);
        }

        // ---------------------------------------------------------------------
        // Anchor triad — the self-healing connectivity backbone of a village.
        // ---------------------------------------------------------------------

        /// <summary>Candidate-probe ring radii (metres) around the registry anchor.</summary>
        private static readonly float[] s_triadProbeRadii = { 1.5f, 3f, 5f, 7f, 9f };

        /// <summary>Dedup distance (metres) for candidate points.</summary>
        private const float TriadDedupDist = 1f;

        /// <summary>
        ///     Minimum free navmesh radius (metres) an anchor / villager-seed sample must clear.
        ///     ~2.5× a humanoid (slot-31 agent radius 0.4) so anchors land on open ground with room
        ///     for a larger body — never on a sliver like a station top or right against a carved
        ///     fire/wall edge. Used as the clearance gate for all anchor sampling.
        /// </summary>
        public const float AnchorSampleClearance = 1.0f;

        /// <summary>The slot-31 villager-agent query filter (same shape used everywhere).</summary>
        private static NavMeshQueryFilter AgentFilter()
        {
            return new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
        }

        /// <summary>
        ///     True when both points sample onto the slot-31 villager navmesh AND a complete
        ///     slot-31 path connects them. False if the agent isn't registered or the mesh is
        ///     unqueryable (either endpoint fails to sample, or the path is partial). This is
        ///     the single connectivity predicate the triad algorithm is built on.
        /// </summary>
        public static bool AnchorsConnected(Vector3 a, Vector3 b)
        {
            if (!VillagerAgentType.IsRegistered) return false;
            var filter = AgentFilter();
            if (!NavMesh.SamplePosition(a, out var ha, 3f, filter)) return false;
            if (!NavMesh.SamplePosition(b, out var hb, 3f, filter)) return false;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(ha.position, hb.position, filter, path)) return false;
            return path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>
        ///     Create / repair / validate the village's anchor triad — three walkable,
        ///     slot-31-on-mesh points near the registry that are mutually connected and each
        ///     connected to the founder F. Idempotent and cheap on the already-valid path
        ///     (~4 CalculatePath checks). The partition entry point: called after the graph is
        ///     saved, before the area refresh.
        ///     <para>USER DECISIONS honoured: max-separation spread layout; fewer than 3
        ///     placeable anchors fails loud (sets <see cref="Village.IsInvalid" />, logs an
        ///     error, pings the registry on the map); a split between the two survivors is
        ///     resolved founder-component-wins.</para>
        /// </summary>
        public static void EnsureAnchorTriad(Village village)
        {
            if (village == null) return;

            // No mesh yet — a later partition will run this. (Triad needs a baked navmesh
            // to test connectivity; we can't validate against a graph that isn't built.)
            if (!village.HasGraph) return;

            // Seed F: founder, else registry, else legacy single anchor.
            if (!TryGetTriadSource(village, out var f)) return; // founder lost / mesh not ready — transient
            // If F itself doesn't sample onto the slot-31 mesh, the mesh isn't ready for F;
            // don't fail the village on a transient — defer to a later partition.
            if (!VillagerAgentType.IsRegistered) return;
            var seedFilter = AgentFilter();
            if (!NavMesh.SamplePosition(f, out _, 3f, seedFilter)) return;

            var existing = village.TriadAnchors;

            // VALID ALREADY? 3 anchors, all mutually connected, each connected to F.
            // Keep this path cheap: 3 pairwise + 3 to-F = the only checks.
            if (existing.Count == 3 && AllMutuallyConnected(existing) && AllConnectedTo(existing, f))
            {
                if (village.IsInvalid) village.IsInvalid = false; // defensive heal
                return;
            }

            // ---- CREATE or REPAIR ----
            // Candidate pool: ring/grid around the registry, each on-mesh AND with a complete
            // path FROM F. Dedup ~1m.
            if (!village.TryGetAnchor(VillageAnchor.Registry, out var registry))
                registry = village.Anchor;
            var pool = BuildCandidatePool(registry, f);

            // Survivors: existing triad anchors still connected to F.
            var survivors = new List<Vector3>();
            foreach (var e in existing)
                if (AnchorsConnected(e, f))
                    survivors.Add(e);

            // Split repair (founder-component-wins): drop survivors that aren't mutually
            // connected to the largest connected survivor sub-set in F's component. Since each
            // survivor is already F-connected, F-connectivity + mutual-connectivity coincide;
            // keep the largest mutually-connected subset of survivors.
            survivors = LargestMutuallyConnectedSubset(survivors);

            List<Vector3> chosen;
            if (survivors.Count >= 2)
            {
                // REPAIR: keep survivors, fill the remaining slots from the pool, choosing
                // points connected to ALL survivors (and F) and maximizing min pairwise dist.
                chosen = new List<Vector3>(survivors);
                FillMaxSeparation(chosen, pool, f, 3);
            }
            else
            {
                // CREATE (or single survivor — treat as create, it'll naturally be re-picked):
                // greedily pick 3 from the pool, max-separation, mutually connected + F-connected.
                chosen = GreedyMaxSeparation(pool, registry, f, 3);
            }

            if (chosen.Count < 3)
            {
                village.IsInvalid = true;
                Plugin.Log?.LogError(
                    $"[Triad] village {village.VillageId}: only {chosen.Count}/3 connected anchors " +
                    "near registry — village INVALID");
                PingRegistry(registry,
                    $"Village invalid: only {chosen.Count}/3 connected anchors near registry");
                return;
            }

            for (var i = 0; i < 3; i++)
                village.SetAnchor(VillageAnchor.Triad[i], chosen[i]);
            village.IsInvalid = false;

            var minSep = MinPairwiseDistance(chosen);
            Plugin.Log?.LogInfo(
                $"[Triad] village {village.VillageId}: triad OK " +
                $"a0=({chosen[0].x:F1},{chosen[0].y:F1},{chosen[0].z:F1}) " +
                $"a1=({chosen[1].x:F1},{chosen[1].y:F1},{chosen[1].z:F1}) " +
                $"a2=({chosen[2].x:F1},{chosen[2].y:F1},{chosen[2].z:F1}) " +
                $"(minSep={minSep:F1}m)");
        }

        /// <summary>
        ///     Resolve a walkable point near <paramref name="near" /> that is connected to the
        ///     village's main component. Path source preference: any present triad anchor, else
        ///     the founder, else the registry. False when the village is null/invalid or no
        ///     connected seed resolves. THE replacement for the self-referential
        ///     <c>TryResolveApproach(X, X, …)</c> calls that landed on the registry island.
        /// </summary>
        public static bool TryResolveVillagerSeed(Village village, Vector3 near, out Vector3 seed)
        {
            seed = near;
            if (village == null || village.IsInvalid) return false;

            Vector3 source;
            var triad = village.TriadAnchors;
            if (triad.Count > 0) source = triad[0];
            else if (village.TryGetAnchor(VillageAnchor.Founder, out var founder)) source = founder;
            else if (village.TryGetAnchor(VillageAnchor.Registry, out var registry)) source = registry;
            else return false;

            return VillagerMovement.TryResolveApproach(near, source, null, out seed, AnchorSampleClearance);
        }

        // ---- triad helpers ----

        /// <summary>Founder, else registry, else legacy single anchor; false if none set.</summary>
        private static bool TryGetTriadSource(Village village, out Vector3 source)
        {
            if (village.TryGetAnchor(VillageAnchor.Founder, out source)) return true;
            if (village.TryGetAnchor(VillageAnchor.Registry, out source)) return true;
            source = village.Anchor;
            return source != Vector3.zero;
        }

        private static bool AllConnectedTo(IReadOnlyList<Vector3> pts, Vector3 to)
        {
            foreach (var p in pts)
                if (!AnchorsConnected(p, to))
                    return false;
            return true;
        }

        private static bool AllMutuallyConnected(IReadOnlyList<Vector3> pts)
        {
            for (var i = 0; i < pts.Count; i++)
                for (var j = i + 1; j < pts.Count; j++)
                    if (!AnchorsConnected(pts[i], pts[j]))
                        return false;
            return true;
        }

        /// <summary>
        ///     Probe a ring/grid around the registry and keep on-mesh points with a complete
        ///     path FROM F. Deduped to ~<see cref="TriadDedupDist" />.
        /// </summary>
        private static List<Vector3> BuildCandidatePool(Vector3 registry, Vector3 f)
        {
            var pool = new List<Vector3>();
            void Consider(Vector3 probe)
            {
                if (!VillagerMovement.TryResolveApproach(probe, f, null, out var approach, AnchorSampleClearance)) return;
                foreach (var existing in pool)
                    if ((existing - approach).sqrMagnitude < TriadDedupDist * TriadDedupDist)
                        return;
                pool.Add(approach);
            }

            Consider(registry); // center
            float[] dirsX = { 0f, 1f, 1f, 0f, -1f, -1f, -1f, 0f, 1f };
            float[] dirsZ = { 0f, 0f, 1f, 1f, 1f, 0f, -1f, -1f, -1f };
            foreach (var r in s_triadProbeRadii)
                for (var d = 1; d < dirsX.Length; d++) // skip center (index 0) — already added
                    Consider(registry + new Vector3(dirsX[d] * r, 0f, dirsZ[d] * r));

            return pool;
        }

        /// <summary>
        ///     Largest subset of <paramref name="pts" /> that is mutually connected. Greedy
        ///     over candidate "seed" members, taking each member compatible with all already
        ///     kept — sufficient for the tiny survivor sets (≤3) the triad ever holds.
        /// </summary>
        private static List<Vector3> LargestMutuallyConnectedSubset(List<Vector3> pts)
        {
            List<Vector3> best = new();
            for (var s = 0; s < pts.Count; s++)
            {
                var subset = new List<Vector3> { pts[s] };
                for (var i = 0; i < pts.Count; i++)
                {
                    if (i == s) continue;
                    var ok = true;
                    foreach (var k in subset)
                        if (!AnchorsConnected(pts[i], k)) { ok = false; break; }
                    if (ok) subset.Add(pts[i]);
                }

                if (subset.Count > best.Count) best = subset;
            }

            return best;
        }

        /// <summary>
        ///     Fill <paramref name="chosen" /> up to <paramref name="target" /> members from
        ///     <paramref name="pool" />, each connected to F and to every already-chosen point,
        ///     greedily maximizing the min distance to the chosen set (max-separation spread).
        /// </summary>
        private static void FillMaxSeparation(
            List<Vector3> chosen, List<Vector3> pool, Vector3 f, int target)
        {
            while (chosen.Count < target)
            {
                var bestMinDist = -1f;
                var bestIdx = -1;
                for (var i = 0; i < pool.Count; i++)
                {
                    var c = pool[i];
                    if (TooClose(chosen, c)) continue;
                    if (!AnchorsConnected(c, f)) continue;
                    if (!ConnectedToAll(c, chosen)) continue;

                    var minDist = MinDistanceTo(chosen, c);
                    if (minDist > bestMinDist)
                    {
                        bestMinDist = minDist;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0) break; // nothing else qualifies
                chosen.Add(pool[bestIdx]);
                pool.RemoveAt(bestIdx);
            }
        }

        /// <summary>
        ///     Greedily pick <paramref name="target" /> points from the pool: first the one
        ///     farthest from the registry, then each next maximizing the min distance to the
        ///     already-picked set while staying connected to all picked AND to F.
        /// </summary>
        private static List<Vector3> GreedyMaxSeparation(
            List<Vector3> pool, Vector3 registry, Vector3 f, int target)
        {
            var chosen = new List<Vector3>();
            var working = new List<Vector3>(pool);

            // First pick: farthest from registry (and F-connected — pool already guarantees it).
            var bestDist = -1f;
            var bestIdx = -1;
            for (var i = 0; i < working.Count; i++)
            {
                var d = (working[i] - registry).sqrMagnitude;
                if (d > bestDist) { bestDist = d; bestIdx = i; }
            }

            if (bestIdx < 0) return chosen;
            chosen.Add(working[bestIdx]);
            working.RemoveAt(bestIdx);

            FillMaxSeparation(chosen, working, f, target);
            return chosen;
        }

        private static bool ConnectedToAll(Vector3 c, IReadOnlyList<Vector3> pts)
        {
            foreach (var p in pts)
                if (!AnchorsConnected(c, p))
                    return false;
            return true;
        }

        private static bool TooClose(IReadOnlyList<Vector3> pts, Vector3 c)
        {
            foreach (var p in pts)
                if ((p - c).sqrMagnitude < TriadDedupDist * TriadDedupDist)
                    return true;
            return false;
        }

        private static float MinDistanceTo(IReadOnlyList<Vector3> pts, Vector3 c)
        {
            var min = float.MaxValue;
            foreach (var p in pts)
            {
                var d = Vector3.Distance(p, c);
                if (d < min) min = d;
            }

            return min;
        }

        private static float MinPairwiseDistance(IReadOnlyList<Vector3> pts)
        {
            var min = float.MaxValue;
            for (var i = 0; i < pts.Count; i++)
                for (var j = i + 1; j < pts.Count; j++)
                {
                    var d = Vector3.Distance(pts[i], pts[j]);
                    if (d < min) min = d;
                }

            return min == float.MaxValue ? 0f : min;
        }

        /// <summary>
        ///     Best-effort loud ping when a village goes invalid. The game's
        ///     <c>Minimap.AddPin</c> overload carries a PlatformUserID param from a separate
        ///     assembly, so a direct call isn't trivially bindable (FragmentCombiner uses
        ///     reflection for it); per spec we therefore fall back to a center-screen message
        ///     on the local player.
        /// </summary>
        private static void PingRegistry(Vector3 registry, string message)
        {
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
        }

        /// <summary>
        ///     Rebuild the live cache from the world's village ZDOs and hydrate each
        ///     graph from its blob. Called once after world load by the
        ///     <c>village_index</c> task. Returns (villages, withGraph) for logging.
        /// </summary>
        public static (int villages, int withGraph) HydrateAll()
        {
            s_live.Clear();
            var villages = 0;
            var withGraph = 0;
            foreach (var v in EnumerateAll())
            {
                villages++;
                v.HydrateGraphFromZdo();
                if (v.HasGraph) withGraph++;
            }

            return (villages, withGraph);
        }

        [RegisterCleanup]
        public static void ClearAll()
        {
            foreach (var v in s_live.Values) v.Graph?.Clear();
            s_live.Clear();
        }
    }
}
