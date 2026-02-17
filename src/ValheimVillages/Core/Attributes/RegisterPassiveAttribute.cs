using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks an IPassiveEffect class for automatic registration in the passive registry.
    /// AttributeScanner discovers and instantiates it, keyed by Id.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterPassiveAttribute : Attribute
    {
        /// <summary>Unique passive effect identifier (e.g. "spawnblock").</summary>
        public string Id { get; }

        public RegisterPassiveAttribute(string id)
        {
            Id = id;
        }
    }
}
