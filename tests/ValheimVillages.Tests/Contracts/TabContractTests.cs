using System;
using System.Linq;
using System.Reflection;
using ValheimVillages.Core.Attributes;
using ValheimVillages.UI.Core;
using Xunit;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
/// Verify that every class annotated with [RegisterTab] implements IVillagerTab.
/// </summary>
public class TabContractTests
{
    private static readonly Assembly CoreAssembly = typeof(IVillagerTab).Assembly;

    [Fact]
    public void RegisterTabAttribute_ClassesMustImplementIVillagerTab()
    {
        var violations = CoreAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<RegisterTabAttribute>() != null)
            .Where(t => !typeof(IVillagerTab).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RegisterTabAttribute_IdMustNotBeEmpty()
    {
        var emptyIds = CoreAssembly.GetTypes()
            .Select(t => (type: t, attr: t.GetCustomAttribute<RegisterTabAttribute>()))
            .Where(x => x.attr != null && string.IsNullOrWhiteSpace(x.attr.Id))
            .Select(x => x.type.FullName)
            .ToList();

        Assert.Empty(emptyIds);
    }
}
