using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Find loaded UI sprites by name, and dump the trophy plaque's hierarchy.
    ///
    ///     <para>Exists so "which sprite is the coat-of-arms backdrop?" is answered by looking
    ///     at what the game actually has loaded, rather than by guessing a name and shipping a
    ///     silent null.</para>
    /// </summary>
    internal static class SpriteFindCommand
    {
        [DevCommand("Find loaded sprites by name: vv_spritefind <substring> | trophy",
            Name = "vv_spritefind")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                Print("[vv_spritefind] usage: vv_spritefind <substring> | trophy");
                return;
            }

            if (string.Equals(args[1], "trophy", System.StringComparison.OrdinalIgnoreCase))
            {
                DumpTrophyElement();
                return;
            }

            if (string.Equals(args[1], "levelroot", System.StringComparison.OrdinalIgnoreCase))
            {
                DumpImages(InventoryGui.instance?.m_craftingStationLevelRoot,
                    "m_craftingStationLevelRoot");
                return;
            }

            var needle = args[1].ToLowerInvariant();
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>()
                .Where(s => s != null && s.name.ToLowerInvariant().Contains(needle))
                .Select(s => $"{s.name} ({s.rect.width:F0}x{s.rect.height:F0})")
                .Distinct()
                .OrderBy(n => n)
                .Take(40)
                .ToList();

            Print(sprites.Count == 0
                ? $"[vv_spritefind] no loaded sprite matches '{args[1]}'"
                : $"[vv_spritefind] {sprites.Count} match(es):\n  " + string.Join("\n  ", sprites));
        }

        /// <summary>Dump the trophy list element prefab: every Image and the sprite on it.</summary>
        private static void DumpTrophyElement()
        {
            var gui = InventoryGui.instance;
            var prefab = gui != null ? gui.m_trophieElementPrefab : null;
            if (prefab == null)
            {
                Print("[vv_spritefind] InventoryGui.m_trophieElementPrefab is null here");
                return;
            }

            var sb = new StringBuilder($"[vv_spritefind] trophy element '{prefab.name}':");
            foreach (var image in prefab.GetComponentsInChildren<Image>(true))
                sb.Append($"\n  {Path(image.transform, prefab.transform)} " +
                          $"sprite={(image.sprite != null ? image.sprite.name : "NONE")} " +
                          $"size={image.rectTransform.sizeDelta}");

            Print(sb.ToString());
        }

        /// <summary>
        ///     Write a named sprite to a PNG so it can actually be LOOKED at.
        ///
        ///     <para>Goes through a RenderTexture because UI sprites are atlased and their
        ///     textures report isReadable == false, so GetPixels throws — the same wall the cot
        ///     blanket print hit. Picking an icon by name alone is how the first attempt landed
        ///     on the achievements cup instead of the trophy shield.</para>
        /// </summary>
        [DevCommand("Write a sprite to PNG: vv_spritedump <spriteName>", Name = "vv_spritedump")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                Print("[vv_spritedump] usage: vv_spritedump <spriteName>");
                return;
            }

            var sprite = Resources.FindObjectsOfTypeAll<Sprite>()
                .FirstOrDefault(sp => sp != null && sp.name == args[1]);
            if (sprite == null)
            {
                Print($"[vv_spritedump] no loaded sprite named '{args[1]}'");
                return;
            }

            var rect = sprite.textureRect;
            var w = Mathf.Max(1, Mathf.RoundToInt(rect.width));
            var h = Mathf.Max(1, Mathf.RoundToInt(rect.height));
            var src = sprite.texture;

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            var scale = new Vector2(rect.width / src.width, rect.height / src.height);
            var offset = new Vector2(rect.x / src.width, rect.y / src.height);
            Graphics.Blit(src, rt, scale, offset);

            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            var dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "vv_dumps");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"sprite_{sprite.name}.png");
            System.IO.File.WriteAllBytes(path, copy.EncodeToPNG());
            UnityEngine.Object.Destroy(copy);

            Print($"[vv_spritedump] {sprite.name} ({w}x{h}) -> {path}");
        }

        /// <summary>Dump every Image under a UI object, with the sprite each one draws.</summary>
        private static void DumpImages(Transform root, string label)
        {
            if (root == null)
            {
                Print($"[vv_spritefind] {label} is null here");
                return;
            }

            var sb = new StringBuilder($"[vv_spritefind] {label} ('{root.name}'):");
            foreach (var image in root.GetComponentsInChildren<Image>(true))
                sb.Append($"\n  {Path(image.transform, root)} " +
                          $"sprite={(image.sprite != null ? image.sprite.name : "NONE")} " +
                          $"color={UnityEngine.ColorUtility.ToHtmlStringRGBA(image.color)} " +
                          $"size={image.rectTransform.sizeDelta}");

            Print(sb.ToString());
        }

        private static string Path(Transform node, Transform root)
        {
            var parts = "";
            while (node != null && node != root)
            {
                parts = parts.Length == 0 ? node.name : node.name + "/" + parts;
                node = node.parent;
            }

            return parts.Length == 0 ? "(root)" : parts;
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
