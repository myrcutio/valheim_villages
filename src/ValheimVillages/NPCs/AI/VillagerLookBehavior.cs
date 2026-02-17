using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Restricts villager look-at so they only look at players who are close
    /// and only for a short time. Prevents staring at distant players.
    /// </summary>
    public class VillagerLookBehavior : MonoBehaviour
    {
        private Character m_character;
        private BaseAI m_baseAi;
        private float m_lookingAtPlayerSince = -1f;
        private float m_lookAwayUntil = -1f;

        private const float ClosePlayerRadius = 6f;
        private const float MaxLookAtPlayerDuration = 3f;
        private const float LookAwayDuration = 2.5f;
        private const float LookTransitionTime = 0.4f;

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_baseAi = GetComponent<BaseAI>();
        }

        private void LateUpdate()
        {
            if (m_character == null || m_baseAi == null) return;
            if (m_character.IsDead()) return;

            // Only override look when idle (no active movement target); let pathfinding control look while moving
            var bridge = GetComponent<VillagerBehaviorBridge>();
            if (bridge?.CurrentTarget != null)
                return;

            Player nearest = Player.GetClosestPlayer(transform.position, 25f);
            float dist = nearest != null
                ? Vector3.Distance(transform.position, nearest.transform.position)
                : float.MaxValue;

            // Far player: always look forward so we don't stare at distant players
            if (nearest == null || dist > ClosePlayerRadius)
            {
                m_lookingAtPlayerSince = -1f;
                SetLookForward();
                return;
            }

            // Close player: allow look at player only for a limited time, then look away
            if (Time.time < m_lookAwayUntil)
            {
                SetLookForward();
                return;
            }

            Vector3 toPlayer = (nearest.transform.position + Vector3.up * 1.6f) - transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f) return;
            toPlayer.Normalize();

            Vector3 lookDir = m_character.GetLookDir();
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude < 0.01f) return;
            lookDir.Normalize();

            float dot = Vector3.Dot(lookDir, toPlayer);
            bool isLookingAtPlayer = dot > 0.7f;

            if (isLookingAtPlayer)
            {
                if (m_lookingAtPlayerSince < 0f)
                    m_lookingAtPlayerSince = Time.time;
                float duration = Time.time - m_lookingAtPlayerSince;
                if (duration >= MaxLookAtPlayerDuration)
                {
                    m_lookAwayUntil = Time.time + LookAwayDuration;
                    m_lookingAtPlayerSince = -1f;
                    SetLookForward();
                }
            }
            else
            {
                m_lookingAtPlayerSince = -1f;
            }
        }

        private void SetLookForward()
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            else fwd.Normalize();
            m_character.SetLookDir(fwd, LookTransitionTime);
        }
    }
}
