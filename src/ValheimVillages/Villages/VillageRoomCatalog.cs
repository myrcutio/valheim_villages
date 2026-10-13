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
    ///     Catalogs furniture/stations per NAV REGION and tags each region with the room roles it
    ///     qualifies for (bedroom, dining hall, workshop, …). Villager level requirements poll
    ///     this rather than measuring a radius around a villager, so "the village has a warm
    ///     room" is a property of the village's rooms and not of where one villager happens to
    ///     sleep.
    ///     <para>Deliberately loose. A <see cref="RegionGraph" /> region is a navmesh patch, not a
    ///     wall-bounded room — but vertically-stacked piece layers and levelled areas share a
    ///     region and don't split by doorway, so "region == room" is a usable approximation. True
    ///     wall/doorway segmentation is deferred.</para>
    ///     <para>Scans like <see cref="VillagePoiRegistry" /> (AABB OverlapBox) but buckets each
    ///     piece by <see cref="RegionGraph.PointToRegionId" /> instead of by village. Recomputed
    ///     per partition via <see cref="RefreshFor" />.</para>
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

            /// <summary>
            ///     The warming fires found in this region. Held as live components rather than
            ///     counted, because whether a fire is LIT changes as its fuel burns: the catalog
            ///     snapshots where the fires ARE (stable, changes only when you build), and
            ///     <see cref="HasWarmth" /> asks whether one is burning at the moment it is
            ///     asked. Counting at scan time would either freeze "warm" on forever or miss a
            ///     relit fire until the next partition.
            /// </summary>
            public readonly List<Fireplace> WarmingFires = new();

            public Vector3 Center;
            public int PieceCount;
        }

        // villageId -> regionId -> room
        private static readonly Dictionary<string, Dictionary<string, RegionRoom>> s_roomsByVillage = new();

        /// <summary>Broad-phase XZ padding (m) around the village polygon AABB. Coarse prefilter
        /// only — region bucketing decides the real placement.</summary>
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

            // Counted so the log can say how many fires were seen versus how many actually
            // warm. "My village has no warmth" and "my village is lit entirely by torches" are
            // the same thing in-world and need telling apart without guesswork.
            var fireplacesSeen = 0;
            foreach (var col in Physics.OverlapBox(center, half, Quaternion.identity))
            {
                if (col == null || col.gameObject == null) continue;
                var piece = col.GetComponentInParent<Piece>();
                var go = piece != null ? piece.gameObject : col.gameObject;
                if (!seen.Add(go)) continue;
                if (go.GetComponentInParent<Fireplace>() != null) fireplacesSeen++;
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

                if (feature == Feature.Fire)
                {
                    var fireplace = go.GetComponentInParent<Fireplace>() ?? go.GetComponent<Fireplace>();
                    if (fireplace != null) room.WarmingFires.Add(fireplace);
                }
            }

            foreach (var room in rooms.Values)
            {
                if (room.PieceCount > 0) room.Center /= room.PieceCount;
                AssignRoles(room);
            }

            s_roomsByVillage[area.VillageId] = rooms;

            var warming = 0;
            foreach (var room in rooms.Values) warming += room.WarmingFires.Count;

            Plugin.Log?.LogInfo(
                $"[VillageRoomCatalog] {area.VillageId}: {rooms.Count} furnished region(s); " +
                $"{fireplacesSeen} fireplace(s) seen, {warming} of them warm " +
                $"({fireplacesSeen - warming} light-only, e.g. torches)");
        }

        /// <summary>Remove a village's room cache (called on UnregisterArea).</summary>
        public static void RemoveFor(string villageId) => s_roomsByVillage.Remove(villageId);

        /// <summary>Tagged rooms for one village (empty if it has none cached).</summary>
        public static IReadOnlyCollection<RegionRoom> GetRooms(string villageId)
        {
            if (string.IsNullOrEmpty(villageId)) return Array.Empty<RegionRoom>();
            return s_roomsByVillage.TryGetValue(villageId, out var rooms)
                ? rooms.Values
                : (IReadOnlyCollection<RegionRoom>)Array.Empty<RegionRoom>();
        }

        /// <summary>Tagged rooms for the village containing the position (empty if none).</summary>
        public static IReadOnlyCollection<RegionRoom> GetRooms(Vector3 position)
        {
            return GetRooms(VillageRegistry.GetVillageAt(position)?.VillageId);
        }

        /// <summary>
        ///     Does any room in this village have a warming fire that is burning RIGHT NOW?
        ///     One lit fire anywhere in the village is enough — warmth is a property the village
        ///     either provides or does not.
        /// </summary>
        public static bool HasWarmth(string villageId)
        {
            foreach (var room in GetRooms(villageId))
                foreach (var fire in room.WarmingFires)
                    // Unity's null check also covers a component destroyed by a zone unload, in
                    // which case the village reads as cold until the next partition rescans.
                    if (fire != null && fire.IsBurning())
                        return true;

            return false;
        }

        /// <summary>Does any room in this village contain at least one of this feature?</summary>
        public static bool HasFeature(string villageId, Feature feature)
        {
            if (feature == Feature.Fire) return HasWarmth(villageId);

            foreach (var room in GetRooms(villageId))
                if (Count(room, feature) > 0)
                    return true;

            return false;
        }

        /// <summary>Does any single room carry every one of these features at once?</summary>
        public static bool HasRoomWithAll(string villageId, params Feature[] features)
        {
            if (features == null || features.Length == 0) return false;

            foreach (var room in GetRooms(villageId))
            {
                var all = true;
                foreach (var feature in features)
                {
                    var present = feature == Feature.Fire
                        ? AnyBurning(room)
                        : Count(room, feature) > 0;
                    if (present) continue;
                    all = false;
                    break;
                }

                if (all) return true;
            }

            return false;
        }

        /// <summary>
        ///     Parse a feature name as authored in <c>tieredBenefits[].requires</c>. Returns
        ///     false for anything unrecognised so the validator can name the bad value at load
        ///     rather than silently treating a typo as a satisfied requirement.
        /// </summary>
        public static bool TryParseFeature(string name, out Feature feature)
        {
            feature = default;
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Enum.TryParse(name.Trim(), true, out feature) && Enum.IsDefined(typeof(Feature), feature);
        }

        /// <summary>
        ///     Does this village satisfy every named requirement? Each is checked independently
        ///     across all rooms — a village with a warm hall and a separate bedroom satisfies
        ///     [Fire, Bed], because the requirement is on the village's rooms collectively.
        ///     Use <see cref="HasRoomWithAll" /> when they must share one room.
        /// </summary>
        public static bool Satisfies(string villageId, IEnumerable<string> requirements)
        {
            if (requirements == null) return true;

            foreach (var name in requirements)
            {
                if (!TryParseFeature(name, out var feature)) return false;
                if (!HasFeature(villageId, feature)) return false;
            }

            return true;
        }

        /// <summary>The requirements this village does NOT currently meet, for the UI.</summary>
        public static List<string> Unmet(string villageId, IEnumerable<string> requirements)
        {
            var missing = new List<string>();
            if (requirements == null) return missing;

            foreach (var name in requirements)
            {
                if (!TryParseFeature(name, out var feature)) continue;
                if (!HasFeature(villageId, feature)) missing.Add(Describe(feature));
            }

            return missing;
        }

        /// <summary>Player-facing phrasing for a feature — named as something to go and build.</summary>
        public static string Describe(Feature feature)
        {
            return feature switch
            {
                Feature.Fire => "a lit fire (campfire, hearth, bonfire or brazier — not a torch)",
                Feature.Seat => "somewhere to sit",
                Feature.Table => "a table",
                Feature.Bed => "a bed",
                Feature.WorkStation => "a crafting station",
                Feature.Storage => "a chest",
                Feature.Farm => "cultivated ground",
                _ => feature.ToString(),
            };
        }

        private static bool AnyBurning(RegionRoom room)
        {
            foreach (var fire in room.WarmingFires)
                if (fire != null && fire.IsBurning())
                    return true;
            return false;
        }

        private static int Count(RegionRoom r, Feature f) => r.Counts.TryGetValue(f, out var c) ? c : 0;

        // A region can earn several roles at once — coarse regions span multiple functional
        // areas, so we tag every role it qualifies for rather than forcing one label.
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
        ///     Classify a piece into a single room feature, or none. Components/reflection where
        ///     Valheim exposes them; a table/farm name heuristic otherwise. Seating is detected
        ///     generically via the <see cref="Chair" /> component OR any component exposing the
        ///     sit-attach field (<c>m_attachAnimation</c>), so modded seats count with no prefab
        ///     list.
        /// </summary>
        private static bool TryClassifyFeature(GameObject go, out Feature feature)
        {
            feature = default;
            if (go == null) return false;

            // Either counts: a player bed and a Villager Cot both furnish a bedroom. The cot
            // has no Bed component at all (stripped, so players cannot claim or sleep in it),
            // so it needs its own test rather than falling out of the Bed check.
            if (go.GetComponentInParent<Bed>() != null || Items.VillagerBedPiece.IsCot(go))
            {
                feature = Feature.Bed;
                return true;
            }
            if (VillageStationRegistry.TryClassifyStation(go, out _)) { feature = Feature.WorkStation; return true; }
            if (IsSittable(go)) { feature = Feature.Seat; return true; }
            if (IsWarmingFire(go)) { feature = Feature.Fire; return true; }
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

        /// <summary>
        ///     A fire that WARMS, as opposed to one that merely lights. Torches are
        ///     <see cref="Fireplace" />s too, so testing for that component alone counted every
        ///     wall torch as a hearth.
        ///     <para>The discriminator is the game's own: a warming fire carries an
        ///     <see cref="EffectArea" /> flagged <c>Heat</c> or <c>WarmCozyArea</c> — the
        ///     "FireWarmth" child on <c>hearth</c>, <c>piece_brazierfloor01</c> and the campfire —
        ///     while <c>piece_groundtorch</c> has only a <c>FireArea</c> (the damage volume) and
        ///     no heat. Reading the engine's flag rather than matching prefab names means modded
        ///     braziers count and modded torches do not, with no list to maintain.</para>
        ///     <para>Searched including INACTIVE children: the heat area lives under
        ///     <c>_enabled_high</c> and so is switched off whenever the fire is out. Presence here
        ///     means "can warm"; <see cref="HasWarmth" /> decides "is warm".</para>
        /// </summary>
        public static bool IsWarmingFire(GameObject go)
        {
            var fireplace = go.GetComponentInParent<Fireplace>();
            if (fireplace == null) return false;

            const EffectArea.Type warming = EffectArea.Type.Heat | EffectArea.Type.WarmCozyArea;
            foreach (var area in fireplace.GetComponentsInChildren<EffectArea>(true))
                if (area != null && (area.m_type & warming) != 0)
                    return true;

            return false;
        }

        // Per-component-type cache: does the type expose the Chair sit-attach field
        // `m_attachAnimation`? That field drives Humanoid.AttachStart (the sit pose), so its
        // presence marks "you can sit on this" — list-free and modded-seat-friendly.
        private static readonly Dictionary<Type, bool> s_sitTypeCache = new();

        private static bool HasSitField(Type t)
        {
            if (s_sitTypeCache.TryGetValue(t, out var has)) return has;
            has = t.GetField("m_attachAnimation",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null;
            s_sitTypeCache[t] = has;
            return has;
        }

        /// <summary>True when the piece carries something you can sit on.</summary>
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
            var sb = new StringBuilder();
            var player = Player.m_localPlayer;

            // Headless has no player, so fall back to reporting every cached village rather
            // than refusing — this is the only view of what the level requirements can see.
            var villageIds = new List<string>();
            if (player != null)
            {
                var here = VillageRegistry.GetVillageAt(player.transform.position)?.VillageId;
                if (!string.IsNullOrEmpty(here)) villageIds.Add(here);
            }

            if (villageIds.Count == 0) villageIds.AddRange(s_roomsByVillage.Keys);

            if (villageIds.Count == 0)
            {
                sb.AppendLine("[vv_rooms] no villages with a cached room catalog.");
            }
            else
            {
                foreach (var villageId in villageIds)
                {
                    var rooms = GetRooms(villageId);
                    sb.AppendLine($"[vv_rooms] village {villageId}: {rooms.Count} furnished region(s), " +
                                  $"warmth={(HasWarmth(villageId) ? "YES" : "no")}");

                    foreach (var r in rooms)
                    {
                        var roles = r.Roles.Count > 0 ? string.Join(", ", r.Roles) : "(unlabeled)";
                        var feats = new List<string>();
                        foreach (var kv in r.Counts) feats.Add($"{kv.Key}x{kv.Value}");

                        var lit = r.WarmingFires.Count > 0
                            ? $" lit={(AnyBurning(r) ? "yes" : "NO")}"
                            : "";

                        sb.AppendLine(
                            $"  [{r.RegionId}] {roles} @ ({r.Center.x:F1},{r.Center.y:F1},{r.Center.z:F1}) — " +
                            string.Join(" ", feats) + lit);
                    }
                }
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        [RegisterCleanup]
        public static void Clear() => s_roomsByVillage.Clear();
    }
}
