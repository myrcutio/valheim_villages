using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Tags;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Which passive village effects are currently earned, and by which village.
    ///
    ///     <para>A passive is provided by a living villager whose definition carries the
    ///     matching <c>passive:&lt;id&gt;</c> tag — so spawn protection belongs to the village
    ///     that has a Guard in it, not to anyone who has planted a registry. Driven off the tag
    ///     rather than the type name so a second protective role needs no code change.</para>
    ///
    ///     <para>Cached, because the only caller that matters is on the spawn path:
    ///     <c>SpawnSystem.Spawn</c> runs for every spawn attempt in the world, and walking the
    ///     record ZDOs there would put a scan behind each one.</para>
    /// </summary>
    public static class VillagePassives
    {
        /// <summary>
        ///     How long a lookup is reused. Recruiting or losing a guard takes effect within
        ///     this long, which is far below the interval at which spawns actually matter and
        ///     keeps the record scan off the hot path.
        /// </summary>
        public const float CacheSeconds = 5f;

        public const string SpawnBlock = "spawnblock";

        private const string PassiveNamespace = "passive";

        private static readonly Dictionary<string, HashSet<string>> s_byVillage = new();
        private static float s_nextRefresh;

        /// <summary>Does this village have a living villager providing this passive?</summary>
        public static bool VillageHas(string villageId, string passiveId)
        {
            if (string.IsNullOrEmpty(villageId) || string.IsNullOrEmpty(passiveId)) return false;

            Refresh();
            return s_byVillage.TryGetValue(villageId, out var passives) && passives.Contains(passiveId);
        }

        /// <summary>
        ///     Does the village at this position provide this passive? False when no village
        ///     covers the position at all.
        /// </summary>
        public static bool AtPositionHas(Vector3 position, float radius, string passiveId)
        {
            return VillageAreaManager.TryGetVillageNear(position, radius, out var villageId)
                   && VillageHas(villageId, passiveId);
        }

        /// <summary>
        ///     The passives this village has earned, and the villager types providing them.
        ///     For diagnostics — an earned passive is otherwise invisible, which is half the
        ///     reason the Guard read as doing nothing.
        /// </summary>
        public static IReadOnlyDictionary<string, List<string>> EarnedWithProviders(string villageId)
        {
            var result = new Dictionary<string, List<string>>();
            if (string.IsNullOrEmpty(villageId)) return result;

            foreach (var record in VillagerRecordTable.EnumerateAll())
            {
                if (record == null || !record.IsValid) continue;
                if (record.Status != RecordStatus.Alive) continue;
                if (!VillagerRecordTable.KeysMatch(record.Village, villageId)) continue;

                var def = Villager.Registry.VillagerRegistry.Get(record.Type);
                if (def == null) continue;

                foreach (var passive in TagParser.GetValues(def.tags, PassiveNamespace))
                {
                    if (!result.TryGetValue(passive, out var providers))
                    {
                        providers = new List<string>();
                        result[passive] = providers;
                    }

                    if (!providers.Contains(record.Type)) providers.Add(record.Type);
                }
            }

            return result;
        }

        /// <summary>Every villager type that declares it provides this passive.</summary>
        public static List<string> TypesProviding(string passiveId)
        {
            var types = new List<string>();
            foreach (var entry in Villager.Registry.VillagerRegistry.Definitions)
                if (TagParser.HasTag(entry.Value.tags, PassiveNamespace, passiveId))
                    types.Add(entry.Key);
            return types;
        }

        /// <summary>
        ///     Rebuild the map from the villager records, at most once per
        ///     <see cref="CacheSeconds" />. Lazy, so an idle server with nothing asking does no
        ///     work at all.
        /// </summary>
        private static void Refresh()
        {
            if (Time.time < s_nextRefresh) return;
            s_nextRefresh = Time.time + CacheSeconds;

            s_byVillage.Clear();

            // EnumerateAll, not QueryByStatus: that filters by village key, and a null key
            // matches only records with NO village rather than every village.
            foreach (var record in VillagerRecordTable.EnumerateAll())
            {
                if (record == null || !record.IsValid) continue;
                if (record.Status != RecordStatus.Alive) continue;
                if (string.IsNullOrEmpty(record.Village)) continue;

                var def = Villager.Registry.VillagerRegistry.Get(record.Type);
                if (def == null) continue;

                var provided = TagParser.GetValues(def.tags, PassiveNamespace);
                if (provided.Count == 0) continue;

                if (!s_byVillage.TryGetValue(record.Village, out var passives))
                {
                    passives = new HashSet<string>();
                    s_byVillage[record.Village] = passives;
                }

                foreach (var passive in provided)
                    passives.Add(passive);
            }
        }

        /// <summary>Drop the cache so a hot reload cannot serve a stale roster.</summary>
        [RegisterCleanup]
        public static void Clear()
        {
            s_byVillage.Clear();
            s_nextRefresh = 0f;
        }
    }
}
