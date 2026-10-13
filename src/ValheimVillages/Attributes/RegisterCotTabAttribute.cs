using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an <see cref="UI.Core.ICotTabUI" /> class for automatic registration with
    ///     <see cref="UI.Core.CotTabManager" />. Parallel to
    ///     <see cref="RegisterRegistryTabAttribute" /> so the tab populations stay separate — a
    ///     cot must not grow a Revive tab because the registry has one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterCotTabAttribute : Attribute
    {
        public RegisterCotTabAttribute(string id)
        {
            Id = id;
        }

        /// <summary>Tab identifier (e.g. "assign").</summary>
        public string Id { get; }

        /// <summary>Display order (lower = further left).</summary>
        public int Order { get; set; }
    }
}
