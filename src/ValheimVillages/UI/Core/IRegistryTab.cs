namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     A tab in the Village Registry UI. Same shape as the villager tabs but
    ///     bound to a <see cref="RegistryContext" /> subject instead of a villager,
    ///     so both share <see cref="CraftingTabHostBase{TSubject}" />. Implementations
    ///     are discovered via <see cref="Attributes.RegisterRegistryTabAttribute" />.
    /// </summary>
    public interface IRegistryTabUI : ITabContent<RegistryContext>
    {
    }
}
