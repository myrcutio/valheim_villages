using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks an IVillagerTab class for automatic registration with VillagerTabManager.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterTabAttribute : Attribute
    {
        /// <summary>Tab identifier (e.g. "info", "debug").</summary>
        public string Id { get; }

        /// <summary>Display order (lower = further left).</summary>
        public int Order { get; set; }

        public RegisterTabAttribute(string id)
        {
            Id = id;
        }
    }
}
