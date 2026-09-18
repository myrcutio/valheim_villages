namespace ValheimVillages
{
    /// <summary>
    ///     Structured diagnostics. Everything goes through <see cref="Event" /> (see
    ///     DebugLog.Events.cs), which writes one line to the BepInEx log.
    ///     <para>
    ///     There used to be a second channel here, <c>Append</c>, writing NDJSON straight to
    ///     <c>vv_dumps/legacy_debug.ndjson</c> with a <c>File.AppendAllText</c> per line — a
    ///     synchronous open/write/close on the main thread — wrapped in an empty catch. It
    ///     was never rotated or size-capped and had accumulated 4.1 GB on the dev machine.
    ///     Its six callers were stale investigation scaffolding (they still passed dead
    ///     hypothesis ids like "H3"/"run1"), and one of them logged from a scan that runs
    ///     every 0.5s per villager for the lifetime of the world. All six now use
    ///     <see cref="Event" />; the channel is gone rather than fixed, because nothing
    ///     needed a second one.
    ///     </para>
    /// </summary>
    internal static partial class DebugLog
    {
    }
}
