using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks an IAbility class for automatic registration in the ability registry.
    /// AttributeScanner discovers and instantiates it, keyed by Id.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterAbilityAttribute : Attribute
    {
        /// <summary>Unique ability identifier (e.g. "mountainstride").</summary>
        public string Id { get; }

        public RegisterAbilityAttribute(string id)
        {
            Id = id;
        }
    }
}
