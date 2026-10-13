using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.Levels;
using Xunit;

namespace ValheimVillages.Tests.Definitions;

/// <summary>
///     Pins the level semantics against the REAL shipped villager definitions, loaded from the
///     plugin assembly's embedded resources rather than hand-built fixtures.
///
///     <para>That matters here because the whole level system is authored in JSON and read by
///     code: a fixture would only prove the arithmetic, while these prove the arithmetic agrees
///     with what actually ships. The level graph had already drifted one-sided once before
///     anything read it.</para>
/// </summary>
public class VillagerLevelTests
{
    private static readonly Dictionary<string, VillagerDef> Defs = LoadShippedDefinitions();

    /// <summary>
    ///     Reads the same embedded JSON <c>VillagerRegistry</c> loads at runtime. Uses
    ///     System.Text.Json with IncludeFields because <see cref="VillagerDef" /> is all public
    ///     fields (the game ships Newtonsoft, but the test host has no reason to).
    /// </summary>
    private static Dictionary<string, VillagerDef> LoadShippedDefinitions()
    {
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var assembly = typeof(VillagerDef).Assembly;
        var defs = new Dictionary<string, VillagerDef>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Villager.Registry.Definitions.") || !name.EndsWith(".json")) continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var def = JsonSerializer.Deserialize<VillagerDef>(reader.ReadToEnd(), options)!;
            defs[def.type] = def;
        }

