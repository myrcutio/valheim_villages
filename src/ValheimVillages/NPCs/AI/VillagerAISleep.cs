using System.Reflection;
using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Sleep animation and movement test delegation for VillagerAI.
    /// Extracted to keep the core AI loop focused on behavior/movement.
    /// </summary>
    public partial class VillagerAI
    {
        // Sleep animation state
        private bool m_isSleepAnimationActive;
        private float m_savedViewRange;
        private float m_savedHearRange;
        private static readonly MethodInfo s_sleepMethod =
            typeof(MonsterAI).GetMethod("Sleep", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo s_wakeupMethod =
            typeof(MonsterAI).GetMethod("Wakeup", BindingFlags.NonPublic | BindingFlags.Instance);

        // Movement test (null when no test is running)
        private VillagerMovementTest m_activeTest;

        public bool IsSleepAnimationActive => m_isSleepAnimationActive;

        #region Sleep Animation

        /// <summary>
        /// Trigger the MonsterAI sleep animation (NPC lays down, ZDO synced).
        /// Safe to call multiple times; no-ops if already sleeping.
        /// </summary>
        public void EnterSleepAnimation()
        {
            if (m_isSleepAnimationActive) return;
            m_isSleepAnimationActive = true;

            // Zero out detection ranges so sleeping NPCs don't react to players/enemies
            m_savedViewRange = m_instance.m_viewRange;
            m_savedHearRange = m_instance.m_hearRange;
            m_instance.m_viewRange = 0f;
            m_instance.m_hearRange = 0f;

            try
            {
                s_sleepMethod?.Invoke(m_instance, null);
                Plugin.Log?.LogDebug($"[AI:{NpcName}] Entered sleep animation (detection disabled)");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[AI:{NpcName}] Failed to enter sleep animation: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear the sleep animation (NPC stands up, ZDO synced).
        /// Safe to call multiple times; no-ops if not sleeping.
        /// </summary>
        public void ExitSleepAnimation()
        {
            if (!m_isSleepAnimationActive) return;
            m_isSleepAnimationActive = false;

            // Restore detection ranges on waking
            m_instance.m_viewRange = m_savedViewRange;
            m_instance.m_hearRange = m_savedHearRange;

            try
            {
                s_wakeupMethod?.Invoke(m_instance, null);
                Plugin.Log?.LogDebug($"[AI:{NpcName}] Exited sleep animation (detection restored)");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[AI:{NpcName}] Failed to exit sleep animation: {ex.Message}");
            }
        }

        #endregion

        #region Movement Tests

        public bool IsMovementTestActive => m_activeTest != null && m_activeTest.IsActive;

        /// <summary>
        /// Start a multi-waypoint movement test.
        /// </summary>
        public bool StartMovementTest()
        {
            if (IsMovementTestActive) return false;
            m_activeTest = new VillagerMovementTest(this);
            return m_activeTest.Start();
        }

        /// <summary>
        /// Cancel a running movement test.
        /// </summary>
        public void CancelMovementTest()
        {
            m_activeTest?.Cancel();
            m_activeTest = null;
        }

        /// <summary>
        /// Get current test progress info for the UI.
        /// </summary>
        public (int completed, int total, string label) GetTestProgress()
        {
            if (m_activeTest == null) return (0, 0, "");
            return (m_activeTest.WaypointsCompleted, m_activeTest.WaypointsTotal, m_activeTest.CurrentLabel);
        }

        #endregion
    }
}
