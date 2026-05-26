using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an IPassiveEffect class for automatic registration in the passive registry.
    ///     AttributeScanner discovers and instantiates it, keyed by Id.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterPassiveAttribute : Attribute
    {
        public RegisterPassiveAttribute(string id)
        {
            Id = id;
        }

        /// <summary>Unique passive effect identifier (e.g. "spawnblock").</summary>
        public string Id { get; }
    }
}