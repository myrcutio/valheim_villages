using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Factory for creating Unity UI elements that match Valheim's native style.
    ///     Clones text/button elements from InventoryGui for consistent look.
    /// </summary>
    public static partial class VillagerUIFactory
    {
        private static readonly Color ValheimBeige = new(0.8529f, 0.725f, 0.5331f, 1f);
        private static readonly Color ValheimYellow = new(1f, 0.889f, 0f, 1f);

        /// <summary>
        ///     Cached reference to a text template GameObject (found once, reused).
        ///     We avoid accessing m_craftingStationName.gameObject directly because
        ///     it pulls in the TMPro assembly dependency at compile time.
        /// </summary>
        private static GameObject s_textTemplate;

        /// <summary>Create a text label by cloning a TMPro text from the UI.</summary>
        public static GameObject CreateLabel(
            Transform parent, string text,
            float fontSize = 16f, Color? color = null)
        {
            EnsureTextTemplate();
            if (s_textTemplate == null) return null;

            var labelGO = Object.Instantiate(s_textTemplate, parent);
            labelGO.name = "VV_Label";

            SetTMPText(labelGO, text);
            SetTMPProperties(labelGO, fontSize, color ?? ValheimBeige, 257);

            // Auto-height for vertical layout
            var csf = labelGO.GetComponent<ContentSizeFitter>()
                      ?? labelGO.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            labelGO.SetActive(true);
            return labelGO;
        }

        /// <summary>Create a button cloned from the Craft button.</summary>
        public static Button CreateButton(
            Transform parent, string text, UnityAction onClick)
        {
            var gui = InventoryGui.instance;
            if (gui?.m_craftButton == null) return null;

            var btnGO = Object.Instantiate(
                gui.m_craftButton.gameObject, parent);
            btnGO.name = $"VV_Btn_{text}";

            SetTMPText(btnGO, text);

            var le = btnGO.GetComponent<LayoutElement>()
                     ?? btnGO.AddComponent<LayoutElement>();
            le.preferredHeight = 36f;
            le.flexibleWidth = 1f;

            var btn = btnGO.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(onClick);
                // Cloned from the craft button which may be in a disabled
                // state — force interactable so it's visually active
                btn.interactable = true;
            }

            btnGO.SetActive(true);
            return btn;
        }

        /// <summary>Create a horizontal layout group container.</summary>
        public static GameObject CreateHorizontalGroup(
            Transform parent, float spacing = 4f, float height = 36f,
            bool forceExpandWidth = true)
        {
            var go = new GameObject("HGroup",
                typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);

            var hlg = go.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = spacing;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = forceExpandWidth;
            hlg.childForceExpandHeight = false;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;

            return go;
        }

        /// <summary>Create a vertical layout group container.</summary>
        public static GameObject CreateVerticalGroup(
            Transform parent, float spacing = 2f)
        {
            var go = new GameObject("VGroup",
                typeof(RectTransform), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);

            var vlg = go.GetComponent<VerticalLayoutGroup>();
            vlg.spacing = spacing;
            vlg.childAlignment = TextAnchor.MiddleLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

            return go;
        }

        /// <summary>Create a fixed-size square icon image.</summary>
        public static Image CreateIcon(Transform parent, float size)
        {
            var go = new GameObject("VV_Icon",
                typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = size;
            le.preferredHeight = size;
            le.flexibleWidth = 0f;

            var img = go.GetComponent<Image>();
            img.preserveAspect = true;
            return img;
        }

        /// <summary>Create a thin horizontal divider line.</summary>
        public static void CreateDivider(Transform parent)
        {
            var go = new GameObject("Divider",
                typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            go.GetComponent<Image>().color =
                new Color(0.6f, 0.5f, 0.3f, 0.6f);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 2f;
            le.flexibleWidth = 1f;
        }

        /// <summary>Create a spacer that expands to fill leftover vertical space,
        /// pushing subsequent siblings to the bottom of a vertical layout.</summary>
        public static void CreateFlexibleSpacer(Transform parent)
        {
            var go = new GameObject("FlexSpacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;
            le.flexibleWidth = 1f;
        }

        /// <summary>Create an empty spacer of a given height.</summary>
        public static void CreateSpacer(Transform parent, float height = 8f)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;
        }

        #region Template & TMPro Reflection Helpers

        /// <summary>
        ///     Find a TMPro text template from the InventoryGui via reflection
        ///     to avoid compile-time TMPro assembly dependency.
        /// </summary>
        private static void EnsureTextTemplate()
        {
            if (s_textTemplate != null) return;

            var gui = InventoryGui.instance;
            if (gui == null) return;

            // Access m_craftingStationName via reflection to avoid
            // pulling in the TMPro assembly at compile time
            var field = typeof(InventoryGui).GetField("m_craftingStationName",
                BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return;

            var tmpText = field.GetValue(gui) as Component;
            if (tmpText != null)
                s_textTemplate = tmpText.gameObject;
        }

        /// <summary>
        ///     Override the crafting-panel header (Valheim's station-name label) with
        ///     <paramref name="text" />. The villager's virtual station keeps its
        ///     internal "$vv_..." m_name (needed for matching/cleanup); this only
        ///     changes what the player sees. Call each frame (LateUpdate) — Valheim
        ///     rewrites the label from the station name in UpdateCraftingPanel.
        /// </summary>
        public static void SetCraftingStationName(InventoryGui gui, string text)
        {
            if (gui == null) return;
            var field = typeof(InventoryGui).GetField("m_craftingStationName",
                BindingFlags.Public | BindingFlags.Instance);
            var comp = field?.GetValue(gui) as Component;
            if (comp != null) SetTMPText(comp.gameObject, text);
        }

        public static void SetTMPText(GameObject go, string text)
        {
            if (go == null)
            {
                Plugin.Log?.LogWarning("[UIFactory] SetTMPText called with null GameObject");
                return;
            }

            var comp = FindTMP(go);
            if (comp == null)
            {
                Plugin.Log?.LogWarning(
                    $"[UIFactory] SetTMPText: no TextMeshPro component under '{go.name}'");
                return;
            }

            var prop = comp.GetType().GetProperty("text");
            if (prop == null)
            {
                Plugin.Log?.LogWarning(
                    $"[UIFactory] SetTMPText: '{comp.GetType().Name}' has no 'text' property");
                return;
            }

            // Valheim disables these TMP components (Behaviour.enabled = false) when
            // no recipe is selected — leaving the GameObject active but the text
            // un-rendered. Re-enable so our text actually shows.
            if (comp is Behaviour behaviour && !behaviour.enabled)
                behaviour.enabled = true;

            prop.SetValue(comp, text);
        }

        public static void SetTMPProperties(
            GameObject go, float fontSize, Color color, int alignment)
        {
            var comp = FindTMP(go);
            if (comp == null) return;

            var type = comp.GetType();
            type.GetProperty("fontSize")?.SetValue(comp, fontSize);
            type.GetProperty("color")?.SetValue(comp, color);
            type.GetProperty("enableWordWrapping")?.SetValue(comp, true);

            var alignProp = type.GetProperty("alignment");
            if (alignProp != null)
                alignProp.SetValue(comp,
                    Enum.ToObject(alignProp.PropertyType, alignment));
        }

        private static Component FindTMP(GameObject go)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
                if (comp != null &&
                    comp.GetType().Name.Contains("TextMeshPro"))
                    return comp;
            return null;
        }

        private static void StretchFill(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        #endregion
    }
}