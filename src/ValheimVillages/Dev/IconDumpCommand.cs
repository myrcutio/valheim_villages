using System.IO;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Items;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: write the Village Registry's current build-menu icon to a PNG.
    ///
    ///     <para>Exists because the icon is otherwise only inspectable by opening the build menu
    ///     and squinting at a button. Dumping the sprite to disk makes framing, lighting and
    ///     cropping reviewable directly, which is the difference between tuning the camera by
    ///     guesswork and tuning it by looking.</para>
    /// </summary>
    public static class IconDumpCommand
    {
        [DevCommand("Write the registry build-menu icon to a PNG: vv_icon_dump [path]",
            Name = "vv_icon_dump")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var prefab = ZNetScene.instance?.GetPrefab(PieceFactory.RegistryPrefabName);
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (piece == null)
            {
                Print("[vv_icon_dump] Registry prefab has no Piece");
                return;
            }

            var path = args != null && args.Length > 1
                ? args[1]
                : Path.Combine(Application.persistentDataPath, "vv_registry_icon.png");

            // An optional size re-shoots the SAME framing at higher resolution, purely so the
            // result is legible when reviewed outside the game. The icon on the piece is
            // untouched.
            var sprite = piece.m_icon;
            if (args != null && args.Length > 2 && int.TryParse(args[2], out var size))
            {
                // A 4th arg isolates ONE node by name, which is how you find out whether a mesh
                // that is missing from the shot is absent or merely hidden behind something.
                var onlyNode = args.Length > 3 && args[3] != "*" ? args[3] : null;
                var substitute = args.Length > 4 && args[4] == "std";
                sprite = Items.Icons.RegistryIconRenderer.Render(
                    prefab, size, onlyNode, substitute);
            }

            if (sprite == null)
            {
                Print("[vv_icon_dump] No icon on the registry piece");
                return;
            }

            var png = sprite.texture.EncodeToPNG();
            if (png == null)
            {
                Print("[vv_icon_dump] Icon texture is not readable");
                return;
            }

            File.WriteAllBytes(path, png);
            Print($"[vv_icon_dump] Wrote {sprite.texture.width}x{sprite.texture.height} " +
                  $"({png.Length} bytes) to {path}");
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
