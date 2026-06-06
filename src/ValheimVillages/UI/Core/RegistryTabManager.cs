using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Drives the tabbed crafting UI for the Village Registry station. All the
    ///     GUI chrome lives in <see cref="CraftingTabHostBase{TSubject}" />; this
    ///     subclass binds it to a <see cref="RegistryContext" /> and owns the static
    ///     lifecycle entry points called from <see cref="Interaction.RegistryInteract" />.
    ///     The registry has no crafting recipes, so no native Orders/Upgrade tabs are
    ///     shown — the first custom tab (Roster) is the default.
    /// </summary>
    [RegisterModObject("RegistryTabManager")]
    public class RegistryTabManager : CraftingTabHostBase<RegistryContext>
    {
        private static RegistryTabManager s_instance;
        private RegistryContext m_context;

        protected override RegistryContext CurrentSubject => m_context;
        protected override bool HasSubject => m_context != null;

        /// <summary>
        ///     Whether the registry UI is currently driving the shared crafting panel.
        ///     Lets <see cref="Items.WorkOrders.CraftingStationPatch" /> stand down (the
        ///     registry station is "$vv_"-named but has no work orders, so the Order
        ///     button must not appear over the registry's own action buttons).
        /// </summary>
        public static bool IsActive => s_instance != null && s_instance.m_active;

        public static void Activate(RegistryContext context)
        {
            EnsureInstance();
            s_instance.m_context = context;
            s_instance.m_active = true;
            s_instance.m_hasCraftingRecipes = false;
            s_instance.m_lastUpdateTime = Time.time;
            s_instance.m_headerName = "Village Registry";
            s_instance.SetupTabHandler();
        }

        public static void Deactivate()
        {
            if (s_instance == null) return;
            foreach (var t in s_instance.m_tabs) t.OnDeselected();
            s_instance.m_active = false;
            s_instance.m_context = null;
            s_instance.RestoreCraftingPanel();
            s_instance.TeardownTabHandler();
        }

        public static void RegisterTab(IRegistryTabUI tab)
        {
            EnsureInstance();
            s_instance.m_tabs.Add(tab);
        }

        private static void EnsureInstance()
        {
            if (s_instance != null) return;
            var go = new GameObject("RegistryTabManager");
            s_instance = go.AddComponent<RegistryTabManager>();
            DontDestroyOnLoad(go);
        }
    }
}
