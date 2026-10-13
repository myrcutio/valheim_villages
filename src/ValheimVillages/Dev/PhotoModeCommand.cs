using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Clear the frame for documentation screenshots: <c>vv_photo [on|off]</c> hides the HUD
    ///     and the local player's model, and <c>vv_photo</c> alone toggles.
    ///
    ///     <para>Valheim hides the HUD on Ctrl+F3 and has a <c>freefly</c> camera, but nothing
    ///     hides the player themselves — in third person they stand in the middle of every shot.
    ///     Pair this with <c>freefly</c> to move the camera freely.</para>
    /// </summary>
    internal static class PhotoModeCommand
    {
        /// <summary>
        ///     Renderers this command switched off, so restoring puts back exactly what was
        ///     hidden and does not reveal something the game had hidden for its own reasons
        ///     (a helmet on a hairstyle, an unequipped item).
        /// </summary>
        private static readonly List<Renderer> s_hidden = new();

        private static bool s_active;

        [DevCommand("Hide HUD + player for screenshots: vv_photo [on|off]", Name = "vv_photo")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var on = !s_active;
            if (args != null && args.Length >= 2)
                on = !string.Equals(args[1], "off", System.StringComparison.OrdinalIgnoreCase);

            if (on) Enable();
            else Disable();

            s_active = on;
            Print($"[vv_photo] {(on ? "on" : "off")} — HUD {(on ? "hidden" : "shown")}, " +
                  $"player {(on ? $"hidden ({s_hidden.Count} renderer(s))" : "shown")}. " +
                  "Use 'freefly' to move the camera.");
        }

        private static void Enable()
        {
            Disable(); // never stack two hides, or the restore list drifts

            if (Hud.instance != null) Hud.instance.m_userHidden = true;

            var player = Player.m_localPlayer;
            if (player == null) return;

            foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;

                renderer.enabled = false;
                s_hidden.Add(renderer);
            }
        }

        private static void Disable()
        {
            if (Hud.instance != null) Hud.instance.m_userHidden = false;

            foreach (var renderer in s_hidden)
                if (renderer != null)
                    renderer.enabled = true;

            s_hidden.Clear();
        }

        /// <summary>Put everything back if the assembly reloads mid-shoot.</summary>
        [RegisterCleanup]
        public static void Reset()
        {
            Disable();
            s_active = false;
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
