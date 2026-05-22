using System.Collections.Generic;
using UnityEngine;


namespace ValheimVillages.Villager
{
    /// <summary>
    /// Lightweight replacement for Valheim's NpcTalk.
    /// Handles greet/goodbye/random talk via Chat.SetNpcText.
    /// </summary>
    public class VillagerTalk : MonoBehaviour
    {
        public List<string> randomTalk = new();
        public List<string> randomGreets = new();
        public List<string> randomGoodbye = new();

        public float maxRange = 20f;
        public float greetRange = 10f;
        public float byeRange = 15f;
        public float offset = 2.2f;
        public float hideDialogDelay = 12f;
        public float randomTalkInterval = 12f;
        public float randomTalkChance = 0.3f;
        public float minTalkInterval = 24f;

        private static float s_lastTalkTime;

        private Player m_targetPlayer;
        private bool m_didGreet;
        private bool m_didGoodbye;
        private float m_lastTargetUpdate;
        private float m_nextRandomTalk;
        private void Start()
        {
            m_nextRandomTalk = Time.time + Random.Range(4f, randomTalkInterval);
        }

        private void Update()
        {
            UpdateTarget();

            if (m_targetPlayer == null) return;

            float dist = Vector3.Distance(
                m_targetPlayer.transform.position, transform.position);

            if (!m_didGreet && dist < greetRange)
            {
                m_didGreet = true;
                m_didGoodbye = false;
                QueueSay(randomGreets);
            }

            if (m_didGreet && !m_didGoodbye && dist > byeRange)
            {
                m_didGoodbye = true;
                QueueSay(randomGoodbye);
            }

            if (Time.time >= m_nextRandomTalk)
            {
                m_nextRandomTalk = Time.time + randomTalkInterval;
                if (Random.value < randomTalkChance)
                    QueueSay(randomTalk);
            }
        }

        private void UpdateTarget()
        {
            if (Time.time - m_lastTargetUpdate < 1f) return;
            m_lastTargetUpdate = Time.time;

            m_targetPlayer = null;
            var closest = Player.GetClosestPlayer(transform.position, maxRange);
            if (closest == null) return;

            m_targetPlayer = closest;
        }

        private void QueueSay(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;
            if (Time.time - s_lastTalkTime < minTalkInterval) return;

            string text = lines[Random.Range(0, lines.Count)];
            Say(text);
        }

        private void Say(string text)
        {
            s_lastTalkTime = Time.time;
            Chat.instance?.SetNpcText(
                gameObject,
                Vector3.up * offset,
                20f,
                hideDialogDelay,
                "",
                text,
                false);
        }
    }
}
