using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an ITaskHandler class for automatic registration.
    ///     AttributeScanner instantiates and registers it with TaskHandlerRegistry.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterTaskHandlerAttribute : Attribute
    {
    }
}