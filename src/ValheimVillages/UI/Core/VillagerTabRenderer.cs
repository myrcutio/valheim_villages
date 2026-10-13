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

            // A pending focus (the player walked up to a villager with a "!" over their head)
            // claims the row before the default "first row" below does.
            if (m_pendingListSelection >= 0)
            {
                m_selectedListIndex = m_pendingListSelection;
                m_pendingListSelection = -1;
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

                // Same exemption the map needed: the work-order editor installs a
                // UIGroupHandler at priority 10000, which makes Valheim deactivate every other
                // group and leaves this row's CanvasGroup non-interactable, so the click never
                // arrives. Applied per row because the rows are recycled native elements and
                // nothing else owns them.
                var rowGroup = elem.GetComponent<CanvasGroup>() ?? elem.AddComponent<CanvasGroup>();
                rowGroup.ignoreParentGroups = true;
                rowGroup.interactable = true;
                rowGroup.blocksRaycasts = true;

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
            // Selecting a different row re-frames the map on a different subject, so the
            // previous row's zoom does not carry over.
            Panels.MapViewState.EnsureKey(m_selectedListIndex.ToString());

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
                {
                    // An empty title still occupies its line, leaving a blank band at the top
                    // of the panel. Hide the element rather than blanking its text.
                    var title = detail?.Title ?? "";
                    nameChild.gameObject.SetActive(!string.IsNullOrEmpty(title));
                    if (!string.IsNullOrEmpty(title))
                        VillagerUIFactory.SetTMPText(nameChild.gameObject, title);
                }

                var descChild = descPanel.Find("Description");
                if (descChild != null)
                    VillagerUIFactory.SetTMPText(
                        descChild.gameObject, detail?.Description ?? "");
            }

            UpdateLevelCrest(descPanel, detail?.StationLevel ?? 0);
            UpdateLevelRungs(descPanel, detail?.Rungs);
            UpdateMapImage(descPanel, detail?.MapTexture, detail?.MapPins);

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
        ///     Height of one ladder row. Sized to the badge it carries, which has to be big
        ///     enough to read the level off at a glance — the whole point of using the shield
        ///     rather than a tick.
        /// </summary>
        private const float RungHeight = 68f;

        /// <summary>Font size for the level digit on a rung badge, scaled to the badge.</summary>
        private const float RungBadgeFontSize = 30f;

        /// <summary>
        ///     Highest the map may start, as a fraction of the description panel. Leaves room
        ///     for at least a title plus a line or two even when the text is short.
        /// </summary>
        private const float MapTopMax = 0.70f;

        /// <summary>
        ///     Lowest the map may be pushed. Past this the map is too small to read, and
        ///     clipping a long reason is the better trade than a map nobody can use.
        /// </summary>
        private const float MapTopMin = 0.34f;

        /// <summary>
        ///     Where the map's top edge belongs: just below the description text, measured
        ///     rather than assumed.
        /// </summary>
        private static float MapTopAnchor(Transform descPanel)
        {
            var descText = descPanel != null ? descPanel.Find("Description") : null;
            var panelRect = descPanel as RectTransform;
            if (descText == null || panelRect == null) return MapTopMax;

            var panelHeight = panelRect.rect.height;
            if (panelHeight <= 1f) return MapTopMax;

            var textHeight = VillagerUIFactory.GetTMPPreferredHeight(descText.gameObject);
            if (textHeight <= 0f) return MapTopMax;

            // The description starts at the panel top, so the fraction it consumes is its
            // height over the panel's; the map begins below that, plus a small gap.
            var consumed = (textHeight + MapGap) / panelHeight;
            return Mathf.Clamp(1f - consumed, MapTopMin, MapTopMax);
        }

        /// <summary>Breathing room between the description text and the map below it.</summary>
        private const float MapGap = 10f;

        /// <summary>Breathing room between the end of the description text and the ladder.</summary>
        private const float RungGap = 8f;

        /// <summary>Signature of the rendered ladder, so rows are rebuilt only when they change.</summary>
        private string m_renderedRungSignature;

        /// <summary>
        ///     Draw the level ladder as rows, a shield badge on each rung already earned.
        ///
        ///     <para>Rows rather than text because the badge carries the level NUMBER, and a
        ///     digit cannot be drawn inside an inline TMP sprite. Rebuilt only when the ladder
        ///     actually changes — this runs on the description refresh timer, and recreating a
        ///     handful of objects several times a second is how the map pins first broke.</para>
        /// </summary>
        private void UpdateLevelRungs(Transform descPanel, List<LevelRung> rungs)
        {
            // Height is part of the signature: the ladder's position depends on how tall the
            // description rendered, so a description that changed length must reflow the rows
            // even when the rungs themselves are identical.
            var descForHeight = descPanel != null ? descPanel.Find("Description") : null;
            var heightKey = descForHeight != null
                ? Mathf.RoundToInt(VillagerUIFactory.GetTMPPreferredHeight(descForHeight.gameObject))
                : 0;
            var signature = ComputeRungSignature(rungs) + "@" + heightKey;
            if (signature == m_renderedRungSignature && m_rungRoot != null)
            {
                m_rungRoot.SetActive(rungs != null && rungs.Count > 0);
                return;
            }

            m_renderedRungSignature = signature;

            if (m_rungRoot != null)
            {
                Destroy(m_rungRoot);
                m_rungRoot = null;
            }

            if (rungs == null || rungs.Count == 0 || descPanel == null) return;

            m_rungRoot = new GameObject("VV_LevelRungs", typeof(RectTransform));

            // Parented to the DESCRIPTION text and offset by its own rendered height, so the
            // ladder always starts just below whatever the text happens to be. A fixed anchor
            // cannot work here: the description grows and shrinks with the villager's state
            // (blocker lines, "Now:", mood), which is why the rows kept landing on top of it.
            var descText = descPanel.Find("Description");
            var host = descText != null ? descText : descPanel;
            m_rungRoot.transform.SetParent(host, false);

            var textHeight = descText != null
                ? VillagerUIFactory.GetTMPPreferredHeight(descText.gameObject)
                : 0f;

            var root = m_rungRoot.GetComponent<RectTransform>();
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = new Vector2(0f, -(textHeight + RungGap));
            root.sizeDelta = new Vector2(0f, RungHeight * rungs.Count);

            for (var i = 0; i < rungs.Count; i++) BuildRung(root, rungs[i], i, rungs.Count);
        }

        private static string ComputeRungSignature(List<LevelRung> rungs)
        {
            if (rungs == null || rungs.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var r in rungs)
                sb.Append(r.Level).Append(r.Reached ? '+' : r.IsNext ? '>' : '-')
                  .Append(r.Text).Append(';');

            return sb.ToString();
        }

        /// <summary>One ladder row: badge-or-marker on the left, what it asks for on the right.</summary>
        private static void BuildRung(RectTransform parent, LevelRung rung, int index, int count)
        {
            var row = new GameObject($"VV_Rung{rung.Level}", typeof(RectTransform));
            var rt = row.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -RungHeight * index);
            rt.sizeDelta = new Vector2(0f, RungHeight);

            if (rung.Reached)
            {
                // The badge IS the tick now: same shield as the map pin and the panel header.
                var badge = new GameObject("Badge", typeof(RectTransform), typeof(Image));
                var brt = badge.GetComponent<RectTransform>();
                brt.SetParent(rt, false);
                brt.anchorMin = new Vector2(0f, 0.5f);
                brt.anchorMax = new Vector2(0f, 0.5f);
                brt.pivot = new Vector2(0f, 0.5f);
                brt.anchoredPosition = Vector2.zero;
                // The star is square, unlike the shield the map pins wear.
                brt.sizeDelta = new Vector2(RungHeight, RungHeight);

                var img = badge.GetComponent<Image>();
                img.sprite = UiSprites.LevelStar();
                // Greyscale sprite tinted gold, exactly as the native level readout does it.
                img.color = UiSprites.LevelStarTint;
                img.preserveAspect = true;
                img.raycastTarget = false;

                var num = VillagerUIFactory.CreateLabel(
                    brt, rung.Level.ToString(), RungBadgeFontSize);
                if (num != null)
                {
                    var nrt = num.GetComponent<RectTransform>();
                    if (nrt != null)
                    {
                        nrt.anchorMin = Vector2.zero;
                        nrt.anchorMax = Vector2.one;
                        // Raise the bottom edge, not lower the top: the shield's visual centre
                        // sits above its geometric one because it tapers to a point.
                        // Centred: a star is near enough symmetric that the digit needs no
                        // lift, unlike the shield, which tapers to a point and pulled its
                        // visual centre upward.
                        nrt.offsetMin = Vector2.zero;
                        nrt.offsetMax = Vector2.zero;
                    }

                    VillagerUIFactory.SetTMPProperties(
                        num, RungBadgeFontSize, LevelCrestTextColor, TmpCenter);
                }
            }

            var label = VillagerUIFactory.CreateLabel(
                rt, $"{(rung.Reached ? "" : rung.IsNext ? "▸ " : "· ")}Level {rung.Level} — {rung.Text}",
                14f);
            if (label == null) return;

            var lrt = label.GetComponent<RectTransform>();
            if (lrt != null)
            {
                lrt.anchorMin = new Vector2(0f, 0f);
                lrt.anchorMax = new Vector2(1f, 1f);
                // Indent past the badge column so reached and unreached rows line up.
                lrt.offsetMin = new Vector2(RungHeight + 8f, 0f);
                lrt.offsetMax = Vector2.zero;
            }

            VillagerUIFactory.SetTMPProperties(
                label, 14f,
                rung.Reached ? RungMetColor : rung.IsNext ? RungNextColor : RungLockedColor,
                TmpMidlineLeft);
        }

        /// <summary>TMP TextAlignmentOptions: Center, and MidlineLeft for row text.</summary>
        private const int TmpCenter = 514;

        private const int TmpMidlineLeft = 513;

        private static readonly Color RungMetColor = new(0.56f, 0.82f, 0.56f, 1f);
        private static readonly Color RungNextColor = new(1f, 0.76f, 0.29f, 1f);
        private static readonly Color RungLockedColor = new(0.43f, 0.48f, 0.53f, 1f);

        /// <summary>Size of the level shield in the description panel, in pixels.</summary>
        private const float LevelCrestHeight = 46f;

        /// <summary>
        ///     Show the villager's level as a number on the same shield their map pin wears, in
        ///     the top-right of the description panel.
        ///
        ///     <para>Built here rather than reusing Valheim's station-level star because that
        ///     star is only re-published for details that carry recipe requirements — a Status
        ///     detail has a level and no ingredients, so it fell through that branch and showed
        ///     nothing. Reusing the crest ties the number to the same badge the villager is
        ///     marked with on the map.</para>
        /// </summary>
        private void UpdateLevelCrest(Transform descPanel, int level)
        {
            if (level <= 0)
            {
                if (m_levelCrestObject != null) m_levelCrestObject.SetActive(false);
                return;
            }

            if (m_levelCrestObject == null && descPanel != null)
            {
                m_levelCrestObject = new GameObject(
                    "VV_LevelCrest", typeof(RectTransform), typeof(Image));
                m_levelCrestObject.transform.SetParent(descPanel, false);

                var img = m_levelCrestObject.GetComponent<Image>();
                img.sprite = UiSprites.Crest();
                img.preserveAspect = true;
                img.raycastTarget = false;
                if (img.sprite == null) img.color = new Color(0.35f, 0.30f, 0.22f, 0.9f);

                VillagerUIFactory.CreateLabel(m_levelCrestObject.transform, "", 22f);
            }

            if (m_levelCrestObject == null) return;

            var rt = m_levelCrestObject.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-6f, -4f);
            rt.sizeDelta = new Vector2(LevelCrestHeight * UiSprites.CrestAspect, LevelCrestHeight);

            m_levelCrestObject.SetActive(true);

            // The number sits on the shield, filling it, so it stays centred whatever the digit.
            if (m_levelCrestObject.transform.childCount == 0) return;
            var label = m_levelCrestObject.transform.GetChild(0).gameObject;
            var lrt = label.GetComponent<RectTransform>();
            if (lrt != null)
            {
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                // Nudge the digit UP by raising the BOTTOM edge. The previous attempt lowered
                // the top edge instead, which shrinks the rect from the other side and moves
                // centred text DOWN — the opposite of the intent, and why the number sat low.
                lrt.offsetMin = new Vector2(0f, 7f);
                lrt.offsetMax = Vector2.zero;
            }

            VillagerUIFactory.SetTMPText(label, level.ToString());
            VillagerUIFactory.SetTMPProperties(label, 22f, LevelCrestTextColor, 514);
        }

        /// <summary>Warm gold for the level digit, to read as a stamped badge.</summary>
        private static readonly Color LevelCrestTextColor = new(0.96f, 0.84f, 0.52f, 1f);

        /// <summary>
        ///     Show or hide a map texture in the description panel.
        /// </summary>
        private void UpdateMapImage(
            Transform descPanel, Texture2D texture, List<Panels.MapPin> pins)
        {
            if (texture == null)
            {
                if (m_mapImageObject != null)
                {
                    // Clear the hit targets too, or an invisible button from the previous row
                    // keeps swallowing clicks over a hidden map.
                    Panels.MapPinOverlay.Clear(m_mapImageObject);
                    m_renderedPinSignature = null;
                    m_mapImageObject.SetActive(false);

                    // Hiding the map is also where its texture stops being referenced.
                    var raw = m_mapImageObject.GetComponent<RawImage>();
                    var stale = raw != null ? raw.texture as Texture2D : null;
                    if (stale != null)
                    {
                        raw.texture = null;
                        Object.Destroy(stale);
                    }
                }

                return;
            }

            if (m_mapImageObject == null && descPanel != null)
            {
                m_mapImageObject = new GameObject("VV_MapImage",
                    typeof(RectTransform), typeof(RawImage));
                m_mapImageObject.transform.SetParent(descPanel, false);

                // Stay clickable even when another UI group takes over. The work-order editor
                // installs a UIGroupHandler at priority 10000, which makes Valheim deactivate
                // every other group — the crafting panel included — so its CanvasGroup goes
                // non-interactable and map pin clicks are silently swallowed. That is why
                // clicking a second order while editing the first appeared to do nothing.
                // The editor exempts itself the same way; the map has to as well, or the map
                // is dead whenever anything it opens is on screen.
                var cg = m_mapImageObject.AddComponent<CanvasGroup>();
                cg.ignoreParentGroups = true;
                cg.interactable = true;
                cg.blocksRaycasts = true;

                // Wheel zoom and drag pan. On the map itself, which is the parent of every pin
                // hit target, so the events reach it from anywhere over the map.
                m_mapImageObject.AddComponent<Panels.MapViewInput>();
            }

            if (m_mapImageObject != null)
            {
                // Anchored to the lower portion of the description panel, below
                // the title/station/legend text. Re-applied each time so a tweak
                // takes effect without recreating the object.
                // Top edge follows the description's MEASURED height rather than a fixed
                // fraction. The text grows with the villager's state, so a constant anchor
                // either wastes space or lets the text run under the map — which it did.
                var rt = m_mapImageObject.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.05f, 0.04f);
                rt.anchorMax = new Vector2(0.95f, MapTopAnchor(descPanel));
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                m_mapImageObject.SetActive(true);
                var rawImage = m_mapImageObject.GetComponent<RawImage>();
                if (rawImage != null && rawImage.texture != texture)
                {
                    // Every render allocates a fresh 256x256 RGBA32 and nothing used to free the
                    // one it replaced, so an open panel bled a quarter of a megabyte a second.
                    // Native texture memory is not reclaimed by the GC, and zoom re-renders far
                    // more often than the old one-second tick did.
                    var previous = rawImage.texture as Texture2D;
                    rawImage.texture = texture;
                    if (previous != null) Object.Destroy(previous);
                }

                var pinSignature = ComputePinSignature(pins);
                if (pinSignature != m_renderedPinSignature)
                {
                    Panels.MapPinOverlay.Apply(m_mapImageObject, pins, FocusTabRow);
                    m_renderedPinSignature = pinSignature;
                }
            }
        }

        /// <summary>
        ///     Value signature of a pin set: what it is called and where it sits, rounded to the
        ///     pixel the hit target would land on. Compared by value because the pins are rebuilt
        ///     fresh on every detail refresh, so references always differ.
        /// </summary>
        private static string ComputePinSignature(List<Panels.MapPin> pins)
        {
            if (pins == null || pins.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var pin in pins)
                sb.Append(pin.Label).Append('@')
                  .Append(Mathf.RoundToInt(pin.Normalized.x * 256f)).Append(',')
                  .Append(Mathf.RoundToInt(pin.Normalized.y * 256f)).Append('|')
                  .Append(pin.TargetTab).Append(pin.TargetRow).Append(';');

            return sb.ToString();
        }

        private void OnListItemClicked(int index)
        {
            DismissWorkOrderEditor();
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
                m_renderedPinSignature = null;
            }

            if (m_rungRoot != null)
            {
                Destroy(m_rungRoot);
                m_rungRoot = null;
                m_renderedRungSignature = null;
            }

            if (m_levelCrestObject != null)
            {
                Destroy(m_levelCrestObject);
                m_levelCrestObject = null;
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
