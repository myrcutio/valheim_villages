using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an <see cref="UI.Core.IRegistryTabUI" /> class for automatic
    ///     registration with <see cref="UI.Core.RegistryTabManager" />. Parallel to
    ///     <see cref="RegisterTabAttribute" /> (which targets the villager UI) so the
    ///     two tab populations stay separate.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterRegistryTabAttribute : Attribute
    {
        public RegisterRegistryTabAttribute(string id)
        {
            Id = id;
        }

        /// <summary>Tab identifier (e.g. "roster", "add", "revive").</summary>
        public string Id { get; }

        /// <summary>Display order (lower = further left).</summary>
        public int Order { get; set; }
    }
}
