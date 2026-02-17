using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Factory for creating Unity UI elements that match Valheim's native style.
    /// Clones text/button elements from InventoryGui for consistent look.
    /// </summary>
    public static partial class VillagerUIFactory
    {
        private static readonly Color ValheimBeige = new(0.8529f, 0.725f, 0.5331f, 1f);
        private static readonly Color ValheimYellow = new(1f, 0.889f, 0f, 1f);

        /// <summary>
        /// Cached reference to a text template GameObject (found once, reused).
        /// We avoid accessing m_craftingStationName.gameObject directly because
        /// it pulls in the TMPro assembly dependency at compile time.
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

        /// <summary>Create a section title in Valheim yellow.</summary>
        public static GameObject CreateTitle(Transform parent, string text)
        {
            return CreateLabel(parent, text, 18f, ValheimYellow);
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
            Transform parent, float spacing = 4f, float height = 36f)
        {
            var go = new GameObject("HGroup",
                typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);

            var hlg = go.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = spacing;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = false;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;

            return go;
        }

        /// <summary>Create a thin horizontal divider line.</summary>
        public static GameObject CreateDivider(Transform parent)
        {
            var go = new GameObject("Divider",
                typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            go.GetComponent<Image>().color =
                new Color(0.6f, 0.5f, 0.3f, 0.6f);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 2f;
            le.flexibleWidth = 1f;

            return go;
        }

        /// <summary>Create an empty spacer of a given height.</summary>
        public static GameObject CreateSpacer(
            Transform parent, float height = 8f)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;

            return go;
        }

        /// <summary>Create a ScrollView that fills its parent.</summary>
        public static (ScrollRect scroll, Transform content)
            CreateScrollView(Transform parent)
        {
            var scrollGO = new GameObject("ScrollView",
                typeof(RectTransform), typeof(ScrollRect));
            scrollGO.transform.SetParent(parent, false);
            StretchFill(scrollGO);

            var viewportGO = new GameObject("Viewport",
                typeof(RectTransform), typeof(Mask), typeof(Image));
            viewportGO.transform.SetParent(scrollGO.transform, false);
            StretchFill(viewportGO);
            viewportGO.GetComponent<Image>().color = new Color(0, 0, 0, 0.01f);

            var contentGO = new GameObject("Content",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            contentGO.transform.SetParent(viewportGO.transform, false);

            var crt = contentGO.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1);
            crt.anchorMax = new Vector2(1, 1);
            crt.pivot = new Vector2(0.5f, 1);
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = Vector2.zero;

            var vlg = contentGO.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(10, 10, 10, 10);

            var csf = contentGO.GetComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollGO.GetComponent<ScrollRect>();
            scroll.viewport = viewportGO.GetComponent<RectTransform>();
            scroll.content = crt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 30f;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            return (scroll, contentGO.transform);
        }

        #region Template & TMPro Reflection Helpers

        /// <summary>
        /// Find a TMPro text template from the InventoryGui via reflection
        /// to avoid compile-time TMPro assembly dependency.
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

        public static void SetTMPText(GameObject go, string text)
        {
            var comp = FindTMP(go);
            comp?.GetType().GetProperty("text")?.SetValue(comp, text);
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
                    System.Enum.ToObject(alignProp.PropertyType, alignment));
        }

        private static Component FindTMP(GameObject go)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp != null &&
                    comp.GetType().Name.Contains("TextMeshPro"))
                    return comp;
            }
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
