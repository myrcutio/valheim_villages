using UnityEngine;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     The framing a map was drawn with: enough to project a world position back onto the
    ///     rendered texture, so interactive overlays land exactly on the dots that were baked
    ///     into it.
    ///
    ///     <para>The renderer owns framing and the caller owns meaning — the caller knows a
    ///     position is "their cot" or "the chest they are walking to", the renderer only knows
    ///     where it put the pixel. Handing the frame back keeps that split instead of pushing
    ///     labels and click targets down into the drawing code.</para>
    /// </summary>
    public sealed class MapFrame
    {
        public Vector2 Min;
        public float WorldW = 1f;
        public float WorldH = 1f;

        /// <summary>
        ///     World position as a 0..1 coordinate within the texture, measured from its
        ///     bottom-left. Clamped, to match the way an off-frame pin is drawn hard against
        ///     the border rather than dropped.
        /// </summary>
        public Vector2 Normalize(Vector3 world)
        {
            return new Vector2(
                Mathf.Clamp01((world.x - Min.x) / WorldW),
                Mathf.Clamp01((world.z - Min.y) / WorldH));
        }

        /// <summary>False when the position had to be clamped to be shown at all.</summary>
        public bool Contains(Vector3 world)
        {
            return world.x >= Min.x && world.x <= Min.x + WorldW &&
                   world.z >= Min.y && world.z <= Min.y + WorldH;
        }
    }

    /// <summary>A rendered map plus the interactive description of what is on it.</summary>
    public sealed class MapRender
    {
        public Texture2D Texture;
        public System.Collections.Generic.List<MapPin> Pins = new();
    }

    /// <summary>
    ///     One point of interest on a village map: where it is, what to call it, and which row
    ///     to open when the player clicks it.
    ///
    ///     <para>Pins are drawn INTO the map texture by the renderer; this is the parallel
    ///     description used to lay transparent hit targets over those dots. Keeping the two
    ///     separate means the picture is unchanged by making it interactive.</para>
    /// </summary>
    public readonly struct MapPin
    {
        public MapPin(Vector3 world, Vector2 normalized, string label,
            string targetTab = null, int targetRow = -1, bool offFrame = false,
            Sprite icon = null, float rotation = 0f, Sprite backdrop = null,
            string shortLabel = null, string orderItem = null, string orderStation = null)
        {
            World = world;
            Normalized = normalized;
            Label = label;
            TargetTab = targetTab;
            TargetRow = targetRow;
            OffFrame = offFrame;
            Icon = icon;
            Rotation = rotation;
            Backdrop = backdrop;
            ShortLabel = shortLabel;
            OrderItem = orderItem;
            OrderStation = orderStation;
        }

        /// <summary>
        ///     The work order this pin stands for, as the item it produces. Set means clicking
        ///     opens that order's settings panel instead of navigating to a row.
        ///
        ///     <para>Stored as identity rather than a callback so a MapPin stays plain data —
        ///     the pins are rebuilt every refresh and compared by value to decide whether the
        ///     overlay needs rebuilding, which a captured delegate would quietly defeat.</para>
        /// </summary>
        public string OrderItem { get; }

        /// <summary>Station the order belongs to, to disambiguate within a chest.</summary>
        public string OrderStation { get; }

        /// <summary>True when clicking should open the order editor rather than a row.</summary>
        public bool OpensOrder => !string.IsNullOrEmpty(OrderItem);

        /// <summary>
        ///     One or two words shown on a button chip beside the pin ("Farmer", "Cot",
        ///     "Task"). Null for no chip.
        ///
        ///     <para>Separate from <see cref="Label" />, which is the hover text and can afford
        ///     to be a sentence. A chip is read at a glance while scanning the map, so it names
        ///     the KIND of thing rather than describing the instance.</para>
        /// </summary>
        public string ShortLabel { get; }

        /// <summary>
        ///     Crest drawn BEHIND <see cref="Icon" />, or null for a bare icon.
        ///
        ///     <para>Villagers get one so a person is distinguishable at a glance from the
        ///     things on the map — their station icon is a tool, and a hammer pin and a
        ///     hammer-icon building read identically at pin size without it.</para>
        /// </summary>
        public Sprite Backdrop { get; }

        /// <summary>
        ///     Sprite to draw at this pin, or null to leave the dot the renderer baked.
        ///
        ///     <para>Drawn by the overlay rather than composited into the texture: the sprites
        ///     are the game's own authored icons, which are not CPU-readable, so compositing
        ///     them would mean a RenderTexture round trip per pin per refresh. As a UI Image
        ///     they also stay crisp at any panel size instead of being resampled into a 256px
        ///     map.</para>
        /// </summary>
        public Sprite Icon { get; }

        /// <summary>Z rotation in degrees — the player arrow points where the player faces.</summary>
        public float Rotation { get; }

        public Vector3 World { get; }

        /// <summary>Position within the map texture, 0..1 from the bottom-left.</summary>
        public Vector2 Normalized { get; }

        /// <summary>Hover text. Always present — a pin the player cannot name is noise.</summary>
        public string Label { get; }

        /// <summary><see cref="Core.ITabContent{T}.TabName" /> to open on click, or null.</summary>
        public string TargetTab { get; }

        /// <summary>Row to select in that tab, or -1.</summary>
        public int TargetRow { get; }

        /// <summary>True when this pin sits outside the frame and was clamped to the border.</summary>
        public bool OffFrame { get; }

        public bool Clickable => !string.IsNullOrEmpty(TargetTab) && TargetRow >= 0;
    }
}
