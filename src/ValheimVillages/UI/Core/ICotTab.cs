namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     A tab in the Villager Cot UI. Same shape as the registry tabs but bound to a
    ///     <see cref="CotContext" />, so all three tab populations share
    ///     <see cref="CraftingTabHostBase{TSubject,TSelf}" />. Implementations are discovered via
    ///     <see cref="Attributes.RegisterCotTabAttribute" />.
    /// </summary>
    public interface ICotTabUI : ITabContent<CotContext>
    {
    }
}
