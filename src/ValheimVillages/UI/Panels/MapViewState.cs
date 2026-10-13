using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Zoom and pan for the villager panel's map, held as process state rather than threaded
    ///     through the render call.
    ///
    ///     <para>Framing is decided in exactly one place — <see cref="PatrolMapRenderer" />'s
    ///     ComputeBounds — and every pin is normalised through the frame that method produced
    ///     (see <see cref="MapFrame" />). So the view only has to be readable from there for the
    ///     whole map, texture and pins alike, to follow it. Passing it down instead would mean a
    ///     new parameter on RenderForTask, GetDetail and TabDetailDataUI carrying a value that
    ///     only the renderer ever reads.</para>
    ///
    ///     <para>The map is re-rasterised at the new bounds rather than magnified: the texture is
    ///     <see cref="FilterMode.Point" />, so scaling the image would just go blocky, whereas
    ///     redrawing at tighter bounds genuinely resolves more detail out of the lookup cells.</para>
    /// </summary>
    internal static class MapViewState
    {
        /// <summary>Loosest view: the auto-fitted frame, exactly as it was before zoom existed.</summary>
        public const float MinZoom = 1f;

        /// <summary>
        ///     Tightest view. Past about 6x, a 256px texture is spreading a few metres across the
        ///     whole panel and the lookup cells read as featureless blocks.
        /// </summary>
        public const float MaxZoom = 6f;

        /// <summary>Zoom multiplier per wheel notch.</summary>
        public const float ZoomStep = 1.25f;

        private static float s_zoom = MinZoom;
        private static Vector2 s_centre;
        private static bool s_hasCentre;
        private static string s_key;
        private static bool s_dirty;

        // The frame the last render actually used. The scroll and drag handlers work in pointer
        // space, so this is what lets them answer "which world point is under the cursor" —
        // without it, cursor-anchored zoom has no anchor.
        private static Vector2 s_frameMin;
        private static float s_frameSize;
        private static bool s_hasFrame;

        /// <summary>Current zoom, as a multiple of the auto-fitted frame.</summary>
        public static float Zoom => s_zoom;

        /// <summary>Side length, in world metres, of the frame the last render used.</summary>
        public static float FrameSize => s_frameSize;

        /// <summary>
        ///     True once the player has moved the view off its auto-fitted default. Pin culling
        ///     keys off this so the default map keeps its existing edge-clamp behaviour.
        /// </summary>
        public static bool Active => s_zoom > MinZoom + 0.0001f || s_hasCentre;

        /// <summary>
        ///     Apply the view to an auto-fitted frame. Called at the tail of ComputeBounds, with
        ///     the square, padded frame the map would otherwise have used.
        /// </summary>
        public static void Apply(ref Vector2 min, ref Vector2 max)
        {
            var fitMin = min;
            var fitMax = max;

            // Record the fitted frame even at default zoom: the first wheel notch needs
            // something to anchor and clamp against.
            if (!Active)
            {
                RecordFrame(fitMin, fitMax.x - fitMin.x);
                return;
            }

            // Square by the time it reaches us, so one half-extent covers both axes.
            var half = (fitMax.x - fitMin.x) * 0.5f / s_zoom;
            var centre = s_hasCentre ? s_centre : (fitMin + fitMax) * 0.5f;

            // Hold the view inside the fitted extent. Panning off into unmapped space reads as
            // the map having broken rather than as having been scrolled.
            centre.x = Mathf.Clamp(centre.x, fitMin.x + half, fitMax.x - half);
            centre.y = Mathf.Clamp(centre.y, fitMin.y + half, fitMax.y - half);

            s_centre = centre;
            s_hasCentre = true;

            min = new Vector2(centre.x - half, centre.y - half);
            max = new Vector2(centre.x + half, centre.y + half);
            RecordFrame(min, half * 2f);
        }

        /// <summary>
        ///     Zoom by <paramref name="factor" /> while holding <paramref name="worldAnchor" />
        ///     still under the pointer.
        /// </summary>
        public static void ZoomBy(float factor, Vector2 worldAnchor)
        {
            if (!s_hasFrame) return;

            var previous = s_zoom;
            var next = Mathf.Clamp(previous * factor, MinZoom, MaxZoom);
            if (Mathf.Approximately(next, previous)) return;

            s_zoom = next;

            // Fully zoomed out, drop the centre too, so the map returns to the auto-fit that
            // tracks the village as it grows instead of staying pinned where it was left.
            if (next <= MinZoom + 0.0001f)
            {
                s_hasCentre = false;
                s_dirty = true;
                return;
            }

            // The half-extent scales by previous/next, so to keep the anchor at the same point
            // on screen the centre has to close on it by that same ratio.
            var half = s_frameSize * 0.5f;
            var centre = s_frameMin + new Vector2(half, half);
            s_centre = worldAnchor + (centre - worldAnchor) * (previous / next);
            s_hasCentre = true;
            s_dirty = true;
        }

        /// <summary>
        ///     Drag the map by a world-space delta: the content follows the pointer, so the
        ///     centre moves against it.
        /// </summary>
        public static void PanBy(Vector2 worldDelta)
        {
            if (!s_hasFrame || !Active) return;

            var half = s_frameSize * 0.5f;
            var centre = s_hasCentre ? s_centre : s_frameMin + new Vector2(half, half);
            s_centre = centre - worldDelta;
            s_hasCentre = true;
            s_dirty = true;
        }

        /// <summary>World point at a normalised position within the last rendered frame.</summary>
        public static bool TryWorldAt(Vector2 normalised, out Vector2 world)
        {
            world = Vector2.zero;
            if (!s_hasFrame) return false;

            world = s_frameMin + normalised * s_frameSize;
            return true;
        }

        /// <summary>
        ///     Reset the view when the thing being looked at changes. Without this, opening the
        ///     next villager — or the next row — inherits the previous one's zoom, which looks
        ///     like the map failed to draw.
        /// </summary>
        public static void EnsureKey(string key)
        {
            if (key == s_key) return;

            s_key = key;
            if (!Active) return;

            s_zoom = MinZoom;
            s_hasCentre = false;
            s_dirty = true;
        }

        /// <summary>
        ///     True once if the view moved since the last call. The host polls this per frame, so
        ///     a burst of wheel notches collapses into a single re-render.
        /// </summary>
        public static bool ConsumeDirty()
        {
            if (!s_dirty) return false;

            s_dirty = false;
            return true;
        }

        [RegisterCleanup]
        public static void Reset()
        {
            s_zoom = MinZoom;
            s_centre = Vector2.zero;
            s_hasCentre = false;
            s_key = null;
            s_dirty = false;
            s_frameMin = Vector2.zero;
            s_frameSize = 0f;
            s_hasFrame = false;
        }

        private static void RecordFrame(Vector2 min, float size)
        {
            s_frameMin = min;
            s_frameSize = size;
            s_hasFrame = size > 0f;
        }
    }
}
