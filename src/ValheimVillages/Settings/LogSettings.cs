namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Runtime verbosity flags for high-volume diagnostic logging.
    ///     Toggled from dev commands; values are mutable so they can be
    ///     flipped without recompiling. Keep additions small and focused.
    /// </summary>
    public static class LogSettings
    {
        /// <summary>
        ///     When true, the ingredient scan logs its per-container probe ("Looking for
        ///     Nx X across M containers", then one line per container). That is one line
        ///     PER CONTAINER PER SCAN and dominated the log at ~79% of all mod output
        ///     (4930 of ~7000 lines in one session), evicting everything else from the
        ///     in-memory ring buffer the MCP log tools read. Off by default: the outcome
        ///     line still reports what was missing, throttled.
        /// </summary>
        public static bool VerboseIngredientScan = false;

        /// <summary>
        ///     When true, <c>ItemDropAwakePatch</c> logs every ItemDrop spawned. Unbounded
        ///     — every dropped Wood, Honey and Raspberry in the world — and it was at
        ///     LogInfo, so it could not be filtered out by level either.
        /// </summary>
        public static bool VerboseItemSpawns = false;
    }
}