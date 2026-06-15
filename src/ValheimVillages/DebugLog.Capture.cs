using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        private static int _captureCounter;
        private static DebugCaptureBehaviour _captureBehaviour;

        /// <summary>
        ///     Request a screenshot capture from the active player camera plus a
        ///     sidecar JSON (counter, trigger, world time, camera pose). Safe to
        ///     call from any thread; the actual capture marshals onto the main
        ///     Unity thread via a queued coroutine. Silently no-ops if
        ///     <see cref="Plugin.Instance" /> or <see cref="Camera.main" /> is
        ///     unavailable (e.g. main-menu reload). Output overwrites
        ///     &lt;SidecarDir&gt;/last_capture.png and last_capture.json on every call.
        ///
        ///     <para>For incident captures that need a custom output directory
        ///     and/or a non-seed-anchor anchor, use the
        ///     <see cref="Capture(CaptureRequest)"/> overload.</para>
        /// </summary>
        public static void Capture(string trigger)
        {
            Capture(CaptureRequest.Default(trigger));
        }

        /// <summary>
        ///     Request a screenshot with a fully-specified <see cref="CaptureRequest"/>.
        ///     The request controls trigger name, output directory/filename, and
        ///     optional anchor override (e.g. anchor at a villager position with
        ///     a tighter clearance for an incident capture).
        /// </summary>
        public static void Capture(CaptureRequest request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;

                if (_captureBehaviour == null)
                    _captureBehaviour = plugin.gameObject.GetComponent<DebugCaptureBehaviour>()
                                        ?? plugin.gameObject.AddComponent<DebugCaptureBehaviour>();
                _captureBehaviour.Enqueue(request);
            }
            catch
            {
                /* capture must never break the mod */
            }
        }

        internal static int NextCaptureCounter()
        {
            return Interlocked.Increment(ref _captureCounter);
        }
    }

    /// <summary>
    ///     What to capture and where to put it. Constructed by callers
    ///     (<see cref="DebugLog.Capture(string)"/> for the default
    ///     last_capture.png flow; the incident recorder for villager-anchored
    ///     captures into <c>vv_dumps/incidents/&lt;id&gt;/</c>). Marshalled
    ///     across threads via the capture behaviour's queue.
    /// </summary>
    internal readonly struct CaptureRequest
    {
        public readonly string Trigger;

        /// <summary>
        ///     Output directory relative to <c>Paths.ConfigPath/vv_dumps/</c>
        ///     (empty = directly into vv_dumps/). Created if missing.
        /// </summary>
        public readonly string OutputSubdir;

        /// <summary>
        ///     Base filename for the PNG + sidecar JSON (without extension).
        ///     Default capture flow uses "last_capture"; incident captures use
        ///     "villager" / "destination".
        /// </summary>
        public readonly string OutputBaseName;

        /// <summary>
        ///     Optional anchor override. When null, the orchestration session
        ///     resolves via <see cref="CaptureAnchor.Resolve()"/> (nearest seed
        ///     anchor at default clearance). When set, the session anchors at
        ///     this position with the provided clearance — incident captures
        ///     use this with the villager/destination pos and a 10m clearance.
        /// </summary>
        public readonly Vector3? AnchorOverride;
        public readonly float AnchorClearance;

        /// <summary>
        ///     When true, the sidecar JSON gets the full <c>diagnostics</c>
        ///     block (last relaxation pass, village bounds). Incident captures
        ///     set this to false — they have their own diagnostic JSON
        ///     (incident.json) and don't need to duplicate the global state.
        /// </summary>
        public readonly bool IncludeDiagnostics;

        public CaptureRequest(string trigger, string outputSubdir, string outputBaseName,
            Vector3? anchorOverride, float anchorClearance, bool includeDiagnostics)
        {
            Trigger = trigger ?? "unknown";
            OutputSubdir = outputSubdir ?? "";
            OutputBaseName = string.IsNullOrEmpty(outputBaseName) ? "last_capture" : outputBaseName;
            AnchorOverride = anchorOverride;
            AnchorClearance = anchorClearance;
            IncludeDiagnostics = includeDiagnostics;
        }

        /// <summary>
        ///     Default request: writes vv_dumps/last_capture.{png,json}, uses
        ///     seed-anchor anchor at default clearance, includes diagnostics.
        ///     Matches the pre-refactor <see cref="DebugLog.Capture(string)"/>
        ///     contract.
        /// </summary>
        public static CaptureRequest Default(string trigger)
        {
            return new CaptureRequest(trigger, "", "last_capture",
                anchorOverride: null,
                anchorClearance: Diagnostics.CaptureAnchor.VerticalClearance,
                includeDiagnostics: true);
        }

        /// <summary>
        ///     Incident-flow request: anchor at a specific world position with
        ///     a tighter clearance, write into a per-incident subdirectory,
        ///     skip the global diagnostics block.
        /// </summary>
        public static CaptureRequest ForIncident(string trigger, string incidentSubdir,
            string baseName, Vector3 anchor, float clearance)
        {
            return new CaptureRequest(trigger, incidentSubdir, baseName,
                anchorOverride: anchor,
                anchorClearance: clearance,
                includeDiagnostics: false);
        }
    }

    internal class DebugCaptureBehaviour : MonoBehaviour
    {
        private readonly ConcurrentQueue<CaptureRequest> _pending = new();

        /// <summary>
        ///     Serialization flag for the capture pipeline. Captures MUST run
        ///     one-at-a-time: when two are enqueued back-to-back (e.g. the
        ///     incident system queues a villager view + a destination view),
        ///     running them in parallel coroutines causes the second
        ///     <see cref="OrchestrationSession.TryBegin"/> to snapshot the
        ///     POST-FIRST-TELEPORT state as its "original" — so when the
        ///     second capture restores, the camera lands at the first
        ///     capture's anchor pose instead of where the player actually was.
        ///     Symptom: camera sticks at last-screenshot location after a
        ///     two-capture incident dump.
        /// </summary>
        private bool _processing;

        /// <summary>
        ///     Triggers whose capture should be orchestrated (teleport player to
        ///     the seed-anchor anchor, look straight down, hide HUD, snap, restore
        ///     state in a <c>finally</c>). Repartition is included because the
        ///     auto-repartition that follows hot-reload is the canonical "graph
        ///     is fully rebuilt — show me the state" moment; capturing it
        ///     orchestrated gives one deterministic PNG per cycle instead of
        ///     two with the first overwritten. Add new triggers here as they
        ///     emerge.
        /// </summary>
        private static readonly HashSet<string> OrchestratedTriggers = new HashSet<string>
        {
            "repartition",
            "manual",
        };

        private void Update()
        {
            if (_processing) return;
            if (_pending.IsEmpty) return;
            StartCoroutine(ProcessQueue());
        }

        public void Enqueue(CaptureRequest req)
        {
            _pending.Enqueue(req);
        }

        /// <summary>
        ///     Drain the pending queue sequentially. Each capture's
        ///     orchestration session must fully complete (teleport, snap,
        ///     restore) before the next begins, otherwise overlapping
        ///     TryBegin calls contaminate each other's saved camera/player
        ///     state — see <see cref="_processing"/> doc.
        ///
        ///     <para>The <c>yield return null</c> between captures is load-
        ///     bearing: after a capture's Restore re-enables GameCamera, that
        ///     component's LateUpdate needs at least one frame to reposition
        ///     the camera to follow the player. Without the yield, the next
        ///     capture's TryBegin snapshots the still-teleported camera pose
        ///     as its "original", and ends up restoring to that stale value
        ///     when it finishes. Symptom: camera sticks at the last capture's
        ///     anchor.</para>
        /// </summary>
        private IEnumerator ProcessQueue()
        {
            _processing = true;
            try
            {
                while (_pending.TryDequeue(out var req))
                {
                    var counter = DebugLog.NextCaptureCounter();
                    yield return CaptureRoutine(req, counter);
                    // Let GameCamera.LateUpdate run so the camera actually
                    // follows the player back before the next capture
                    // snapshots its "original" state.
                    yield return null;
                }
            }
            finally
            {
                _processing = false;
            }
        }

        private IEnumerator CaptureRoutine(CaptureRequest req, int counter)
        {
            // Let any debug overlay (RegionDebugVisualization etc.) redraw
            // against post-event state before we grab the frame.
            yield return new WaitForSecondsRealtime(0.6f);

            var cam = Camera.main;
            if (cam == null) yield break;

            OrchestrationSession session = null;
            if (OrchestratedTriggers.Contains(req.Trigger) || req.AnchorOverride.HasValue)
            {
                session = OrchestrationSession.TryBegin(req);
                session?.LogStarted(req.Trigger, session.AnchorPos);
            }

            try
            {
                if (session != null)
                    // One frame for the player transform / camera follow / HUD
                    // toggle to settle before we sample camera pose and snap.
                    yield return null;

                // Re-assert the orchestrated camera pose AFTER the settle frame.
                // GameCamera is disabled in TryBegin, but other LateUpdate hooks
                // (other mods, etc.) may still touch the camera; this is the
                // last write before the frame is captured.
                session?.ApplyCameraPose();

                var pos = cam.transform.position;
                var euler = cam.transform.eulerAngles;
                var worldTime = -1f;
                try
                {
                    if (EnvMan.instance != null) worldTime = EnvMan.instance.GetDayFraction();
                }
                catch
                {
                    /* EnvMan may be missing on early reload */
                }

                // CaptureScreenshotAsTexture requires end-of-frame so all camera
                // passes (incl. debug overlays) have rendered.
                yield return new WaitForEndOfFrame();

                WritePngAndSidecar(req, counter, pos, euler, worldTime);
            }
            finally
            {
                session?.Restore();
            }
        }

        private static void WritePngAndSidecar(CaptureRequest req, int counter,
            Vector3 pos, Vector3 euler, float worldTime)
        {
            string dir;
            try
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

                dir = Path.Combine(root, "vv_dumps");
                if (!string.IsNullOrEmpty(req.OutputSubdir))
                    dir = Path.Combine(dir, req.OutputSubdir);
                Directory.CreateDirectory(dir);
            }
            catch
            {
                return;
            }

            var pngPath = Path.Combine(dir, req.OutputBaseName + ".png");
            var jsonPath = Path.Combine(dir, req.OutputBaseName + ".json");

            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                var png = tex.EncodeToPNG();
                File.WriteAllBytes(pngPath, png);
            }
            catch
            {
                /* swallow — sidecar below still useful */
            }
            finally
            {
                if (tex != null) Destroy(tex);
            }

            // Write sidecar AFTER the PNG so a polling reader can use the
            // monotonic counter as the freshness signal for the PNG.
            try
            {
                var sb = new StringBuilder(2048);
                sb.Append('{');
                sb.Append("\"counter\":").Append(counter.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"trigger\":\"").Append(JsonEscape(req.Trigger)).Append("\",");
                sb.Append("\"timestampUtc\":\"")
                    .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",");
                sb.Append("\"worldTimeOfDay\":")
                    .Append(worldTime.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"cameraPos\":[")
                    .Append(pos.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(pos.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(pos.z.ToString("R", CultureInfo.InvariantCulture)).Append("],");
                sb.Append("\"cameraYaw\":")
                    .Append(euler.y.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"cameraPitch\":")
                    .Append(euler.x.ToString("R", CultureInfo.InvariantCulture));
                if (req.IncludeDiagnostics) AppendDiagnostics(sb);
                sb.Append('}');
                File.WriteAllText(jsonPath, sb.ToString());
            }
            catch
            {
                /* swallow */
            }
        }

        /// <summary>
        ///     Appends a "diagnostics" object to the in-progress sidecar JSON:
        ///     last-relaxation drop stats (the dedup summary, as structured data),
        ///     and per-village bounds (BFS lookup grid vs. boundary cells). A
        ///     mismatch between the two bounds is the smoking gun for the BFS
        ///     coverage class of bugs that this overhaul exists to make
        ///     diagnosable without log scraping.
        /// </summary>
        private static void AppendDiagnostics(StringBuilder sb)
        {
            sb.Append(",\"diagnostics\":{");

            // Legacy WaypointRelaxation diagnostics were removed with the old
            // patrol boundary pipeline; the geometric PatrolRouteBuilder has no
            // relaxation pass. Stub kept so sidecar consumers keep parsing.
            sb.Append("\"lastRelaxation\":{\"available\":false},");

            // Per-village bounds. Iterates Villages.Entity.VillageRegistry.AllGraphs() so multi-village
            // worlds emit one entry per village; the diff between BFS and bake
            // bounds per village is what the human or a downstream tool reads.
            sb.Append("\"villages\":[");
            var first = true;
            try
            {
                foreach (var graph in Villages.Entity.VillageRegistry.AllGraphs())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{');
                    sb.Append("\"key\":\"").Append(JsonEscape(graph.RegisteredVillageKey ?? "")).Append("\",");
                    sb.Append("\"regionCount\":").Append(graph.RegionCount).Append(',');
                    sb.Append("\"linkCount\":").Append(graph.LinkCount).Append(',');
                    sb.Append("\"bfsCells\":").Append(graph.Diagnostics.LookupGridCellCount).Append(',');
                    sb.Append("\"boundaryCells\":").Append(graph.Diagnostics.BoundaryCellCount).Append(',');
                    sb.Append("\"bfsBounds\":");
                    AppendBounds(sb,
                        graph.Diagnostics.TryGetLookupGridBounds(
                            out var bMinX, out var bMaxX, out var bMinZ, out var bMaxZ),
                        bMinX, bMaxX, bMinZ, bMaxZ);
                    sb.Append(",\"boundaryBounds\":");
                    AppendBounds(sb,
                        graph.Diagnostics.TryGetBoundaryCellsBounds(
                            out var cMinX, out var cMaxX, out var cMinZ, out var cMaxZ),
                        cMinX, cMaxX, cMinZ, cMaxZ);
                    sb.Append('}');
                }
            }
            catch
            {
                /* swallow — diagnostics must never break capture */
            }
            sb.Append(']');

            sb.Append('}');
        }

        private static void AppendBounds(StringBuilder sb, bool ok,
            float minX, float maxX, float minZ, float maxZ)
        {
            if (!ok)
            {
                sb.Append("null");
                return;
            }
            sb.Append("{\"x\":[")
              .Append(minX.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(maxX.ToString("R", CultureInfo.InvariantCulture)).Append("],\"z\":[")
              .Append(minZ.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(maxZ.ToString("R", CultureInfo.InvariantCulture)).Append("]}");
        }

        /// <summary>JSON-safe float emitter: NaN/Infinity must become null per RFC 8259.</summary>
        private static string JsonFloat(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonEscape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }

    /// <summary>
    ///     One-shot orchestration of the player's transform + HUD visibility
    ///     for a deterministic capture. <see cref="TryBegin"/> snapshots the
    ///     current state, teleports the player to the seed-anchor anchor pose,
    ///     and hides the HUD; <see cref="Restore"/> must be called from a
    ///     <c>finally</c> so any exception in the capture pipeline still
    ///     returns the player to where they were and re-shows the HUD.
    ///
    ///     <para>Why disable <see cref="GameCamera"/>: Valheim's GameCamera
    ///     drives the main camera transform every <c>LateUpdate</c> to follow
    ///     the player. Without disabling it, our orchestrated camera pose is
    ///     overwritten before <c>WaitForEndOfFrame</c> resolves and the PNG
    ///     reflects the player-following pose, not the top-down anchor pose.
    ///     The anchor's <see cref="ApplyCameraPose"/> is re-asserted right
    ///     before the frame is grabbed to guarantee the snap.</para>
    ///
    ///     Returns <c>null</c> from <see cref="TryBegin"/> when prerequisites
    ///     aren't met (no Player, no seed anchor). In that case the capture
    ///     degrades to a passive snapshot from the current camera — see the
    ///     "no silent fallbacks" rule: we never substitute fabricated anchor
    ///     coordinates.
    /// </summary>
    internal class OrchestrationSession
    {
        private readonly Player m_player;
        private readonly Vector3 m_savedPos;
        private readonly Quaternion m_savedRot;
        private readonly Hud m_hud;
        private readonly bool m_savedHudVisible;
        private readonly Vector3 m_savedCamPos;
        private readonly Quaternion m_savedCamRot;
        private readonly GameCamera m_gameCamera;
        private readonly bool m_savedGameCameraEnabled;
        private readonly Vector3 m_anchorPos;
        private readonly Quaternion m_anchorRot;

        private OrchestrationSession(Player player, Vector3 savedPos, Quaternion savedRot,
            Hud hud, bool savedHudVisible, Vector3 savedCamPos, Quaternion savedCamRot,
            GameCamera gameCamera, bool savedGameCameraEnabled,
            Vector3 anchorPos, Quaternion anchorRot)
        {
            m_player = player;
            m_savedPos = savedPos;
            m_savedRot = savedRot;
            m_hud = hud;
            m_savedHudVisible = savedHudVisible;
            m_savedCamPos = savedCamPos;
            m_savedCamRot = savedCamRot;
            m_gameCamera = gameCamera;
            m_savedGameCameraEnabled = savedGameCameraEnabled;
            m_anchorPos = anchorPos;
            m_anchorRot = anchorRot;
        }

        public Vector3 AnchorPos => m_anchorPos;

        /// <summary>
        ///     Re-assert the orchestrated camera pose. Called immediately before
        ///     <c>WaitForEndOfFrame</c> in the capture routine to defeat any
        ///     <c>LateUpdate</c>-driven camera follower that ran during the
        ///     post-teleport settle frame.
        /// </summary>
        public void ApplyCameraPose()
        {
            var cam = Camera.main;
            if (cam == null) return;
            try
            {
                cam.transform.position = m_anchorPos;
                cam.transform.rotation = m_anchorRot;
            }
            catch
            {
                /* swallow */
            }
        }

        public static OrchestrationSession TryBegin(CaptureRequest req)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Plugin.Log?.LogInfo(
                    "[Capture] Orchestration skipped: no local player. " +
                    "Falling back to passive capture.");
                return null;
            }

            Diagnostics.CaptureAnchor.Result anchor;
            if (req.AnchorOverride.HasValue)
            {
                // Caller-supplied anchor (incident flow): a specific world
                // position with a tighter clearance. Bypasses seed-anchor
                // resolution entirely so incidents at distant villagers /
                // destinations don't get mis-anchored on the wrong village.
                anchor = Diagnostics.CaptureAnchor.ResolveAt(
                    req.AnchorOverride.Value, req.AnchorClearance);
            }
            else
            {
                anchor = Diagnostics.CaptureAnchor.Resolve(player.transform.position);
            }
            if (!anchor.HasAnchor)
            {
                Plugin.Log?.LogWarning(
                    $"[Capture] Orchestration skipped: {anchor.Reason}. " +
                    "Falling back to passive capture (no silent coordinate fabrication).");
                return null;
            }

            // Snapshot first — every field we touch must round-trip back in Restore.
            var savedPos = player.transform.position;
            var savedRot = player.transform.rotation;
            var hud = Hud.instance;
            var savedHudVisible = true;
            try
            {
                if (hud != null) savedHudVisible = hud.gameObject.activeSelf;
            }
            catch
            {
                /* Hud API may differ across Valheim versions; degrade gracefully */
            }

            var cam = Camera.main;
            var savedCamPos = cam != null ? cam.transform.position : Vector3.zero;
            var savedCamRot = cam != null ? cam.transform.rotation : Quaternion.identity;

            var gameCamera = GameCamera.instance;
            var savedGameCameraEnabled = gameCamera != null && gameCamera.enabled;

            // Apply the orchestrated pose. Yaw=0 (north-up) means look at +Z;
            // Pitch=90 means look straight down. Quaternion order: pitch-then-yaw
            // matches Unity's Euler convention used elsewhere in this file.
            var anchorRot = Quaternion.Euler(anchor.Pitch, anchor.Yaw, 0f);
            try
            {
                player.transform.position = anchor.Pos;
                player.transform.rotation = anchorRot;
                if (cam != null)
                {
                    cam.transform.position = anchor.Pos;
                    cam.transform.rotation = anchorRot;
                }
                // Disable GameCamera's follow LateUpdate so our pose survives
                // until WaitForEndOfFrame resolves. Re-enabled in Restore.
                if (gameCamera != null) gameCamera.enabled = false;
                if (hud != null) hud.gameObject.SetActive(false);
            }
            catch (System.Exception ex)
            {
                // Already snapshotted — best to roll back what we managed to
                // apply and bail rather than capture in a half-orchestrated state.
                Plugin.Log?.LogWarning(
                    $"[Capture] Orchestration apply failed: {ex.GetType().Name}: {ex.Message}. " +
                    "Restoring state and falling back to passive capture.");
                try { player.transform.position = savedPos; player.transform.rotation = savedRot; }
                catch { /* swallow */ }
                try { if (gameCamera != null) gameCamera.enabled = savedGameCameraEnabled; }
                catch { /* swallow */ }
                try { if (hud != null) hud.gameObject.SetActive(savedHudVisible); }
                catch { /* swallow */ }
                return null;
            }

            return new OrchestrationSession(player, savedPos, savedRot,
                hud, savedHudVisible, savedCamPos, savedCamRot,
                gameCamera, savedGameCameraEnabled, anchor.Pos, anchorRot);
        }

        public void LogStarted(string trigger, Vector3 anchorPos)
        {
            // Single info line so we can confirm the orchestrated path actually
            // ran — passive captures emit no log line, so silence on success
            // would otherwise be indistinguishable from the orchestrator being
            // skipped entirely.
            Plugin.Log?.LogInfo(
                $"[Capture] Orchestrated trigger='{trigger}' anchor=({anchorPos.x:F1},{anchorPos.y:F1},{anchorPos.z:F1}) yaw=0 pitch=90");
        }

        public void Restore()
        {
            // Player transform first: GameCamera reads its position/rotation
            // on the next LateUpdate to position the follow camera, so the
            // player needs to be back where they belong BEFORE GameCamera
            // re-enables.
            try
            {
                if (m_player != null)
                {
                    m_player.transform.position = m_savedPos;
                    m_player.transform.rotation = m_savedRot;
                }
            }
            catch
            {
                /* swallow — restore must never throw past the capture pipeline */
            }

            // Re-enable GameCamera. We deliberately do NOT restore the camera
            // transform ourselves — explicit cam.transform writes here race
            // against GameCamera.LateUpdate and leave the camera wedged at
            // whichever write happened last in the frame. Letting GameCamera
            // re-attach to the (now-restored) player is what the player
            // expects anyway: third-person follow at character defaults.
            try
            {
                if (m_gameCamera != null) m_gameCamera.enabled = m_savedGameCameraEnabled;
            }
            catch
            {
                /* swallow */
            }

            try
            {
                if (m_hud != null) m_hud.gameObject.SetActive(m_savedHudVisible);
            }
            catch
            {
                /* swallow */
            }
        }
    }
}