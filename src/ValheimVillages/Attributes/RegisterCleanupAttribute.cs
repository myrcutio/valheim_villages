using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks a static parameterless method as a cleanup hook for hot-reload.
    ///     AttributeScanner.InvokeAllCleanup() calls all annotated methods.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RegisterCleanupAttribute : Attribute
    {
    }
}