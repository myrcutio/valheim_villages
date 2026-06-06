using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Items.WorkOrders;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Subject-agnostic host for a tabbed UI rendered inside Valheim's crafting
    ///     GUI. Clones the native Craft/Upgrade tab buttons into a unified
    ///     <see cref="TabHandler" />, injects custom tabs, and populates Valheim's
    ///     own RecipeList (left pane) and Decription panel (right pane) so all
    ///     backgrounds and chrome stay intact. Subclasses bind it to a concrete
    ///     subject: <see cref="VillagerTabManager" /> drives it with a villager,
    ///     <see cref="RegistryTabManager" /> with a <see cref="RegistryContext" />.
    ///     Content rendering lives in the partial in VillagerTabRenderer.cs.
    /// </summary>
    public abstract partial class CraftingTabHostBase<TSubject> : MonoBehaviour
    {
        private const float UpdateInterval = 1f;

        /// <summary>Children we snapshot-hid when a custom tab is active.</summary>
        private readonly Dictionary<Transform, bool> m_childSnapshot = new();

        private readonly List<GameObject> m_clonedButtons = new();

        /// <summary>Static LT/RT trigger glyphs we add to the first/last tab.</summary>
        private readonly List<GameObject> m_triggerHints = new();

        /// <summary>List item GameObjects created in the recipe list.</summary>
        private readonly List<GameObject> m_listElements = new();

        /// <summary>The registered custom tabs, in display order.</summary>
        protected readonly List<ITabContent<TSubject>> m_tabs = new();

        protected bool m_active;
        protected int m_firstCustomTabIndex;
        protected bool m_hasCraftingRecipes;
        protected float m_lastUpdateTime;
        protected string m_headerName;

        /// <summary>Injected RawImage for displaying a map texture in the description panel.</summary>
        private GameObject m_mapImageObject;

        private int m_selectedListIndex = -1;

        protected TabHandler m_tabHandler;

        /// <summary>The subject the active tab renders for (villager, registry, ...).</summary>
        protected abstract TSubject CurrentSubject { get; }

        /// <summary>Whether a subject is bound (replaces the old m_villager null-guard).</summary>
        protected abstract bool HasSubject { get; }

        #region Lifecycle

        protected virtual void Update()
        {
            if (!m_active || !HasSubject) return;

            var ci = GetCustomIndex();
            if (ci >= 0) HandleCustomListGamepadNav();

            if (Time.time - m_lastUpdateTime <= UpdateInterval) return;
            m_lastUpdateTime = Time.time;

            if (ci < 0) return;
            m_tabs[ci].OnUpdate(CurrentSubject);
            RefreshCustomContent(ci);
        }

        /// <summary>
        ///     Left-stick / D-pad up-down moves the selection through a custom
        ///     tab's list, mirroring the native recipe-list gamepad nav
        ///     (InventoryGui.UpdateRecipeGamepadInput) which only drives Valheim's
        ///     own list and so does nothing on our tabs. The action button (A /
        ///     m_craftButton) still triggers the selected item's action.
        /// </summary>
        private void HandleCustomListGamepadNav()
        {
            var count = m_listElements.Count;
            if (count == 0) return;

            int dir;
            if (ZInput.GetButtonDown("JoyLStickDown") || ZInput.GetButtonDown("JoyDPadDown"))
                dir = 1;
            else if (ZInput.GetButtonDown("JoyLStickUp") || ZInput.GetButtonDown("JoyDPadUp"))
                dir = -1;
            else return;

            var next = Mathf.Clamp(Mathf.Max(m_selectedListIndex, 0) + dir, 0, count - 1);
            if (next == m_selectedListIndex) return;
            OnListItemClicked(next);
        }

        /// <summary>
        ///     LateUpdate runs AFTER all Update calls (including InventoryGui's
        ///     UpdateCraftingPanel).  We re-apply the description panel and
        ///     re-hide Valheim's recipe elements every frame so Valheim's
        ///     per-frame updates can't overwrite our content.
        /// </summary>
        protected virtual void LateUpdate()
        {
            if (!m_active || !HasSubject) return;

            var gui = InventoryGui.instance;
            if (gui == null) return;

            // Native UpdateCraftingPanel re-activates the real Upgrade tab button
            // (and its RTrigger hint glyph) whenever the Orders/craft tab is
            // active. We drive tabs via clones, so force the native tab buttons
            // hidden every frame — otherwise a stray RT glyph shows on the middle
            // tab. Our own trigger hints (first/last tab) show only on a gamepad.
            if (gui.m_tabCraft != null) gui.m_tabCraft.gameObject.SetActive(false);
            if (gui.m_tabUpgrade != null) gui.m_tabUpgrade.gameObject.SetActive(false);
            var gamepadActive = ZInput.IsGamepadActive();
            foreach (var hint in m_triggerHints)
                if (hint != null) hint.SetActive(gamepadActive);

            var ci = GetCustomIndex();
            if (ci < 0) return;

            SuppressNativeRecipeElements(gui);
            UpdateSelectionVisuals();
            CraftingStationPatch.HideWorkOrderButton();
            PopulateDescription(gui, m_tabs[ci]);
            ReapplyChildHiding(gui);
            if (!string.IsNullOrEmpty(m_headerName))
                VillagerUIFactory.SetCraftingStationName(gui, m_headerName);
        }

        protected virtual void OnDestroy()
        {
            TeardownTabHandler();
        }

        #endregion

        #region TabHandler Setup / Teardown

        protected void SetupTabHandler()
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
            ConfigureGamepadTabNav(gui);
            AddTriggerHints(gui);
        }

        /// <summary>
        ///     Show a controller trigger glyph on the first tab (left trigger) and
        ///     the last tab (right trigger) as a static affordance for LT/RT tab
        ///     cycling. We clone the native Craft/Upgrade buttons' hint glyphs
        ///     (which render the correct LTrigger/RTrigger icons) rather than the
        ///     tab clones' own copies, which resolved to MISSING-BUTTON-DEF.
        ///     Visibility is gated to gamepad input in LateUpdate.
        /// </summary>
        private void AddTriggerHints(InventoryGui gui)
        {
            // Nothing to cycle with a single tab — first and last would be the
            // same button, showing both LT and RT glyphs on it. Show neither.
            if (m_tabHandler?.m_tabs == null || m_tabHandler.m_tabs.Count <= 1) return;
            var leftSrc = gui.m_tabCraft != null
                ? gui.m_tabCraft.GetComponent<UIGamePad>()?.m_hint
                : null;
            var rightSrc = gui.m_tabUpgrade != null
                ? gui.m_tabUpgrade.GetComponent<UIGamePad>()?.m_hint
                : null;
            CloneHintOnto(leftSrc, m_tabHandler.m_tabs[0].m_button);
            CloneHintOnto(rightSrc, m_tabHandler.m_tabs[m_tabHandler.m_tabs.Count - 1].m_button);
        }

        private void CloneHintOnto(GameObject hintSrc, Button targetButton)
        {
            if (hintSrc == null || targetButton == null) return;
            var copy = Instantiate(hintSrc, targetButton.transform);
            copy.name = "VV_TabHint";
            var srcRT = hintSrc.GetComponent<RectTransform>();
            var dstRT = copy.GetComponent<RectTransform>();
            if (srcRT != null && dstRT != null)
            {
                dstRT.anchorMin = srcRT.anchorMin;
                dstRT.anchorMax = srcRT.anchorMax;
                dstRT.pivot = srcRT.pivot;
                dstRT.anchoredPosition = srcRT.anchoredPosition;
                dstRT.sizeDelta = srcRT.sizeDelta;
            }

            // Static copy — drop any UIGamePad so nothing toggles or re-resolves it.
            foreach (var pad in copy.GetComponentsInChildren<UIGamePad>(true))
                Destroy(pad);
            copy.SetActive(true);
            m_triggerHints.Add(copy);
        }

        /// <summary>
        ///     Enable TabHandler's built-in gamepad tab cycling across ALL tabs.
        ///     It defaults to off (m_gamepadInput=false); the original setup relied
        ///     instead on the native buttons' per-button UIGamePad hotkeys, which we
        ///     strip in CloneButton (they duplicated RTrigger onto every custom tab
        ///     and showed MISSING-BUTTON-DEF hints). We point it at the same trigger
        ///     keys, read off the native buttons so nothing is hardcoded. Clamped at
        ///     the ends (m_cycling=false). Fails loudly — no silent default — if the
        ///     native keys can't be read.
        /// </summary>
        private void ConfigureGamepadTabNav(InventoryGui gui)
        {
            var leftKey = gui.m_tabCraft != null
                ? gui.m_tabCraft.GetComponent<UIGamePad>()?.m_zinputKey
                : null;
            var rightKey = gui.m_tabUpgrade != null
                ? gui.m_tabUpgrade.GetComponent<UIGamePad>()?.m_zinputKey
                : null;

            if (string.IsNullOrEmpty(leftKey) || string.IsNullOrEmpty(rightKey))
            {
                Debug.LogWarning(
                    "[VV] Could not read native tab trigger keys "
                    + $"(L='{leftKey}', R='{rightKey}') — gamepad tab switching "
                    + "stays disabled this session.");
                return;
            }

            m_tabHandler.m_cycling = false;
            m_tabHandler.m_gamepadInput = true;
            m_tabHandler.m_gamepadNavigateLeft = leftKey;
            m_tabHandler.m_gamepadNavigateRight = rightKey;
        }

        protected void TeardownTabHandler()
        {
            if (m_tabHandler != null)
            {
                Destroy(m_tabHandler);
                m_tabHandler = null;
            }

            foreach (var btn in m_clonedButtons)
                if (btn != null)
                    Destroy(btn);
            m_clonedButtons.Clear();
            // Trigger-hint glyphs are children of the cloned buttons just
            // destroyed; just drop our references.
            m_triggerHints.Clear();

            var gui = InventoryGui.instance;
            if (gui == null) return;
            // Restore the player's native tabs to their normal clickable state.
            // We hid them (SetActive(false)) and drove clones instead; leaving them
            // non-interactable here leaks into the player's own crafting menu (the
            // Upgrade tab goes dead until a relog). Valheim re-derives the correct
            // per-recipe interactability when the player next opens a station.
            gui.m_tabCraft?.gameObject.SetActive(true);
            gui.m_tabUpgrade?.gameObject.SetActive(true);
            if (gui.m_tabCraft != null) gui.m_tabCraft.interactable = true;
            if (gui.m_tabUpgrade != null) gui.m_tabUpgrade.interactable = true;
        }

        private static void CleanupOrphanedTabObjects(Transform tabParent)
        {
            for (var i = tabParent.childCount - 1; i >= 0; i--)
            {
                var child = tabParent.GetChild(i);
                if (child.name.StartsWith("VV_Tab_"))
                    Destroy(child.gameObject);
            }

            foreach (var th in tabParent.GetComponents<TabHandler>())
                Destroy(th);
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
            if (sel != null) Destroy(sel.gameObject);
            m_clonedButtons.Add(go);
            m_tabHandler.m_tabs.Add(new TabHandler.Tab
                { m_button = go.GetComponent<Button>(), m_default = isDefault });
        }

        private void AddCustomTabs(InventoryGui gui)
        {
            var template = gui.m_tabUpgrade;
            if (template == null) return;
            var spacing = ComputeTabSpacing(gui);
            var refRect = m_hasCraftingRecipes
                ? template.GetComponent<RectTransform>()
                : gui.m_tabCraft?.GetComponent<RectTransform>();
            var posOffset = m_hasCraftingRecipes ? 1 : 0;

            for (var i = 0; i < m_tabs.Count; i++)
            {
                var btnGO = CloneButton(template);
                btnGO.name = $"VV_Tab_{m_tabs[i].TabName}";
                var rect = btnGO.GetComponent<RectTransform>();
                if (rect != null && refRect != null)
                    rect.anchoredPosition = refRect.anchoredPosition
                                            + new Vector2(spacing * (i + posOffset), 0f);
                VillagerUIFactory.SetTMPText(btnGO, m_tabs[i].TabName);
                var sel = btnGO.transform.Find("Selected");
                if (sel != null) Destroy(sel.gameObject);
                m_clonedButtons.Add(btnGO);
                m_tabHandler.m_tabs.Add(new TabHandler.Tab
                {
                    m_button = btnGO.GetComponent<Button>(),
                    m_default = !m_hasCraftingRecipes && i == 0,
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
            // Instantiate copies the native Craft/Upgrade button's UIGamePad
            // hotkey (LTrigger/RTrigger) plus its hint glyph. Reparented onto our
            // clones the hints resolve to "MISSING BUTTON DEF", and the duplicated
            // trigger bindings (every custom tab is cloned from Upgrade, so they
            // all inherit RTrigger) make a single trigger fire several tabs at
            // once. Strip them — HandleTriggerTabNav drives trigger navigation.
            foreach (var pad in go.GetComponentsInChildren<UIGamePad>(true))
            {
                if (pad.m_hint != null) Destroy(pad.m_hint);
                Destroy(pad);
            }

            go.SetActive(true);
            return go;
        }

        private static float ComputeTabSpacing(InventoryGui gui)
        {
            var a = gui.m_tabCraft?.GetComponent<RectTransform>();
            var b = gui.m_tabUpgrade?.GetComponent<RectTransform>();
            if (a != null && b != null)
            {
                var s = b.anchoredPosition.x - a.anchoredPosition.x;
                if (s > 1f) return s;
            }

            return 100f;
        }

        #endregion

        #region Tab Changed

        private int GetCustomIndex()
        {
            if (m_tabHandler == null) return -1;
            var i = m_tabHandler.GetActiveTab() - m_firstCustomTabIndex;
            return i >= 0 && i < m_tabs.Count ? i : -1;
        }

        private void OnTabChanged(int index)
        {
            foreach (var t in m_tabs) t.OnDeselected();

            var ci = index - m_firstCustomTabIndex;
            if (ci >= 0 && ci < m_tabs.Count)
            {
                HideCraftingChildren();
                m_selectedListIndex = -1;
                m_tabs[ci].OnSelected(CurrentSubject);
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
