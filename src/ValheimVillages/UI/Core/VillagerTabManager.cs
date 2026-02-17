using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Core.Attributes;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Manages the tab system for the villager interaction UI.
    /// Clones native Craft/Upgrade buttons and registers custom tabs
    /// in a unified TabHandler.  Custom tabs populate Valheim's own
    /// RecipeList (left pane) and Decription panel (right pane) so
    /// all backgrounds and chrome stay intact.
    /// Content rendering is in VillagerTabRenderer.cs (partial class).
    /// </summary>
    [RegisterModObject("VillagerTabManager")]
    public partial class VillagerTabManager : MonoBehaviour
    {
        private static VillagerTabManager s_instance;

        private readonly List<IVillagerTab> m_tabs = new();
        private VillagerBehaviorBridge m_villager;
        private bool m_active;
        private bool m_hasCraftingRecipes;
        private float m_lastUpdateTime;

        private TabHandler m_tabHandler;
        private int m_firstCustomTabIndex;
        private readonly List<GameObject> m_clonedButtons = new();
        private int m_selectedListIndex = -1;

        /// <summary>Children we snapshot-hid when a custom tab is active.</summary>
        private readonly Dictionary<Transform, bool> m_childSnapshot = new();
        /// <summary>List item GameObjects created in the recipe list.</summary>
        private readonly List<GameObject> m_listElements = new();

        /// <summary>Injected RawImage for displaying a map texture in the description panel.</summary>
        private GameObject m_mapImageObject;

        private const float UpdateInterval = 1f;

        public static bool IsCustomTabActive =>
            s_instance != null && s_instance.m_active &&
            s_instance.m_tabHandler != null &&
            s_instance.m_tabHandler.GetActiveTab()
                >= s_instance.m_firstCustomTabIndex;

        public static bool IsNonCrafterActive =>
            s_instance != null && s_instance.m_active &&
            !s_instance.m_hasCraftingRecipes;

        #region Lifecycle

        public static void Activate(
            VillagerBehaviorBridge villager, bool hasCraftingRecipes)
        {
            EnsureInstance();
            s_instance.m_villager = villager;
            s_instance.m_active = true;
            s_instance.m_hasCraftingRecipes = hasCraftingRecipes;
            s_instance.m_lastUpdateTime = Time.time;
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
            s_instance.m_tabs.Add(tab);
        }

        private static void EnsureInstance()
        {
            if (s_instance != null) return;
            var go = new GameObject("VillagerTabManager");
            s_instance = go.AddComponent<VillagerTabManager>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (!m_active || m_villager == null) return;
            if (Time.time - m_lastUpdateTime <= UpdateInterval) return;
            m_lastUpdateTime = Time.time;

            int ci = GetCustomIndex();
            if (ci < 0) return;
            m_tabs[ci].OnUpdate(m_villager);
            RefreshCustomContent(ci);
        }

        /// <summary>
        /// LateUpdate runs AFTER all Update calls (including InventoryGui's
        /// UpdateCraftingPanel).  We re-apply the description panel and
        /// re-hide Valheim's recipe elements every frame so Valheim's
        /// per-frame updates can't overwrite our content.
        /// </summary>
        private void LateUpdate()
        {
            if (!m_active || m_villager == null) return;
            int ci = GetCustomIndex();
            if (ci < 0) return;

            var gui = InventoryGui.instance;
            if (gui == null) return;

            SuppressNativeRecipeElements(gui);
            UpdateSelectionVisuals();
            Items.WorkOrders.CraftingStationPatch.HideWorkOrderButton();
            PopulateDescription(gui, m_tabs[ci]);
            ReapplyChildHiding(gui);
        }

        private void OnDestroy() => TeardownTabHandler();

        #endregion

        #region TabHandler Setup / Teardown

        private void SetupTabHandler()
        {
            var gui = InventoryGui.instance;
            var tabParent = gui?.m_tabCraft?.transform.parent;
            if (tabParent == null) return;

            CleanupOrphanedTabObjects(tabParent);

            m_tabHandler = tabParent.gameObject.AddComponent<TabHandler>();
            m_tabHandler.m_tabs = new List<TabHandler.Tab>();
            m_tabHandler.m_blockingElements = new List<GameObject>();

            if (m_hasCraftingRecipes)
            {
                gui.m_tabCraft.gameObject.SetActive(false);
                gui.m_tabUpgrade.gameObject.SetActive(false);
                AddNativeTabClone(gui, gui.m_tabCraft, true);
                AddNativeTabClone(gui, gui.m_tabUpgrade, false);
                m_firstCustomTabIndex = 2;
            }
            else
            {
                gui.m_tabCraft?.gameObject.SetActive(false);
                gui.m_tabUpgrade?.gameObject.SetActive(false);
                m_firstCustomTabIndex = 0;
            }

            AddCustomTabs(gui);
            m_tabHandler.ActiveTabChanged += OnTabChanged;
            m_tabHandler.Init(true);
        }

        private void TeardownTabHandler()
        {
            if (m_tabHandler != null)
            {
                Destroy(m_tabHandler);
                m_tabHandler = null;
            }
            foreach (var btn in m_clonedButtons)
                if (btn != null) Destroy(btn);
            m_clonedButtons.Clear();

            var gui = InventoryGui.instance;
            if (gui == null) return;
            gui.m_tabCraft?.gameObject.SetActive(true);
            gui.m_tabUpgrade?.gameObject.SetActive(true);
            if (gui.m_tabCraft != null) gui.m_tabCraft.interactable = false;
            if (gui.m_tabUpgrade != null) gui.m_tabUpgrade.interactable = true;
        }

        private static void CleanupOrphanedTabObjects(Transform tabParent)
        {
            for (int i = tabParent.childCount - 1; i >= 0; i--)
            {
                var child = tabParent.GetChild(i);
                if (child.name.StartsWith("VV_Tab_"))
                    Object.Destroy(child.gameObject);
            }
            foreach (var th in tabParent.GetComponents<TabHandler>())
                Object.Destroy(th);
        }

        #endregion

        #region Tab Button Helpers

        private void AddNativeTabClone(InventoryGui gui, Button template, bool isDefault)
        {
            var go = CloneButton(template);
            go.name = $"VV_Tab_{template.name}";
            if (gui != null && template == gui.m_tabCraft)
                VillagerUIFactory.SetTMPText(go, "Orders");
            var srcRect = template.GetComponent<RectTransform>();
            var dstRect = go.GetComponent<RectTransform>();
            if (srcRect != null && dstRect != null)
                dstRect.anchoredPosition = srcRect.anchoredPosition;
            var sel = go.transform.Find("Selected");
            if (sel != null) Object.Destroy(sel.gameObject);
            m_clonedButtons.Add(go);
            m_tabHandler.m_tabs.Add(new TabHandler.Tab
                { m_button = go.GetComponent<Button>(), m_default = isDefault });
        }

        private void AddCustomTabs(InventoryGui gui)
        {
            var template = gui.m_tabUpgrade;
            if (template == null) return;
            float spacing = ComputeTabSpacing(gui);
            var refRect = m_hasCraftingRecipes
                ? template.GetComponent<RectTransform>()
                : gui.m_tabCraft?.GetComponent<RectTransform>();
            int posOffset = m_hasCraftingRecipes ? 1 : 0;

            for (int i = 0; i < m_tabs.Count; i++)
            {
                var btnGO = CloneButton(template);
                btnGO.name = $"VV_Tab_{m_tabs[i].Name}";
                var rect = btnGO.GetComponent<RectTransform>();
                if (rect != null && refRect != null)
                    rect.anchoredPosition = refRect.anchoredPosition
                        + new Vector2(spacing * (i + posOffset), 0f);
                VillagerUIFactory.SetTMPText(btnGO, m_tabs[i].Name);
                var sel = btnGO.transform.Find("Selected");
                if (sel != null) Object.Destroy(sel.gameObject);
                m_clonedButtons.Add(btnGO);
                m_tabHandler.m_tabs.Add(new TabHandler.Tab
                {
                    m_button = btnGO.GetComponent<Button>(),
                    m_default = !m_hasCraftingRecipes && i == 0
                });
            }
        }

        private static GameObject CloneButton(Button template)
        {
            var go = Instantiate(template.gameObject, template.transform.parent);
            var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
            cg.ignoreParentGroups = true;
            var btn = go.GetComponent<Button>();
            if (btn != null) btn.onClick = new Button.ButtonClickedEvent();
            go.SetActive(true);
            return go;
        }

        private static float ComputeTabSpacing(InventoryGui gui)
        {
            var a = gui.m_tabCraft?.GetComponent<RectTransform>();
            var b = gui.m_tabUpgrade?.GetComponent<RectTransform>();
            if (a != null && b != null)
            {
                float s = b.anchoredPosition.x - a.anchoredPosition.x;
                if (s > 1f) return s;
            }
            return 100f;
        }

        #endregion

        #region Tab Changed

        private int GetCustomIndex()
        {
            if (m_tabHandler == null) return -1;
            int i = m_tabHandler.GetActiveTab() - m_firstCustomTabIndex;
            return i >= 0 && i < m_tabs.Count ? i : -1;
        }

        private void OnTabChanged(int index)
        {
            foreach (var t in m_tabs) t.OnDeselected();

            int ci = index - m_firstCustomTabIndex;
            if (ci >= 0 && ci < m_tabs.Count)
            {
                HideCraftingChildren();
                m_selectedListIndex = -1;
                m_tabs[ci].OnSelected(m_villager);
                RefreshCustomContent(ci);
            }
            else
            {
                RestoreCraftingPanel();
                var gui = InventoryGui.instance;
                if (gui != null)
                {
                    if (index == 0) gui.OnTabCraftPressed();
                    else if (index == 1) gui.OnTabUpgradePressed();
                }
            }
        }

        #endregion
    }
}
