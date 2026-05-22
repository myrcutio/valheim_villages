using System.Globalization;
using System.Text;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        /// <summary>
        /// Emits a structured one-line event at Info severity:
        ///   [Component] event_name t=+12.34s k1=v1 k2=v2
        /// k=v pairs are space-separated. Values containing spaces or '=' are
        /// quoted. Use snake_case for event_name and key names.
        /// </summary>
        public static void Event(string component, string eventName,
                                 params (string key, object val)[] kv)
        {
            var sb = new StringBuilder(64 + (kv?.Length ?? 0) * 16);
            sb.Append('[').Append(component).Append("] ").Append(eventName);
            sb.Append(' ').Append(T());
            if (kv != null)
            {
                for (int i = 0; i < kv.Length; i++)
                {
                    sb.Append(' ').Append(kv[i].key).Append('=');
                    AppendValue(sb, kv[i].val);
                }
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void AppendValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            string s;
            switch (v)
            {
                case float f:  s = f.ToString("0.###", CultureInfo.InvariantCulture); break;
                case double d: s = d.ToString("0.###", CultureInfo.InvariantCulture); break;
                case bool b:   s = b ? "true" : "false"; break;
                default:       s = v.ToString() ?? ""; break;
            }
            bool needsQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '=' || c == '"') { needsQuote = true; break; }
            }
            if (needsQuote)
            {
                sb.Append('"').Append(s.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                sb.Append(s);
            }
        }
    }
}
