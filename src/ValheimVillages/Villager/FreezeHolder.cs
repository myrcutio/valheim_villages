using UnityEngine;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Holds a villager paused for a dev session, using exactly the mechanism the villager
    ///     menu uses: ask the HOST to pause, and renew every
    ///     <see cref="VillagerPauseRpc.HeartbeatSeconds" /> for as long as the hold is wanted.
    ///
    ///     <para>The pause is a short lease on purpose, so no peer can strand a villager frozen
    ///     forever. A hold is therefore a heartbeat, not one long pause — and it has to go to the
    ///     host, because that is where the villager is simulated; setting the local AI's flag
    ///     does nothing on a dedicated server.</para>
    ///
    ///     <para>This is the same thing <c>VillagerInteract</c> does while its menu is open, with
    ///     a timer in place of the menu. When this component stops running — released, hot
    ///     reloaded, crashed — the lease simply lapses and the villager resumes.</para>
    /// </summary>
    internal class FreezeHolder : MonoBehaviour
    {
        private UI.Interaction.VillagerBehaviorBridge m_bridge;
        private float m_until;
        private float m_nextRenewAt;

        public void Hold(UI.Interaction.VillagerBehaviorBridge bridge, float seconds)
        {
            m_bridge = bridge;
            m_until = Time.time + seconds;
            m_nextRenewAt = 0f;
        }

        /// <summary>Seconds of hold remaining, for reporting.</summary>
        public float Remaining => Mathf.Max(0f, m_until - Time.time);

        public void Release()
        {
            m_bridge?.SetPaused(false);
            Destroy(this);
        }

        private void Update()
        {
            if (m_bridge == null) return;

            if (Time.time >= m_until)
            {
                Release();
                return;
            }

            if (Time.time < m_nextRenewAt) return;
            m_nextRenewAt = Time.time + VillagerPauseRpc.HeartbeatSeconds;
            m_bridge.SetPaused(true);
        }
    }
}
