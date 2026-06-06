using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Structured bundle dump for villager path failures. When a stall
    ///     escape fires, <see cref="Record"/> writes a per-incident directory
    ///     under <c>vv_dumps/incidents/&lt;id&gt;/</c> containing
    ///     incident.json (villager + destination + lastPath + HNA region BFS
    ///     trace + recent AI events) and two PNGs (villager view, destination
    ///     view) — enough to diagnose the failure offline without log
    ///     scraping or live-debug introspection.
    ///
    ///     <para>Deduped by composite key <c>{villager_id}_{round(dest.x/4)}_
    ///     {round(dest.z/4)}_{failureKind}</c> so the same Farmer-stuck-on-the-
    ///     same-cooking-station spam collapses to one incident with an
    ///     occurrence counter. INDEX.json at the root makes <c>jq</c> queries
    ///     cheap and avoids per-incident directory probing for dedup.</para>
    ///
    ///     <para>Lifecycle: <see cref="ClearOnLoad"/> wipes the directory at
    ///     plugin load; past-session incidents are noise once the world state
    ///     has changed.</para>
    /// </summary>
    internal static class IncidentRecorder
    {
        private const int DestBucketM = 4;
        private const float IncidentAnchorClearance = 10f;
        private const float EventRingLookbackSec = 30f;

        // Composite-key cache, populated lazily from INDEX.json so we don't
        // re-read it from disk on every stall. Reset by ClearOnLoad.
        private static readonly Dictionary<string, string> s_keyToIncidentId = new Dictionary<string, string>();
        private static int s_nextIncidentSeq;

        public static void ClearOnLoad()
        {
            try
            {
                var dir = IncidentsDir();
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[IncidentRecorder] Failed to clear incidents/ on load: {ex.GetType().Name}: {ex.Message}");
            }
            s_keyToIncidentId.Clear();
            s_nextIncidentSeq = 0;
        }

        /// <summary>
        ///     Record an incident for the given villager. <paramref name="destPos"/>
        ///     is the destination the villager was failing to reach — passed
        ///     in by the caller rather than read from VillagerAI so the
        ///     recorder doesn't depend on which VillagerAI field happens to
        ///     hold "target" at the moment of failure (it varies by call
        ///     site). Composite key dedups so repeats of the same
        ///     villager-target-kind tuple bump an occurrences counter on the
        ///     existing incident.json instead of writing a new bundle. Safe
        ///     to call from any thread that can read VillagerAI state; the
        ///     screenshot enqueue marshals onto the Unity main thread via
        ///     the existing capture queue.
        /// </summary>
        public static void Record(VillagerAI ai, Vector3 destPos, string failureKind)
        {
            if (ai == null) return;
            // Off by default: this dumps incident.json AND enqueues orchestrated
            // screenshots that teleport the player to the villager/destination
            // for a top-down snap — a frame/restore hiccup can leave the
            // character stranded in the sky. Gated behind the same switch as the
            // repartition auto-capture. See AutoDiagnosticCaptureEnabled.
            if (!Settings.VillagerSettings.AutoDiagnosticCaptureEnabled) return;
            try
            {
                var key = ComputeKey(ai, destPos, failureKind);

                if (s_keyToIncidentId.TryGetValue(key, out var existingId))
                {
                    BumpExisting(existingId);
                    return;
                }

                var incidentId = $"{++s_nextIncidentSeq:D3}_{Sanitize(ai.NpcName)}_{failureKind}";
                s_keyToIncidentId[key] = incidentId;

                WriteIncidentBundle(ai, destPos, failureKind, incidentId, key);
                EnqueueIncidentScreenshots(ai, destPos, incidentId);
            }
            catch (Exception ex)
            {
                // Incident dump must never break the mod — incidents are
                // diagnostic-only, the actual stall-recovery logic is upstream.
                Plugin.Log?.LogWarning(
                    $"[IncidentRecorder] Record failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string ComputeKey(VillagerAI ai, Vector3 destPos, string failureKind)
        {
            var bx = Mathf.RoundToInt(destPos.x / DestBucketM);
            var bz = Mathf.RoundToInt(destPos.z / DestBucketM);
            return $"{Sanitize(ai.UniqueId)}_{bx}_{bz}_{failureKind}";
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static void BumpExisting(string incidentId)
        {
            try
            {
                var dir = Path.Combine(IncidentsDir(), incidentId);
                var jsonPath = Path.Combine(dir, "incident.json");
                if (!File.Exists(jsonPath)) return; // disk state mismatch — silently ignore
                // Crude but sufficient: read, increment "occurrences", rewrite.
                // The schema is small enough that full read/write is cheaper
                // than streaming JSON edits.
                var text = File.ReadAllText(jsonPath);
                text = BumpField(text, "occurrences");
                text = ReplaceField(text, "lastSeenUtc",
                    "\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"");
                File.WriteAllText(jsonPath, text);
                UpdateIndexBump(incidentId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[IncidentRecorder] Bump failed for {incidentId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string BumpField(string json, string fieldName)
        {
            var marker = "\"" + fieldName + "\":";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return json;
            var valStart = idx + marker.Length;
            var valEnd = valStart;
            while (valEnd < json.Length && (char.IsDigit(json[valEnd]) || json[valEnd] == ' ')) valEnd++;
            var current = json.Substring(valStart, valEnd - valStart).Trim();
            if (!int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return json;
            return json.Substring(0, valStart) + (n + 1).ToString(CultureInfo.InvariantCulture)
                   + json.Substring(valEnd);
        }

        private static string ReplaceField(string json, string fieldName, string newValueLiteral)
        {
            var marker = "\"" + fieldName + "\":";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return json;
            var valStart = idx + marker.Length;
            // Skip whitespace then capture the next value (string or null).
            var p = valStart;
            while (p < json.Length && json[p] == ' ') p++;
            if (p >= json.Length) return json;
            int valEnd;
            if (json[p] == '"')
            {
                valEnd = p + 1;
                while (valEnd < json.Length && json[valEnd] != '"') valEnd++;
                if (valEnd < json.Length) valEnd++; // include closing quote
            }
            else
            {
                valEnd = p;
                while (valEnd < json.Length && json[valEnd] != ',' && json[valEnd] != '}') valEnd++;
            }
            return json.Substring(0, p) + newValueLiteral + json.Substring(valEnd);
        }

        private static void WriteIncidentBundle(
            VillagerAI ai, Vector3 destPos, string failureKind,
            string incidentId, string compositeKey)
        {
            var dir = Path.Combine(IncidentsDir(), incidentId);
            Directory.CreateDirectory(dir);

            var villagerPos = ai.transform != null ? ai.transform.position : Vector3.zero;
            var graph = Villages.Entity.VillageRegistry.GraphAt(villagerPos);

            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"incidentId\":\"").Append(JsonEscape(incidentId)).Append("\",");
            sb.Append("\"compositeKey\":\"").Append(JsonEscape(compositeKey)).Append("\",");
            sb.Append("\"failureKind\":\"").Append(JsonEscape(failureKind)).Append("\",");
            sb.Append("\"occurrences\":1,");
            sb.Append("\"firstSeenUtc\":\"")
                .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"lastSeenUtc\":\"")
                .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",");

            AppendVillagerBlock(sb, ai, villagerPos);
            sb.Append(',');
            AppendDestinationBlock(sb, ai, destPos);
            sb.Append(',');
            AppendLastPathBlock(sb, ai);
            sb.Append(',');
            AppendHnaBlock(sb, graph, villagerPos, destPos);
            sb.Append(',');
            AppendRecentEventsBlock(sb, ai);

            sb.Append('}');
            File.WriteAllText(Path.Combine(dir, "incident.json"), sb.ToString());

            UpdateIndexAdd(incidentId, ai, destPos, failureKind);
            Plugin.Log?.LogInfo(
                $"[IncidentRecorder] {incidentId}: dumped {failureKind} for {ai.NpcName} -> " +
                $"({destPos.x:F1},{destPos.z:F1})");
        }

        private static void AppendVillagerBlock(StringBuilder sb, VillagerAI ai, Vector3 pos)
        {
            sb.Append("\"villager\":{");
            sb.Append("\"id\":\"").Append(JsonEscape(ai.UniqueId)).Append("\",");
            sb.Append("\"name\":\"").Append(JsonEscape(ai.NpcName)).Append("\",");
            sb.Append("\"type\":\"").Append(JsonEscape(ai.VillagerType.ToString())).Append("\",");
            sb.Append("\"state\":\"").Append(JsonEscape(ai.CurrentState.ToString())).Append("\",");
            sb.Append("\"pos\":").Append(JsonVec3(pos)).Append(',');
            var bp = ai.Memory != null ? ai.Memory.BedPosition : Vector3.zero;
            sb.Append("\"bedPosition\":").Append(JsonVec3(bp));
            sb.Append('}');
        }

        private static void AppendDestinationBlock(StringBuilder sb, VillagerAI ai, Vector3 destPos)
        {
            sb.Append("\"destination\":{");
            sb.Append("\"pos\":").Append(JsonVec3(destPos));
            sb.Append('}');
        }

        private static void AppendLastPathBlock(StringBuilder sb, VillagerAI ai)
        {
            sb.Append("\"lastPath\":{");
            sb.Append("\"available\":");
            // The mod's path-storage uses reflection in VillagerAI; we read it
            // here via the same field. Best-effort — if absent, just say so.
            var corners = TryGetCurrentPathCorners(ai);
            if (corners == null || corners.Count == 0)
            {
                sb.Append("false}");
                return;
            }
            sb.Append("true,\"cornerCount\":").Append(corners.Count).Append(",\"corners\":[");
            for (var i = 0; i < corners.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonVec3(corners[i]));
            }
            sb.Append("]");

            // Per-segment capsule cast: villager pos → corner[0] → corner[1] → …
            // Reports which segment (if any) is physically blocked by a
            // piece / static-solid collider and what hit. This is the
            // missing diagnostic when NavMesh says the path is Complete but
            // the agent capsule can't actually walk it — the kind of stall
            // that incident 002_Blacksmith (May 2026) surfaced.
            sb.Append(",\"segmentCasts\":");
            AppendSegmentCasts(sb, ai.transform != null ? ai.transform.position : Vector3.zero, corners);

            sb.Append('}');
        }

        /// <summary>
        ///     Sweep an agent-sized capsule along each path segment and emit a
        ///     JSON array entry per segment with hit info. Diagnostic-only —
        ///     a "blocked" result here doesn't change the path the AI is
        ///     trying to walk; it just tells the next investigation
        ///     <em>what is in the way</em> without needing a separate probe
        ///     command. Capsule dimensions approximate
        ///     <c>NavMeshLinkPlacer.IsLinkGeometricallyTraversable</c>'s body
        ///     model so the diagnostic roughly agrees with the link-validation
        ///     rejection log lines on the same geometry.
        /// </summary>
        private static void AppendSegmentCasts(StringBuilder sb, Vector3 villagerPos, List<Vector3> corners)
        {
            sb.Append('[');
            const float bodyBottomLift = 0.5f;
            const float bodyTopLift = 1.35f;
            const float bodyRadius = 0.4f;
            var mask = UnityEngine.LayerMask.GetMask(
                "Default", "static_solid", "piece", "blocker", "pathblocker");

            var prev = villagerPos;
            for (var i = 0; i < corners.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var cur = corners[i];
                var dir = cur - prev;
                var dist = dir.magnitude;
                sb.Append("{\"segment\":").Append(i)
                  .Append(",\"distM\":").Append(dist.ToString("F2", CultureInfo.InvariantCulture));
                if (dist < 0.001f)
                {
                    sb.Append(",\"degenerate\":true}");
                    prev = cur;
                    continue;
                }

                try
                {
                    var bottomCap = prev + UnityEngine.Vector3.up * bodyBottomLift;
                    var topCap = prev + UnityEngine.Vector3.up * bodyTopLift;
                    if (UnityEngine.Physics.CapsuleCast(
                            bottomCap, topCap, bodyRadius,
                            dir / dist, out var hit, dist,
                            mask, UnityEngine.QueryTriggerInteraction.Ignore))
                    {
                        var hitName = hit.collider != null ? hit.collider.name : "(null)";
                        var hitLayer = hit.collider != null ? hit.collider.gameObject.layer : -1;
                        sb.Append(",\"blocked\":true")
                          .Append(",\"hitName\":\"").Append(JsonEscape(hitName)).Append('"')
                          .Append(",\"hitLayer\":").Append(hitLayer)
                          .Append(",\"hitDistM\":").Append(hit.distance.ToString("F2", CultureInfo.InvariantCulture))
                          .Append(",\"hitPos\":").Append(JsonVec3(hit.point));
                    }
                    else
                    {
                        sb.Append(",\"blocked\":false");
                    }
                }
                catch (Exception ex)
                {
                    sb.Append(",\"castError\":\"").Append(JsonEscape(ex.GetType().Name)).Append('"');
                }
                sb.Append('}');
                prev = cur;
            }
            sb.Append(']');
        }

        private static List<Vector3> TryGetCurrentPathCorners(VillagerAI ai)
        {
            try
            {
                var field = typeof(VillagerAI).GetField("s_pathField",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var pathFi = field?.GetValue(null) as System.Reflection.FieldInfo;
                return pathFi?.GetValue(ai) as List<Vector3>;
            }
            catch
            {
                return null;
            }
        }

        private static void AppendHnaBlock(StringBuilder sb, RegionGraph graph,
            Vector3 villagerPos, Vector3 destPos)
        {
            sb.Append("\"hna\":{");
            if (graph == null || !graph.IsAvailable)
            {
                sb.Append("\"available\":false}");
                return;
            }

            sb.Append("\"available\":true,");
            sb.Append("\"villageKey\":\"").Append(JsonEscape(graph.RegisteredVillageKey ?? "")).Append("\",");

            var sourceRegion = graph.PointToRegionId(villagerPos);
            var targetRegion = graph.PointToRegionId(destPos);
            sb.Append("\"sourceRegionId\":").Append(sourceRegion != null ? "\"" + JsonEscape(sourceRegion) + "\"" : "null").Append(',');
            sb.Append("\"targetRegionId\":").Append(targetRegion != null ? "\"" + JsonEscape(targetRegion) + "\"" : "null").Append(',');

            // Region-graph BFS path (target → seed). Mirrors what vv_bfs_trace
            // surfaces interactively. If target unresolved, omit.
            sb.Append("\"regionPathToSeed\":");
            if (targetRegion != null)
            {
                var path = BfsAdjacencyStore.PathToSeed(targetRegion);
                if (path != null)
                {
                    sb.Append('[');
                    for (var i = 0; i < path.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(JsonEscape(path[i])).Append('"');
                    }
                    sb.Append(']');
                }
                else
                {
                    sb.Append("null");
                }
            }
            else
            {
                sb.Append("null");
            }

            sb.Append(",\"adjacencyCount\":").Append(BfsAdjacencyStore.Adjacency.Count);
            sb.Append(",\"seedCount\":").Append(BfsAdjacencyStore.Seeds.Count);

            // Raw NavMesh check at both endpoints — distinguishes "AI can't
            // path" from "endpoint isn't even on the NavMesh".
            AppendNavMeshSample(sb, "navMeshAtSource", villagerPos);
            AppendNavMeshSample(sb, "navMeshAtTarget", destPos);

            sb.Append('}');
        }

        private static void AppendNavMeshSample(StringBuilder sb, string fieldName, Vector3 pos)
        {
            sb.Append(",\"").Append(fieldName).Append("\":");
            try
            {
                var filter = new NavMeshQueryFilter
                {
                    agentTypeID = VillagerAgentType.ResolveValheimHumanoidAgentTypeID(),
                    areaMask = NavMesh.AllAreas,
                };
                if (NavMesh.SamplePosition(pos, out var hit, 5f, filter))
                {
                    sb.Append("{\"hit\":true,\"snappedPos\":").Append(JsonVec3(hit.position))
                        .Append(",\"deltaY\":").Append((hit.position.y - pos.y).ToString("R", CultureInfo.InvariantCulture))
                        .Append('}');
                }
                else
                {
                    sb.Append("{\"hit\":false}");
                }
            }
            catch
            {
                sb.Append("null");
            }
        }

        private static void AppendRecentEventsBlock(StringBuilder sb, VillagerAI ai)
        {
            sb.Append("\"recentEvents\":[");
            var now = Time.time;
            var events = ai.EventRing.Snapshot(now, EventRingLookbackSec);
            for (var i = 0; i < events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var ev = events[i];
                sb.Append('{');
                sb.Append("\"tSec\":").Append((ev.TimeSec - now).ToString("F2", CultureInfo.InvariantCulture))
                    .Append(",\"kind\":\"").Append(ev.Kind.ToString()).Append('"')
                    .Append(",\"detail\":\"").Append(JsonEscape(ev.Detail)).Append('"');
                if (ev.Kind == AiEventRing.EventKind.TargetSet)
                {
                    sb.Append(",\"newTarget\":").Append(JsonVec3(ev.PosA))
                        .Append(",\"prevTarget\":").Append(JsonVec3(ev.PosB));
                }
                else if (ev.Kind == AiEventRing.EventKind.PathRecompute)
                {
                    sb.Append(",\"target\":").Append(JsonVec3(ev.PosA))
                        .Append(",\"corners\":").Append(ev.IntA);
                }
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static void EnqueueIncidentScreenshots(VillagerAI ai, Vector3 destPos, string incidentId)
        {
            var subdir = Path.Combine("incidents", incidentId);
            var villagerPos = ai.transform != null ? ai.transform.position : Vector3.zero;
            DebugLog.Capture(CaptureRequest.ForIncident("incident_villager",
                subdir, "villager", villagerPos, IncidentAnchorClearance));
            DebugLog.Capture(CaptureRequest.ForIncident("incident_destination",
                subdir, "destination", destPos, IncidentAnchorClearance));
        }

        #region INDEX.json

        private static void UpdateIndexAdd(string incidentId, VillagerAI ai, Vector3 destPos, string failureKind)
        {
            try
            {
                var idxPath = Path.Combine(IncidentsDir(), "INDEX.json");
                Directory.CreateDirectory(IncidentsDir());
                var entry = new StringBuilder();
                entry.Append('{')
                    .Append("\"id\":\"").Append(JsonEscape(incidentId)).Append("\",")
                    .Append("\"villager\":\"").Append(JsonEscape(ai.NpcName)).Append("\",")
                    .Append("\"failureKind\":\"").Append(JsonEscape(failureKind)).Append("\",")
                    .Append("\"destination\":").Append(JsonVec3(destPos)).Append(',')
                    .Append("\"occurrences\":1,")
                    .Append("\"firstSeenUtc\":\"")
                    .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\"")
                    .Append('}');

                if (!File.Exists(idxPath))
                {
                    File.WriteAllText(idxPath, "{\"incidents\":[" + entry + "]}");
                    return;
                }
                var text = File.ReadAllText(idxPath);
                // Append to the array. Look for the closing "]}" and insert
                // before it. Simpler than full JSON parse, fine for the
                // single-writer mod scenario.
                var closeIdx = text.LastIndexOf("]}", StringComparison.Ordinal);
                if (closeIdx < 0)
                {
                    File.WriteAllText(idxPath, "{\"incidents\":[" + entry + "]}");
                    return;
                }
                // If array already has entries, prepend a comma.
                var arrayOpen = text.IndexOf('[');
                var arrayHasContent = arrayOpen >= 0 && closeIdx - arrayOpen > 1;
                var insert = (arrayHasContent ? "," : "") + entry;
                File.WriteAllText(idxPath, text.Substring(0, closeIdx) + insert + text.Substring(closeIdx));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[IncidentRecorder] INDEX add failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void UpdateIndexBump(string incidentId)
        {
            try
            {
                var idxPath = Path.Combine(IncidentsDir(), "INDEX.json");
                if (!File.Exists(idxPath)) return;
                var text = File.ReadAllText(idxPath);
                // Find the entry by id, bump its occurrences. Cheap textual
                // scan — full parse would be cleaner but overkill here.
                var idMarker = "\"id\":\"" + incidentId + "\"";
                var entryStart = text.IndexOf(idMarker, StringComparison.Ordinal);
                if (entryStart < 0) return;
                var occMarker = "\"occurrences\":";
                var occStart = text.IndexOf(occMarker, entryStart, StringComparison.Ordinal);
                if (occStart < 0) return;
                var valStart = occStart + occMarker.Length;
                var valEnd = valStart;
                while (valEnd < text.Length && char.IsDigit(text[valEnd])) valEnd++;
                if (!int.TryParse(text.Substring(valStart, valEnd - valStart),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return;
                var updated = text.Substring(0, valStart)
                              + (n + 1).ToString(CultureInfo.InvariantCulture)
                              + text.Substring(valEnd);
                File.WriteAllText(idxPath, updated);
            }
            catch
            {
                /* swallow */
            }
        }

        #endregion

        private static string IncidentsDir()
        {
            string root;
            try
            {
                root = Paths.ConfigPath;
            }
            catch
            {
                root = ".";
            }
            return Path.Combine(root, "vv_dumps", "incidents");
        }

        private static string JsonVec3(Vector3 v)
        {
            return "[" + v.x.ToString("R", CultureInfo.InvariantCulture)
                       + "," + v.y.ToString("R", CultureInfo.InvariantCulture)
                       + "," + v.z.ToString("R", CultureInfo.InvariantCulture) + "]";
        }

        private static string JsonEscape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
