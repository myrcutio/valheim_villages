using ValheimVillages.Attributes;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Drives the tabbed crafting UI for a placed Villager Cot. All the GUI chrome lives in
    ///     <see cref="CraftingTabHostBase{TSubject,TSelf}" />; this subclass binds it to a
    ///     <see cref="CotContext" />. The cot has no crafting recipes, so no native
    ///     Orders/Upgrade tabs appear and the first custom tab (Assign) is the default.
    /// </summary>
    [RegisterModObject("CotTabManager")]
    public class CotTabManager : CraftingTabHostBase<CotContext, CotTabManager>
    {
        private CotContext m_context;

        protected override CotContext CurrentSubject => m_context;
        protected override bool HasSubject => m_context != null;
        protected override void ClearSubject() => m_context = null;

        /// <summary>
        ///     Whether the cot UI is currently driving the shared crafting panel. Lets the work
        ///     order patch stand down, exactly as it does for the registry: the cot's station is
        ///     "$vv_"-named but has no work orders, so the Order button must not appear over the
        ///     cot's own action button.
        /// </summary>
        public static bool IsActive => s_instance != null && s_instance.m_active;

        public static void Activate(CotContext context)
        {
            EnsureInstance();
            s_instance.m_context = context;
            s_instance.ActivateCore(false, "Villager Cot");
        }

        public static void Deactivate() => DeactivateInstance();

        public static void RegisterTab(ICotTabUI tab) => RegisterTabInstance(tab);
    }
}
