using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks an ITaskHandler class for automatic registration.
    /// AttributeScanner instantiates and registers it with TaskHandlerRegistry.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterTaskHandlerAttribute : Attribute { }
}
