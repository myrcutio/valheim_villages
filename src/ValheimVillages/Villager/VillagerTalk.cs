using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Lightweight replacement for Valheim's NpcTalk.
    ///     Handles greet/goodbye/random talk via Chat.SetNpcText.
    ///
    ///     <para><b>A villager only talks when it has nothing else to do.</b> Chatter while
    ///     hauling a log across the village reads as noise, and a line delivered mid-stride
    ///     reads as nobody talking to anybody — so every line, greetings included, waits for
    ///     <see cref="BehaviorState.Idle" />. A villager at work is busy; that is the point of
    ///     it being at work.</para>
    ///
    ///     <para>And it stops to say it: <see cref="VillagerAI.HoldStill" /> holds the villager
    ///     in place for a few seconds so the line lands while it is standing there, then the
    ///     lease lapses on its own.</para>
    /// </summary>
    public class VillagerTalk : MonoBehaviour
    {
        /// <summary>
        ///     Shared floor across EVERY villager, so a crowded village does not turn into a
        ///     chorus — one of them may speak a minute.
        /// </summary>
        private static float s_lastTalkTime;

        public List<string> randomTalk = new();
        public List<string> randomGreets = new();
        public List<string> randomGoodbye = new();

        public float maxRange = 20f;
        public float greetRange = 10f;
        public float byeRange = 15f;
        public float offset = 2.2f;
        public float hideDialogDelay = 12f;

        /// <summary>
        ///     How often an idle villager CONSIDERS speaking, and how likely it is to. Together
        ///     these give roughly one line every two minutes per villager — deliberately sparse:
        ///     at the old 12s/30% a villager standing near you spoke about every 40 seconds,
        ///     which wore out its handful of lines within a minute or two.
        /// </summary>
        public float randomTalkInterval = 45f;

        public float randomTalkChance = 0.35f;

        /// <summary>Shared quiet period after ANY villager speaks.</summary>
        public float minTalkInterval = 60f;

        /// <summary>
        ///     How long the speaker stands still for. Shorter than
        ///     <see cref="hideDialogDelay" /> on purpose: the pause is there to make the line
        ///     look spoken rather than to freeze the villager for as long as the bubble hangs
        ///     around.
        /// </summary>
        public float talkHoldSeconds = 5f;

        private VillagerAI m_ai;
        private bool m_didGoodbye;
        private bool m_didGreet;
        private float m_lastTargetUpdate;
        private float m_nextRandomTalk;

        private Player m_targetPlayer;

        private void Start()
        {
            m_nextRandomTalk = Time.time + Random.Range(randomTalkInterval * 0.5f, randomTalkInterval);
        }

        private void Update()
        {
            UpdateTarget();

            if (m_targetPlayer == null) return;

            var dist = Vector3.Distance(
                m_targetPlayer.transform.position, transform.position);

            // Greet/goodbye state still tracks the player while the villager is busy — it is
            // the SPEAKING that waits for idle, not the noticing. Otherwise a villager that was
            // working when you arrived would greet you on your way out.
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

            // The timer advances whether or not the line gets out, so a villager that was
            // busy at its turn simply misses it rather than banking a speech for the moment
            // it finally stands still.
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

        /// <summary>
        ///     Idle and unoccupied. Resolved lazily because <see cref="VillagerAI" /> is added
        ///     by <c>Villager.Awake</c>, which can run after this component's Start.
        /// </summary>
        private bool CanSpeak()
        {
            if (m_ai == null) m_ai = GetComponent<VillagerAI>();
            if (m_ai == null) return false;

            // Not while paused: either someone has the villager stopped for a menu, or it is
            // still standing through the line it just said.
            return m_ai.CurrentState == BehaviorState.Idle && !m_ai.IsPaused;
        }

        private void QueueSay(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;
            if (Time.time - s_lastTalkTime < minTalkInterval) return;
            if (!CanSpeak()) return;

            var text = lines[Random.Range(0, lines.Count)];
            Say(text);
        }

        private void Say(string text)
        {
            s_lastTalkTime = Time.time;
            m_ai?.HoldStill(talkHoldSeconds);

            if (Settings.LogSettings.VerboseTalk)
                Plugin.Log?.LogInfo(
                    $"[Talk:{m_ai?.NpcName ?? name}] \"{text}\" (state={m_ai?.CurrentState}, " +
                    $"holding {talkHoldSeconds:F0}s)");

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
