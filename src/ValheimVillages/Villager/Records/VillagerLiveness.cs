using ValheimVillages.Villager.AI;

namespace ValheimVillages.Villager.Records
{
    /// <summary>
    ///     Where a villager's NPC GameObject is RIGHT NOW relative to its persistent
    ///     record. This is a transient, per-peer, DERIVED fact — never the record's
    ///     lifecycle (that is <see cref="RecordStatus" />). A villager whose zone is
    ///     unloaded is <see cref="Away" />, not dead.
    /// </summary>
    public enum LivePresence
    {
        /// <summary>A VillagerAI instance is registered on THIS peer.</summary>
        Live,

        /// <summary>No local instance, but the linked NPC ZDO exists (loaded/owned on another peer, or unloaded-but-persistent).</summary>
        Away,

        /// <summary>Host-confirmed gone: Alive record, NpcZdoId set, but that ZDO no longer exists. An orphan / true death not yet recorded.</summary>
        Missing,

        /// <summary>NpcZdoId is None — no NPC link (never spawned, or cleared on a recorded death).</summary>
        Unlinked,

        /// <summary>On a client the ZDO isn't local, but that may just mean "not replicated to me" — can't tell Away from Missing.</summary>
        Unknown,
    }

    /// <summary>
    ///     Resolves a record's <see cref="LivePresence" /> by cross-checking the live AI
    ///     registry and the linked NPC ZDO. Read-only: it never mutates records or the
    ///     registry. The Missing verdict is only reliable on the host (after world load
    ///     <c>ZDOMan.m_objectsByID</c> holds every persisted ZDO); on a client a missing
    ///     ZDO is reported as <see cref="LivePresence.Unknown" />.
    /// </summary>
    public static class VillagerLiveness
    {
        public static LivePresence Resolve(VillagerRecord record)
        {
            if (record == null) return LivePresence.Unlinked;

            // 1. A live AI instance on this peer is the strongest, unambiguous signal.
            if (VillagerAIManager.ActiveVillagers.TryGetValue(record.RecordId, out var ai) && ai != null)
                return LivePresence.Live;

            // 2. No local instance — fall back to the NPC back-link.
            var npc = record.NpcZdoId;
            if (npc == ZDOID.None) return LivePresence.Unlinked;

            var zdoMan = ZDOMan.instance;
            if (zdoMan != null && zdoMan.GetZDO(npc) != null) return LivePresence.Away;

            // 3. ZDO not found locally. On the host that means truly gone; on a client it
            //    may simply not be replicated, so don't claim death.
            var host = ZNet.instance != null && ZNet.instance.IsServer();
            return host ? LivePresence.Missing : LivePresence.Unknown;
        }

        /// <summary>Compact tag for dev-command output (live/away/missing/unlinked/?).</summary>
        public static string Tag(LivePresence p)
        {
            switch (p)
            {
                case LivePresence.Live: return "live";
                case LivePresence.Away: return "away";
                case LivePresence.Missing: return "missing";
                case LivePresence.Unlinked: return "unlinked";
                default: return "?";
            }
        }

        /// <summary>Player-facing label for the registry UI (collapses the can't-tell cases to "away").</summary>
        public static string Label(LivePresence p)
        {
            switch (p)
            {
                case LivePresence.Live: return "in world";
                case LivePresence.Missing: return "missing";
                default: return "away"; // Away / Unlinked / Unknown all read as "not here right now"
            }
        }

        /// <summary>
        ///     Which peer this command ran on, and whether its record view is complete.
        ///     Only the host's <c>m_objectsByID</c> holds every persisted record; a client
        ///     sees only synced sectors, so its counts can undercount.
        /// </summary>
        public static string PeerLabel()
        {
            if (ZNet.instance == null) return "[no net]";
            return ZNet.instance.IsServer() ? "[host]" : "[client — partial view]";
        }
    }
}
