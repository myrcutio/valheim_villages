using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Hover feedback for a clickable map pin: the ICON grows and the label's button plate
    ///     lights up. The label itself never changes size.
    ///
    ///     <para>Scaling only the icon is deliberate. Scaling the whole marker dragged the label
    ///     with it, which made the text blur mid-animation and shoved it into its neighbours;
    ///     the label is there to be read while scanning, so it has to hold still.</para>
    ///
    ///     <para>Driven from Update rather than a coroutine so it survives the object being
    ///     disabled and re-enabled with the panel, and it only ever touches
    ///     <c>localScale</c> — the player arrow keeps its heading in <c>localRotation</c>.</para>
    /// </summary>
    internal class MapPinHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>How much bigger a hovered icon gets.</summary>
        private const float HoverScale = 1.35f;

        /// <summary>Approach rate; ~12 lands the growth inside a tenth of a second.</summary>
        private const float Speed = 12f;

        private RectTransform m_icon;
        private Image m_chip;
        private Sprite m_chipNormal;
        private Sprite m_chipHover;
        private float m_target = 1f;

        /// <summary>Bind the parts this pin animates. Either may be null.</summary>
        public void Bind(RectTransform icon, Image chip)
        {
            m_icon = icon;
            m_chip = chip;
            m_chipNormal = chip != null ? chip.sprite : null;
            m_chipHover = UiSprites.Find("button_highlight");
        }

        private void OnEnable()
        {
            // Re-shown with the panel: start from rest, since the cursor is not necessarily
            // still over us.
            m_target = 1f;
            if (m_icon != null) m_icon.localScale = Vector3.one;
            if (m_chip != null && m_chipNormal != null) m_chip.sprite = m_chipNormal;
        }

        private void Update()
        {
            if (m_icon == null) return;

            var current = m_icon.localScale.x;
            if (Mathf.Approximately(current, m_target)) return;

            var next = Mathf.MoveTowards(current, m_target, Speed * Time.unscaledDeltaTime);
            m_icon.localScale = new Vector3(next, next, 1f);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            m_target = HoverScale;
            if (m_chip != null && m_chipHover != null) m_chip.sprite = m_chipHover;

            // Come to the front: on a dense map the pin you are pointing at is the one you
            // want to see whole, not the one that happens to have been added last.
            transform.SetAsLastSibling();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            m_target = 1f;
            if (m_chip != null && m_chipNormal != null) m_chip.sprite = m_chipNormal;
        }
    }
}
