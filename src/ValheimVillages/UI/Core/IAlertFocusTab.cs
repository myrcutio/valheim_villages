namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     A tab that can say which of its own list rows a subject's pending alert is about, so
    ///     <see cref="CraftingTabHostBase{TSubject,TSelf}" /> can open straight onto it.
    ///
    ///     <para>The row index has to come from the tab because only the tab knows its list
    ///     layout — the Tasks list is a current-activity row, then the blocked entries, then any
    ///     registered ability panels, and that shape is free to change without the host (shared
    ///     with the Village Registry UI) knowing anything about it.</para>
    /// </summary>
    public interface IAlertFocusTab<in TSubject>
    {
        /// <summary>
        ///     Index into this tab's <c>GetListItems</c> result for the row explaining the
        ///     subject's active alert, or -1 when the subject has no alert for this tab to show.
        /// </summary>
        int FindAlertRow(TSubject subject);
    }
}
