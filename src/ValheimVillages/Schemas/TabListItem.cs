namespace ValheimVillages.Schemas
{
    /// <summary>
    ///     A single item in the tab's recipe list (left pane).
    ///     Core version without Unity Sprite dependency.
    ///     The mod assembly extends this with Sprite Icon support.
    /// </summary>
    public class TabListItem
    {
        public string TabName { get; set; }
    }
}