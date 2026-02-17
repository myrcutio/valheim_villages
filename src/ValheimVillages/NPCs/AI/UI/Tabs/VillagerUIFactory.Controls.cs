using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Extension of VillagerUIFactory with interactive control builders
    /// (slider, input field, popup panel).
    /// </summary>
    public static partial class VillagerUIFactory
    {
        private static readonly Color ValheimOrange = new(1f, 0.631f, 0.235f, 1f);

        /// <summary>
        /// Create a centered popup panel with a dark Valheim-style background,
        /// title, and vertical content area. Returns the root GO and content transform.
        /// </summary>
        public static (GameObject root, Transform content) CreatePopupPanel(
            string title, float width, float height)
        {
            // Root canvas (overlay, renders on top of everything)
            var rootGO = new GameObject("VV_Popup_" + title);
            var canvas = rootGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            rootGO.AddComponent<GraphicRaycaster>();

            var scaler = rootGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            // Semi-transparent backdrop
            var backdropGO = new GameObject("Backdrop",
                typeof(RectTransform), typeof(Image));
            backdropGO.transform.SetParent(rootGO.transform, false);
            StretchFill(backdropGO);
            backdropGO.GetComponent<Image>().color = new Color(0, 0, 0, 0.5f);

            // Main panel
            var panelGO = new GameObject("Panel",
                typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup));
            panelGO.transform.SetParent(rootGO.transform, false);

            var panelRT = panelGO.GetComponent<RectTransform>();
            panelRT.anchorMin = panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(width, height);

            panelGO.GetComponent<Image>().color =
                new Color(0.12f, 0.09f, 0.06f, 0.95f);

            var outline = panelGO.AddComponent<Outline>();
            outline.effectColor = new Color(0.45f, 0.35f, 0.25f, 1f);
            outline.effectDistance = new Vector2(2, -2);

            var vlg = panelGO.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 6f;
            vlg.padding = new RectOffset(16, 16, 12, 12);

            // Title and divider
            CreateTitle(panelGO.transform, title);
            CreateDivider(panelGO.transform);

            return (rootGO, panelGO.transform);
        }

        /// <summary>
        /// Create a horizontal slider styled to match Valheim's aesthetic.
        /// </summary>
        public static Slider CreateSlider(
            Transform parent, float min, float max,
            bool wholeNumbers, UnityAction<float> onValueChanged)
        {
            var go = new GameObject("VV_Slider",
                typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 20f;
            le.flexibleWidth = 1f;

            // Background track
            var bgGO = new GameObject("Background",
                typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            var bgRT = bgGO.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0, 0.25f);
            bgRT.anchorMax = new Vector2(1, 0.75f);
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color =
                new Color(0.15f, 0.12f, 0.08f, 0.9f);

            // Fill area
            var fillAreaGO = new GameObject("Fill Area",
                typeof(RectTransform));
            fillAreaGO.transform.SetParent(go.transform, false);
            var fillAreaRT = fillAreaGO.GetComponent<RectTransform>();
            fillAreaRT.anchorMin = new Vector2(0, 0.25f);
            fillAreaRT.anchorMax = new Vector2(1, 0.75f);
            fillAreaRT.offsetMin = new Vector2(5, 0);
            fillAreaRT.offsetMax = new Vector2(-15, 0);

            var fillGO = new GameObject("Fill",
                typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = Vector2.one;
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            fillGO.GetComponent<Image>().color =
                new Color(1f, 0.631f, 0.235f, 0.8f);

            // Handle slide area
            var handleAreaGO = new GameObject("Handle Slide Area",
                typeof(RectTransform));
            handleAreaGO.transform.SetParent(go.transform, false);
            var handleAreaRT = handleAreaGO.GetComponent<RectTransform>();
            handleAreaRT.anchorMin = Vector2.zero;
            handleAreaRT.anchorMax = Vector2.one;
            handleAreaRT.offsetMin = new Vector2(10, 0);
            handleAreaRT.offsetMax = new Vector2(-10, 0);

            var handleGO = new GameObject("Handle",
                typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.sizeDelta = new Vector2(20, 0);
            handleRT.anchorMin = new Vector2(0, 0);
            handleRT.anchorMax = new Vector2(0, 1);
            handleGO.GetComponent<Image>().color = ValheimBeige;

            // Configure slider component
            var slider = go.GetComponent<Slider>();
            slider.fillRect = fillRT;
            slider.handleRect = handleRT;
            slider.targetGraphic = handleGO.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.value = min;

            if (onValueChanged != null)
                slider.onValueChanged.AddListener(onValueChanged);

            return slider;
        }

        /// <summary>
        /// Create a numeric input field using Unity's legacy InputField.
        /// </summary>
        public static InputField CreateInputField(
            Transform parent, string initialText,
            UnityAction<string> onEndEdit)
        {
            var go = new GameObject("VV_InputField",
                typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);

            go.GetComponent<Image>().color =
                new Color(0.10f, 0.08f, 0.06f, 0.9f);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 60f;
            le.preferredHeight = 28f;
            le.flexibleWidth = 0f;

            // Text child
            var textGO = new GameObject("Text",
                typeof(RectTransform), typeof(Text));
            textGO.transform.SetParent(go.transform, false);
            StretchFill(textGO);
            var textRT = textGO.GetComponent<RectTransform>();
            textRT.offsetMin = new Vector2(4, 2);
            textRT.offsetMax = new Vector2(-4, -2);

            var text = textGO.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 14;
            text.color = ValheimBeige;
            text.alignment = TextAnchor.MiddleCenter;
            text.supportRichText = false;

            // Outline for border
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.35f, 0.28f, 0.18f, 1f);
            outline.effectDistance = new Vector2(1, -1);

            var inputField = go.GetComponent<InputField>();
            inputField.textComponent = text;
            inputField.text = initialText;
            inputField.contentType = InputField.ContentType.IntegerNumber;
            inputField.characterLimit = 4;

            if (onEndEdit != null)
                inputField.onEndEdit.AddListener(onEndEdit);

            return inputField;
        }
    }
}
