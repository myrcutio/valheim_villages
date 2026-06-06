using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Drives the tabbed crafting UI for an interacted villager NPC. All the
    ///     GUI chrome (native tab cloning, RecipeList/Decription population, gamepad
    ///     nav) lives in <see cref="CraftingTabHostBase{TSubject}" />; this subclass
    ///     only binds it to a <see cref="VillagerBehaviorBridge" /> subject and owns
    ///     the static lifecycle entry points called from <see cref="VillagerInteract" />.
    /// </summary>
    [RegisterModObject("VillagerTabManager")]
    public class VillagerTabManager : CraftingTabHostBase<VillagerBehaviorBridge>
    {
        private static VillagerTabManager s_instance;
        private VillagerBehaviorBridge m_villager;

        protected override VillagerBehaviorBridge CurrentSubject => m_villager;
        protected override bool HasSubject => m_villager != null;

        public static bool IsCustomTabActive =>
            s_instance != null && s_instance.m_active &&
            s_instance.m_tabHandler != null &&
            s_instance.m_tabHandler.GetActiveTab()
            >= s_instance.m_firstCustomTabIndex;

        public static bool IsNonCrafterActive =>
            s_instance != null && s_instance.m_active &&
            !s_instance.m_hasCraftingRecipes;

        /// <summary>Whether the villager crafting UI is currently driving the panel.</summary>
        public static bool IsActive => s_instance != null && s_instance.m_active;

        /// <summary>
        ///     True when the active tab is the cloned native Upgrade tab (the slot just
        ///     before the first custom tab). Callers must use this instead of
        ///     <see cref="InventoryGui.InUpradeTab" />: that native flag tracks the
        ///     craft/upgrade buttons' interactable state, which our TabHandler-driven
        ///     clones never toggle — so it desyncs and wrongly reports "upgrade" while
        ///     we sit on the Orders tab, suppressing the Order button.
        /// </summary>
        public static bool IsUpgradeTabActive =>
            s_instance != null && s_instance.m_active &&
            s_instance.m_hasCraftingRecipes &&
            s_instance.m_tabHandler != null &&
            s_instance.m_tabHandler.GetActiveTab() == s_instance.m_firstCustomTabIndex - 1;

        public static void Activate(
            VillagerBehaviorBridge villager, bool hasCraftingRecipes)
        {
            EnsureInstance();
            s_instance.m_villager = villager;
            s_instance.m_active = true;
            s_instance.m_hasCraftingRecipes = hasCraftingRecipes;
            s_instance.m_lastUpdateTime = Time.time;
            // Header shows the villager's type (e.g. "Guard"), not the generic
            // station-name fallback ("Villager").
            var type = villager?.VillagerType;
            s_instance.m_headerName = !string.IsNullOrEmpty(type)
                ? VillagerRegistry.Get(type)?.displayName ?? type
                : "Villager";
            s_instance.SetupTabHandler();
        }

        public static void Deactivate()
        {
            if (s_instance == null) return;
            foreach (var t in s_instance.m_tabs) t.OnDeselected();
            s_instance.m_active = false;
            s_instance.m_villager = null;
            s_instance.RestoreCraftingPanel();
            s_instance.TeardownTabHandler();
        }

        public static void RegisterTab(IVillagerTab tab)
        {
            EnsureInstance();
            // Real tabs implement IVillagerTabUI : ITabContent<VillagerBehaviorBridge>;
            // a bare IVillagerTab without the UI surface is a registration error and
            // should fail loudly rather than be silently dropped.
            s_instance.m_tabs.Add((IVillagerTabUI)tab);
        }

        private static void EnsureInstance()
        {
            if (s_instance != null) return;
            var go = new GameObject("VillagerTabManager");
            s_instance = go.AddComponent<VillagerTabManager>();
            DontDestroyOnLoad(go);
        }
    }
}
