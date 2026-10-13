using System;
using System.Collections.Generic;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages;

namespace ValheimVillages.Villager.Levels
{
    /// <summary>
    ///     One thing that does, or could, raise a villager's level — paired with whether it is
    ///     currently satisfied. The unsatisfied ones are the whole point: they are the gap
    ///     between current and potential level that the UI shows the player, so each label has
    ///     to name something to go and do ("a Miner", "comfort 7") rather than a condition.
    /// </summary>
    public readonly struct LevelContributor
    {
        public LevelContributor(string label, bool satisfied, string grants)
        {
            Label = label;
            Satisfied = satisfied;
            Grants = grants;
        }

        public string Label { get; }
        public bool Satisfied { get; }

        /// <summary>What reaching it gives, from the authored tier. May be empty.</summary>
        public string Grants { get; }
    }

    /// <summary>A villager's level, where it could reach, and why it is not there yet.</summary>
    public readonly struct VillagerLevelState
    {
        public VillagerLevelState(int current, int potential, List<LevelContributor> contributors,
            TieredBenefit benefit, int comfort)
        {
            Current = current;
            Potential = potential;
            Contributors = contributors;
            Benefit = benefit;
            Comfort = comfort;
        }

        public int Current { get; }
        public int Potential { get; }

        /// <summary>Milestones for this villager, satisfied and not, in ascending order.</summary>
        public IReadOnlyList<LevelContributor> Contributors { get; }

        /// <summary>The authored tier <see cref="Current" /> falls in, or null if none covers it.</summary>
        public TieredBenefit Benefit { get; }

        /// <summary>Comfort at the villager's home. -1 when this villager does not scale on it.</summary>
        public int Comfort { get; }

        public bool AtPotential => Current >= Potential;
    }

    /// <summary>
    ///     Computes a villager's level from its surroundings, the way a crafting station's level
    ///     comes from its extensions rather than from anything the station did:
    ///     <c>CraftingStation.GetLevel() == 1 + GetExtentionCount()</c>.
    ///
    ///     <para>Level is therefore never earned or stored — it is derived on demand, so
    ///     furnishing a villager's room or recruiting a neighbour changes it immediately, and
    ///     there is no progress state to persist, migrate or let desync between peers.</para>
    ///
    ///     <para>What raises it comes from the <c>scalingType</c> already authored on every
    ///     definition:</para>
    ///     <list type="bullet">
    ///         <item>
    ///             <c>Comfort</c> — the level IS the comfort of the villager's home, on Valheim's
    ///             own scale. Every Comfort-scaled definition authors its first tier at exactly
    ///             its <c>minComfortLevel</c> (Guard 2/2, Mountaineer 4/4, Scout 2/2, Shipwright
    ///             3/3, Trader 4/4), so the tier bands are already written in comfort points and
    ///             need no translation.
    ///         </item>
    ///         <item><c>VillagerVariety</c> — one level per type in <c>receivesLevelFrom</c> living in the village.</item>
    ///         <item><c>VillagerDependent</c> — one level per living villager of <c>dependentOnNpcType</c>.</item>
    ///     </list>
    /// </summary>
    public static class VillagerLevel
    {
        /// <summary>Every villager is at least this, with nothing around it.</summary>
        public const int BaseLevel = 1;

        public const string ScalingComfort = "Comfort";
        public const string ScalingVariety = "VillagerVariety";
        public const string ScalingDependent = "VillagerDependent";

        private static readonly VillagerLevelState Unknown =
            new(BaseLevel, BaseLevel, new List<LevelContributor>(), null, -1);

        public static VillagerLevelState Evaluate(VillagerRecord record)
        {
            if (record == null || !record.IsValid) return Unknown;

            var def = Registry.VillagerRegistry.Get(record.Type);
            if (def == null) return Unknown;

            return def.scalingType switch
            {
                ScalingVariety => FromPresentTypes(def, LivingTypesInVillage(record.Village)),
                ScalingDependent => FromDependentCount(def,
                    CountLivingOfType(record.Village, def.dependentOnNpcType)),
                // Comfort is the schema default, so it is also what an unrecognised scalingType
                // resolves to — better a villager that levels on its rooms than one stuck at 1.
                _ => FromRooms(def, record.Village)
            };
        }

        /// <summary>
        ///     The level a room-scaled villager has, from the TAGS ON ITS VILLAGE'S ROOMS.
        ///
        ///     <para>Each authored tier names the room features the village must provide
        ///     (<c>tieredBenefits[].requires</c>) and the level is the highest tier whose
        ///     requirements are all met. Tiers are cumulative: once one is unmet the villager
        ///     stops there even if a later tier happens to be satisfied, so a village cannot
        ///     skip a step by building the fancy thing and not the basic one.</para>
        ///
        ///     <para>Replaces sampling <c>SE_Rested.CalculateComfortLevel</c> at the villager's
        ///     home anchor. That measured a 10m radius around where the villager sleeps, so a
        ///     Guard homed at an outdoor registry read comfort 1 however good the village was —
        ///     and "needs comfort 7" told the player nothing about what to build. A requirement
        ///     names the thing to go and do.</para>
        /// </summary>
        public static VillagerLevelState FromRooms(VillagerDef def, string villageId)
        {
            return FromRooms(def, requires => VillageRoomCatalog.Unmet(villageId, requires));
        }

