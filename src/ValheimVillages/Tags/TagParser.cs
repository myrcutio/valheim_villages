using System;
using System.Collections.Generic;

namespace ValheimVillages.Tags
{
    /// <summary>
    ///     Parses and matches "namespace:value" style tags used in NPC definitions.
    ///     Tags follow the format "namespace:value" (e.g. "behavior:patrol", "listpanel:patrolstatus").
    /// </summary>
    public static class TagParser
    {
        /// <summary>
        ///     Parse a tag string into its namespace and value components.
        ///     Returns false if the tag is malformed.
        /// </summary>
        public static bool TryParse(string tag, out string ns, out string value)
        {
            ns = null;
            value = null;
            if (string.IsNullOrEmpty(tag)) return false;

            var colonIdx = tag.IndexOf(':');
            if (colonIdx <= 0 || colonIdx >= tag.Length - 1) return false;

            ns = tag.Substring(0, colonIdx).Trim();
            value = tag.Substring(colonIdx + 1).Trim();
            return ns.Length > 0 && value.Length > 0;
        }

        /// <summary>
        ///     Get the namespace of a tag string. Returns null if malformed.
        /// </summary>
        public static string GetNamespace(string tag)
        {
            return TryParse(tag, out var ns, out _) ? ns : null;
        }

        /// <summary>
        ///     Get the value of a tag string. Returns null if malformed.
        /// </summary>
        public static string GetValue(string tag)
        {
            return TryParse(tag, out _, out var value) ? value : null;
        }

        /// <summary>
        ///     Filter a list of tags to only those matching a given namespace.
        /// </summary>
        public static List<string> FilterByNamespace(IEnumerable<string> tags, string ns)
        {
            var result = new List<string>();
            foreach (var tag in tags)
                if (TryParse(tag, out var tagNs, out _) &&
                    string.Equals(tagNs, ns, StringComparison.OrdinalIgnoreCase))
                    result.Add(tag);

            return result;
        }

        /// <summary>
        ///     Get all values for tags matching a given namespace.
        /// </summary>
        public static List<string> GetValues(IEnumerable<string> tags, string ns)
        {
            var result = new List<string>();
            foreach (var tag in tags)
                if (TryParse(tag, out var tagNs, out var value) &&
                    string.Equals(tagNs, ns, StringComparison.OrdinalIgnoreCase))
                    result.Add(value);

            return result;
        }

        /// <summary>
        ///     Check if any tag in the list matches the given namespace and value.
        /// </summary>
        public static bool HasTag(IEnumerable<string> tags, string ns, string value)
        {
            foreach (var tag in tags)
                if (TryParse(tag, out var tagNs, out var tagValue) &&
                    string.Equals(tagNs, ns, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tagValue, value, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        ///     Check if any tag in the list matches the given namespace (any value).
        /// </summary>
        public static bool HasNamespace(IEnumerable<string> tags, string ns)
        {
            foreach (var tag in tags)
                if (TryParse(tag, out var tagNs, out _) &&
                    string.Equals(tagNs, ns, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }
}