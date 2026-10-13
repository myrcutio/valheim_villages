using System.Collections.Generic;
using ValheimVillages.Attributes;

namespace ValheimVillages.Abilities.FearBadges
{
    /// <summary>
    ///     Which creature types a player's carried badges ward off, stored on the PLAYER's ZDO.
    ///
    ///     <para>It has to live on the ZDO rather than be read from the local inventory, because
    ///     creature AI runs on the creature's owner — usually a different peer from the player
    ///     being feared. Valheim's own crown mode has exactly this problem and solves it the same
    ///     way, with <c>ZDOVars.s_crowned</c> on the player's ZDO
    ///     (<c>Player.InCrownMode()</c>). An inventory check would work in single player and
    ///     silently do nothing on a server.</para>
    ///
    ///     <para>Stored as one comma-separated string of creature prefab names: a few dozen
    ///     short names is a few hundred bytes, it replicates like any other ZDO field, and it
    ///     stays readable in a ZDO dump — which matters for a feature whose failure mode is
    ///     "nothing happened".</para>
    /// </summary>
    public static class FearBadgeStore
    {
        public const string Key = "vv_fear_badges";

        /// <summary>
        ///     How close a warded creature has to be before it turns and runs. Deliberately
        ///     finite: crown mode uses 9999f, which would empty the map rather than clear your
        ///     path, and the design calls for a path not a vacuum.
        /// </summary>
        public const float FearRange = 30f;

        /// <summary>Cap on simultaneous wards, so the ZDO field cannot grow without bound.</summary>
        public const int MaxBadges = 32;

        private const char Separator = ',';

        /// <summary>
        ///     Parsed wards per player ZDO, invalidated by the ZDO's own data revision.
        ///     <see cref="IsWarded" /> is called from <c>MonsterAI.UpdateAI</c> — once per
        ///     creature per frame — so the string must not be re-split on every one of them.
        /// </summary>
        private static readonly Dictionary<ZDOID, (uint revision, HashSet<string> wards)> s_cache = new();

        /// <summary>Is this creature type warded by <paramref name="player" />'s badges?</summary>
        public static bool IsWarded(Player player, string creaturePrefab)
        {
            if (player == null || string.IsNullOrEmpty(creaturePrefab)) return false;

            var wards = Wards(player);
            return wards != null && wards.Contains(creaturePrefab);
        }

        /// <summary>The creature types this player currently wards. Never null for a live player.</summary>
        public static HashSet<string> Wards(Player player)
        {
            var nview = NViewOf(player);
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return null;

            if (s_cache.TryGetValue(zdo.m_uid, out var cached) && cached.revision == zdo.DataRevision)
                return cached.wards;

            var parsed = Parse(zdo.GetString(Key, ""));
            s_cache[zdo.m_uid] = (zdo.DataRevision, parsed);
            return parsed;
        }

        /// <summary>
        ///     Publish the ward set. Owner-only: a non-owner ZDO write is dropped, so this
        ///     reports the failure rather than leaving the caller believing it applied.
        /// </summary>
        public static bool Publish(Player player, IEnumerable<string> creaturePrefabs)
        {
            var nview = NViewOf(player);
            if (nview == null || !nview.IsValid()) return false;
            if (!nview.IsOwner()) return false;

            var zdo = nview.GetZDO();
            if (zdo == null) return false;

            zdo.Set(Key, string.Join(Separator.ToString(), Sanitise(creaturePrefabs).ToArray()));
            return true;
        }

        /// <summary>Add one ward. Returns false if it was already present or the write failed.</summary>
        public static bool Add(Player player, string creaturePrefab)
        {
            var wards = Wards(player);
            if (wards == null || string.IsNullOrEmpty(creaturePrefab)) return false;
            if (wards.Contains(creaturePrefab)) return false;

            var next = new List<string>(wards) { creaturePrefab };
            return Publish(player, next);
        }

        /// <summary>Remove one ward. Returns false if it was not present or the write failed.</summary>
        public static bool Remove(Player player, string creaturePrefab)
        {
            var wards = Wards(player);
            if (wards == null || !wards.Contains(creaturePrefab)) return false;

            var next = new List<string>();
            foreach (var ward in wards)
                if (ward != creaturePrefab)
                    next.Add(ward);

            return Publish(player, next);
        }

        /// <summary>
        ///     Deduplicate, drop anything unstorable, and enforce the cap. Split out as a pure
        ///     function so the rules can be pinned by tests — a name containing the separator
        ///     would otherwise round-trip as two bogus wards, which reads in-world as the
        ///     mechanic silently not working.
        /// </summary>
        internal static List<string> Sanitise(IEnumerable<string> creaturePrefabs)
        {
            var kept = new List<string>();
            if (creaturePrefabs == null) return kept;

            foreach (var prefab in creaturePrefabs)
            {
                if (string.IsNullOrWhiteSpace(prefab)) continue;
                if (prefab.IndexOf(Separator) >= 0) continue;
                var name = prefab.Trim();
                if (kept.Contains(name)) continue;
                if (kept.Count >= MaxBadges) break;
                kept.Add(name);
            }

            return kept;
        }

        /// <summary>Parse a stored ward string. Exposed for tests alongside <see cref="Sanitise" />.</summary>
        internal static HashSet<string> ParseWards(string raw)
        {
            return Parse(raw);
        }

        /// <summary>
        ///     <c>Character.m_nview</c> is protected, so the player's net view is fetched as a
        ///     component rather than read off the field.
        /// </summary>
        private static ZNetView NViewOf(Player player)
        {
            return player == null ? null : player.GetComponent<ZNetView>();
        }

        private static HashSet<string> Parse(string raw)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(raw)) return set;

            foreach (var part in raw.Split(Separator))
            {
                var name = part.Trim();
                if (name.Length > 0) set.Add(name);
            }

            return set;
        }

        /// <summary>Drop the parse cache so a hot reload cannot serve wards from a dead ZDO.</summary>
        [RegisterCleanup]
        public static void Clear()
        {
            s_cache.Clear();
        }
    }
}
