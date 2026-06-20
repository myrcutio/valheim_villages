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
    public abstract partial class CraftingTabHostBase<TSubject, TSelf> : MonoBehaviour
        where TSelf : CraftingTabHostBase<TSubject, TSelf>
    {
        private const float UpdateInterval = 1f;

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

        /// <summary>
        ///     Native Craft/Upgrade tab interactable flags captured at setup. Valheim
        ///     encodes the active tab as the non-interactable button (InCraftTab() ==
        ///     !m_tabCraft.interactable); we drive cloned tabs that flip these via
        ///     OnTab*Pressed, so teardown restores the snapshot to give the player's own
        ///     crafting back the tab it was on.
        /// </summary>
        private bool m_savedTabCraftInteractable;

        private bool m_savedTabUpgradeInteractable;
        private bool m_savedTabStateValid;

        /// <summary>Injected RawImage for displaying a map texture in the description panel.</summary>
        private GameObject m_mapImageObject;

        private int m_selectedListIndex = -1;

        protected TabHandler m_tabHandler;

        /// <summary>The subject the active tab renders for (villager, registry, ...).</summary>
        protected abstract TSubject CurrentSubject { get; }

        /// <summary>Whether a subject is bound (replaces the old m_villager null-guard).</summary>
        protected abstract bool HasSubject { get; }

        /// <summary>Clear the bound subject on deactivation (subclass-owned field).</summary>
        protected abstract void ClearSubject();

        #region Singleton Lifecycle

        /// <summary>
        ///     The single live host instance per concrete subclass. Each closed
        ///     generic (villager vs registry) gets its own static field, so the two
        ///     tab hosts never collide. The boilerplate below (create/activate/
        ///     deactivate/register) used to be duplicated verbatim in each manager.
        /// </summary>
        protected static TSelf s_instance;

        protected static TSelf EnsureInstance()
        {
            if (s_instance != null) return s_instance;
            var go = new GameObject(typeof(TSelf).Name);
            s_instance = go.AddComponent<TSelf>();
            DontDestroyOnLoad(go);
            return s_instance;
        }

        /// <summary>Shared activation: subclasses bind their subject, then call this.</summary>
        protected void ActivateCore(bool hasCraftingRecipes, string headerName)
        {
            m_active = true;
            m_hasCraftingRecipes = hasCraftingRecipes;
            m_lastUpdateTime = Time.time;
            m_headerName = headerName;
            SetupTabHandler();
        }

        protected static void DeactivateInstance()
        {
            if (s_instance == null) return;
            foreach (var t in s_instance.m_tabs) t.OnDeselected();
            s_instance.m_active = false;
            s_instance.ClearSubject();
            s_instance.RestoreCraftingPanel();
            s_instance.TeardownTabHandler();
        }

        protected static void RegisterTabInstance(ITabContent<TSubject> tab)
        {
            EnsureInstance();
            s_instance.m_tabs.Add(tab);
        }

        #endregion

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

            // A non-crafter villager/registry isn't a crafting station: hide the
            // native station icon + level header every frame. UpdateRecipe re-derives
            // them from the virtual station each frame (it runs per-frame, unlike the
            // event-driven UpdateCraftingPanel), so this must re-apply here. The name
            // label is separately overridden to the villager/registry type below.
            // (Replaces the old whole-panel CanvasGroup hide in VillagerCraftingPatch,
            // which also blanked the Info/Debug content rendered into m_crafting.)
            if (!m_hasCraftingRecipes)
            {
                if (gui.m_craftingStationIcon != null)
                    gui.m_craftingStationIcon.gameObject.SetActive(false);
                if (gui.m_craftingStationLevelRoot != null)
                    gui.m_craftingStationLevelRoot.gameObject.SetActive(false);
            }

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

            // Snapshot the player's current vanilla tab state before we drive clones
            // (which flip these flags via OnTab*Pressed). Restored on teardown so the
            // next vanilla station opens on the tab the player last used — not stuck on
            // Upgrade. The active tab is the non-interactable one.
            if (gui.m_tabCraft != null && gui.m_tabUpgrade != null)
            {
                m_savedTabCraftInteractable = gui.m_tabCraft.interactable;
                m_savedTabUpgradeInteractable = gui.m_tabUpgrade.interactable;
                m_savedTabStateValid = true;
            }

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
            
            DestroyActionButton();

            var gui = InventoryGui.instance;
            if (gui == null) return;
            // Re-show the native tabs (we hid them and drove clones instead) and restore
            // the exact interactable flags we captured at setup. Setting BOTH to true
            // (the old code) is an invalid state — InCraftTab() == !m_tabCraft.interactable
            // then reads false, so the next vanilla station the player opens is stuck on
            // the Upgrade tab. Restoring the snapshot returns them to their last vanilla tab.
            gui.m_tabCraft?.gameObject.SetActive(true);
            gui.m_tabUpgrade?.gameObject.SetActive(true);
            if (m_savedTabStateValid)
            {
                if (gui.m_tabCraft != null)
                    gui.m_tabCraft.interactable = m_savedTabCraftInteractable;
                if (gui.m_tabUpgrade != null)
                    gui.m_tabUpgrade.interactable = m_savedTabUpgradeInteractable;
                m_savedTabStateValid = false;
            }
        }

        private static void CleanupOrphanedTabObjects(Transform tabParent)
        {
            for (var i = tabParent.childCount - 1; i >= 0; i--)
            {
                var child = tabParent.GetChild(i);
                if (child.name.StartsWith("VV_"))
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
                m_selectedListIndex = -1;
                // Force a list rebuild for the new tab — the previous tab's elements are
                // still in m_listElements, so the change-detection must not short-circuit.
                m_renderedListSignature = null;
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
