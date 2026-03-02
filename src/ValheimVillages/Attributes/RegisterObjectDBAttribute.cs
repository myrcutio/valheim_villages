using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    /// Marks a static method for ObjectDB registration. The method must be
    /// <c>static void Method(ObjectDB db)</c>.
    /// Called by AttributeScanner during ObjectDB.Awake and hot-reload.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RegisterObjectDBAttribute : Attribute { }
}
