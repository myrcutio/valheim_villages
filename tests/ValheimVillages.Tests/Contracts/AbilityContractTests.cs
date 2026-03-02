using System;
using System.Linq;
using System.Reflection;
using ValheimVillages.Interfaces;
using ValheimVillages.Attributes;
using Xunit;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
/// Verify that every class annotated with [RegisterAbility] implements IAbility.
/// </summary>
public class AbilityContractTests
{
    private static readonly Assembly TargetAssembly = typeof(IAbility).Assembly;

    [Fact]
    public void RegisterAbilityAttribute_ClassesMustImplementIAbility()
    {
        var violations = AssemblyHelper.GetLoadableTypes(TargetAssembly)
            .Where(t => AssemblyHelper.TryGetCustomAttribute<RegisterAbilityAttribute>(t) != null)
            .Where(t => !typeof(IAbility).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RegisterAbilityAttribute_IdMustNotBeEmpty()
    {
        var emptyIds = AssemblyHelper.GetLoadableTypes(TargetAssembly)
            .Select(t => (type: t, attr: AssemblyHelper.TryGetCustomAttribute<RegisterAbilityAttribute>(t)))
            .Where(x => x.attr != null && string.IsNullOrWhiteSpace(x.attr.Id))
            .Select(x => x.type.FullName)
            .ToList();

        Assert.Empty(emptyIds);
    }
}
