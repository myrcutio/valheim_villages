using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Click handling for a map pin: left selects the pin's row, right opens the work
    ///     order's editor.
    ///
    ///     <para>Not a <see cref="UnityEngine.UI.Button" />, because Button only ever reports a
    ///     LEFT click — it has no concept of which mouse button was used. Splitting the two
    ///     actions therefore needs the raw pointer event.</para>
    ///
    ///     <para>Selecting the row is the left-click action because it is the safe, reversible
    ///     one: it changes what you are looking at. Editing an order changes the village's
    ///     configuration, so it sits behind the deliberate button.</para>
    /// </summary>
    internal class MapPinClick : MonoBehaviour, IPointerClickHandler
    {
        private Action m_onLeft;
        private Action m_onRight;

        public void Bind(Action onLeft, Action onRight)
        {
            m_onLeft = onLeft;
            m_onRight = onRight;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null) return;

            if (eventData.button == PointerEventData.InputButton.Right) m_onRight?.Invoke();
            else if (eventData.button == PointerEventData.InputButton.Left) m_onLeft?.Invoke();
        }
    }
}
