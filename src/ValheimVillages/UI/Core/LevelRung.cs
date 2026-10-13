namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     One rung of a villager's level ladder, as DATA rather than a line of text.
    ///
    ///     <para>It used to be a formatted string with a "✓" in front. A reached rung now wears
    ///     the same shield badge the villager wears on the map and in the panel header, and a
    ///     digit cannot be drawn inside an inline TMP sprite — so the rung has to be a real row
    ///     the renderer can put an image on, and the tab has to hand over the parts rather than
    ///     a pre-rendered sentence.</para>
    /// </summary>
    public readonly struct LevelRung
    {
        public LevelRung(int level, string text, bool reached, bool isNext)
        {
            Level = level;
            Text = text;
            Reached = reached;
            IsNext = isNext;
        }

        /// <summary>The level this rung grants, shown on the badge when reached.</summary>
        public int Level { get; }

        /// <summary>What the rung asks for, in buildable terms.</summary>
        public string Text { get; }

        /// <summary>Already earned — draws the badge.</summary>
        public bool Reached { get; }

        /// <summary>The next one to earn — the thing the player can act on now.</summary>
        public bool IsNext { get; }
    }
}
