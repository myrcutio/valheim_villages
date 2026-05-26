using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Villager.AI.Pathfinding;
using VillagerAI = ValheimVillages.Villager.AI.VillagerAI;
using VillagerMemory = ValheimVillages.Villager.AI.Memory.VillagerMemory;

namespace ValheimVillages.UI.Interaction
{
    /// <summary>
    ///     MonoBehaviour bridge that exposes VillagerAI to the UI layer.
    ///     Attached to the NPC GameObject, it resolves the VillagerAI instance
    ///     from VillagerAIManager using the stored unique ID.
    /// </summary>
    public class VillagerBehaviorBridge : MonoBehaviour
    {
        /// <summary>
        ///     Resolve the VillagerAI instance (cached with periodic refresh).
        /// </summary>
        public Villager.Villager villagerInstance;

        private VillagerAI m_cachedAI;
        private float m_lastLookupTime;

        /// <summary>Called after spawn/restore to set the unique ID (for compatibility; resolution uses villagerInstance).</summary>
        public void Initialize(string uniqueId)
        {
        }

        #region Public API (used by VillagerInteract)

        /// <summary>
        ///     Access to the NPC's memory.
        /// </summary>
        public VillagerMemory Memory => villagerInstance.villagerAI.GetMemory();

        /// <summary>
        ///     Current behavior state.
        /// </summary>
        public BehaviorState CurrentState => villagerInstance.villagerAI?.CurrentState ?? BehaviorState.Idle;

        /// <summary>
        ///     Current target position.
        /// </summary>
        public VillagerWaypoint CurrentWaypoint => villagerInstance.villagerAI.GetCurrentWaypoint();

        /// <summary>
        ///     Unique villager ID (for activity log / debug).
        /// </summary>
        public string UniqueId => villagerInstance.uid;

        /// <summary>VillagerAI instance (for UI and tests).</summary>
        public VillagerAI AI => villagerInstance?.villagerAI;

        /// <summary>Villager type string from JSON definition (for filtering/tests).</summary>
        public string VillagerType => villagerInstance?.villagerType ?? "";

        /// <summary>
        ///     Pause/unpause the villager AI.
        /// </summary>
        public void SetPaused(bool paused)
        {
            villagerInstance.villagerAI?.SetPaused(paused);
        }

        #endregion
    }
}