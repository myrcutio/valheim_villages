using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks an IListPanel class for automatic registration with its parent tab.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterListPanelAttribute : Attribute
    {
        /// <summary>Panel identifier (e.g. "guardstatus").</summary>
        public string Id { get; }

        /// <summary>Parent tab identifier (e.g. "info").</summary>
        public string ParentTab { get; }

        public RegisterListPanelAttribute(string id, string parentTab)
        {
            Id = id;
            ParentTab = parentTab;
        }
    }
}
