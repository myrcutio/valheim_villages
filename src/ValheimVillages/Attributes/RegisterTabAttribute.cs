using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an IVillagerTab class for automatic registration with VillagerTabManager.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterTabAttribute : Attribute
    {
        public RegisterTabAttribute(string id)
        {
            Id = id;
        }

        /// <summary>Tab identifier (e.g. "info", "debug").</summary>
        public string Id { get; }

        /// <summary>Display order (lower = further left).</summary>
        public int Order { get; set; }
    }
}