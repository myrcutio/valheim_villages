using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Proof-of-concept room system: catalogs furniture/stations per NAV REGION
    ///     and tags each region with the room roles it qualifies for (bedroom, dining,
    ///     workshop, …).
    ///     <para>Deliberately loose. A <see cref="RegionGraph" /> region is a navmesh
    ///     patch, not a wall-bounded room — but in this village vertically-stacked piece
    ///     layers and leveled areas share a region and don't split by doorway, so
    ///     "region == room" is a usable first approximation. True wall/doorway boundary
    ///     segmentation is deferred; this just proves the catalog + tagging pipeline.</para>
    ///     <para>Scans like <see cref="VillagePoiRegistry" /> (AABB OverlapBox) but
    ///     buckets each piece by <see cref="RegionGraph.PointToRegionId" /> instead of by
    ///     village. Recomputed per partition via <see cref="RefreshFor" />.</para>
    /// </summary>
    public static class VillageRoomCatalog
    {
        /// <summary>Function categories a piece can contribute to a room.</summary>
        public enum Feature { Fire, Seat, Table, Bed, WorkStation, Storage, Farm }

        /// <summary>Per-region furnishing tally + derived room roles for one village.</summary>
        public sealed class RegionRoom
        {
            public string RegionId;
            public readonly Dictionary<Feature, int> Counts = new();
            public readonly List<string> Roles = new();
            public Vector3 Center;
            public int PieceCount;
        }

        // villageKey -> regionId -> room
        private static readonly Dictionary<string, Dictionary<string, RegionRoom>> s_roomsByVillage = new();

        /// <summary>Broad-phase XZ padding (m) around the village polygon AABB; matches
        /// <see cref="VillageStationRegistry" />. Only a coarse prefilter — region
        /// bucketing decides the real placement.</summary>
        private const float ScanPadXZ = 12f;

        /// <summary>(Re)scan and re-tag the rooms inside the given village area.</summary>
        public static void RefreshFor(VillageArea area)
        {
            if (area == null || area.Waypoints == null || area.Waypoints.Count < 3) return;
            var graph = VillageRegistry.FindById(area.VillageId)?.Graph;
            if (graph == null) return;

            float minX = float.MaxValue, minZ = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue, maxY = float.MinValue;
            foreach (var wp in area.Waypoints)
            {
                if (wp.x < minX) minX = wp.x;
                if (wp.x > maxX) maxX = wp.x;
                if (wp.z < minZ) minZ = wp.z;
                if (wp.z > maxZ) maxZ = wp.z;
                if (wp.y < minY) minY = wp.y;
                if (wp.y > maxY) maxY = wp.y;
            }

            var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            var half = new Vector3((maxX - minX) * 0.5f + ScanPadXZ, (maxY - minY) * 0.5f + 20f,
                (maxZ - minZ) * 0.5f + ScanPadXZ);

            var rooms = new Dictionary<string, RegionRoom>();
            var seen = new HashSet<GameObject>();
            foreach (var col in Physics.OverlapBox(center, half, Quaternion.identity))
            {
                if (col == null || col.gameObject == null) continue;
                var piece = col.GetComponentInParent<Piece>();
                var go = piece != null ? piece.gameObject : col.gameObject;
                if (!seen.Add(go)) continue;
                if (!TryClassifyFeature(go, out var feature)) continue;

                var pos = go.transform.position;
                var regionId = graph.PointToRegionId(pos);
                if (string.IsNullOrEmpty(regionId))
                    graph.TryFindNearestLookupCell(pos, null, out _, out regionId, 3f);
                if (string.IsNullOrEmpty(regionId)) continue;

                if (!rooms.TryGetValue(regionId, out var room))
                    rooms[regionId] = room = new RegionRoom { RegionId = regionId };
                room.Counts.TryGetValue(feature, out var c);
                room.Counts[feature] = c + 1;
                room.Center += pos;
                room.PieceCount++;
            }

            foreach (var room in rooms.Values)
            {
                if (room.PieceCount > 0) room.Center /= room.PieceCount;
                AssignRoles(room);
            }

            s_roomsByVillage[area.VillageId] = rooms;
            Plugin.Log?.LogInfo(
                $"[VillageRoomCatalog] {area.VillageId}: {rooms.Count} furnished region(s)");
        }

        /// <summary>Remove a village's room cache (called on UnregisterArea).</summary>
        public static void RemoveFor(string villageKey) => s_roomsByVillage.Remove(villageKey);

        /// <summary>Tagged rooms for the village containing the position (empty if none).</summary>
        public static IReadOnlyCollection<RegionRoom> GetRooms(Vector3 position)
        {
            var village = VillageRegistry.GetVillageAt(position);
            if (village == null)
                return System.Array.Empty<RegionRoom>();
            return s_roomsByVillage.TryGetValue(village.VillageId, out var rooms)
                ? rooms.Values
                : (IReadOnlyCollection<RegionRoom>)System.Array.Empty<RegionRoom>();
        }

        private static int Count(RegionRoom r, Feature f) => r.Counts.TryGetValue(f, out var c) ? c : 0;

        // A region can earn several roles at once — coarse regions span multiple
        // functional areas, so we tag every role it qualifies for rather than forcing
        // one label. Thresholds are intentionally lenient for the POC.
        private static void AssignRoles(RegionRoom r)
        {
            if (Count(r, Feature.Bed) >= 1) r.Roles.Add("Bedroom");
            if (Count(r, Feature.Table) >= 1 && Count(r, Feature.Seat) >= 1)
                r.Roles.Add(Count(r, Feature.Fire) >= 1 ? "Dining Hall" : "Dining Room");
            if (Count(r, Feature.WorkStation) >= 1) r.Roles.Add("Workshop");
            if (Count(r, Feature.Farm) >= 1) r.Roles.Add("Garden");
            if (Count(r, Feature.Storage) >= 1) r.Roles.Add("Storage");
            if (r.Roles.Count == 0 && Count(r, Feature.Fire) >= 1) r.Roles.Add("Hearth");
        }

        /// <summary>
        ///     Classify a piece into a single room feature, or none. Components/reflection
        ///     where Valheim exposes them; a table/farm name heuristic otherwise. Seating
        ///     is detected like stations — generically via the <see cref="Chair" />
        ///     component OR any component exposing the sit-attach field
        ///     (<c>m_attachAnimation</c>) — so modded seats and sit-able benches count with
        ///     no prefab/type list.
        /// </summary>
        private static bool TryClassifyFeature(GameObject go, out Feature feature)
        {
            feature = default;
            if (go == null) return false;

            if (go.GetComponentInParent<Bed>() != null) { feature = Feature.Bed; return true; }
            if (VillageStationRegistry.TryClassifyStation(go, out _)) { feature = Feature.WorkStation; return true; }
            if (IsSittable(go)) { feature = Feature.Seat; return true; }
            if (go.GetComponentInParent<Fireplace>() != null) { feature = Feature.Fire; return true; }
            if (go.GetComponentInParent<Container>() != null) { feature = Feature.Storage; return true; }

            var name = go.name.ToLowerInvariant();
            if (name.Contains("table")) { feature = Feature.Table; return true; }
            if (name.Contains("cultivat") || name.Contains("sapling") || name.Contains("plant_"))
            {
                feature = Feature.Farm;
                return true;
            }

            return false;
        }

        // Per-component-type cache: does the type expose the Chair sit-attach field
        // `m_attachAnimation`? That field drives Humanoid.AttachStart (the sit pose), so
        // its presence marks "you can sit on this" — the seating analog of the
        // `m_conversion` station signal: list-free and modded-seat-friendly.
        private static readonly Dictionary<Type, bool> s_sitTypeCache = new();

        private static bool HasSitField(Type t)
        {
            if (s_sitTypeCache.TryGetValue(t, out var has)) return has;
            has = t.GetField("m_attachAnimation",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null;
            s_sitTypeCache[t] = has;
            return has;
        }

        /// <summary>True when the piece carries something you can sit on — the canonical
        /// <see cref="Chair" /> component, or any component exposing the sit-attach field.</summary>
        private static bool IsSittable(GameObject go)
        {
            var root = go.GetComponentInParent<Piece>();
            var host = root != null ? root.gameObject : go;
            foreach (var comp in host.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;
                if (comp is Chair) return true;
                if (HasSitField(comp.GetType())) return true;
            }

            return false;
        }

        [DevCommand("List per-region room roles + furnishings for the village containing the player",
            Name = "vv_rooms")]
        public static void DumpRooms()
        {
            var player = Player.m_localPlayer;
            var sb = new StringBuilder();
            if (player == null)
            {
                sb.AppendLine("[vv_rooms] No local player.");
            }
            else if (VillageRegistry.GetVillageAt(player.transform.position) is not { } village)
            {
                sb.AppendLine("[vv_rooms] player is not inside any registered village.");
            }
            else if (!s_roomsByVillage.TryGetValue(village.VillageId, out var rooms) || rooms.Count == 0)
            {
                sb.AppendLine($"[vv_rooms] village {village.VillageId}: no furnished regions cached.");
            }
            else
            {
                sb.AppendLine($"[vv_rooms] village {village.VillageId}: {rooms.Count} furnished region(s)");
                foreach (var r in rooms.Values)
                {
                    var roles = r.Roles.Count > 0 ? string.Join(", ", r.Roles) : "(unlabeled)";
                    var feats = new List<string>();
                    foreach (var kv in r.Counts) feats.Add($"{kv.Key}×{kv.Value}");
                    sb.AppendLine(
                        $"  [{r.RegionId}] {roles} @ ({r.Center.x:F1},{r.Center.y:F1},{r.Center.z:F1}) — " +
                        string.Join(" ", feats));
                }
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        [RegisterCleanup]
        public static void Clear() => s_roomsByVillage.Clear();
    }
}
