using UnityEngine;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     A point of interest a TAB wants shown on a villager's map, before the map layer turns
    ///     it into a positioned <see cref="MapPin" />.
    ///
    ///     <para>A named struct rather than the tuple this used to be: it reached seven
    ///     positional fields, where every addition silently reordered every call site and the
    ///     only thing catching it was the compiler complaining about arity. Named fields also
    ///     document what a pin can carry, which a <c>(Vector3, Color, string, Sprite, int,
    ///     string, string)</c> very much does not.</para>
    /// </summary>
    public readonly struct TaskPin
    {
        public TaskPin(
            Vector3 position, Color color, string label,
            Sprite icon = null, int row = -1,
            string orderItem = null, string orderStation = null, bool landmark = false)
        {
            Position = position;
            Color = color;
            Label = label;
            Icon = icon;
            Row = row;
            OrderItem = orderItem;
            OrderStation = orderStation;
            Landmark = landmark;
        }

        /// <summary>
        ///     Context only — never clickable, whatever else it carries.
        ///
        ///     <para>Explicit rather than inferred from the label. Non-interactive pins were
        ///     recognised by matching against "Gate"/"You"/"Patrol route", which cannot
        ///     describe an ingredient chest or a station and would have silently made both
        ///     clickable.</para>
        /// </summary>
        public bool Landmark { get; }

        public Vector3 Position { get; }

        /// <summary>Colour of the dot drawn when <see cref="Icon" /> is null.</summary>
        public Color Color { get; }

        /// <summary>Hover text, and the chip text for a task.</summary>
        public string Label { get; }

        /// <summary>Sprite to draw instead of a dot, or null.</summary>
        public Sprite Icon { get; }

        /// <summary>Tab row to open on click, or -1 for "the row this map belongs to".</summary>
        public int Row { get; }

        /// <summary>Work order this pin stands for; set makes the click open its editor.</summary>
        public string OrderItem { get; }

        public string OrderStation { get; }
    }
}
