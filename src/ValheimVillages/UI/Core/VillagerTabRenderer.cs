using System.Collections.Generic;
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
    public abstract partial class CraftingTabHostBase<TSubject>
    {
        private const string ListItemPrefix = "VV_ListItem_";
        private Vector2? m_savedListRootAnchorMax;

        /// <summary>Saved m_recipeListRoot layout so we can restore after custom tabs.</summary>
        private Vector2? m_savedListRootAnchorMin;

        private Vector2? m_savedListRootPivot;

        #region Custom Content (populates native RecipeList + Decription)

        private void RefreshCustomContent(int ci)
        {
            var gui = InventoryGui.instance;
            if (gui == null || ci < 0 || ci >= m_tabs.Count) return;

            var tab = m_tabs[ci];
            var items = tab.GetListItems(CurrentSubject) ?? new List<TabListItemUI>();
            PopulateRecipeList(gui, items);

            if (m_selectedListIndex < 0 && items.Count > 0)
                m_selectedListIndex = 0;

            UpdateSelectionVisuals();
            PopulateDescription(gui, tab);
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

            if (gui.m_craftButton != null)
            {
                var hasAction = detail?.OnAction != null;
                gui.m_craftButton.interactable = hasAction;
                gui.m_craftButton.gameObject.SetActive(hasAction);
                if (hasAction)
                {
                    VillagerUIFactory.SetTMPText(
                        gui.m_craftButton.gameObject,
                        detail.ActionText ?? "Action");
                    gui.m_craftButton.onClick.RemoveAllListeners();
                    gui.m_craftButton.onClick.AddListener(() =>
                        detail.OnAction?.Invoke());
                }
            }

            HideDescriptionSubElements(gui);
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
        ///     Hide crafting-specific children inside the Decription panel.
        ///     ("Decription" is Valheim's actual misspelled object name.)
        /// </summary>
        private static void HideDescriptionSubElements(InventoryGui gui)
        {
            var desc = gui.m_crafting?.Find("Decription");
            if (desc == null) return;

            string[] toHide =
            {
                "SelectVariant", "CraftType", "UpgradePanel",
                "OLD_QualityPanel", "requirements",
            };
            foreach (var name in toHide)
            {
                var child = desc.Find(name);
                if (child != null) child.gameObject.SetActive(false);
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

        /// <summary>
        ///     Re-hide crafting children that Valheim may re-show each frame.
        /// </summary>
        private void ReapplyChildHiding(InventoryGui gui)
        {
            if (gui?.m_crafting == null) return;
            foreach (Transform child in gui.m_crafting)
                if (s_craftingChildrenToHide.Contains(child.name))
                    child.gameObject.SetActive(false);
        }

        #endregion

        #region Crafting Panel Hide / Restore

        /// <summary>
        ///     Names of direct children of m_crafting to hide on custom tabs.
        /// </summary>
        private static readonly HashSet<string> s_craftingChildrenToHide = new();

        private void HideCraftingChildren()
        {
            if (m_childSnapshot.Count > 0) return;

            var gui = InventoryGui.instance;
            if (gui?.m_crafting == null) return;

            foreach (Transform child in gui.m_crafting)
            {
                m_childSnapshot[child] = child.gameObject.activeSelf;
                if (s_craftingChildrenToHide.Contains(child.name))
                    child.gameObject.SetActive(false);
            }
        }

        protected void RestoreCraftingPanel()
        {
            foreach (var go in m_listElements)
            {
                if (go == null) continue;
                go.transform.SetParent(null);
                Destroy(go);
            }

            m_listElements.Clear();

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

            foreach (var kvp in m_childSnapshot)
                if (kvp.Key != null)
                    kvp.Key.gameObject.SetActive(kvp.Value);
            m_childSnapshot.Clear();

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
