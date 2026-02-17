using UnityEngine;
using ValheimVillages.NPCs;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// MonoBehaviour bridge that exposes VillagerAI to the UI layer.
    /// Attached to the NPC GameObject, it resolves the VillagerAI instance
    /// from VillagerAIManager using the stored unique ID.
    /// </summary>
    public class VillagerBehaviorBridge : MonoBehaviour
    {
        private string m_uniqueId;
        private VillagerAI m_cachedAI;
        private float m_lastLookupTime;

        /// <summary>
        /// Initialize the bridge with a unique ID.
        /// </summary>
        public void Initialize(string uniqueId)
        {
            m_uniqueId = uniqueId;
        }

        /// <summary>
        /// Resolve the VillagerAI instance (cached with periodic refresh).
        /// </summary>
        public VillagerAI AI
        {
            get
            {
                // Try to resolve unique ID from ZDO if not set
                if (string.IsNullOrEmpty(m_uniqueId))
                {
                    var nview = GetComponent<ZNetView>();
                    if (nview != null && nview.GetZDO() != null)
                        m_uniqueId = nview.GetZDO().GetString("vv_villager_id", "");
                }

                if (string.IsNullOrEmpty(m_uniqueId))
                    return null;

                // Refresh cache every 2 seconds
                if (m_cachedAI == null || Time.time - m_lastLookupTime > 2f)
                {
                    m_lastLookupTime = Time.time;
                    VillagerAIManager.ActiveVillagers.TryGetValue(m_uniqueId, out m_cachedAI);
                }

                return m_cachedAI;
            }
        }

        #region Public API (used by VillagerInteract and VillagerDialogMenu)

        /// <summary>
        /// Get the NPC type from ZDO (e.g. Mountaineer, Guard, etc.).
        /// </summary>
        public NpcType? NpcType
        {
            get
            {
                var nview = GetComponent<ZNetView>();
                if (nview == null || nview.GetZDO() == null) return null;
                int typeInt = nview.GetZDO().GetInt("vv_npc_type", -1);
                if (typeInt < 0) return null;
                return (NpcType)typeInt;
            }
        }

        /// <summary>
        /// Access to the NPC's memory.
        /// </summary>
        public VillagerMemory Memory => AI?.Memory;

        /// <summary>
        /// Current behavior state.
        /// </summary>
        public BehaviorState CurrentState => AI?.CurrentState ?? BehaviorState.Idle;

        /// <summary>
        /// Current target position.
        /// </summary>
        public Vector3? CurrentTarget => AI?.CurrentTarget;

        /// <summary>
        /// Unique villager ID (for activity log / debug).
        /// </summary>
        public string UniqueId => !string.IsNullOrEmpty(m_uniqueId) ? m_uniqueId : (AI?.UniqueId ?? "");

        /// <summary>
        /// Pause/unpause the villager AI.
        /// </summary>
        public void SetPaused(bool paused)
        {
            AI?.SetPaused(paused);
        }

        /// <summary>
        /// Force the villager to travel to a specific location type.
        /// </summary>
        public Vector3? DebugWanderToLocationType(LocationType type)
        {
            return AI?.DebugWanderToLocationType(type);
        }

        /// <summary>
        /// Start a multi-waypoint movement test (bed -> fire -> chair -> origin).
        /// </summary>
        public bool RunMovementTests()
        {
            var ai = AI;
            if (ai == null) return false;
            return ai.StartMovementTest();
        }

        /// <summary>
        /// Cancel a running movement test.
        /// </summary>
        public void CancelMovementTest()
        {
            AI?.CancelMovementTest();
        }

        /// <summary>
        /// Whether a movement test is currently running.
        /// </summary>
        public bool IsTestRunning => AI?.IsMovementTestActive ?? false;

        #endregion
    }
}
