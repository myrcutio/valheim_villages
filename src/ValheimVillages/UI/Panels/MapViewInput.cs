using UnityEngine;
using UnityEngine.EventSystems;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Wheel-to-zoom and drag-to-pan for the villager panel's map.
    ///
    ///     <para>Lives on the map image itself, which is the parent of every pin hit target, so
    ///     both events reach it: Unity dispatches scroll and drag to the first ancestor that
    ///     handles them, and the pins handle neither. A drag also clears the pointer's
    ///     eligible-for-click flag, so dragging across a pin does not also select its row.</para>
    /// </summary>
    internal class MapViewInput : MonoBehaviour, IScrollHandler, IDragHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            if (eventData == null) return;

            var notches = eventData.scrollDelta.y;
            if (Mathf.Approximately(notches, 0f)) return;

            // Anchor on the world point under the cursor so you can zoom into a corner of the
            // village without having to pan back to it afterwards.
            if (!TryWorldUnderPointer(eventData, out var anchor)) return;

            MapViewState.ZoomBy(
                notches > 0f ? MapViewState.ZoomStep : 1f / MapViewState.ZoomStep, anchor);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null) return;

            var rt = transform as RectTransform;
            if (rt == null) return;

            var rect = rt.rect;
            if (rect.width <= 0f || rect.height <= 0f) return;

            // Difference two local-space points rather than scaling eventData.delta directly:
            // delta is in screen pixels and the rect is in canvas units, which differ by the
            // canvas scale factor. Differencing cancels both that and the pivot.
            if (!TryLocalPoint(eventData, eventData.position, out var now)) return;
            if (!TryLocalPoint(eventData, eventData.position - eventData.delta, out var before))
                return;

            var frame = MapViewState.FrameSize;
            MapViewState.PanBy(new Vector2(
                (now.x - before.x) / rect.width * frame,
                (now.y - before.y) / rect.height * frame));
        }

        private bool TryWorldUnderPointer(PointerEventData eventData, out Vector2 world)
        {
            world = Vector2.zero;

            var rt = transform as RectTransform;
            if (rt == null) return false;
            if (!TryLocalPoint(eventData, eventData.position, out var local)) return false;

            var rect = rt.rect;
            return MapViewState.TryWorldAt(
                new Vector2(
                    Mathf.InverseLerp(rect.xMin, rect.xMax, local.x),
                    Mathf.InverseLerp(rect.yMin, rect.yMax, local.y)),
                out world);
        }

        private bool TryLocalPoint(
            PointerEventData eventData, Vector2 screenPoint, out Vector2 local)
        {
            local = Vector2.zero;

            var rt = transform as RectTransform;
            if (rt == null) return false;

            // Null for a screen-space-overlay canvas, which is what Valheim's UI uses; the
            // press camera is the one that is set during a drag.
            var cam = eventData.enterEventCamera != null
                ? eventData.enterEventCamera
                : eventData.pressEventCamera;

            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rt, screenPoint, cam, out local);
        }
    }
}
