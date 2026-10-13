using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Panels;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Drive the villager map's zoom/pan from the console: <c>vv_mapzoom</c> alone reports the
    ///     view, <c>vv_mapzoom &lt;factor&gt; [x z]</c> zooms about a world anchor (the frame
    ///     centre by default), <c>vv_mapzoom reset</c> restores the auto fit, and
    ///     <c>vv_mapzoom pan &lt;dx&gt; &lt;dz&gt;</c> drags by a world-space delta.
    ///
    ///     <para>Calls the same <see cref="MapViewState" /> entry points the wheel and drag
    ///     handlers call, so what it exercises is the shipped path rather than a parallel one.
    ///     It exists because zoom is driven by a mouse the headless MCP target does not have —
    ///     without it, "do the pins follow the frame?" could only be answered from a
    ///     screenshot.</para>
    /// </summary>
    internal static class MapZoomCommand
    {
        [DevCommand("Zoom/pan the villager map: vv_mapzoom [factor|reset|pan dx dz] [x z]",
            Name = "vv_mapzoom")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                Report("view");
                return;
            }

            var verb = args[1];

            if (verb == "reset")
            {
                MapViewState.Reset();
                Report("reset");
                return;
            }

            if (verb == "pan")
            {
                if (args.Length < 4 ||
                    !float.TryParse(args[2], out var dx) ||
                    !float.TryParse(args[3], out var dz))
                {
                    Print("[vv_mapzoom] usage: vv_mapzoom pan <dx> <dz>");
                    return;
                }

                MapViewState.PanBy(new Vector2(dx, dz));
                Report($"pan {dx} {dz}");
                return;
            }

            if (!float.TryParse(verb, out var factor))
            {
                Print($"[vv_mapzoom] not a factor: '{verb}'");
                return;
            }

            // Anchor defaults to the centre of the frame the last render used, which is what
            // the view does when the cursor is in the middle of the map.
            var anchor = Vector2.zero;
            if (args.Length >= 4 &&
                float.TryParse(args[2], out var ax) && float.TryParse(args[3], out var az))
            {
                anchor = new Vector2(ax, az);
            }
            else if (!MapViewState.TryWorldAt(new Vector2(0.5f, 0.5f), out anchor))
            {
                Print("[vv_mapzoom] no frame recorded yet — open a villager panel " +
                      "or run vv_mappins first");
                return;
            }

            MapViewState.ZoomBy(factor, anchor);
            Report($"zoom x{factor} about ({anchor.x:F1},{anchor.y:F1})");
        }

        private static void Report(string did)
        {
            var centre = MapViewState.TryWorldAt(new Vector2(0.5f, 0.5f), out var c)
                ? $"({c.x:F1},{c.y:F1})"
                : "none";

            Print($"[vv_mapzoom] {did} -> zoom={MapViewState.Zoom:F2} " +
                  $"active={MapViewState.Active} frame={MapViewState.FrameSize:F1}m " +
                  $"centre={centre}");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
