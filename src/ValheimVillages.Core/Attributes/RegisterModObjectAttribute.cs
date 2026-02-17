using System;

namespace ValheimVillages.Core.Attributes
{
    /// <summary>
    /// Marks a MonoBehaviour class as a mod-created singleton GameObject.
    /// AttributeScanner.GetModObjectNames() returns all annotated names
    /// so HotReloadHelper can detect orphaned objects.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RegisterModObjectAttribute : Attribute
    {
        /// <summary>The GameObject name used at runtime.</summary>
        public string GameObjectName { get; }

        public RegisterModObjectAttribute(string gameObjectName)
        {
            GameObjectName = gameObjectName;
        }
    }
}
