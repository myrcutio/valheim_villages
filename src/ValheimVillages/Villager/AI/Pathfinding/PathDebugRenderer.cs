using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using ValheimVillages.Attributes;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Villager.AI.Pathfinding
{
    /// <summary>
    ///     GL-based in-game path visualization for all active villagers.
    ///     Draws path segments and corner markers colored by path status:
    ///     green = path found, yellow = partial/stale, red = no path.
    ///     Toggle with the "vv_path_debug" console command.
    /// </summary>
    public class PathDebugRenderer : MonoBehaviour
    {
        private const float NodeMarkerSize = 0.15f;
        private const float LineYOffset = 0.1f;

        private static PathDebugRenderer s_instance;
        private static bool s_enabled = true;
        private static bool s_showTriangulation = true;

        /// <summary>
        ///     When set, debug overlays render ONLY for the camera with this name
        ///     (e.g. ValheimMCP's off-screen render camera), keeping them off the
        ///     player's view. Null = draw for every camera. Set via <c>cam=&lt;name&gt;</c>.
        /// </summary>
        private static string s_targetCameraName;

        /// <summary>
        ///     Region IDs whose triangulation edges should be drawn with extra
        ///     emphasis (overlaid in bright white at multiple Y offsets). Set
        ///     by <c>vv_bfs_trace</c> to highlight the path from a target
        ///     region back to a seed. Empty = no highlight.
        /// </summary>
        public static readonly HashSet<string> HighlightedRegions = new();

        /// <summary>
        ///     Ad-hoc polyline to overlay (magenta), set by <c>vv_drawpath</c>.
        ///     Used to visualize a raw NavMesh path's corners for comparison
        ///     against the HNA graph. Empty = nothing drawn.
        /// </summary>
        public static readonly List<Vector3> DebugPolyline = new();
        private static readonly Color ColorDebugPolyline = Color.magenta;

        private static readonly Color ColorComplete = Color.green;
        private static readonly Color ColorPartial = Color.yellow;
        private static readonly Color ColorNoPath = Color.red;
        private static readonly Color ColorTarget = new(1f, 0.4f, 0f); // orange

        private Material m_lineMaterial;

        private void Awake()
        {
            m_lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"));
            m_lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            m_lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m_lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m_lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            m_lineMaterial.SetInt("_ZWrite", 0);
            m_lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
        }

        private void OnDestroy()
        {
            if (m_lineMaterial != null)
                Destroy(m_lineMaterial);
        }

        private void OnRenderObject()
        {
            if (!s_enabled && !s_showTriangulation && DebugPolyline.Count == 0) return;

            // Camera filter: when targeting a specific camera (e.g. the off-screen
            // MCP render camera), skip every other camera's render pass so the
            // overlay never appears in the player's view.
            if (s_targetCameraName != null &&
                (Camera.current == null || Camera.current.name != s_targetCameraName))
                return;

            m_lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);

            if (s_enabled)
            {
                foreach (var kvp in VillagerAIManager.ActiveVillagers)
                {
                    var ai = kvp.Value;
                    if (ai == null) continue;
                    DrawVillagerPath(ai);
                }

            }

            if (s_showTriangulation)
                DrawTriangulation();

            if (DebugPolyline.Count > 0)
                DrawDebugPolyline();

            GL.PopMatrix();
        }

        // Draw the ad-hoc DebugPolyline (set by vv_drawpath) as a bright magenta
        // line through its corners, lifted well above the floor so it's legible
        // over the triangulation overlay, with a marker at each corner.
        private void DrawDebugPolyline()
        {
            var yOff = Vector3.up * 0.4f;
            GL.Begin(GL.LINES);
            GL.Color(ColorDebugPolyline);
            for (var i = 0; i < DebugPolyline.Count - 1; i++)
            {
                GL.Vertex(DebugPolyline[i] + yOff);
                GL.Vertex(DebugPolyline[i + 1] + yOff);
            }

            GL.End();

            for (var i = 0; i < DebugPolyline.Count; i++)
                DrawWireOctahedron(DebugPolyline[i] + yOff, NodeMarkerSize * 2f, ColorDebugPolyline);
        }

        [DevCommand("Toggle villager path debug viz. Optional cam=<cameraName> restricts the overlay to that camera (cam=off clears).", Name = "vv_path_debug")]
        public static void Toggle(Terminal.ConsoleEventArgs args)
        {
            if (TryApplyCameraArg(args, out var camMsg))
            {
                Console.instance?.Print(camMsg);
                return;
            }

            s_enabled = !s_enabled;

            if (s_enabled)
            {
                EnsureInstance();
            }
            else if (s_instance != null)
            {
                Destroy(s_instance.gameObject);
                s_instance = null;
            }

            var state = s_enabled ? "ON" : "OFF";
            Console.instance?.Print($"Path debug rendering {state}{CamSuffix()}");
            Plugin.Log?.LogInfo($"[PathDebug] Visualization {state}");
        }

        [DevCommand("Overlay the raw slot-31 NavMesh path between two points (magenta). " +
                    "Usage: vv_drawpath <fromX> <fromZ> <toX> <toZ> | vv_drawpath off", Name = "vv_drawpath")]
        public static void DrawRawPath(Terminal.ConsoleEventArgs args)
        {
            if (args?.Args != null && args.Args.Length >= 2 &&
                args.Args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                DebugPolyline.Clear();
                Console.instance?.Print("[vv_drawpath] cleared");
                return;
            }

            var inv = CultureInfo.InvariantCulture;
            if (args?.Args == null || args.Args.Length < 5
                || !float.TryParse(args.Args[1], NumberStyles.Float, inv, out var fx)
                || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var fz)
                || !float.TryParse(args.Args[3], NumberStyles.Float, inv, out var tx)
                || !float.TryParse(args.Args[4], NumberStyles.Float, inv, out var tz))
            {
                Console.instance?.Print("Usage: vv_drawpath <fromX> <fromZ> <toX> <toZ> | vv_drawpath off");
                return;
            }

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            if (!NavMesh.SamplePosition(new Vector3(fx, 40f, fz), out var fHit, 8f, filter)
                || !NavMesh.SamplePosition(new Vector3(tx, 40f, tz), out var tHit, 8f, filter))
            {
                Console.instance?.Print("[vv_drawpath] endpoint sample failed");
                return;
            }

            var path = new NavMeshPath();
            NavMesh.CalculatePath(fHit.position, tHit.position, filter, path);
            DebugPolyline.Clear();
            DebugPolyline.AddRange(path.corners);
            EnsureInstance();
            Console.instance?.Print(
                $"[vv_drawpath] status={path.status} corners={path.corners.Length} (magenta overlay)");
        }




        [DevCommand("Toggle NavMesh triangulation wireframe. Optional cam=<cameraName> restricts the overlay to that camera (cam=off clears).", Name = "vv_tri_debug")]
        public static void ToggleTriangulation(Terminal.ConsoleEventArgs args)
        {
            if (TryApplyCameraArg(args, out var camMsg))
            {
                Console.instance?.Print(camMsg);
                return;
            }

            s_showTriangulation = !s_showTriangulation;
            if (s_showTriangulation)
                EnsureInstance();

            var state = s_showTriangulation ? "ON" : "OFF";
            var count = RegionBuilder.CachedTriangles?.Count ?? 0;
            Console.instance?.Print($"Triangulation wireframe {state} ({count} triangles){CamSuffix()}");
            Plugin.Log?.LogInfo($"[PathDebug] Triangulation wireframe {state}");
        }

        private static string CamSuffix()
        {
            return s_targetCameraName != null ? $" [cam={s_targetCameraName}]" : "";
        }

        /// <summary>
        ///     Parse an optional <c>cam=&lt;name&gt;</c> argument. <c>cam=&lt;name&gt;</c>
        ///     restricts overlays to that camera (and ensures the renderer exists);
        ///     <c>cam=off</c> (or empty) clears the filter. Returns true if a cam= arg
        ///     was present and handled (so the caller skips its normal toggle).
        /// </summary>
        private static bool TryApplyCameraArg(Terminal.ConsoleEventArgs args, out string message)
        {
            message = null;
            if (args?.Args == null) return false;
            foreach (var a in args.Args)
            {
                if (string.IsNullOrEmpty(a) || !a.StartsWith("cam=")) continue;
                var val = a.Substring(4);
                if (val.Length == 0 || val.Equals("off", System.StringComparison.OrdinalIgnoreCase))
                {
                    s_targetCameraName = null;
                    message = "Debug overlay camera filter cleared (renders to all cameras).";
                }
                else
                {
                    s_targetCameraName = val;
                    EnsureInstance();
                    message = $"Debug overlay restricted to camera '{val}'.";
                }

                return true;
            }

            return false;
        }

        /// <summary>
        ///     Create the renderer instance if enabled but not yet instantiated.
        ///     Called from Plugin.Update so the GL renderer is ready before the first frame.
        /// </summary>
        public static void AutoEnable()
        {
            if ((s_enabled || s_showTriangulation) && s_instance == null)
                EnsureInstance();
        }

        private static void EnsureInstance()
        {
            if (s_instance != null) return;
            var go = new GameObject("PathDebugRenderer");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<PathDebugRenderer>();
        }

        private void DrawVillagerPath(VillagerAI ai)
        {
            var npcPos = ai.transform.position;
            var yOff = Vector3.up * LineYOffset;

            // Read the live NavMeshAgent route the mover actually follows.
            var hasPath = ai.TryGetAgentPath(out var corners, out var status);

            if (!hasPath || corners.Length == 0)
            {
                // No active route — draw a red line from the villager to its
                // destination target (if any) so the intent is still visible.
                if (ai.CurrentTarget.HasValue)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(ColorNoPath);
                    GL.Vertex(npcPos + yOff);
                    GL.Vertex(ai.CurrentTarget.Value + yOff);
                    GL.End();
                    DrawWireOctahedron(ai.CurrentTarget.Value + yOff, NodeMarkerSize * 2f, ColorNoPath);
                }

                return;
            }

            // Green = the agent can reach the target; yellow = partial/invalid path.
            var lineColor = status == NavMeshPathStatus.PathComplete ? ColorComplete : ColorPartial;

            // Villager position to first corner, then corner-to-corner.
            GL.Begin(GL.LINES);
            GL.Color(lineColor);
            GL.Vertex(npcPos + yOff);
            GL.Vertex(corners[0] + yOff);
            for (var i = 0; i < corners.Length - 1; i++)
            {
                GL.Vertex(corners[i] + yOff);
                GL.Vertex(corners[i + 1] + yOff);
            }

            GL.End();

            // Corner markers along the agent path.
            for (var i = 0; i < corners.Length; i++)
                DrawWireOctahedron(corners[i] + yOff, NodeMarkerSize, lineColor);

            // Destination target (orange), with a connector if the agent's last
            // corner doesn't already coincide with it.
            if (ai.CurrentTarget.HasValue)
            {
                var target = ai.CurrentTarget.Value;
                var lastCorner = corners[corners.Length - 1];
                if (Vector3.Distance(target, lastCorner) > 0.5f)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(ColorTarget);
                    GL.Vertex(lastCorner + yOff);
                    GL.Vertex(target + yOff);
                    GL.End();
                }

                DrawWireOctahedron(target + yOff, NodeMarkerSize * 2f, ColorTarget);
            }
        }

        /// <summary>
        ///     Wire octahedron — 6 vertices, 12 edges. Visible from any angle as a "sphere-like" marker.
        /// </summary>
        private static void DrawWireOctahedron(Vector3 center, float radius, Color color)
        {
            var top = center + Vector3.up * radius;
            var bottom = center - Vector3.up * radius;
            var north = center + Vector3.forward * radius;
            var south = center - Vector3.forward * radius;
            var east = center + Vector3.right * radius;
            var west = center - Vector3.right * radius;

            GL.Begin(GL.LINES);
            GL.Color(color);

            // Top ring
            GL.Vertex(top);
            GL.Vertex(north);
            GL.Vertex(top);
            GL.Vertex(south);
            GL.Vertex(top);
            GL.Vertex(east);
            GL.Vertex(top);
            GL.Vertex(west);

            // Bottom ring
            GL.Vertex(bottom);
            GL.Vertex(north);
            GL.Vertex(bottom);
            GL.Vertex(south);
            GL.Vertex(bottom);
            GL.Vertex(east);
            GL.Vertex(bottom);
            GL.Vertex(west);

            // Equator
            GL.Vertex(north);
            GL.Vertex(east);
            GL.Vertex(east);
            GL.Vertex(south);
            GL.Vertex(south);
            GL.Vertex(west);
            GL.Vertex(west);
            GL.Vertex(north);

            GL.End();
        }



        private void DrawTriangulation()
        {
            var tris = RegionBuilder.CachedTriangles;
            if (tris == null || tris.Count == 0) return;

            var yOff = Vector3.up * LineYOffset;

            GL.Begin(GL.LINES);
            foreach (var t in tris)
            {
                GL.Color(RegionColor(t.RegionId));
                // Edge 0-1
                GL.Vertex(t.V0 + yOff);
                GL.Vertex(t.V1 + yOff);
                // Edge 1-2
                GL.Vertex(t.V1 + yOff);
                GL.Vertex(t.V2 + yOff);
                // Edge 2-0
                GL.Vertex(t.V2 + yOff);
                GL.Vertex(t.V0 + yOff);
            }

            GL.End();

            // Highlight pass: draw highlighted regions in bright white,
            // overlaid at multiple Y offsets to simulate thicker lines.
            if (HighlightedRegions.Count == 0) return;
            var highlightColor = new Color(1f, 1f, 1f, 0.95f);
            GL.Begin(GL.LINES);
            GL.Color(highlightColor);
            for (var pass = 0; pass < 5; pass++)
            {
                var dy = LineYOffset + 0.05f + pass * 0.02f;
                var off = Vector3.up * dy;
                foreach (var t in tris)
                {
                    if (!HighlightedRegions.Contains(t.RegionId)) continue;
                    GL.Vertex(t.V0 + off);
                    GL.Vertex(t.V1 + off);
                    GL.Vertex(t.V1 + off);
                    GL.Vertex(t.V2 + off);
                    GL.Vertex(t.V2 + off);
                    GL.Vertex(t.V0 + off);
                }
            }

            GL.End();
        }

        private static Color RegionColor(string regionId)
        {
            if (string.IsNullOrEmpty(regionId))
                return new Color(1f, 1f, 1f, 0.3f);
            var hash = regionId.GetHashCode() & 0x7FFFFFFF;
            var hue = hash % 360 / 360f;
            var sat = 0.6f + hash / 360 % 4 * 0.1f;
            var val = 0.7f + hash / 1440 % 3 * 0.1f;
            var c = Color.HSVToRGB(hue, sat, val);
            c.a = 0.7f;
            return c;
        }

        [DevCommand("Inspect village region graph (use near=x,z or near=player for position-filtered triangle detail)",
            Name = "vv_tri_inspect")]
        public static void TriInspect(Terminal.ConsoleEventArgs args)
        {
            var nearPos = ParseNearArg(args);
            if (nearPos.HasValue)
                InspectNearPosition(nearPos.Value);
            else
                InspectVillage();
        }

        private static Vector3? ParseNearArg(Terminal.ConsoleEventArgs args)
        {
            if (args?.Args == null) return null;
            for (var i = 1; i < args.Args.Length; i++)
            {
                var a = args.Args[i];
                if (string.IsNullOrEmpty(a) || !a.StartsWith("near=", StringComparison.OrdinalIgnoreCase))
                    continue;
                var val = a.Substring("near=".Length);
                if (val.Equals("player", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Player.m_localPlayer;
                    return p != null ? p.transform.position : null;
                }

                var parts = val.Split(',');
                if (parts.Length >= 2
                    && float.TryParse(parts[0], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var nx)
                    && float.TryParse(parts[1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var nz))
                    return new Vector3(nx, 0f, nz);
            }

            return null;
        }

        private static void InspectVillage()
        {
            var graphIdx = 0;
            var totalGraphs = 0;
            var totalRegions = 0;
            var totalLinks = 0;
            var summaryLines = new List<string>();

            foreach (var graph in RegionGraph.GetAll())
            {
                totalGraphs++;
                totalRegions += graph.RegionCount;
                totalLinks += graph.LinkCount;

                // Per-region data goes to a sidecar so the console summary stays terse.
                var regionEntries = new List<object>();
                foreach (var center in graph.Diagnostics.GetAllRegionCenters())
                    regionEntries.Add(
                        $"{center.x:F2},{center.y:F2},{center.z:F2}");
                DebugLog.List("Region", $"village_{graphIdx}_centers", regionEntries);

                graph.GetOrigin(out var ox, out var oz);
                var line =
                    $"  graph[{graphIdx}] regions={graph.RegionCount} " +
                    $"links={graph.LinkCount} origin=({ox:F0},{oz:F0})";
                summaryLines.Add(line);
                graphIdx++;
            }

            if (totalGraphs == 0)
            {
                var empty =
                    "[TriInspect] No region graphs available. Run hna_partition (or move a Guard into a village).";
                Console.instance?.Print(empty);
                Plugin.Log?.LogInfo(empty);
                return;
            }

            DebugLog.Event("TriInspect", "village_summary",
                ("graphs", totalGraphs),
                ("regions", totalRegions),
                ("links", totalLinks));

            var sb = new StringBuilder();
            sb.Append("[TriInspect] village graphs=").Append(totalGraphs)
                .Append(" regions=").Append(totalRegions)
                .Append(" links=").Append(totalLinks).AppendLine();
            foreach (var line in summaryLines) sb.AppendLine(line);
            sb.AppendLine("  (per-region centroids written to <BepInEx>/config/vv_dumps/)");

            var output = sb.ToString();
            Console.instance?.Print(output);
            Plugin.Log?.LogInfo(output);
        }

        private static void InspectNearPosition(Vector3 pos)
        {
            var tris = RegionBuilder.CachedTriangles;
            if (tris == null || tris.Count == 0)
            {
                Console.instance?.Print("No cached triangles. Run vv_tri_debug first.");
                return;
            }

            const float radius = 2f;
            const float r2 = radius * radius;

            var regionTris = new Dictionary<string, List<RegionBuilder.CachedTriangle>>();
            foreach (var t in tris)
            {
                var centroid = (t.V0 + t.V1 + t.V2) / 3f;
                if ((centroid - pos).sqrMagnitude > r2) continue;
                var key = string.IsNullOrEmpty(t.RegionId) ? "(none)" : t.RegionId;
                if (!regionTris.TryGetValue(key, out var list))
                {
                    list = new List<RegionBuilder.CachedTriangle>();
                    regionTris[key] = list;
                }

                list.Add(t);
            }

            if (regionTris.Count == 0)
            {
                Console.instance?.Print($"No triangles within {radius}m of ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                Plugin.Log?.LogInfo(
                    $"[TriInspect] No triangles within {radius}m of ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                return;
            }

            var graph = RegionGraph.GetNearest(pos);
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas,
            };

            var terrainY = 0f;
            var hasTerrain = ZoneSystem.instance != null;
            if (hasTerrain)
                terrainY = ZoneSystem.instance.GetGroundHeight(new Vector3(pos.x, 0f, pos.z));

            var sb = new StringBuilder();
            sb.AppendLine($"[TriInspect] Pos=({pos.x:F1}, {pos.y:F1}, {pos.z:F1})  " +
                          $"Terrain={terrainY:F1}  Δ={pos.y - terrainY:F1}m above terrain");
            sb.AppendLine($"  {regionTris.Count} region(s) within {radius}m sphere:");

            const float subdivCell = 3f;
            const float heightBand = 2f;

            // Collect all vertices per region for shared-edge analysis
            var regionVertices = new Dictionary<string, List<(Vector3 v, int vi)>>();

            foreach (var kv in regionTris)
            {
                var rid = kv.Key;
                var triList = kv.Value;

                float yMin = float.MaxValue, yMax = float.MinValue;
                var totalArea = 0f;
                var maxEdgeLen = 0f;
                var vertList = new List<(Vector3 v, int vi)>();
                var triIdx = 0;
                foreach (var t in triList)
                {
                    foreach (var y in new[] { t.V0.y, t.V1.y, t.V2.y })
                    {
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                    }

                    totalArea += Vector3.Cross(t.V1 - t.V0, t.V2 - t.V0).magnitude * 0.5f;
                    var e0 = Vector3.Distance(t.V0, t.V1);
                    var e1 = Vector3.Distance(t.V1, t.V2);
                    var e2 = Vector3.Distance(t.V2, t.V0);
                    var em = Mathf.Max(e0, Mathf.Max(e1, e2));
                    if (em > maxEdgeLen) maxEdgeLen = em;
                    vertList.Add((t.V0, triIdx * 3));
                    vertList.Add((t.V1, triIdx * 3 + 1));
                    vertList.Add((t.V2, triIdx * 3 + 2));
                    triIdx++;
                }

                regionVertices[rid] = vertList;

                var samplePt = (triList[0].V0 + triList[0].V1 + triList[0].V2) / 3f;
                var gx = Mathf.FloorToInt(samplePt.x / subdivCell);
                var gz = Mathf.FloorToInt(samplePt.z / subdivCell);
                var hb = Mathf.FloorToInt(samplePt.y / heightBand);

                sb.AppendLine($"  --- Region {rid} ({triList.Count} tri) ---");
                sb.AppendLine($"    Y range: [{yMin:F2}, {yMax:F2}]  ΔY={yMax - yMin:F2}m");
                sb.AppendLine($"    Area: {totalArea:F2}m²  MaxEdge: {maxEdgeLen:F2}m");
                sb.AppendLine($"    Cell: gx={gx} gz={gz} hb={hb}  " +
                              $"(X[{gx * subdivCell:F0},{(gx + 1) * subdivCell:F0}] " +
                              $"Z[{gz * subdivCell:F0},{(gz + 1) * subdivCell:F0}])");

                if (hasTerrain)
                {
                    var midY = (yMin + yMax) * 0.5f;
                    sb.AppendLine($"    Above terrain: {midY - terrainY:F2}m");
                }

                var hasGround = CellValidator.HasGroundBelow(samplePt);
                sb.AppendLine($"    GroundBelow: {hasGround}");

                if (NavMesh.FindClosestEdge(samplePt, out var edgeHit, filter))
                    sb.AppendLine($"    EdgeDist: {edgeHit.distance:F3}m");

                var wideEnough = CellValidator.IsSurfaceWideEnough(samplePt, filter);
                sb.AppendLine($"    SurfaceWide: {wideEnough}");

                if (graph != null)
                {
                    var resolvedId = graph.PointToRegionId(samplePt);
                    sb.AppendLine($"    GraphLookup: {resolvedId ?? "(unresolved)"}");
                }

                foreach (var t in triList)
                {
                    var tc = (t.V0 + t.V1 + t.V2) / 3f;
                    sb.AppendLine($"    Tri: ({t.V0.x:F2},{t.V0.y:F2},{t.V0.z:F2}) " +
                                  $"({t.V1.x:F2},{t.V1.y:F2},{t.V1.z:F2}) " +
                                  $"({t.V2.x:F2},{t.V2.y:F2},{t.V2.z:F2})  " +
                                  $"cen=({tc.x:F2},{tc.y:F2},{tc.z:F2})");
                }
            }

            // Shared-edge analysis: check which regions share vertex positions
            const float vertexEps = 0.01f;
            sb.AppendLine("  --- Edge sharing ---");
            var rids = new List<string>(regionVertices.Keys);
            var anyShared = false;
            for (var i = 0; i < rids.Count; i++)
            for (var j = i + 1; j < rids.Count; j++)
            {
                var shared = 0;
                foreach (var (va, _) in regionVertices[rids[i]])
                foreach (var (vb, _) in regionVertices[rids[j]])
                    if (Vector3.Distance(va, vb) < vertexEps)
                        shared++;
                if (shared >= 2)
                {
                    sb.AppendLine($"    {rids[i]} <-> {rids[j]}: {shared} shared vertices (CONNECTED)");
                    anyShared = true;
                }
                else if (shared == 1)
                {
                    sb.AppendLine($"    {rids[i]} <-> {rids[j]}: 1 shared vertex (TOUCHING, no shared edge)");
                    anyShared = true;
                }
            }

            if (!anyShared)
                sb.AppendLine("    No shared edges or vertices between nearby regions");

            var output = sb.ToString();
            Plugin.Log?.LogInfo(output);
            Console.instance?.Print(output);
        }

        [RegisterCleanup]
        public static void Cleanup()
        {
            if (s_instance != null)
            {
                Destroy(s_instance.gameObject);
                s_instance = null;
            }
        }
    }
}