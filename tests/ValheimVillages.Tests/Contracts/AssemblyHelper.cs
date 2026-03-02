using System;
using System.Collections.Generic;
using System.Reflection;

namespace ValheimVillages.Tests.Contracts;

/// <summary>
/// Safe assembly reflection for test environments where game/Unity assemblies
/// are not available. Returns only types whose dependencies can be resolved.
/// </summary>
internal static class AssemblyHelper
{
    public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    /// <summary>
    /// Safely get a custom attribute, returning null if the type's metadata
    /// references assemblies that aren't available (e.g. Unity in a test env).
    /// </summary>
    public static T? TryGetCustomAttribute<T>(Type type) where T : Attribute
    {
        try
        {
            return type.GetCustomAttribute<T>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
