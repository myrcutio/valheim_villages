using UnityEngine;
using ValheimVillages.Core.Attributes;

namespace ValheimVillages.Abilities
{
    /// <summary>
    /// Manages villager-taught abilities. Tracks which abilities the player has learned,
    /// handles activation via keybind, and manages cooldowns.
    /// 
    /// Abilities are persisted via Player unique keys (survive death/logout).
    /// </summary>
    public static class VillagerAbilityManager
    {
        public const string MountainStrideKey = "vv_ability_mountain_stride";
        private static float s_cooldownRemaining;
        private static readonly KeyCode ActivationKey = KeyCode.R;

        /// <summary>
        /// Check if the player has learned Mountain Stride.
        /// </summary>
        public static bool HasLearnedMountainStride()
        {
            var player = Player.m_localPlayer;
            return player != null && player.HaveUniqueKey(MountainStrideKey);
        }

        /// <summary>
        /// Teach the player Mountain Stride. Called from the dialog menu.
        /// </summary>
        public static void LearnMountainStride()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (player.HaveUniqueKey(MountainStrideKey))
            {
                player.Message(MessageHud.MessageType.Center, "You already know Mountain Stride");
                return;
            }

            player.AddUniqueKey(MountainStrideKey);
            player.Message(MessageHud.MessageType.Center,
                "Learned Mountain Stride! Press R to activate.");
            Plugin.Log?.LogInfo("[Mountaineer] Player learned Mountain Stride");
        }

        /// <summary>
        /// Try to activate Mountain Stride. Returns true if activated.
        /// </summary>
        public static bool ActivateMountainStride()
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;

            if (!HasLearnedMountainStride())
            {
                player.Message(MessageHud.MessageType.Center,
                    "You haven't learned this technique yet");
                return false;
            }

            if (s_cooldownRemaining > 0f)
            {
                int minutes = Mathf.CeilToInt(s_cooldownRemaining / 60f);
                player.Message(MessageHud.MessageType.Center,
                    $"Mountain Stride not ready ({minutes}m remaining)");
                return false;
            }

            // Check the same conditions as guardian powers
            if (player.InAttack() || player.InDodge() || !player.CanMove()
                || player.IsKnockedBack() || player.IsStaggering())
            {
                return false;
            }

            // Add the status effect
            var seman = player.GetSEMan();
            int hash = SE_MountainStride.EffectName.GetStableHashCode();

            if (seman.HaveStatusEffect(hash))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Mountain Stride is already active");
                return false;
            }

            seman.AddStatusEffect(hash);
            s_cooldownRemaining = SE_MountainStride.Cooldown;

            Plugin.Log?.LogInfo("[Mountaineer] Mountain Stride activated");
            return true;
        }

        /// <summary>
        /// Called every frame to handle keybind and cooldown.
        /// Should be called from a Player.Update patch.
        /// </summary>
        public static void Update(float dt)
        {
            // Tick cooldown
            if (s_cooldownRemaining > 0f)
                s_cooldownRemaining -= dt;

            // Don't process input if a menu is open or chat is active
            if (Player.m_localPlayer == null) return;
            if (Menu.IsVisible() || Console.IsVisible() || TextInput.IsVisible()) return;
            if (Minimap.IsOpen()) return;
            if (InventoryGui.IsVisible()) return;

            // Check activation key
            if (Input.GetKeyDown(ActivationKey) && HasLearnedMountainStride())
            {
                ActivateMountainStride();
            }
        }

        /// <summary>
        /// Get remaining cooldown in seconds (for UI display).
        /// </summary>
        public static float GetCooldownRemaining() => Mathf.Max(0f, s_cooldownRemaining);

        /// <summary>
        /// Whether the buff is currently active on the local player.
        /// </summary>
        public static bool IsActive()
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;
            int hash = SE_MountainStride.EffectName.GetStableHashCode();
            return player.GetSEMan().HaveStatusEffect(hash);
        }

        /// <summary>
        /// Reset the cooldown to zero (debug command).
        /// </summary>
        [DevCommand("Reset Mountain Stride cooldown", Name = "vv_reset_cooldown")]
        public static void ResetCooldown()
        {
            s_cooldownRemaining = 0f;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                "Mountain Stride cooldown reset");
            Plugin.Log?.LogInfo("[Mountaineer] Cooldown reset via debug command");
        }

        [DevCommand("Log HNA attributes for current player position to .cursor/debug.log (region, bounds, heights)", Name = "hna_debug_player")]
        public static void LogHnaPlayerPosition(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null || player.transform == null)
            {
                if (Console.instance != null) Console.instance.Print("No local player.");
                return;
            }
            Vector3 pos = player.transform.position;
            PathTelemetry.LogHnaPlayerDebug(pos);
            if (Console.instance != null) Console.instance.Print($"HNA player debug written: pos=({pos.x:F1},{pos.y:F1},{pos.z:F1})");
        }
    }
}
