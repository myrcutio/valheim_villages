using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks an IListPanel class for automatic registration with its parent tab.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterListPanelAttribute : Attribute
    {
        public RegisterListPanelAttribute(string id, string parentTab)
        {
            Id = id;
            ParentTab = parentTab;
        }

        /// <summary>Panel identifier (e.g. "patrolstatus").</summary>
        public string Id { get; }

        /// <summary>Parent tab identifier (e.g. "info").</summary>
        public string ParentTab { get; }
    }
}