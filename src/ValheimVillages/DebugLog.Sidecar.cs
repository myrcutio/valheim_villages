using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        private static string SidecarDir
        {
            get
            {
                // BepInEx.Paths.ConfigPath is available — write under <config>/vv_dumps
                string root;
                try
                {
                    root = Paths.ConfigPath;
                }
                catch
                {
                    root = ".";
                }

                return Path.Combine(root, "vv_dumps");
            }
        }

        /// <summary>
        ///     Logs a (potentially large) collection as a compact one-line summary
        ///     and writes the full content to a sidecar JSON file named with a short
        ///     content hash. Re-emits are deduplicated: identical content -> same file,
        ///     written only once.
        /// </summary>
        public static void List(string component, string name, IEnumerable<object> items)
        {
            var arr = items?.Select(i => i?.ToString() ?? "null").ToArray()
                      ?? new string[0];
            var joined = string.Join(",", arr);
            var sha = ShortSha(joined);
            var dir = SidecarDir;
            var file = Path.Combine(dir, $"{name}_{sha}.json");
            try
            {
                Directory.CreateDirectory(dir);
                if (!File.Exists(file))
                {
                    var sb = new StringBuilder();
                    sb.Append('[');
                    for (var i = 0; i < arr.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(JsonEscape(arr[i])).Append('"');
                    }

                    sb.Append(']');
                    File.WriteAllText(file, sb.ToString());
                }
            }
            catch
            {
                /* sidecar write failure must not break logging */
            }

            // Use Event so the summary line is structured + timestamped.
            Event(component, name,
                ("count", arr.Length),
                ("sha", sha),
                ("path", file));
        }

        private static string ShortSha(string s)
        {
            using (var h = SHA1.Create())
            {
                var b = h.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(8);
                for (var i = 0; i < 4; i++) sb.AppendFormat("{0:x2}", b[i]);
                return sb.ToString();
            }
        }

        private static string JsonEscape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}