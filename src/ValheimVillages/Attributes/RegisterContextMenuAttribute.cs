using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an IContextMenu class for automatic discovery.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterContextMenuAttribute : Attribute
    {
        public RegisterContextMenuAttribute(string id)
        {
            Id = id;
        }

        /// <summary>Context menu identifier (e.g. "workorder").</summary>
        public string Id { get; }
    }
}