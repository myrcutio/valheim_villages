using UnityEngine;

namespace ValheimVillages.UI.Alerts
{
    /// <summary>
    ///     The floating "!" over a villager who has something to tell the player, plus the line
    ///     it says when the player comes near.
    ///
    ///     <para>The badge is a billboarded child sprite ("VV_AlertMarker" — the VV_ prefix is
    ///     what lets the hot-reload sweep reclaim it). The spoken line goes through
    ///     <c>Chat.SetNpcText</c>, the same channel <see cref="Villager.VillagerTalk" /> uses, so
    ///     it renders exactly like every other villager remark instead of inventing a second
    ///     speech UI.</para>
    ///
    ///     <para>Speech is throttled and only fires when the player is close and looking at the
    ///     village, so a standing condition (storage full for ten minutes) does not spam chat —
    ///     the badge carries the persistent signal, the line is the occasional reminder.</para>
    /// </summary>
    public class VillagerAlertMarker : MonoBehaviour
    {
        private const string ChildName = "VV_AlertMarker";

        /// <summary>Height above the villager's origin to float the badge.</summary>
        private const float HeightOffset = 2.35f;

        /// <summary>World size of the badge quad.</summary>
        private const float BadgeScale = 0.45f;

        /// <summary>How close the player must be before the villager speaks the line.</summary>
        private const float SpeakRange = 12f;

        /// <summary>Minimum gap between spoken reminders for the same villager.</summary>
        private const float SpeakInterval = 45f;

        /// <summary>How long the spoken line stays on screen.</summary>
        private const float SpeakDuration = 6f;

        /// <summary>Gentle bob so the badge reads as a UI hint rather than set dressing.</summary>
        private const float BobAmplitude = 0.08f;

        private const float BobSpeed = 2.2f;

        private GameObject m_badge;
        private float m_nextSpeakTime;
        private string m_message;

        /// <summary>The line this villager will say. Empty clears the alert entirely.</summary>
        public string Message => m_message;

        /// <summary>Raise (or update) the alert. Re-arms the spoken line when the text changes.</summary>
        public void Set(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                Clear();
                return;
            }

            if (m_message != message)
            {
                m_message = message;
                m_nextSpeakTime = 0f; // a NEW problem should be voiced promptly

                // Log raise/change (not every evaluation) so the alert is verifiable from the
                // log. Without this the badge is rendered-only state with no read-out, which
                // makes "is it actually firing?" unanswerable outside the game window.
                Plugin.Log?.LogInfo($"[VillageAlert] {name}: \"{message}\"");
            }

            EnsureBadge(true);
        }

        /// <summary>Drop the alert — condition resolved.</summary>
        public void Clear()
        {
            if (!string.IsNullOrEmpty(m_message))
                Plugin.Log?.LogInfo($"[VillageAlert] {name}: cleared");
            m_message = null;
            EnsureBadge(false);
        }

        private void LateUpdate()
        {
            if (string.IsNullOrEmpty(m_message))
            {
                EnsureBadge(false);
                return;
            }

            if (m_badge == null) return;

            // Billboard to the camera and bob. LateUpdate so it tracks the camera after it has
            // finished moving for the frame.
            var cam = Camera.main;
            if (cam != null)
                m_badge.transform.rotation = cam.transform.rotation;

            var bob = Mathf.Sin(Time.time * BobSpeed) * BobAmplitude;
            m_badge.transform.localPosition = new Vector3(0f, HeightOffset + bob, 0f);

            TrySpeak();
        }

        private void TrySpeak()
        {
            if (Time.time < m_nextSpeakTime) return;

            var player = Player.m_localPlayer;
            if (player == null) return;
            if (Vector3.Distance(player.transform.position, transform.position) > SpeakRange) return;

            m_nextSpeakTime = Time.time + SpeakInterval;
            Chat.instance?.SetNpcText(
                gameObject,
                Vector3.up * HeightOffset,
                SpeakRange,
                SpeakDuration,
                "",
                m_message,
                false);
        }

        private void EnsureBadge(bool wanted)
        {
            if (!wanted)
            {
                if (m_badge != null) Destroy(m_badge);
                m_badge = null;
                return;
            }

            if (m_badge != null) return;

            m_badge = new GameObject(ChildName);
            m_badge.transform.SetParent(transform, false);
            m_badge.transform.localPosition = new Vector3(0f, HeightOffset, 0f);
            m_badge.transform.localScale = Vector3.one * BadgeScale;

            var sr = m_badge.AddComponent<SpriteRenderer>();
            sr.sprite = AlertArt.Sprite;
            // Draw over the villager rather than z-fighting with the hat/hair.
            sr.sortingOrder = 100;
        }

        private void OnDestroy()
        {
            if (m_badge != null) Destroy(m_badge);
        }
    }
}
