namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     A tab that wants a particular one of its rows selected when the UI first opens,
    ///     instead of the default first row.
    ///
    ///     <para>Separate from <see cref="IAlertFocusTab{TSubject}" />: an alert is a
    ///     transient "look at this now" that also switches tab, whereas this is the resting
    ///     answer to "what is this thing's current state?" — the assigned villager on a made
    ///     cot, for instance. The row index has to come from the tab because only the tab
    ///     knows its own list layout.</para>
    /// </summary>
    public interface IInitialRowTab<in TSubject>
    {
        /// <summary>
        ///     Index into this tab's <c>GetListItems</c> result to open on, or -1 to take the
        ///     default first row. Called after <c>OnSelected</c>, so the tab's list data is
        ///     already populated.
        /// </summary>
        int FindInitialRow(TSubject subject);
    }
}
