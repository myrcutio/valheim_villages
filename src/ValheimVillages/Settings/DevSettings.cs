namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Developer tooling toggle. Off for player/test builds: hides the
    ///     villager Debug tab and starts the navmesh/path debug overlays off.
    ///     Flip to true to restore all debug tooling in one place.
    /// </summary>
    public static class DevSettings
    {
        public const bool ShowDebugTools = false;
    }
}
