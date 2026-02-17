using System;
using System.Linq;
using System.Reflection;
using ValheimVillages.Abilities;
using ValheimVillages.Core.Attributes;
using Xunit;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
/// Verify that every class annotated with [RegisterAbility] implements IAbility.
/// </summary>
public class AbilityContractTests
{
    private static readonly Assembly CoreAssembly = typeof(IAbility).Assembly;

    [Fact]
    public void RegisterAbilityAttribute_ClassesMustImplementIAbility()
    {
        var violations = CoreAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<RegisterAbilityAttribute>() != null)
            .Where(t => !typeof(IAbility).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RegisterAbilityAttribute_IdMustNotBeEmpty()
    {
        var emptyIds = CoreAssembly.GetTypes()
            .Select(t => (type: t, attr: t.GetCustomAttribute<RegisterAbilityAttribute>()))
            .Where(x => x.attr != null && string.IsNullOrWhiteSpace(x.attr.Id))
            .Select(x => x.type.FullName)
            .ToList();

        Assert.Empty(emptyIds);
    }
}
