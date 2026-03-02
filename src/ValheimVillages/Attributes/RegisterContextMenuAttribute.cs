using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    /// Marks an IContextMenu class for automatic discovery.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterContextMenuAttribute : Attribute
    {
        /// <summary>Context menu identifier (e.g. "workorder").</summary>
        public string Id { get; }

        public RegisterContextMenuAttribute(string id)
        {
            Id = id;
        }
    }
}
