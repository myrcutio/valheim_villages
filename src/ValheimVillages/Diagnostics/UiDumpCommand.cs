using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     TEMP dev command: dumps the InventoryGui chest/player window Image tree
    ///     so we can identify the exact wood-frame element to clone. Remove once
    ///     the work order editor styling is finalized.
    /// </summary>
    internal static class UiDumpCommand
    {
        [DevCommand("TEMP: dump InventoryGui window Image tree", Name = "vv_ui_dump")]
        public static void Dump()
        {
            var gui = InventoryGui.instance;
            if (gui == null)
            {
                Print("no InventoryGui");
                return;
            }

            DumpWindow("container", gui.m_container);
            DumpWindow("player", gui.m_player);
        }

        private static void DumpWindow(string label, RectTransform win)
        {
            if (win == null)
            {
                Print($"== {label}: null ==");
                return;
            }

            Print($"== {label}: {win.name} ==");
            Walk(win, 0);
        }

        private static void Walk(Transform t, int depth)
        {
            // Skip the repeating grid slots — we only care about the frame.
            var n = t.name.ToLowerInvariant();
            if (depth > 0 && (n.Contains("element") || n.Contains("griditem")))
                return;

            var pad = new string(' ', depth * 2);
            var rt = t as RectTransform;
            var rectInfo = rt != null
                ? $" rect[aMin=({rt.anchorMin.x:F2},{rt.anchorMin.y:F2}) " +
                  $"aMax=({rt.anchorMax.x:F2},{rt.anchorMax.y:F2}) " +
                  $"piv=({rt.pivot.x:F2},{rt.pivot.y:F2}) " +
                  $"pos=({rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0}) " +
                  $"size={rt.rect.width:F0}x{rt.rect.height:F0}]"
                : "";

            var img = t.GetComponent<Image>();
            var imgInfo = img != null
                ? $" img[sprite={(img.sprite != null ? img.sprite.name : "-")} " +
                  $"type={img.type} col=({img.color.r:F2},{img.color.g:F2}," +
                  $"{img.color.b:F2},{img.color.a:F2})]"
                : "";

            // TMP text props (font size/color) via reflection.
            var tmpInfo = "";
            foreach (var comp in t.GetComponents<Component>())
                if (comp != null && comp.GetType().Name.Contains("TextMeshPro"))
                {
                    var ty = comp.GetType();
                    var fs = ty.GetProperty("fontSize")?.GetValue(comp);
                    var col = ty.GetProperty("color")?.GetValue(comp);
                    var txt = ty.GetProperty("text")?.GetValue(comp);
                    tmpInfo = $" tmp[size={fs} col={col} text=\"{txt}\"]";
                    break;
                }

            Print($"{pad}{t.name}{rectInfo}{imgInfo}{tmpInfo}");

            if (depth >= 2) return;
            for (var i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), depth + 1);
        }

        private static void Print(string s)
        {
            Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
