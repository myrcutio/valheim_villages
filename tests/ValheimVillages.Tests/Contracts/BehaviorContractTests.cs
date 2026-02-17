using System;
using System.Linq;
using System.Reflection;
using ValheimVillages.Behaviors;
using ValheimVillages.Core.Attributes;
using Xunit;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
/// Verify that every class annotated with [RegisterBehavior] implements IBehavior.
/// These tests validate the Core assembly only; mod-assembly checks require
/// integration tests with Valheim loaded.
/// </summary>
public class BehaviorContractTests
{
    private static readonly Assembly CoreAssembly = typeof(IBehavior).Assembly;

    [Fact]
    public void RegisterBehaviorAttribute_ClassesMustImplementIBehavior()
    {
        var violations = CoreAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<RegisterBehaviorAttribute>() != null)
            .Where(t => !typeof(IBehavior).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RegisterBehaviorAttribute_TagMustNotBeEmpty()
    {
        var emptyTags = CoreAssembly.GetTypes()
            .Select(t => (type: t, attr: t.GetCustomAttribute<RegisterBehaviorAttribute>()))
            .Where(x => x.attr != null && string.IsNullOrWhiteSpace(x.attr.Tag))
            .Select(x => x.type.FullName)
            .ToList();

        Assert.Empty(emptyTags);
    }
}
