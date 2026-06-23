using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using RawImage = UnityEngine.UI.RawImage;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Content rendering, scroll handling, and layout for <see cref="CraftingTabHostBase{TSubject}" />.
    ///     Populates the native RecipeList (left pane) and Description panel (right pane).
    ///     Partial class — see CraftingTabHostBase.cs for lifecycle and tab switching.
    ///     (Shared by VillagerTabManager and RegistryTabManager.)
    /// </summary>
    public abstract partial class CraftingTabHostBase<TSubject, TSelf>
        where TSelf : CraftingTabHostBase<TSubject, TSelf>
    {
        private const string ListItemPrefix = "VV_ListItem_";
        private Vector2? m_savedListRootAnchorMax;

        /// <summary>Saved m_recipeListRoot layout so we can restore after custom tabs.</summary>
        private Vector2? m_savedListRootAnchorMin;

        private Vector2? m_savedListRootPivot;

        /// <summary>
        ///     Signature of the list items last rendered into the recipe pane. Lets
        ///     <see cref="RefreshCustomContent" /> skip the destroy-and-rebuild when the
        ///     content is unchanged. Reset to null (force a rebuild) on tab switch and on
        ///     <see cref="RestoreCraftingPanel" />.
        /// </summary>
        private string m_renderedListSignature;

        #region Custom Content (populates native RecipeList + Decription)

        private void RefreshCustomContent(int ci)
        {
            var gui = InventoryGui.instance;
            if (gui == null || ci < 0 || ci >= m_tabs.Count) return;

            var tab = m_tabs[ci];
            var items = tab.GetListItems(CurrentSubject) ?? new List<TabListItemUI>();

            // Only rebuild the buttons when the rendered content actually changes.
            // RefreshCustomContent runs on a timer (and on tab switch); destroying and
            // recreating the recipe elements every tick made custom lists (villager /
            // registry) flicker and dropped clicks that landed on a rebuild frame. The
            // signature covers everything we render (name + icon), so an unchanged
            // signature means the existing buttons are already correct. The selection
            // highlight and description still refresh below without touching the buttons.
            var signature = ComputeListSignature(items);
            if (signature != m_renderedListSignature)
            {
                PopulateRecipeList(gui, items);
                m_renderedListSignature = signature;
            }

            if (m_selectedListIndex < 0 && m_listElements.Count > 0)
                m_selectedListIndex = 0;
            if (m_selectedListIndex >= m_listElements.Count)
                m_selectedListIndex = m_listElements.Count - 1;

            UpdateSelectionVisuals();
            PopulateDescription(gui, tab);
        }

        /// <summary>
        ///     A value signature of everything the recipe pane renders per item (name +
        ///     icon identity). GetListItems hands back fresh objects each call, so we
        ///     compare by value, not reference.
        /// </summary>
        private static string ComputeListSignature(List<TabListItemUI> items)
        {
            var sb = new StringBuilder(items.Count * 24);
            foreach (var item in items)
            {
                // Length-prefix the name so it can't collide with the delimiters,
                // whatever characters the name itself contains.
                var name = item?.TabName ?? "";
                sb.Append(name.Length).Append(':').Append(name);
                sb.Append('@').Append(item?.Icon != null ? item.Icon.GetInstanceID() : 0);
                sb.Append(';');
            }

            return sb.ToString();
        }

        private void PopulateRecipeList(InventoryGui gui, List<TabListItemUI> items)
        {
            foreach (var go in m_listElements)
            {
                if (go == null) continue;
                go.transform.SetParent(null);
                Destroy(go);
            }

            m_listElements.Clear();

            if (gui.m_recipeListRoot != null)
                foreach (Transform child in gui.m_recipeListRoot)
                    child.gameObject.SetActive(false);

            if (gui.m_recipeElementPrefab == null || gui.m_recipeListRoot == null)
                return;

            var rootRT = gui.m_recipeListRoot;
            if (!m_savedListRootAnchorMin.HasValue)
            {
                m_savedListRootAnchorMin = rootRT.anchorMin;
                m_savedListRootAnchorMax = rootRT.anchorMax;
                m_savedListRootPivot = rootRT.pivot;
            }

            rootRT.anchorMin = new Vector2(rootRT.anchorMin.x, 1f);
            rootRT.anchorMax = new Vector2(rootRT.anchorMax.x, 1f);
            rootRT.pivot = new Vector2(rootRT.pivot.x, 1f);

            var yPos = 0f;
            for (var i = 0; i < items.Count; i++)
            {
                var elem = Instantiate(
                    gui.m_recipeElementPrefab, gui.m_recipeListRoot);
                elem.name = $"{ListItemPrefix}{i}";
                var rt = elem.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchoredPosition = new Vector2(0f, yPos);
                    yPos -= gui.m_recipeListSpace;
                }

                CleanRecipeElement(elem);

                var nameField = elem.transform.Find("name");
                if (nameField != null)
                    VillagerUIFactory.SetTMPText(
                        nameField.gameObject, items[i].TabName);

                var icon = elem.transform.Find("icon");
                if (icon != null)
                {
                    var img = icon.GetComponent<Image>();
                    if (img != null)
                    {
                        var iconSprite = items[i].Icon;
                        if (iconSprite != null)
                            img.sprite = iconSprite;
                        else
                            img.enabled = false;
                    }
                }

                var idx = i;
                var btn = elem.GetComponent<Button>();
                if (btn == null) btn = elem.AddComponent<Button>();
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => OnListItemClicked(idx));

                elem.SetActive(true);
                m_listElements.Add(elem);
            }

            // Resize the list root to fit
            var listRootRT = gui.m_recipeListRoot;
            if (listRootRT != null)
            {
                var totalH = items.Count * gui.m_recipeListSpace;
                listRootRT.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Vertical, totalH);
            }
        }

        private void PopulateDescription(InventoryGui gui, ITabContent<TSubject> tab)
        {
            var detail = m_selectedListIndex >= 0
                ? tab.GetDetail(m_selectedListIndex, CurrentSubject)
                : null;

            if (gui.m_recipeIcon != null)
            {
                gui.m_recipeIcon.enabled = detail?.Icon != null;
                if (detail?.Icon != null) gui.m_recipeIcon.sprite = detail.Icon;
            }

            // "Decription" is Valheim's actual (misspelled) child object name — do
            // NOT "correct" it to "Description" or the lookup returns null and the
            // whole detail panel (title/body/map) stops rendering. The inner body
            // text child below IS spelled "Description".
            var descPanel = gui.m_crafting?.Find("Decription");
            if (descPanel != null)
            {
                // SetTMPText also re-enables the TMP component: Valheim disables the
                // Name/Description text components when no recipe is selected (our
                // custom tabs never select one), which leaves the GameObject active
                // but the text un-rendered.
                var nameChild = descPanel.Find("Name");
                if (nameChild != null)
                    VillagerUIFactory.SetTMPText(
                        nameChild.gameObject, detail?.Title ?? "");

                var descChild = descPanel.Find("Description");
                if (descChild != null)
                    VillagerUIFactory.SetTMPText(
                        descChild.gameObject, detail?.Description ?? "");
            }

            UpdateMapImage(descPanel, detail?.MapTexture);

            ApplyTabAction(gui, detail);

            HideDescriptionSubElements(gui);

            // Re-publish the native ingredient row + station-level star for details that carry
            // recipe-style requirements (e.g. recruit = 1 Lode Core). Runs per-frame from
            // LateUpdate AFTER native UpdateRecipe (which hides them when no native recipe is
            // selected) and AFTER HideDescriptionSubElements (which hides the container).
            PopulateRequirements(gui, detail);
        }

        /// <summary>
        ///     Show the native ingredient row + min-station-level star for a custom detail that
        ///     declares requirements, using Valheim's own <c>SetupRequirement</c> so the icons +
        ///     have/need colouring match the craft menu exactly. Details without requirements
        ///     leave them hidden (native already hid them this frame).
        /// </summary>
        private void PopulateRequirements(InventoryGui gui, TabDetailDataUI detail)
        {
            var list = gui.m_recipeRequirementList;
            var player = Player.m_localPlayer;
            if (list == null) return;

            var reqs = detail?.Requirements;
            if (reqs == null || reqs.Length == 0 || player == null)
            {
                if (gui.m_minStationLevelIcon != null)
                    gui.m_minStationLevelIcon.gameObject.SetActive(false);
                return; // native already hid the ingredient entries this frame
            }

            // HideDescriptionSubElements hid the "requirements" container this frame — re-show it.
            var reqPanel = gui.m_crafting?.Find("Decription")?.Find("requirements");
            if (reqPanel != null) reqPanel.gameObject.SetActive(true);

            for (var i = 0; i < reqs.Length && i < list.Length; i++)
                if (list[i] != null)
                    InventoryGui.SetupRequirement(list[i].transform, reqs[i], player, false, 0);

            if (gui.m_minStationLevelIcon != null)
            {
                var show = detail.StationLevel > 0;
                gui.m_minStationLevelIcon.gameObject.SetActive(show);
                if (show)
                {
                    var levelText = StationLevelTextObject(gui);
                    if (levelText != null)
                        VillagerUIFactory.SetTMPText(levelText, detail.StationLevel.ToString());
                }
            }
        }

        // m_minStationLevelText is a TMP_Text; reflect it as a Component to fetch its GameObject
        // without a hard TextMeshPro assembly reference (this file is deliberately TMP-free — text
        // is set via VillagerUIFactory.SetTMPText, which reflects the component).
        private static GameObject StationLevelTextObject(InventoryGui gui)
        {
            var field = typeof(InventoryGui).GetField("m_minStationLevelText",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            return field?.GetValue(gui) is Component c ? c.gameObject : null;
        }

        /// <summary>The per-host action button for custom tabs (cloned from Craft).</summary>
        private GameObject m_actionButton;

        /// <summary>
        ///     Render the active tab's action on a dedicated cloned button rather than
        ///     hijacking Valheim's native <see cref="InventoryGui.m_craftButton" />.
        ///     The native button is shared by both tab hosts (villager + registry), the
        ///     player's own crafting, and the Order-button swap — mutating its onClick
        ///     leaked actions across sessions (e.g. a registry "Recruit" binding firing
        ///     on the villager Orders tab). We only hide the native button while a custom
        ///     tab is up; its listeners/text are never touched. Restored in
        ///     <see cref="RestoreCraftingPanel" /> / <see cref="TeardownTabHandler" />.
        /// </summary>
        private void ApplyTabAction(InventoryGui gui, TabDetailDataUI detail)
        {
            if (gui.m_craftButton == null) return;

            var hasAction = detail?.OnAction != null;

            // The native craft button is irrelevant on a custom tab — hide it (without
            // touching its onClick/text). Its active state is restored on teardown.
            gui.m_craftButton.gameObject.SetActive(false);

            if (!hasAction)
            {
                if (m_actionButton != null) m_actionButton.SetActive(false);
                return;
            }

            EnsureActionButton(gui);
            if (m_actionButton == null) return;

            VillagerUIFactory.SetTMPText(m_actionButton, detail.ActionText ?? "Action");

            var btn = m_actionButton.GetComponent<Button>();
            if (btn != null)
            {
                btn.interactable = true;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => detail.OnAction?.Invoke());
            }
            
            m_actionButton.SetActive(true);
        }

        /// <summary>Clone the action button once from the native Craft button.</summary>
        private void EnsureActionButton(InventoryGui gui)
        {   
            if (m_actionButton != null) return;
            if (gui.m_craftButton == null) return;
            m_actionButton = Object.Instantiate(
                gui.m_craftButton.gameObject, gui.m_craftButton.transform.parent);
            m_actionButton.name = "VV_TabActionButton";
            m_actionButton.SetActive(false);
        }

        /// <summary>Hide the cloned action button (e.g. when leaving a custom tab).</summary>
        private void HideActionButton()
        {
            if (m_actionButton != null) m_actionButton.SetActive(false);
        }

        /// <summary>Destroy the cloned action button (host teardown).</summary>
        private void DestroyActionButton()
        {
            if (m_actionButton == null) return;
            Destroy(m_actionButton);
            m_actionButton = null;
        }

        /// <summary>
        ///     Show or hide a map texture in the description panel.
        /// </summary>
        private void UpdateMapImage(Transform descPanel, Texture2D texture)
        {
            if (texture == null)
            {
                if (m_mapImageObject != null)
                    m_mapImageObject.SetActive(false);
                return;
            }

            if (m_mapImageObject == null && descPanel != null)
            {
                m_mapImageObject = new GameObject("VV_MapImage",
                    typeof(RectTransform), typeof(RawImage));
                m_mapImageObject.transform.SetParent(descPanel, false);
            }

            if (m_mapImageObject != null)
            {
                // Anchored to the lower portion of the description panel, below
                // the title/station/legend text. Re-applied each time so a tweak
                // takes effect without recreating the object.
                var rt = m_mapImageObject.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.05f, 0.04f);
                rt.anchorMax = new Vector2(0.95f, 0.70f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                m_mapImageObject.SetActive(true);
                var rawImage = m_mapImageObject.GetComponent<RawImage>();
                if (rawImage != null)
                    rawImage.texture = texture;
            }
        }

        private void OnListItemClicked(int index)
        {
            m_selectedListIndex = index;
            UpdateSelectionVisuals();
            var ci = GetCustomIndex();
            if (ci >= 0)
            {
                var gui = InventoryGui.instance;
                if (gui != null)
                    PopulateDescription(gui, m_tabs[ci]);
            }
        }

        /// <summary>
        ///     Toggle the "selected" child on each list element.
        /// </summary>
        private void UpdateSelectionVisuals()
        {
            for (var i = 0; i < m_listElements.Count; i++)
            {
                var elem = m_listElements[i];
                if (elem == null) continue;
                var sel = elem.transform.Find("selected");
                if (sel != null)
                    sel.gameObject.SetActive(i == m_selectedListIndex);
            }
        }

        /// <summary>
        ///     Names of children inside the Decription panel to hide while a custom
        ///     tab is active (no recipe is selected, so they'd show stale content).
        /// </summary>
        private static readonly string[] s_descSubElementsToHide =
        {
            "SelectVariant", "CraftType", "UpgradePanel", "UpradePanel",
            "OLD_QualityPanel", "requirements",
        };

        /// <summary>
        ///     Original activeSelf of the Decription sub-elements we hid, so
        ///     RestoreCraftingPanel can put them back when we leave the custom tab.
        ///     Critical for "requirements" (the ingredients row): native
        ///     SetupRequirementList only toggles the individual entries, never the
        ///     parent container — so without this restore the ingredients row stays
        ///     gone on the Craft/Upgrade tabs after visiting a custom tab.
        /// </summary>
        private readonly Dictionary<GameObject, bool> m_descSubSnapshot = new();

        /// <summary>
        ///     Hide crafting-specific children inside the Decription panel,
        ///     snapshotting their prior active state once so it can be restored.
        ///     ("Decription" is Valheim's actual misspelled object name.)
        /// </summary>
        private void HideDescriptionSubElements(InventoryGui gui)
        {
            var desc = gui.m_crafting?.Find("Decription");
            if (desc == null) return;

            foreach (var name in s_descSubElementsToHide)
            {
                var child = desc.Find(name);
                if (child == null) continue;
                var go = child.gameObject;
                if (!m_descSubSnapshot.ContainsKey(go))
                    m_descSubSnapshot[go] = go.activeSelf;
                go.SetActive(false);
            }
        }

        /// <summary>
        ///     Disable inventory-style sub-elements on a cloned recipe element.
        /// </summary>
        private static void CleanRecipeElement(GameObject elem)
        {
            string[] toDisable = { "Durability", "QualityLevel" };
            foreach (var name in toDisable)
            {
                var child = elem.transform.Find(name);
                if (child != null) child.gameObject.SetActive(false);
            }

            var sel = elem.transform.Find("selected");
            if (sel != null) sel.gameObject.SetActive(false);
        }

        /// <summary>
        ///     Re-hide Valheim's native recipe elements.
        /// </summary>
        private void SuppressNativeRecipeElements(InventoryGui gui)
        {
            if (gui.m_recipeListRoot == null) return;
            var ours = new HashSet<GameObject>(m_listElements);
            foreach (Transform child in gui.m_recipeListRoot)
                child.gameObject.SetActive(ours.Contains(child.gameObject));
        }

        #endregion

        #region Crafting Panel Hide / Restore

        protected void RestoreCraftingPanel()
        {
            foreach (var go in m_listElements)
            {
                if (go == null) continue;
                go.transform.SetParent(null);
                Destroy(go);
            }

            m_listElements.Clear();
            m_renderedListSignature = null;

            if (m_mapImageObject != null)
            {
                Destroy(m_mapImageObject);
                m_mapImageObject = null;
            }

            var gui = InventoryGui.instance;
            if (gui?.m_recipeListRoot != null)
            {
                DestroyOrphanedListItems(gui.m_recipeListRoot);
                RestoreListRootLayout(gui.m_recipeListRoot);
            }

            // Re-activate the Decription sub-elements we hid (notably the
            // "requirements" ingredients row, which native never re-activates).
            foreach (var kvp in m_descSubSnapshot)
                if (kvp.Key != null)
                    kvp.Key.SetActive(kvp.Value);
            m_descSubSnapshot.Clear();

            // Our cloned action button is the custom-tab surface; hide it and hand the
            // native craft button back. We never mutated the native button's listeners,
            // so it returns to normal crafting untouched.
            HideActionButton();

            if (gui?.m_craftButton != null)
                gui.m_craftButton.gameObject.SetActive(true);
        }

        /// <summary>
        ///     Destroy any children of m_recipeListRoot that we created but lost
        ///     track of (e.g. after re-activation without deactivation).
        /// </summary>
        private static void DestroyOrphanedListItems(RectTransform listRoot)
        {
            for (var i = listRoot.childCount - 1; i >= 0; i--)
            {
                var child = listRoot.GetChild(i);
                if (child.name.StartsWith(ListItemPrefix))
                {
                    child.SetParent(null);
                    Destroy(child.gameObject);
                }
            }
        }

        private void RestoreListRootLayout(RectTransform rootRT)
        {
            if (!m_savedListRootAnchorMin.HasValue) return;
            rootRT.anchorMin = m_savedListRootAnchorMin.Value;
            rootRT.anchorMax = m_savedListRootAnchorMax.Value;
            rootRT.pivot = m_savedListRootPivot.Value;
            m_savedListRootAnchorMin = null;
            m_savedListRootAnchorMax = null;
            m_savedListRootPivot = null;
        }

        #endregion
    }
}
