using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Loads scroll-with-ribbon icons for work order items from embedded PNG resources.
    ///     Each crafting station type has a pre-generated PNG with a distinct ribbon colour.
    ///     PNGs live in Items/Icons/WorkOrders/ and are embedded at build time.
    /// </summary>
    public static class WorkOrderIconLoader
    {
        // Generic scroll used when a station has no bespoke parchment, so work
        // orders never fall back to the base clone prefab's icon (e.g. DragonEgg).
        private const string DefaultStation = "workbench";

        private static readonly Dictionary<string, Sprite> _cache = new();
        private static MethodInfo _loadImageMethod;

        /// <summary>
        ///     Load (or return cached) the work order icon for a given station type.
        ///     Station type maps to embedded resource: workorder_{type_lower}.png.
        ///     Falls back to a generic scroll when no bespoke parchment exists.
        /// </summary>
        public static Sprite Load(string stationType)
        {
            var key = stationType ?? "";
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var resName = $"ValheimVillages.Items.Icons.WorkOrders.workorder_{key.ToLowerInvariant()}.png";
            var assembly = Assembly.GetExecutingAssembly();

            using var stream = assembly.GetManifestResourceStream(resName);
            if (stream == null)
            {
                // Degrade to the default scroll rather than the clone prefab icon.
                if (!key.Equals(DefaultStation, System.StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log?.LogWarning(
                        $"Work order icon resource not found: {resName} — " +
                        $"using '{DefaultStation}' scroll.");
                    var fallback = Load(DefaultStation);
                    _cache[key] = fallback;
                    return fallback;
                }

                Plugin.Log?.LogWarning($"Work order icon resource not found: {resName}");
                return null;
            }

            var data = new byte[stream.Length];
            stream.Read(data, 0, data.Length);

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Bilinear };

            if (!InvokeLoadImage(tex, data))
            {
                Plugin.Log?.LogError($"Failed to decode work order icon PNG: {resName}");
                return null;
            }

            var sprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f));

            _cache[key] = sprite;
            Plugin.Log?.LogInfo($"Loaded work order icon: {stationType} ({tex.width}x{tex.height})");
            return sprite;
        }

        /// <summary>
        ///     Calls ImageConversion.LoadImage via reflection to avoid compile-time
        ///     assembly version mismatch (ImageConversionModule targets netstandard 2.1
        ///     while the mod project targets net472).
        /// </summary>
        private static bool InvokeLoadImage(Texture2D tex, byte[] data)
        {
            if (_loadImageMethod == null)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType("UnityEngine.ImageConversion");
                    if (type == null) continue;

                    _loadImageMethod = type.GetMethod("LoadImage",
                        new[] { typeof(Texture2D), typeof(byte[]) });
                    break;
                }

            if (_loadImageMethod == null)
            {
                Plugin.Log?.LogError("ImageConversion.LoadImage not found via reflection");
                return false;
            }

            return (bool)_loadImageMethod.Invoke(null, new object[] { tex, data });
        }
    }
}