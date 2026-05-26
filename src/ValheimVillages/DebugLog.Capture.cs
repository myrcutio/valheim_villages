using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;

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
        /// </summary>
        public static void Capture(string trigger)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;

                if (_captureBehaviour == null)
                    _captureBehaviour = plugin.gameObject.GetComponent<DebugCaptureBehaviour>()
                                        ?? plugin.gameObject.AddComponent<DebugCaptureBehaviour>();
                _captureBehaviour.Enqueue(trigger ?? "unknown");
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

    internal class DebugCaptureBehaviour : MonoBehaviour
    {
        private readonly ConcurrentQueue<string> _pending = new();

        private void Update()
        {
            while (_pending.TryDequeue(out var trigger))
            {
                var counter = DebugLog.NextCaptureCounter();
                StartCoroutine(CaptureRoutine(trigger, counter));
            }
        }

        public void Enqueue(string trigger)
        {
            _pending.Enqueue(trigger);
        }

        private IEnumerator CaptureRoutine(string trigger, int counter)
        {
            // Let any debug overlay (RegionDebugVisualization etc.) redraw
            // against post-event state before we grab the frame.
            yield return new WaitForSecondsRealtime(0.6f);

            var cam = Camera.main;
            if (cam == null) yield break;

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

            WritePngAndSidecar(trigger, counter, pos, euler, worldTime);
        }

        private static void WritePngAndSidecar(string trigger, int counter,
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
                Directory.CreateDirectory(dir);
            }
            catch
            {
                return;
            }

            var pngPath = Path.Combine(dir, "last_capture.png");
            var jsonPath = Path.Combine(dir, "last_capture.json");

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
                var sb = new StringBuilder(256);
                sb.Append('{');
                sb.Append("\"counter\":").Append(counter.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"trigger\":\"").Append(JsonEscape(trigger)).Append("\",");
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
                sb.Append('}');
                File.WriteAllText(jsonPath, sb.ToString());
            }
            catch
            {
                /* swallow */
            }
        }

        private static string JsonEscape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}