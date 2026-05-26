using ValheimVillages.Schemas;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Interface for composable NPC behaviors. Each behavior has a tag (e.g. "patrol", "craft"),
    ///     a priority (higher wins when multiple behaviors want control), and lifecycle hooks
    ///     called by VillagerAI's dispatch loop.
    ///     Save/Load are defined in the mod assembly via IBehaviorPersistence (ZDO-dependent).
    /// </summary>
    public interface IBehavior
    {
        /// <summary>Behavior tag matching the NPC definition's behaviors array (e.g. "patrol", "craft").</summary>
        string Tag { get; }

        /// <summary>
        ///     Priority for dispatch. Higher priority behaviors override lower ones.
        ///     alarm=100, craft/farm=50, patrol=30, explore=20.
        /// </summary>
        int Priority { get; }

        /// <summary>Return true when this behavior wants to take control of the NPC.</summary>
        bool WantsControl(BehaviorContext ctx);

        /// <summary>Called each behavior tick when this behavior has control.</summary>
        void Update(float dt);

        /// <summary>Called when the NPC arrives at its movement target.</summary>
        void OnArrival(float dt);

        /// <summary>Get a human-readable status string for the UI hover text.</summary>
        string GetStatusText();
    }
}