using System.Collections.Generic;
using System.Text;
using ValheimVillages.Schemas;
using ValheimVillages.Villages;

namespace ValheimVillages.Villager.Levels
{
    /// <summary>
    ///     Checks the level data across all villager definitions once, at registry load.
    ///
    ///     <para>These are authored-data invariants, not runtime conditions, so a violation is a
    ///     build-time bug in a JSON file and throws rather than degrading: a one-sided level
    ///     edge or a tier band that disagrees with its comfort floor produces a villager that
    ///     silently never levels, which is exactly the class of fault that left
    ///     <c>tieredBenefits</c> unread for this long.</para>
    ///
    ///     <para>Definitions are validated whether or not they are <c>disabled</c> — a disabled
    ///     type is still referenced by enabled ones (Blacksmith, Carpenter and Farmer all feed
    ///     the disabled Miner), so skipping them would make live edges look dangling.</para>
    /// </summary>
    internal static class VillagerLevelValidator
    {
        public static void Validate(IReadOnlyDictionary<string, VillagerDef> defs)
        {
            var errors = new List<string>();

            foreach (var entry in defs)
            {
                ValidateLevelGraph(defs, entry.Key, entry.Value, errors);
                ValidateRoomRequirements(entry.Key, entry.Value, errors);
            }

            if (errors.Count > 0)
            {
                var message = new StringBuilder("Villager level data is invalid:");
                foreach (var error in errors)
                    message.Append("\n  - ").Append(error);
                throw new System.InvalidOperationException(message.ToString());
            }

            ReportMissingTiers(defs);
        }

        /// <summary>
        ///     <c>providesLevelTo</c> and <c>receivesLevelFrom</c> are two views of one graph, so
        ///     every edge must appear on both ends. It drifted once already: the Lumberjack was
        ///     added with both its edges declared on its own side only, so it fed the Carpenter
        ///     and drew from the Blacksmith according to its own file and neither according to
        ///     theirs.
        /// </summary>
        private static void ValidateLevelGraph(IReadOnlyDictionary<string, VillagerDef> defs,
            string type, VillagerDef def, List<string> errors)
        {
            foreach (var other in def.providesLevelTo)
            {
                if (!defs.TryGetValue(other, out var target))
                {
                    errors.Add($"{type}.providesLevelTo names unknown type '{other}'");
                    continue;
                }

                if (!target.receivesLevelFrom.Contains(type))
                    errors.Add($"{type}.providesLevelTo has '{other}', but {other}.receivesLevelFrom " +
                               $"is missing '{type}' — add it so the edge exists on both ends");
            }

            foreach (var other in def.receivesLevelFrom)
            {
                if (!defs.TryGetValue(other, out var source))
                {
                    errors.Add($"{type}.receivesLevelFrom names unknown type '{other}'");
                    continue;
                }

                if (!source.providesLevelTo.Contains(type))
                    errors.Add($"{type}.receivesLevelFrom has '{other}', but {other}.providesLevelTo " +
                               $"is missing '{type}' — add it so the edge exists on both ends");
            }
        }

        /// <summary>
        ///     Room-scaled tiers must be orderly and actionable: ascending, each one asking for
        ///     at least one room feature, and every feature name real.
        ///
        ///     <para>A tier with no requirement would be reached the moment the one below it was,
        ///     making it invisible; a misspelled feature name would silently never be satisfied,
        ///     capping the villager forever with nothing on screen to explain why. Both are JSON
        ///     bugs, so both throw.</para>
        ///
        ///     <para>This replaced a check that a Comfort role's lowest tier started at its
        ///     <c>minComfortLevel</c>. That was meaningful while tiers were expressed in comfort
        ///     POINTS; now they name room features and the tier numbers are ordinals, so
        ///     <c>minComfortLevel</c> no longer participates in levelling at all.</para>
        /// </summary>
        private static void ValidateRoomRequirements(string type, VillagerDef def, List<string> errors)
        {
            if (def.scalingType != VillagerLevel.ScalingComfort) return;

            var previous = int.MinValue;
            foreach (var tier in def.tieredBenefits)
            {
                if (tier.minLevel <= previous)
                    errors.Add($"{type} tier minLevel {tier.minLevel} does not ascend " +
                               $"(previous was {previous}); tiers are gated in order");
                previous = tier.minLevel;

                if (tier.minLevel <= VillagerLevel.BaseLevel) continue;

                if (tier.requires == null || tier.requires.Count == 0)
                {
                    errors.Add($"{type} tier {tier.minLevel} requires nothing, so it would be " +
                               "reached the instant the tier below it was — give it a room feature");
                    continue;
                }

                foreach (var name in tier.requires)
                    if (!VillageRoomCatalog.TryParseFeature(name, out _))
                        errors.Add($"{type} tier {tier.minLevel} requires unknown room feature " +
                                   $"'{name}' — it could never be satisfied");
            }
        }

        /// <summary>
        ///     An enabled villager with no <c>tieredBenefits</c> computes a level that grants
        ///     nothing. That is a content gap rather than a broken file, so it reports instead of
        ///     throwing — but it reports every time, by name, because an invisible level is the
        ///     same thing to a player as no level at all.
        /// </summary>
        private static void ReportMissingTiers(IReadOnlyDictionary<string, VillagerDef> defs)
        {
            var missing = new List<string>();
            foreach (var entry in defs)
                if (!entry.Value.disabled && entry.Value.tieredBenefits.Count == 0)
                    missing.Add(entry.Key);

            if (missing.Count == 0) return;

            Plugin.Log?.LogWarning(
                $"No tieredBenefits authored for enabled villager(s): {string.Join(", ", missing)}. " +
                "Their level is computed but grants nothing — the UI will show a level with no effect.");
        }
    }
}
