namespace ValheimVillages.Core.Helpers
{
    /// <summary>
    /// Shared helper methods for common UI operations.
    /// Reduces inline boilerplate for HUD messages, console output,
    /// and inventory panel management.
    /// </summary>
    public static class UIHelpers
    {
        /// <summary>
        /// Show a centered HUD message to the local player.
        /// No-op if the local player is null.
        /// </summary>
        public static void ShowHudMessage(string text)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
        }

        /// <summary>
        /// Show a top-left HUD notification to the local player.
        /// No-op if the local player is null.
        /// </summary>
        public static void ShowHudNotification(string text)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, text);
        }

        /// <summary>
        /// Print a message to the in-game console.
        /// No-op if the console instance is null.
        /// </summary>
        public static void PrintConsole(string text)
        {
            Console.instance?.Print(text);
        }

        /// <summary>
        /// Close the inventory GUI if it's currently visible.
        /// </summary>
        public static void CloseInventoryUI()
        {
            InventoryGui.instance?.Hide();
        }
    }
}
