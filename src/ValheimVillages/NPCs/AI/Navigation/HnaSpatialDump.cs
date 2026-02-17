using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using ValheimVillages.Villages;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Dumps spatial data (beds, height grid, doors, building pieces) to a JSON file
    /// so the HNA partition algorithm can be tested offline with unit tests.
    /// Run via console command: hna_dump
    /// </summary>
    public static class HnaSpatialDump
    {
        private const float CellSize = 4f; // must match HnaRegionGraph.CellSize
        private const float ScanRadius = 50f;
        private const float RaycastHeight = 3f;
        private const float RaycastMaxDown = 8f;
        private static readonly int GroundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

        public static void Dump()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Console.instance?.Print("No local player.");
                return;
            }

            var beds = VillagerAIManager.GetAllBedPositions();
            if (beds == null || beds.Count == 0)
            {
                Console.instance?.Print("No villager beds found.");
                return;
            }

            bool hasGuardBounds = VillageAreaManager.TryGetCombinedBounds(
                out float gMinX, out float gMinZ, out float gMaxX, out float gMaxZ);

            // Compute scan bounds (same logic as HnaPartitionHandler)
            float minX, minZ, maxX, maxZ;
            if (hasGuardBounds)
            {
                minX = gMinX; minZ = gMinZ; maxX = gMaxX; maxZ = gMaxZ;
            }
            else
            {
                minX = maxX = beds[0].x;
                minZ = maxZ = beds[0].z;
            }
            foreach (var bed in beds)
            {
                if (bed.x - ScanRadius < minX) minX = bed.x - ScanRadius;
                if (bed.z - ScanRadius < minZ) minZ = bed.z - ScanRadius;
                if (bed.x + ScanRadius > maxX) maxX = bed.x + ScanRadius;
                if (bed.z + ScanRadius > maxZ) maxZ = bed.z + ScanRadius;
            }

            float originX = minX;
            float originZ = minZ;
            int cellCountX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / CellSize));
            int cellCountZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / CellSize));

            var sb = new StringBuilder();
            sb.Append("{\n");

            // Player position
            var pp = player.transform.position;
            sb.Append($"  \"playerPosition\": [{pp.x:F2}, {pp.y:F2}, {pp.z:F2}],\n");

            // Beds
            sb.Append("  \"beds\": [\n");
            for (int i = 0; i < beds.Count; i++)
            {
                var b = beds[i];
                sb.Append($"    [{b.x:F2}, {b.y:F2}, {b.z:F2}]");
                sb.Append(i < beds.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ],\n");

            // Guard bounds
            if (hasGuardBounds)
                sb.Append($"  \"guardBounds\": [{gMinX:F2}, {gMinZ:F2}, {gMaxX:F2}, {gMaxZ:F2}],\n");
            else
                sb.Append("  \"guardBounds\": null,\n");

            // Grid metadata
            sb.Append($"  \"cellSize\": {CellSize:F1},\n");
            sb.Append($"  \"origin\": [{originX:F2}, {originZ:F2}],\n");
            sb.Append($"  \"gridSize\": [{cellCountX}, {cellCountZ}],\n");

            // Height grid: for each cell, raycast from multiple reference heights
            // This lets the test reproduce what the BFS would see from different floors
            sb.Append("  \"heightGrid\": [\n");
            int mask = GroundMask != 0 ? GroundMask : ~0;
            float[] refHeights = GatherReferenceHeights(beds);

            for (int iz = 0; iz < cellCountZ; iz++)
            {
                for (int ix = 0; ix < cellCountX; ix++)
                {
                    float wx = originX + (ix + 0.5f) * CellSize;
                    float wz = originZ + (iz + 0.5f) * CellSize;

                    sb.Append("    { ");
                    sb.Append($"\"ix\": {ix}, \"iz\": {iz}, ");
                    sb.Append($"\"wx\": {wx:F2}, \"wz\": {wz:F2}, ");
                    sb.Append("\"hits\": [");

                    // Track unique hit heights to avoid duplicate entries
                    // when multiple reference heights hit the same surface
                    var seenHitY = new HashSet<int>(); // rounded to 0.25m buckets
                    bool firstHit = true;
                    foreach (float refY in refHeights)
                    {
                        var rayOrigin = new Vector3(wx, refY + RaycastHeight, wz);
                        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RaycastMaxDown, mask, QueryTriggerInteraction.Ignore))
                        {
                            int bucket = Mathf.RoundToInt(hit.point.y * 4); // 0.25m resolution
                            if (!seenHitY.Add(bucket)) continue;
                            if (!firstHit) sb.Append(", ");
                            string layerName = LayerMask.LayerToName(hit.collider.gameObject.layer);
                            sb.Append($"{{\"refY\": {refY:F2}, \"hitY\": {hit.point.y:F2}, \"layer\": \"{layerName}\", \"layerIdx\": {hit.collider.gameObject.layer}}}");
                            firstHit = false;
                        }
                    }

                    sb.Append("] }");
                    bool last = (iz == cellCountZ - 1 && ix == cellCountX - 1);
                    sb.Append(last ? "\n" : ",\n");
                }
            }
            sb.Append("  ],\n");

            // Doors
            sb.Append("  \"doors\": [\n");
            var doors = Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int doorIdx = 0;
            foreach (var door in doors)
            {
                if (door == null) continue;
                var pos = door.transform.position;
                if (pos.x < minX - 10 || pos.x > maxX + 10 || pos.z < minZ - 10 || pos.z > maxZ + 10)
                    continue;
                var fwd = door.transform.forward;
                bool hasPiece = door.GetComponentInParent<Piece>() != null;
                if (doorIdx > 0) sb.Append(",\n");
                sb.Append($"    {{\"pos\": [{pos.x:F2}, {pos.y:F2}, {pos.z:F2}], \"forward\": [{fwd.x:F3}, {fwd.y:F3}, {fwd.z:F3}], \"hasPiece\": {(hasPiece ? "true" : "false")}}}");
                doorIdx++;
            }
            sb.Append("\n  ],\n");

            // Building pieces near beds (walls, floors, stairs, etc.)
            sb.Append("  \"buildingPieces\": [\n");
            var pieces = Object.FindObjectsByType<Piece>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int pieceIdx = 0;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                var pos = piece.transform.position;
                if (pos.x < minX - 5 || pos.x > maxX + 5 || pos.z < minZ - 5 || pos.z > maxZ + 5)
                    continue;
                string pieceName = piece.m_name ?? piece.gameObject.name;
                var ext = piece.GetComponent<Collider>()?.bounds.extents ?? Vector3.zero;
                var fwd = piece.transform.forward;
                int layer = piece.gameObject.layer;
                if (pieceIdx > 0) sb.Append(",\n");
                sb.Append($"    {{\"name\": \"{EscapeJson(pieceName)}\", \"pos\": [{pos.x:F2}, {pos.y:F2}, {pos.z:F2}], " +
                    $"\"fwd\": [{fwd.x:F3}, {fwd.y:F3}, {fwd.z:F3}], \"layer\": {layer}, " +
                    $"\"extents\": [{ext.x:F2}, {ext.y:F2}, {ext.z:F2}]}}");
                pieceIdx++;
            }
            sb.Append("\n  ]\n");

            sb.Append("}\n");

            string path = Path.Combine(
                "/home/benny/Projects/valheim_villages",
                ".cursor", "hna_spatial_dump.json");
            File.WriteAllText(path, sb.ToString());
            Console.instance?.Print($"HNA spatial dump written to {path} ({beds.Count} beds, {cellCountX}x{cellCountZ} grid, {doorIdx} doors, {pieceIdx} pieces)");
            Plugin.Log?.LogInfo($"[HNA] Spatial dump: {path}");
        }

        /// <summary>
        /// Gather reference heights for multi-level raycasting.
        /// For each bed cluster, generates heights every HeightStep meters from
        /// (bedY - HeightMarginBelow) to (bedY + HeightMarginAbove).
        /// This captures ground floor, stairs, upper floors, and rooftops.
        /// </summary>
        private const float HeightStep = 3f;
        private const float HeightMarginBelow = 5f;
        private const float HeightMarginAbove = 10f;

        private static float[] GatherReferenceHeights(List<Vector3> beds)
        {
            var heights = new HashSet<int>();
            foreach (var b in beds)
            {
                int lo = Mathf.FloorToInt((b.y - HeightMarginBelow) / HeightStep) * (int)HeightStep;
                int hi = Mathf.CeilToInt((b.y + HeightMarginAbove) / HeightStep) * (int)HeightStep;
                for (int h = lo; h <= hi; h += (int)HeightStep)
                    heights.Add(h);
            }
            var result = new List<int>(heights);
            result.Sort();
            return result.ConvertAll(h => (float)h).ToArray();
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>
    /// MonoBehaviour that records player positions at a regular interval.
    /// Attach to any active GameObject; controlled via static Start/Stop methods.
    /// </summary>
    public class HnaPathRecorder : MonoBehaviour
    {
        private const float SampleInterval = 0.3f;
        private const float MinMoveDist = 0.5f; // skip if player barely moved

        private static HnaPathRecorder s_instance;
        private static readonly List<Vector3> s_positions = new List<Vector3>();
        private float _timer;
        private Vector3 _lastPos;

        public static void StartRecording()
        {
            if (s_instance != null)
            {
                Console.instance?.Print("Already recording. Use hna_record_stop to stop.");
                return;
            }
            s_positions.Clear();
            var go = new GameObject("HnaPathRecorder");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<HnaPathRecorder>();
            Console.instance?.Print("Recording player path. Walk around all walkable areas, then run: hna_record_stop");
        }

        public static void StopRecording()
        {
            if (s_instance == null)
            {
                Console.instance?.Print("Not recording. Use hna_record_start first.");
                return;
            }
            Destroy(s_instance.gameObject);
            s_instance = null;
            SavePath();
        }

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = SampleInterval;

            var player = Player.m_localPlayer;
            if (player == null || player.transform == null) return;
            var pos = player.transform.position;
            if (s_positions.Count > 0 && Vector3.Distance(pos, _lastPos) < MinMoveDist)
                return;
            s_positions.Add(pos);
            _lastPos = pos;
        }

        private static void SavePath()
        {
            if (s_positions.Count == 0)
            {
                Console.instance?.Print("No positions recorded.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"count\": {s_positions.Count},\n");
            sb.Append("  \"positions\": [\n");
            for (int i = 0; i < s_positions.Count; i++)
            {
                var p = s_positions[i];
                sb.Append($"    [{p.x:F2}, {p.y:F2}, {p.z:F2}]");
                sb.Append(i < s_positions.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");

            string path = Path.Combine(
                "/home/benny/Projects/valheim_villages",
                ".cursor", "hna_walkable_path.json");
            File.WriteAllText(path, sb.ToString());
            Console.instance?.Print($"Saved {s_positions.Count} positions to {path}");
            Plugin.Log?.LogInfo($"[HNA] Walkable path recording: {s_positions.Count} positions → {path}");
        }
    }
}
