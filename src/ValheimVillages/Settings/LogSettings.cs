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

        /// <summary>
        ///     When true, every line a villager speaks is logged, with the gate that let it
        ///     through. Villagers only talk when idle, with a player nearby and a shared quiet
        ///     period between lines — three conditions that are invisible from outside, so
        ///     "why is nobody saying anything?" and "why is he talking while he works?" are
        ///     both unanswerable without this. Off by default; chatter is not diagnostics.
        /// </summary>
        public static bool VerboseTalk = false;

        /// <summary>
        ///     When true, Haul logs every decision: why it declined to start, each scan's
        ///     verdict on every drop it saw (owner, ZDO vs transform height, reachability,
        ///     grouping), every leg change and arrival. One long line per villager per ~8 s
        ///     scan — the telemetry that found the ghost-drop and remote-deposit bugs. The
        ///     outcome ("Stored Nx … in a chest") and carry_dropped stay on regardless.
        /// </summary>
        public static bool VerboseHaul = false;

        /// <summary>When true, every behaviour reselect logs which tier/behaviour won (throttled).</summary>
        public static bool VerboseSelect = false;

        /// <summary>
        ///     When true, each flee trigger logs the threat, distance, line of sight, whether it
        ///     is inside the village graph and whether it is alerted/targeting (throttled).
        /// </summary>
        public static bool VerboseFlee = false;

        /// <summary>
        ///     When true, every network destroy of an item-drop ZDO is traced (received, and
        ///     whether it was really removed). Every pickup in the world fires it. A destroy
        ///     that hits a dead registered instance is always logged, flag or not.
        /// </summary>
        public static bool VerboseItemDestroy = false;
    }
}