using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks a static method as a debug console command. The method must be
    /// <c>static void Method()</c> or <c>static void Method(Terminal.ConsoleEventArgs)</c>.
    /// AttributeScanner auto-registers a Terminal.ConsoleCommand at startup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class DevCommandAttribute : Attribute
    {
        /// <summary>Help text shown in the console's command list.</summary>
        public string Description { get; }

        /// <summary>
        /// Explicit command name. When null, auto-derived as
        /// <c>DeclaringType_MethodName</c> lowercased.
        /// </summary>
        public string Name { get; set; }

        public DevCommandAttribute(string description)
        {
            Description = description;
        }
    }
}