        return defs;
    }

    private static VillagerDef Def(string type)
    {
        Assert.True(Defs.ContainsKey(type), $"no shipped definition for '{type}'");
        return Defs[type];
    }

    [Fact]
    public void ShippedDefinitions_Load()
    {
        // Guards the resource-name filter above: a silent zero would make every other test here
        // vacuous, which is how tieredBenefits stayed empty and unnoticed for so long.
        Assert.Equal(11, Defs.Count);
    }

    [Fact]
    public void ShippedDefinitions_SatisfyLevelInvariants()
    {
        // Throws with the offending definition named, so a failure reads as the JSON bug it is.
        VillagerLevelValidator.Validate(Defs);
    }

    // ---- The validator's own teeth ----
    //
    // ShippedDefinitions_SatisfyLevelInvariants passes on clean data, which proves nothing on
    // its own. These show each invariant actually fires, so that test is falsifiable.

    [Fact]
    public void Validator_RejectsAOneSidedProvidesEdge()
    {
        var defs = new Dictionary<string, VillagerDef>
        {
            ["Smith"] = new() { type = "Smith", providesLevelTo = { "Hauler" } },
            ["Hauler"] = new() { type = "Hauler" } // never declares it receives
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VillagerLevelValidator.Validate(defs));
        Assert.Contains("Hauler.receivesLevelFrom", ex.Message);
    }

    [Fact]
    public void Validator_RejectsAOneSidedReceivesEdge()
    {
        var defs = new Dictionary<string, VillagerDef>
        {
            ["Smith"] = new() { type = "Smith" },
            ["Hauler"] = new() { type = "Hauler", receivesLevelFrom = { "Smith" } }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VillagerLevelValidator.Validate(defs));
        Assert.Contains("Smith.providesLevelTo", ex.Message);
    }

    [Fact]
    public void Validator_RejectsAnEdgeToAnUnknownType()
    {
        var defs = new Dictionary<string, VillagerDef>
        {
            ["Smith"] = new() { type = "Smith", providesLevelTo = { "Ghost" } }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VillagerLevelValidator.Validate(defs));
        Assert.Contains("unknown type 'Ghost'", ex.Message);
    }

    [Fact]
    public void Validator_RejectsATierThatRequiresNothing()
    {
        // It would be reached the instant the tier below it was, so it is invisible.
        var defs = new Dictionary<string, VillagerDef>
        {
            ["Watchman"] = new()
            {
                type = "Watchman",
                scalingType = VillagerLevel.ScalingComfort,
                tieredBenefits = { new TieredBenefit { minLevel = 2, maxLevel = 99 } }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VillagerLevelValidator.Validate(defs));
        Assert.Contains("requires nothing", ex.Message);
    }

    [Fact]
    public void Validator_RejectsAnUnknownRoomFeature()
    {
        // A misspelled feature could never be satisfied, capping the villager silently.
        var defs = new Dictionary<string, VillagerDef>
        {
            ["Watchman"] = new()
            {
                type = "Watchman",
                scalingType = VillagerLevel.ScalingComfort,
                tieredBenefits =
                {
                    new TieredBenefit { minLevel = 2, maxLevel = 99, requires = { "Firepit" } }
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VillagerLevelValidator.Validate(defs));
        Assert.Contains("unknown room feature 'Firepit'", ex.Message);
    }

    [Fact]
    public void Validator_RejectsNonAscendingTiers()
    {
        var defs = new Dictionary<string, VillagerDef>
        {
            ["Watchman"] = new()
            {
                type = "Watchman",
                scalingType = VillagerLevel.ScalingComfort,
                tieredBenefits =
                {
                    new TieredBenefit { minLevel = 4, maxLevel = 4, requires = { "Fire" } },
                    new TieredBenefit { minLevel = 2, maxLevel = 99, requires = { "Bed" } }
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VillagerLevelValidator.Validate(defs));
        Assert.Contains("does not ascend", ex.Message);
    }

    // ---- Room scaling: level is the highest tier whose room requirements are met ----
    //
    // The village lookup is injected, so these pin the GATING (ascending, cumulative, blocked)
    // without needing a navmesh, a ZDO or instantiated pieces.

    /// <summary>A fake village providing exactly the named features.</summary>
    private static Func<IEnumerable<string>, List<string>> Village(params string[] available)
    {
        var have = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        return requires =>
        {
            var missing = new List<string>();
            if (requires == null) return missing;
            foreach (var r in requires)
                if (!have.Contains(r))
                    missing.Add(r);
            return missing;
        };
    }

    [Fact]
    public void Rooms_BareVillageIsLevelOne()
    {
        var state = VillagerLevel.FromRooms(Def("Guard"), Village());
        Assert.Equal(1, state.Current);
        Assert.Equal(6, state.Potential); // Guard authors five tiers, topping out at 6
    }

    [Fact]
    public void Rooms_LitFireAloneReachesTheFirstTier()
    {
        // The user's rule: a region with a lit fire is enough for warmth.
        var state = VillagerLevel.FromRooms(Def("Guard"), Village("Fire"));
        Assert.Equal(2, state.Current);
        Assert.NotNull(state.Benefit);
    }

    [Fact]
    public void Rooms_TiersAreCumulative_AGapStopsProgress()
    {
        // Bed and Storage are present but Fire is not. Guard needs Fire for tier 2, so nothing
        // above it may be reached however much else the village has.
        var state = VillagerLevel.FromRooms(Def("Guard"), Village("Bed", "Storage", "Table", "Seat"));
        Assert.Equal(1, state.Current);
    }

    [Fact]
    public void Rooms_SkippingAMiddleTierDoesNotSkipAhead()
    {
        // Fire (tier 2) and Table+Seat (tier 4) but no Bed (tier 3): stops at 2.
        var state = VillagerLevel.FromRooms(Def("Guard"), Village("Fire", "Table", "Seat"));
        Assert.Equal(2, state.Current);
    }

    [Fact]
    public void Rooms_WalkingUpEveryTierReachesPotential()
    {
        var state = VillagerLevel.FromRooms(Def("Guard"),
            Village("Fire", "Bed", "Table", "Seat", "WorkStation", "Storage"));
        Assert.Equal(6, state.Current);
        Assert.True(state.AtPotential);
    }

    [Fact]
    public void Rooms_UnmetTierNamesWhatIsMissing()
    {
        // The whole point of the model: the row says what to go and build.
        var state = VillagerLevel.FromRooms(Def("Guard"), Village("Fire"));
        var next = state.Contributors.First(c => !c.Satisfied);
        Assert.Contains("Bed", next.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rooms_SatisfiedTiersAreMarkedSatisfied()
    {
        var state = VillagerLevel.FromRooms(Def("Guard"), Village("Fire", "Bed"));
        Assert.Equal(2, state.Contributors.Count(c => c.Satisfied));
        Assert.Equal(3, state.Current);
    }

    [Fact]
    public void Rooms_ABlockedTierThatIsAlreadyBuiltDoesNotReadAsMissing()
    {
        // Village has Fire, Bed and a WorkStation but no Table. Tier 4 (Table+Seat) blocks, so
        // tier 5 (WorkStation) is unreachable — but the player already HAS the workstation and
        // must not be told to build one.
        var state = VillagerLevel.FromRooms(Def("Guard"), Village("Fire", "Bed", "WorkStation"));

        Assert.Equal(3, state.Current);

        var workstationRow = state.Contributors.Single(c => c.Label.Contains("crafting station"));
        Assert.False(workstationRow.Satisfied);
        Assert.Contains("already built", workstationRow.Label);

        var tableRow = state.Contributors.Single(c => c.Label.StartsWith("Table"));
        Assert.DoesNotContain("already built", tableRow.Label);
    }

    [Fact]
    public void Rooms_EveryRoomScaledRoleRequiresWarmthFirst()
    {
        // Warmth is the common entry requirement across the roster, so an unheated village
        // levels nobody. If this changes it should be a decision, not a drift.
        foreach (var entry in Defs)
        {
            if (entry.Value.scalingType != VillagerLevel.ScalingComfort) continue;
            if (entry.Value.tieredBenefits.Count == 0) continue;

            var first = entry.Value.tieredBenefits[0];
            Assert.Contains("Fire", first.requires);
        }
    }

    // ---- VillagerVariety scaling: one level per neighbouring type ----

    [Fact]
    public void Miner_PotentialIsOnePerLevelSource()
    {
        // Miner receives from Farmer, Blacksmith and Carpenter.
        var state = VillagerLevel.FromPresentTypes(Def("Miner"), new List<string>());
        Assert.Equal(4, state.Potential);
        Assert.Equal(1, state.Current);
    }

    [Fact]
    public void Variety_CurrentCountsOnlyPresentTypes()
    {
        var state = VillagerLevel.FromPresentTypes(Def("Miner"), new List<string> { "Farmer", "Carpenter" });
        Assert.Equal(3, state.Current);
        Assert.Equal(4, state.Potential);
    }

    [Fact]
    public void Variety_IgnoresTypesItDoesNotDrawFrom()
    {
        // A Guard in the village does nothing for a Miner's level.
        var state = VillagerLevel.FromPresentTypes(Def("Miner"), new List<string> { "Guard", "Mountaineer" });
        Assert.Equal(1, state.Current);
    }

    [Fact]
    public void Variety_AllSourcesPresent_ReachesPotential()
    {
        var state = VillagerLevel.FromPresentTypes(Def("Miner"),
            new List<string> { "Farmer", "Blacksmith", "Carpenter" });
        Assert.True(state.AtPotential);
        Assert.Equal(4, state.Current);
    }

    [Fact]
    public void Lumberjack_DrawsFromBlacksmith()
    {
        // The edge that was previously declared on one side only.
        var state = VillagerLevel.FromPresentTypes(Def("Lumberjack"), new List<string> { "Blacksmith" });
        Assert.Equal(2, state.Current);
    }

    [Fact]
    public void Carpenter_DrawsFromLumberjack()
    {
        // The other previously one-sided edge.
        var state = VillagerLevel.FromPresentTypes(Def("Carpenter"), new List<string> { "Lumberjack" });
        Assert.Equal(2, state.Current);
    }

    // ---- VillagerDependent scaling: one level per dependent villager ----

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(9, 3)] // capped by the top authored tier
    public void TavernKeeper_LevelsWithFarmerCount(int farmers, int expected)
    {
        Assert.Equal(expected, VillagerLevel.FromDependentCount(Def("TavernKeeper"), farmers).Current);
    }

    [Fact]
    public void TavernKeeper_PotentialIsItsTopTier()
    {
        Assert.Equal(3, VillagerLevel.FromDependentCount(Def("TavernKeeper"), 0).Potential);
    }

    [Fact]
    public void Dependent_FirstContributorNamesTheTypeNeeded()
    {
        var state = VillagerLevel.FromDependentCount(Def("TavernKeeper"), 0);
        Assert.Equal("a Farmer", state.Contributors[0].Label);
        Assert.False(state.Contributors[0].Satisfied);
    }

    // ---- The UI contract: an unmet milestone must name something to go and do ----

    [Fact]
    public void EveryContributor_HasANonEmptyLabel()
    {
        foreach (var entry in Defs)
        {
            var def = entry.Value;
            var state = def.scalingType switch
            {
                VillagerLevel.ScalingVariety => VillagerLevel.FromPresentTypes(def, new List<string>()),
                VillagerLevel.ScalingDependent => VillagerLevel.FromDependentCount(def, 0),
                _ => VillagerLevel.FromRooms(def, Village())
            };

            foreach (var c in state.Contributors)
                Assert.False(string.IsNullOrWhiteSpace(c.Label),
                    $"{entry.Key} has a contributor with no label");
        }
    }

    [Fact]
    public void EveryDefinition_HasAtLeastOneContributor()
    {
        // A villager with no contributors can never level, and its panel would show an
        // unexplained "1 / 1". If this fails, that definition needs tiers or level sources.
        foreach (var entry in Defs)
        {
            var def = entry.Value;
            var state = def.scalingType switch
            {
                VillagerLevel.ScalingVariety => VillagerLevel.FromPresentTypes(def, new List<string>()),
                VillagerLevel.ScalingDependent => VillagerLevel.FromDependentCount(def, 0),
                _ => VillagerLevel.FromRooms(def, Village())
            };

            Assert.True(state.Contributors.Count > 0, $"{entry.Key} can never level");
        }
    }
}
