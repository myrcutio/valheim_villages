namespace ValheimVillages.Behaviors
{
    /// <summary>
    /// Extension of the Core IBehavior interface adding ZDO persistence methods.
    /// Behavior implementations should implement this interface to support save/load.
    /// </summary>
    public interface IBehaviorPersistence
    {
        /// <summary>Save behavior-specific state to ZDO.</summary>
        void Save(ZDO zdo);

        /// <summary>Load behavior-specific state from ZDO.</summary>
        void Load(ZDO zdo);
    }
}
