namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Developer tooling toggle. Off for player/test builds: hides the
    ///     villager Debug tab and starts the navmesh/path debug overlays off.
    ///     Flip to true to restore all debug tooling in one place.
    /// </summary>
    public static class DevSettings
    {
        public static readonly bool ShowDebugTools = false;

        /// <summary>
        ///     Log the ACTUAL slot-31 navmesh extent after every bake (bake overspill /
        ///     disconnected far islands). Off by default because it calls
        ///     <c>NavMesh.CalculateTriangulation()</c>, which walks EVERY village's baked
        ///     mesh and gets slower as villages accumulate — measured at 87-110ms per bake,
        ///     the single largest frame spike left in a partition, for a diagnostic line.
        ///     Turn on when chasing [[villager-flinging-stray-navmesh]]-class bugs.
        /// </summary>
        public static readonly bool LogBakedExtent = false;

        /// <summary>
        ///     Write <c>DebugLog.List</c>'s full contents to a sidecar JSON file under
        ///     <c>vv_dumps/</c>. Off by default: the summary line (count + content hash) is
        ///     always logged, and the sidecar only matters when you are chasing WHICH regions
        ///     a specific partition dropped.
        ///     <para>
        ///     It is content-hash deduplicated, which sounds bounded and is not — the content
        ///     changes every partition, so each rebuild leaves another file behind. Measured
        ///     on the dev machine: 3,451 files (1,197 area_dropped, 1,039 dropped_region_ids,
        ///     861 enclosed_dropped, ...) accumulating on a player's disk with nothing ever
        ///     removing them.
        ///     </para>
        /// </summary>
        public static readonly bool WriteListSidecars = false;

        /// <summary>
        ///     Append a line to <c>vv_dumps/path_telemetry.ndjson</c> on every partition.
        ///     Off by default: it is the last ungated on-disk writer on a gameplay path, it
        ///     opens/writes/closes the file on the main thread, nothing ever trims it, and
        ///     it still tags every line with the dead hypothesis id "path_compare" from the
        ///     investigation it was built for. Everything it records is already in the
        ///     partition's structured log line.
        /// </summary>
        public static readonly bool WritePathTelemetry = false;
    }
}
