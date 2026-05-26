using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks a MonoBehaviour class as a mod-created singleton GameObject.
    ///     AttributeScanner.GetModObjectNames() returns all annotated names
    ///     so HotReloadHelper can detect orphaned objects.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RegisterModObjectAttribute : Attribute
    {
        public RegisterModObjectAttribute(string gameObjectName)
        {
            GameObjectName = gameObjectName;
        }

        /// <summary>The GameObject name used at runtime.</summary>
        public string GameObjectName { get; }
    }
}