using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;

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

        /// <summary>Mint a new durable village anchored at <paramref name="anchor" /> (a registry position).</summary>
        public static Village Create(Vector3 anchor)
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
            zdo.Persistent = true;
            // On a dedicated server the host must own the ZDO so it's written to the
            // world .db (same fix VillagerRecordTable.Create applies).
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                zdo.SetOwner(ZNet.GetUID());

            var id = Guid.NewGuid().ToString();
            zdo.Set(Village.IdKey, id);

            var village = new Village(zdo) { Anchor = anchor };
            s_live[id] = village;

            Plugin.Log?.LogInfo(
                $"[VillageRegistry] Created village {id} at ({anchor.x:F1},{anchor.y:F1},{anchor.z:F1})");
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
            return FindNearAnchor(anchor, relinkRadius) ?? Create(anchor);
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
