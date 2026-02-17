using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks a static parameterless method as a cleanup hook for hot-reload.
    /// AttributeScanner.InvokeAllCleanup() calls all annotated methods.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RegisterCleanupAttribute : Attribute { }
}
