using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks a static method as an integration test for Phase 6.
    ///     Method must be <c>static void Method()</c> or <c>static void Method(TestContext ctx)</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ModTestAttribute : Attribute
    {
        /// <summary>Test name (defaults to method name if null).</summary>
        public string Name { get; set; }

        /// <summary>Execution order (lower runs first).</summary>
        public int Order { get; set; }
    }
}