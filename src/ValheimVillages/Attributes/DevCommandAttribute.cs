using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks a static method as a debug console command. The method must be
    ///     <c>static void Method()</c> or <c>static void Method(Terminal.ConsoleEventArgs)</c>.
    ///     AttributeScanner auto-registers a Terminal.ConsoleCommand at startup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DevCommandAttribute : Attribute
    {
        public DevCommandAttribute(string description)
        {
            Description = description;
        }

        /// <summary>Help text shown in the console's command list.</summary>
        public string Description { get; }

        /// <summary>
        ///     Explicit command name. When null, auto-derived as
        ///     <c>DeclaringType_MethodName</c> lowercased.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Marks a command that destroys world state, damages player builds, or
        ///     fabricates record state. AttributeScanner wraps these so they refuse to
        ///     run unless the invocation carries <c>--yes</c> (or <c>--dry-run</c>, which
        ///     previews without mutating).
        ///     <para>
        ///         Deliberately NOT Terminal's <c>isCheat</c> flag: <c>IsCheatsEnabled()</c>
        ///         additionally requires <c>ZNet.instance.IsServer()</c>, so a cheat-gated
        ///         command is permanently unreachable from a client connected to a
        ///         dedicated server — the topology this mod is normally developed against.
        ///         A confirmation token behaves identically on host, client and server.
        ///     </para>
        /// </summary>
        public bool Destructive { get; set; }

        /// <summary>
        ///     Name of a <c>static IEnumerable&lt;string&gt;</c> or
        ///     <c>static List&lt;string&gt;</c> member on the declaring type supplying
        ///     first-argument tab-completions. Null means no completion.
        /// </summary>
        public string OptionsProvider { get; set; }
    }
}
