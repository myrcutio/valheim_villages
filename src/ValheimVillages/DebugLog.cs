using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        private static readonly string LogPath = Path.Combine(
            Paths.ConfigPath, "vv_dumps", "legacy_debug.ndjson");

        public static void Append(string location, string message, Dictionary<string, object> data, string hypothesisId,
            string runId)
        {
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sb = new StringBuilder();
                sb.Append('{');
                sb.AppendFormat("\"timestamp\":{0}", ts);
                sb.AppendFormat(",\"location\":\"{0}\"", Esc(location));
                sb.AppendFormat(",\"message\":\"{0}\"", Esc(message));
                sb.AppendFormat(",\"hypothesisId\":\"{0}\"", Esc(hypothesisId));
                sb.AppendFormat(",\"runId\":\"{0}\"", Esc(runId));
                sb.Append(",\"data\":{");
                var first = true;
                foreach (var kv in data)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.AppendFormat("\"{0}\":", Esc(kv.Key));
                    if (kv.Value is string s) sb.AppendFormat("\"{0}\"", Esc(s));
                    else if (kv.Value is bool b) sb.Append(b ? "true" : "false");
                    else if (kv.Value is float f) sb.Append(f.ToString(CultureInfo.InvariantCulture));
                    else if (kv.Value is double d) sb.Append(d.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(kv.Value?.ToString() ?? "null");
                }

                sb.Append("}}");
                File.AppendAllText(LogPath, sb + "\n");
            }
            catch
            {
            }
        }

        private static string Esc(string v)
        {
            return v?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        }
    }
}