        /// <summary>
        ///     The tier-gating itself, with the village lookup injected. Split out so the
        ///     cumulative semantics can be pinned by tests without a world — a village is a
        ///     navmesh, a ZDO and a pile of instantiated pieces, none of which exist headless.
        /// </summary>
        internal static VillagerLevelState FromRooms(
            VillagerDef def, Func<IEnumerable<string>, List<string>> unmetFor)
        {
            var contributors = new List<LevelContributor>();
            var current = BaseLevel;
            var potential = BaseLevel;
            var blocked = false;

            foreach (var tier in def.tieredBenefits)
            {
                if (tier.minLevel <= BaseLevel) continue;
                if (tier.minLevel > potential) potential = tier.minLevel;

                var unmet = unmetFor(tier.requires) ?? new List<string>();
                var hasOwn = unmet.Count == 0;
                var reached = hasOwn && !blocked;
                if (reached) current = tier.minLevel;
                else blocked = true;

                // Three states, not two. A tier whose own requirement the village already has
                // but which an earlier gap blocks must NOT read as missing, or the panel sends
                // the player off to build a workbench they are standing next to.
                var label = reached
                    ? TierLabel(tier)
                    : hasOwn
                        ? $"{TierLabel(tier)} — already built, waiting on the step above"
                        : string.Join(", ", unmet.ToArray());

                contributors.Add(new LevelContributor(label, reached, tier.description));
            }

            return new VillagerLevelState(current, potential, contributors,
                BenefitFor(def, current), -1);
        }

        /// <summary>What a satisfied tier asked for, so a met step still reads as a thing.</summary>
        private static string TierLabel(TieredBenefit tier)
        {
            if (tier.requires == null || tier.requires.Count == 0) return $"level {tier.minLevel}";

            var parts = new List<string>();
            foreach (var name in tier.requires)
                if (VillageRoomCatalog.TryParseFeature(name, out var feature))
                    parts.Add(VillageRoomCatalog.Describe(feature));

            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : $"level {tier.minLevel}";
        }

        /// <summary>
        ///     One level per type in <c>receivesLevelFrom</c> present among
        ///     <paramref name="present" />. The list is finite and authored, so it caps the level
        ///     by itself — no tier lookup needed.
        /// </summary>
        public static VillagerLevelState FromPresentTypes(VillagerDef def, ICollection<string> present)
        {
            var contributors = new List<LevelContributor>();
            var satisfied = 0;

            foreach (var type in def.receivesLevelFrom)
            {
                var has = present.Contains(type);
                if (has) satisfied++;
                contributors.Add(new LevelContributor(Article(type), has, BenefitFor(def, BaseLevel + satisfied)?.description ?? ""));
            }

            var current = BaseLevel + satisfied;
            return new VillagerLevelState(current, BaseLevel + contributors.Count, contributors,
                BenefitFor(def, current), -1);
        }

        /// <summary>
        ///     One level per living villager of <c>dependentOnNpcType</c>, capped by the authored
        ///     tiers — a busier kitchen supports a better cook.
        /// </summary>
        public static VillagerLevelState FromDependentCount(VillagerDef def, int count)
        {
            if (string.IsNullOrEmpty(def.dependentOnNpcType))
                return new VillagerLevelState(BaseLevel, BaseLevel, new List<LevelContributor>(), BenefitFor(def, BaseLevel), -1);

            var potential = BaseLevel;
            foreach (var tier in def.tieredBenefits)
                if (tier.minLevel > potential) potential = tier.minLevel;

            var contributors = new List<LevelContributor>();
            for (var level = BaseLevel + 1; level <= potential; level++)
            {
                var needed = level - BaseLevel;
                var label = needed == 1 ? Article(def.dependentOnNpcType) : $"{needed} {def.dependentOnNpcType}s";
                contributors.Add(new LevelContributor(label, count >= needed, BenefitFor(def, level)?.description ?? ""));
            }

            var current = BaseLevel + count;
            if (current > potential) current = potential;
            return new VillagerLevelState(current, potential, contributors, BenefitFor(def, current), -1);
        }

        /// <summary>The authored tier whose band covers <paramref name="level" />, or null.</summary>
        public static TieredBenefit BenefitFor(VillagerDef def, int level)
        {
            foreach (var tier in def.tieredBenefits)
                if (level >= tier.minLevel && level <= tier.maxLevel)
                    return tier;
            return null;
        }

        internal static HashSet<string> LivingTypesInVillage(string villageKey)
        {
            var types = new HashSet<string>();
            foreach (var other in VillagerRecordTable.QueryByStatus(villageKey, RecordStatus.Alive))
                if (!string.IsNullOrEmpty(other.Type))
                    types.Add(other.Type);
            return types;
        }

        private static int CountLivingOfType(string villageKey, string type)
        {
            var count = 0;
            foreach (var other in VillagerRecordTable.QueryByStatus(villageKey, RecordStatus.Alive))
                if (other.Type == type)
                    count++;
            return count;
        }

        /// <summary>"a Miner" / "an Archer" — read as an instruction, not a noun.</summary>
        private static string Article(string type)
        {
            if (string.IsNullOrEmpty(type)) return type;
            return ("AEIOUaeiou".IndexOf(type[0]) >= 0 ? "an " : "a ") + type;
        }
    }
}
