using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks an IBehavior class for automatic discovery by BehaviorFactory.
    /// The Tag must match the NPC definition's behavior tag (e.g. "patrol", "craft").
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterBehaviorAttribute : Attribute
    {
        /// <summary>Behavior tag matching NPC definition (e.g. "patrol").</summary>
        public string Tag { get; }

        public RegisterBehaviorAttribute(string tag)
        {
            Tag = tag;
        }
    }
}
