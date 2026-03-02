using System;
using System.Linq;
using System.Reflection;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Core;
using Xunit;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
/// Verify that every class annotated with [RegisterTab] implements IVillagerTab.
/// </summary>
public class TabContractTests
{
    private static readonly Assembly TargetAssembly = typeof(IVillagerTab).Assembly;

    [Fact]
    public void RegisterTabAttribute_ClassesMustImplementIVillagerTab()
    {
        var violations = AssemblyHelper.GetLoadableTypes(TargetAssembly)
            .Where(t => AssemblyHelper.TryGetCustomAttribute<RegisterTabAttribute>(t) != null)
            .Where(t => !typeof(IVillagerTab).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RegisterTabAttribute_IdMustNotBeEmpty()
    {
        var emptyIds = AssemblyHelper.GetLoadableTypes(TargetAssembly)
            .Select(t => (type: t, attr: AssemblyHelper.TryGetCustomAttribute<RegisterTabAttribute>(t)))
            .Where(x => x.attr != null && string.IsNullOrWhiteSpace(x.attr.Id))
            .Select(x => x.type.FullName)
            .ToList();

        Assert.Empty(emptyIds);
    }
}
