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
        ///     When true, NavMesh probe/sampling code emits throttled structured
        ///     events via <c>DebugLog</c>. When false (default), those high-volume
        ///     probe logs drop to <c>LogDebug</c> to keep the console quiet.
        /// </summary>
        public static bool VerboseNavMesh = false;
    }
}