using System.Reflection;
using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;
using Xunit;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
///     Verify that every class annotated with [RegisterBehavior] implements IBehavior.
/// </summary>
public class BehaviorContractTests
{
    private static readonly Assembly TargetAssembly = typeof(IBehavior).Assembly;

    [Fact]
    public void RegisterBehaviorAttribute_ClassesMustImplementIBehavior()
    {
        var violations = AssemblyHelper.GetLoadableTypes(TargetAssembly)
            .Where(t => AssemblyHelper.TryGetCustomAttribute<RegisterBehaviorAttribute>(t) != null)
            .Where(t => !typeof(IBehavior).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RegisterBehaviorAttribute_TagMustNotBeEmpty()
    {
        var emptyTags = AssemblyHelper.GetLoadableTypes(TargetAssembly)
            .Select(t => (type: t, attr: AssemblyHelper.TryGetCustomAttribute<RegisterBehaviorAttribute>(t)))
            .Where(x => x.attr != null && string.IsNullOrWhiteSpace(x.attr.Tag))
            .Select(x => x.type.FullName)
            .ToList();

        Assert.Empty(emptyTags);
    }
}