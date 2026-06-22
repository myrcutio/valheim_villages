using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Per-village cache of crafting/cooking/smelter station instances inside each
    ///     registered VillageArea polygon. Replaces per-villager LOS discovery for stations:
    ///     villagers query this registry by their anchor position and get back stations from
    ///     their containing village.
    /// </summary>
    public static class VillageStationRegistry
    {
        private static readonly Dictionary<string, List<Component>> s_stationsByVillage = new();

        /// <summary>Broad-phase XZ padding (m) around the village polygon AABB when
        /// gathering station candidates. Generous on purpose — it is only a coarse
        /// prefilter; region-graph membership (<see cref="BelongsToVillage" />) is the
        /// real inside test, so a wide box just ensures edge stations reach it.</summary>
        private const float StationScanPadXZ = 12f;

        /// <summary>Max XZ gap (m) to a walkable village cell accepted for an
        /// obstacle-mounted station (smelter/kiln) whose own pivot has no lookup cell.</summary>
        private const float StationVillageReachXZ = 3f;

        // Per-component-type cache: does the type expose an `m_conversion` field? That
        // field is the input→output table shared by CookingStation, Smelter, Fermenter,
        // and modded conversion stations — the generic "is a station" signal that needs
        // no hard-coded prefab/type list.
        private static readonly Dictionary<Type, bool> s_conversionTypeCache = new();

        /// <summary>
        ///     Durable station metadata persisted on the village ZDO under
        ///     <see cref="Village.StationsKey" />. Full XYZ position (no truncation) plus
        ///     the station's clean prefab name. The live <see cref="Component" /> is a
        ///     disposable projection resolved on demand from <see cref="Position" />.
        /// </summary>
        private readonly struct StationEntry
        {
            public readonly Vector3 Position;
            public readonly string PrefabName;

            public StationEntry(Vector3 position, string prefabName)
            {
                Position = position;
                PrefabName = prefabName ?? "";
            }
        }

        // Same delimited framing as VillageAnchorPersistence: records separated by
        // RecordSep, fields by FieldSep, full XYZ with the round-trip ("R") float format
        // under InvariantCulture. Prefab names are sanitized of the delimiters.
        private const char StationFieldSep = '|';
        private const char StationRecordSep = ';';

        private static string SerializeStations(IEnumerable<StationEntry> entries)
        {
            if (entries == null) return "";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            var first = true;
            foreach (var e in entries)
            {
                if (!first) sb.Append(StationRecordSep);
                first = false;
                sb.Append(e.Position.x.ToString("R", inv)).Append(StationFieldSep)
                    .Append(e.Position.y.ToString("R", inv)).Append(StationFieldSep)
                    .Append(e.Position.z.ToString("R", inv)).Append(StationFieldSep)
                    .Append(SanitizeStationName(e.PrefabName));
            }

            return sb.ToString();
        }

        private static bool RestoreStations(string data, List<StationEntry> outEntries)
        {
            outEntries.Clear();
            if (string.IsNullOrEmpty(data)) return false;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var records = data.Split(StationRecordSep);
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record)) continue;
                var f = record.Split(StationFieldSep);
                if (f.Length < 4) continue;
                if (!float.TryParse(f[0], System.Globalization.NumberStyles.Float, inv, out var x)) continue;
                if (!float.TryParse(f[1], System.Globalization.NumberStyles.Float, inv, out var y)) continue;
                if (!float.TryParse(f[2], System.Globalization.NumberStyles.Float, inv, out var z)) continue;
                outEntries.Add(new StationEntry(new Vector3(x, y, z), f[3]));
            }

            return outEntries.Count > 0;
        }

        private static string SanitizeStationName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace(StationFieldSep, '_').Replace(StationRecordSep, '_');
        }

        /// <summary>Clean prefab name for a station GameObject, stripping the Unity
        /// "(Clone)" suffix so persisted names match prefab names.</summary>
        private static string CleanPrefabName(GameObject go)
        {
            if (go == null) return "";
            var n = go.name;
            if (string.IsNullOrEmpty(n)) return "";
            var idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? n.Substring(0, idx) : n;
        }

        private static bool HasConversionField(Type t)
        {
            if (s_conversionTypeCache.TryGetValue(t, out var has)) return has;
            has = t.GetField("m_conversion",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null;
            s_conversionTypeCache[t] = has;
            return has;
        }

        /// <summary>
        ///     Classify a built piece as a "station" the village should track, with no
        ///     hard-coded prefab/type list. A station is any piece carrying a
        ///     <see cref="CraftingStation" /> (the crafting benches recipes point at) OR
        ///     any component exposing an <c>m_conversion</c> field (CookingStation,
        ///     Smelter, Fermenter, modded conversion stations). Writes the station
        ///     component to register and returns true.
        /// </summary>
        public static bool TryClassifyStation(GameObject go, out Component station)
        {
            station = null;
            if (go == null) return false;

            // Villager NPCs (Dverger/DvergerMage(Clone)) carry our Villager component on
            // the GameObject or somewhere up the parent chain, and the native Dvergr
            // crossbow/workbench colliders would otherwise classify them as stations. A
            // carrier of Villager or VillagerStation is NEVER a station — bail before any
            // CraftingStation/m_conversion probe. The registry station has no Villager
            // component, so it still classifies normally.
            if (go.GetComponentInParent<ValheimVillages.Villager.Villager>() != null) return false;
            if (go.GetComponentInParent<ValheimVillages.Villager.Station.VillagerStation>() != null) return false;

            var cs = go.GetComponentInParent<CraftingStation>();
            if (cs != null) { station = cs; return true; }

            var root = go.GetComponentInParent<Piece>();
            var host = root != null ? root.gameObject : go;
            foreach (var comp in host.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;
                if (HasConversionField(comp.GetType())) { station = comp; return true; }
            }
            return false;
        }

        /// <summary>
        ///     True when <paramref name="pos" /> belongs to the given village's region
        ///     graph. PointToRegionId resolves walkable pivots directly; obstacle-mounted
        ///     stations (smelter/kiln) sit on non-walkable tops with no lookup cell, so we
        ///     also accept a walkable village cell within <see cref="StationVillageReachXZ" />.
        /// </summary>
        private static bool BelongsToVillage(RegionGraph graph, Vector3 pos)
        {
            if (graph == null) return false;
            if (!string.IsNullOrEmpty(graph.PointToRegionId(pos))) return true;
            return graph.TryFindNearestLookupCell(pos, null, out _, out _, StationVillageReachXZ);
        }

        /// <summary>
        ///     Incrementally register a freshly-built station with the village that owns
        ///     its position, so a newly-placed station is usable immediately instead of
        ///     waiting for the next partition rescan. No-op when the piece isn't a station
        ///     or sits in no known village graph. Deduplicated; the periodic
        ///     <see cref="RefreshFor" /> rescan stays the authority that prunes stale entries.
        /// </summary>
        public static void RegisterStation(GameObject go)
        {
            if (!TryClassifyStation(go, out var station)) return;
            var pos = station.transform.position;
            foreach (var village in VillageRegistry.EnumerateWithGraph())
            {
                if (!BelongsToVillage(village.Graph, pos)) continue;
                var key = village.VillageId;
                if (string.IsNullOrEmpty(key)) continue;
                if (!s_stationsByVillage.TryGetValue(key, out var list))
                    s_stationsByVillage[key] = list = new List<Component>();
                if (!list.Contains(station))
                {
                    list.Add(station);
                    Plugin.Log?.LogInfo(
                        $"[VillageStationRegistry] +{station.GetType().Name} {station.gameObject.name} " +
                        $"@ ({pos.x:F1},{pos.y:F1},{pos.z:F1}) → {key} (on build)");
                }
                return;
            }
        }

        /// <summary>
        ///     (Re)scan stations inside the given VillageArea. Called from VillageAreaManager
        ///     when an area is registered or its waypoints change.
        /// </summary>
        public static void RefreshFor(VillageArea area)
        {
            if (area == null || area.Waypoints == null || area.Waypoints.Count < 3) return;

            // Compute AABB from the polygon
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var wp in area.Waypoints)
            {
                if (wp.x < minX) minX = wp.x;
                if (wp.x > maxX) maxX = wp.x;
                if (wp.z < minZ) minZ = wp.z;
                if (wp.z > maxZ) maxZ = wp.z;
                if (wp.y < minY) minY = wp.y;
                if (wp.y > maxY) maxY = wp.y;
            }

            // Pad Y bounds generously — patrol waypoints sit at terrain height, stations may be above
            var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            var halfExtents = new Vector3((maxX - minX) * 0.5f + StationScanPadXZ, (maxY - minY) * 0.5f + 20f, (maxZ - minZ) * 0.5f + StationScanPadXZ);

            // Station membership is decided by region-graph coverage
            // (PointToRegionId / nearest walkable cell), NOT the boundary polygon: a
            // too-tight polygon/AABB edge was silently dropping legitimate interior
            // stations (e.g. a south-wing oven/cauldron) whenever a build shifted the
            // computed boundary. The OverlapBox below is only a coarse prefilter now.
            var graph = VillageRegistry.FindById(area.VillageId)?.Graph;

            var found = new List<Component>();
            var seen = new HashSet<Component>();
            var candidates = 0;
            var kept = 0;

            var hits = Physics.OverlapBox(center, halfExtents, Quaternion.identity);
            foreach (var col in hits)
            {
                if (col == null || col.gameObject == null) continue;
                if (!TryClassifyStation(col.gameObject, out var station)) continue;
                if (!seen.Add(station)) continue;
                candidates++;
                if (graph == null || BelongsToVillage(graph, station.transform.position))
                {
                    found.Add(station);
                    kept++;
                }
            }

            s_stationsByVillage[area.VillageId] = found;

            // Persist station metadata onto the village ZDO so stations survive a
            // save/reload and can be re-resolved on demand before any physics rescan.
            var village = VillageRegistry.FindById(area.VillageId);
            if (village != null)
            {
                var entries = new List<StationEntry>(found.Count);
                foreach (var comp in found)
                {
                    if (comp == null) continue;
                    entries.Add(new StationEntry(comp.transform.position, CleanPrefabName(comp.gameObject)));
                }

                village.SetBlob(Village.StationsKey, SerializeStations(entries));
            }

            if (Plugin.Log != null)
            {
                Plugin.Log.LogInfo(
                    $"[VillageStationRegistry] {area.VillageId}: cached {found.Count} stations " +
                    $"(candidates {candidates} → kept {kept})");
                foreach (var comp in found)
                {
                    var p = comp.transform.position;
                    Plugin.Log.LogInfo(
                        $"  [{comp.GetType().Name}] {comp.gameObject.name} @ ({p.x:F1},{p.y:F1},{p.z:F1})");
                }
            }
        }

        /// <summary>~radius (m) of the OverlapSphere used to resolve a persisted station
        /// position back to its live Component after load.</summary>
        private const float StationResolveRadius = 2f;

        /// <summary>
        ///     Ensure a live station list exists for <paramref name="villageKey" />. When the
        ///     in-memory cache is missing/empty (e.g. right after world load, before any
        ///     physics rescan), hydrate it from the durable ZDO blob: read the persisted
        ///     station positions and resolve each back to its live <see cref="Component" />
        ///     via a small <see cref="Physics.OverlapSphere" /> filtered through the (fixed)
        ///     <see cref="TryClassifyStation" />. The components are disposable projections
        ///     of the durable positions; the periodic <see cref="RefreshFor" /> rescan stays
        ///     the authority. No-op when a non-empty live list already exists.
        /// </summary>
        private static void EnsureHydrated(string villageKey)
        {
            if (string.IsNullOrEmpty(villageKey)) return;
            if (s_stationsByVillage.TryGetValue(villageKey, out var existing) && existing != null && existing.Count > 0)
                return;

            var village = VillageRegistry.FindById(villageKey);
            if (village == null) return;

            var entries = new List<StationEntry>();
            if (!RestoreStations(village.GetBlob(Village.StationsKey), entries)) return;

            var resolved = new List<Component>(entries.Count);
            var seen = new HashSet<Component>();
            foreach (var entry in entries)
            {
                var hits = Physics.OverlapSphere(entry.Position, StationResolveRadius);
                foreach (var col in hits)
                {
                    if (col == null || col.gameObject == null) continue;
                    if (!TryClassifyStation(col.gameObject, out var station)) continue;
                    if (!seen.Add(station)) continue;
                    resolved.Add(station);
                }
            }

            s_stationsByVillage[villageKey] = resolved;
            Plugin.Log?.LogInfo(
                $"[VillageStationRegistry] {villageKey}: hydrated {resolved.Count}/{entries.Count} " +
                "station(s) from ZDO blob");
        }

        /// <summary>Remove a village's station cache (called on UnregisterArea).</summary>
        public static void RemoveFor(string villageKey)
        {
            s_stationsByVillage.Remove(villageKey);
        }

        /// <summary>True if the registry has a village whose polygon contains the position.</summary>
        public static bool HasVillageFor(Vector3 position)
        {
            return TryGetVillage(position, out _);
        }

        /// <summary>
        ///     Look up a station of type T matching the filter inside the village containing the position.
        ///     Returns the nearest match by position when multiple satisfy the filter.
        /// </summary>
        /// <summary>
        ///     Total items currently cooking across all of the village's cooking stations
        ///     (each occupied slot is one pending output). The scheduler counts these toward
        ///     a cooking work order's quota: without it, several villagers pipeline raw input
        ///     onto the stations while the deposited output count is still under the cap
        ///     (cooking takes time), overshooting MaxQuantity. Conservative — counts every
        ///     occupied slot regardless of recipe, so unrelated player cooking can only make
        ///     a villager stop early, never overproduce.
        /// </summary>
        public static int CountCookingOutputInFlight(Vector3 position)
        {
            if (!TryGetVillage(position, out var villageKey)) return 0;
            EnsureHydrated(villageKey);
            if (!s_stationsByVillage.TryGetValue(villageKey, out var list)) return 0;

            var total = 0;
            foreach (var comp in list)
                if (comp is CookingStation cs)
                    total += StationFinder.CountOccupiedSlots(cs);
            return total;
        }

        public static bool TryFindStation<T>(
            Vector3 position,
            Func<T, bool> filter,
            out Vector3 stationPos,
            out T component) where T : Component
        {
            stationPos = Vector3.zero;
            component = null;

            if (!TryGetVillage(position, out var villageKey)) return false;
            EnsureHydrated(villageKey);
            if (!s_stationsByVillage.TryGetValue(villageKey, out var list)) return false;

            T best = null;
            var bestDistSq = float.MaxValue;
            foreach (var comp in list)
            {
                if (comp == null) continue;
                var typed = comp as T;
                if (typed == null) continue;
                if (filter != null && !filter(typed)) continue;
                var d = (typed.transform.position - position).sqrMagnitude;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = typed;
                }
            }

            if (best == null) return false;

            if (!VillagerMovement.TryResolveApproach(best.transform.position, position, null, out var approach))
            {
                Plugin.Log?.LogDebug(
                    $"[VillageStationRegistry] {villageKey}: no HNA-valid approach to {best.gameObject.name} " +
                    $"@ ({best.transform.position.x:F1},{best.transform.position.y:F1},{best.transform.position.z:F1})");
                stationPos = Vector3.zero;
                component = null;
                return false;
            }

            stationPos = approach;
            component = best;
            return true;
        }


        [DevCommand("Diagnose HNA approach resolution to a target from a source (default player). " +
                    "Usage: vv_approach <tx> <tz> [<sx> <sz>]", Name = "vv_approach")]
        public static void ApproachDiag(Terminal.ConsoleEventArgs args)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args?.Args == null || args.Args.Length < 3
                || !float.TryParse(args.Args[1], System.Globalization.NumberStyles.Float, inv, out var tx)
                || !float.TryParse(args.Args[2], System.Globalization.NumberStyles.Float, inv, out var tz))
            {
                global::Console.instance?.Print("Usage: vv_approach <tx> <tz> [<sx> <sz>]");
                return;
            }

            var src = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            if (args.Args.Length >= 5
                && float.TryParse(args.Args[3], System.Globalization.NumberStyles.Float, inv, out var sx)
                && float.TryParse(args.Args[4], System.Globalization.NumberStyles.Float, inv, out var sz))
                src = new Vector3(sx, src.y, sz);

            var groundT = ZoneSystem.instance != null
                ? ZoneSystem.instance.GetGroundHeight(new Vector3(tx, 0f, tz)) : 0f;
            var target = new Vector3(tx, groundT, tz);
            var ok = VillagerMovement.TryResolveApproach(target, src, null, out var approach);
            var msg = $"[vv_approach] ok={ok} approach=({approach.x:F1},{approach.y:F1},{approach.z:F1})";
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        [DevCommand("Dump cached village stations + HNA approach resolution for each villager anchor",
            Name = "vv_stations")]
        public static void DumpStations()
        {
            var sb = new System.Text.StringBuilder();
            var anchors = Villager.AI.VillagerAIManager.GetAllAnchorPositions();
            sb.AppendLine($"[vv_stations] {anchors.Count} anchor(s); {s_stationsByVillage.Count} village(s) cached");

            foreach (var anchor in anchors)
            {
                if (!TryGetVillage(anchor, out var key))
                {
                    sb.AppendLine($"  anchor=({anchor.x:F1},{anchor.y:F1},{anchor.z:F1}) → NO containing village");
                    continue;
                }

                EnsureHydrated(key);
                var list = s_stationsByVillage.TryGetValue(key, out var stations) ? stations : null;
                sb.AppendLine($"  anchor=({anchor.x:F1},{anchor.y:F1},{anchor.z:F1}) → village {key}: " +
                              $"{(list?.Count ?? 0)} station(s)");
                if (list != null)
                    foreach (var comp in list)
                    {
                        if (comp == null) continue;
                        var p = comp.transform.position;
                        var approached = VillagerMovement.TryResolveApproach(p, anchor, null, out var approach);
                        sb.AppendLine(
                            $"    [{comp.GetType().Name}] {comp.gameObject.name} " +
                            $"@ ({p.x:F1},{p.y:F1},{p.z:F1}) dist={Vector3.Distance(anchor, p):F1}m " +
                            $"approach={(approached ? $"({approach.x:F1},{approach.y:F1},{approach.z:F1})" : "UNREACHABLE")}");
                    }
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_stationsByVillage.Clear();
        }

        /// <summary>
        ///     Pick the village whose polygon contains the position. If multiple match, take the smallest
        ///     polygon (most specific). Iterates VillageAreaManager's areas by reflection-free public API.
        /// </summary>
        private static bool TryGetVillage(Vector3 position, out string villageId)
        {
            var village = VillageRegistry.GetVillageAt(position);
            villageId = village?.VillageId;
            return village != null;
        }
    }
}
