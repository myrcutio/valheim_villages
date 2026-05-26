using System;

namespace ValheimVillages.Schemas
{
    /// <summary>
    ///     Detail data shown in the description pane (right pane)
    ///     when a list item is selected.
    ///     Core version without Unity Sprite/Texture2D dependencies.
    ///     The mod assembly extends this with Icon, MapTexture support.
    /// </summary>
    public class TabDetailData
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string ActionText { get; set; }
        public Action OnAction { get; set; }
    }
}