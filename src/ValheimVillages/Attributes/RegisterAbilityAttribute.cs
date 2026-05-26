using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an IAbility class for automatic registration in the ability registry.
    ///     AttributeScanner discovers and instantiates it, keyed by Id.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterAbilityAttribute : Attribute
    {
        public RegisterAbilityAttribute(string id)
        {
            Id = id;
        }

        /// <summary>Unique ability identifier (e.g. "mountainstride").</summary>
        public string Id { get; }
    }
}