using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Lays interactive markers over a village map: the pin's icon, a label naming it, a
    ///     hover tooltip, and a click that opens the relevant row.
    ///
    ///     <para>Each pin is a small hierarchy rather than one object, because the parts behave
    ///     differently: the ICON grows under the cursor, while the label must not — a label that
    ///     scales with its pin is unreadable mid-animation and shoves its neighbours around.
    ///     Scaling the root would have moved both.</para>
    ///
    ///     <code>
    ///     VV_MapPinHit      transparent raycast target, sized to the icon
    ///       |- VV_MapPinIcon      the sprite; the only thing hover scales
    ///       |    \- VV_MapPinCrestIcon   station icon, when the pin wears a crest
    ///       \- VV_MapPinChip      label, on a button plate when the pin is clickable
    ///     </code>
    ///
    ///     <para>Tooltips use Valheim's own <c>UITooltip</c> so they match every other tooltip
    ///     in the game, which is the point of a map trying to look like the game's own.</para>
    /// </summary>
    internal static class MapPinOverlay
    {
        private const string ChildName = "VV_MapPinHit";
        private const string IconChildName = "VV_MapPinIcon";

        /// <summary>Hit-target size for a pin with no icon, which is just a dot on the texture.</summary>
        private const float HitSize = 36f;

        /// <summary>Icon size. Generous: an authored icon needs room to be recognisable.</summary>
        private const float IconSize = 42f;

        /// <summary>How much of a crest the station icon covers, so the shield still frames it.</summary>
        private const float CrestIconFraction = 0.58f;

        private const float ChipHeight = 17f;

        /// <summary>Gap between icon and label. Small on purpose — they are one marker.</summary>
        private const float ChipGap = 1f;

        private const float ChipCharWidth = 7.5f;

        /// <summary>TMP TextAlignmentOptions.Center. 257 is TopLeft, which is NOT what we want.</summary>
        private const int TmpCenter = 514;

        private static readonly Color ChipTextColor = new(0.92f, 0.86f, 0.72f, 1f);

        private static readonly Type s_tooltipType =
            typeof(Localization).Assembly.GetType("UITooltip");

        private static bool s_warnedNoTooltip;
        private static bool s_warnedNoTooltipPrefab;
        private static GameObject s_tooltipPrefab;

        /// <summary>
        ///     The tooltip prefab every UITooltip in the scene shares, borrowed from whichever
        ///     one already has it. Cached on first hit only — a miss means the UI was not built
        ///     yet, and caching that would leave pins mute for the session.
        /// </summary>
        private static GameObject TooltipPrefab()
        {
            if (s_tooltipPrefab != null) return s_tooltipPrefab;
            if (s_tooltipType == null) return null;

            var field = s_tooltipType.GetField(
                "m_tooltipPrefab", BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return null;

            foreach (var existing in Resources.FindObjectsOfTypeAll(s_tooltipType))
            {
                if (field.GetValue(existing) is not GameObject prefab || prefab == null) continue;
                s_tooltipPrefab = prefab;
                return prefab;
            }

            return null;
        }

        /// <summary>Rebuild the markers for <paramref name="pins" />. Null or empty clears them.</summary>
        public static void Apply(
            GameObject mapObject, List<MapPin> pins, Action<string, int> onClick)
        {
            if (mapObject == null) return;

            Clear(mapObject);
            if (pins == null || pins.Count == 0) return;

            foreach (var pin in pins) Build(mapObject.transform, pin, onClick);
        }

        /// <summary>
        ///     Drop every marker. Rebuilt wholesale rather than diffed: the set changes when the
        ///     player selects a different row, so this runs on a click, not a frame.
        /// </summary>
        public static void Clear(GameObject mapObject)
        {
            if (mapObject == null) return;

            for (var i = mapObject.transform.childCount - 1; i >= 0; i--)
            {
                var child = mapObject.transform.GetChild(i);
                if (child != null && child.name == ChildName)
                    UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        private static void Build(Transform parent, MapPin pin, Action<string, int> onClick)
        {
            var box = BoxSize(pin);

            var root = new GameObject(ChildName, typeof(RectTransform), typeof(Image));
            root.transform.SetParent(parent, false);

            var rt = root.GetComponent<RectTransform>();
            rt.anchorMin = pin.Normalized;
            rt.anchorMax = pin.Normalized;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = box;

            // The root is the hit target and nothing else: invisible, but raycastable so the
            // whole icon area answers the pointer.
            var hit = root.GetComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;

            var icon = BuildIcon(root.transform, pin, box);
            var chip = BuildChip(root.transform, pin, box);

            SetTooltip(root, Label(pin));

            if (!pin.Clickable && !pin.OpensOrder) return;

            // Not a Button: Button only ever reports a LEFT click, so the two actions cannot
            // be told apart through it.
            var tab = pin.TargetTab;
            var row = pin.TargetRow;
            var world = pin.World;
            var item = pin.OrderItem;

            var click = root.AddComponent<MapPinClick>();
            click.Bind(
                pin.Clickable ? () => onClick?.Invoke(tab, row) : null,
                pin.OpensOrder ? () => MapOrderOpener.Open(world, item) : null);

            // Hover feedback is only on pins that DO something: it promises the click will
            // land somewhere, so putting it on a landmark would lie.
            var hover = root.AddComponent<MapPinHover>();
            hover.Bind(icon, chip);
        }

        /// <summary>Pin box: the crest's own aspect, a square for a bare icon, else the dot's.</summary>
        private static Vector2 BoxSize(MapPin pin)
        {
            if (pin.Backdrop != null) return new Vector2(IconSize * UiSprites.CrestAspect, IconSize);
            return pin.Icon != null
                ? new Vector2(IconSize, IconSize)
                : new Vector2(HitSize, HitSize);
        }

        /// <summary>
        ///     The pin's sprite, as its own object so hover can scale it alone. Returns null
        ///     when the pin has no icon — then the dot baked into the map shows through.
        /// </summary>
        private static RectTransform BuildIcon(Transform root, MapPin pin, Vector2 box)
        {
            var sprite = pin.Backdrop ?? pin.Icon;
            if (sprite == null) return null;

            var go = new GameObject(IconChildName, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;

            // Only the player arrow rotates; applied here rather than to the root so the chip
            // below stays upright.
            if (!Mathf.Approximately(pin.Rotation, 0f))
                rt.localRotation = Quaternion.Euler(0f, 0f, pin.Rotation);

            if (pin.Backdrop != null && pin.Icon != null) AddCrestIcon(go.transform, pin.Icon, box);
            return rt;
        }

        /// <summary>The station icon, centred on its crest. Never raycastable — the root owns that.</summary>
        private static void AddCrestIcon(Transform crest, Sprite icon, Vector2 box)
        {
            var go = new GameObject("VV_MapPinCrestIcon", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(crest, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = box * CrestIconFraction;

            var img = go.GetComponent<Image>();
            img.sprite = icon;
            img.preserveAspect = true;
            img.raycastTarget = false;
        }

        /// <summary>
        ///     The label under a pin. A clickable pin gets a button plate behind it, because the
        ///     plate is what says "this is pressable"; a landmark gets bare text, since a button
        ///     you cannot press is a worse lie than no button at all.
        /// </summary>
        private static Image BuildChip(Transform root, MapPin pin, Vector2 box)
        {
            if (string.IsNullOrEmpty(pin.ShortLabel)) return null;

            var width = Mathf.Clamp(pin.ShortLabel.Length * ChipCharWidth + 12f, 28f, 96f);

            var go = new GameObject("VV_MapPinChip", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -ChipGap);
            rt.sizeDelta = new Vector2(width, ChipHeight);

            var img = go.GetComponent<Image>();
            img.raycastTarget = false;

            if (pin.Clickable)
            {
                img.sprite = UiSprites.Button();
                // Sliced only when the sprite carries borders; slicing a border-less sprite
                // just warns and draws it simple anyway.
                img.type = img.sprite != null && img.sprite.border.sqrMagnitude > 0f
                    ? Image.Type.Sliced
                    : Image.Type.Simple;
                if (img.sprite == null) img.color = new Color(0.12f, 0.10f, 0.08f, 0.85f);
            }
            else
            {
                img.enabled = false; // text only
            }

            var label = VillagerUIFactory.CreateLabel(go.transform, pin.ShortLabel, 12f);
            if (label == null) return pin.Clickable ? img : null;

            var lrt = label.GetComponent<RectTransform>();
            if (lrt != null)
            {
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = Vector2.zero;
                lrt.offsetMax = Vector2.zero;
            }

            VillagerUIFactory.SetTMPProperties(label, 12f, ChipTextColor, TmpCenter);
            return pin.Clickable ? img : null;
        }

        /// <summary>
        ///     Tooltip text. An off-frame pin says so, because it was clamped to the border and
        ///     is NOT where it appears to be — without that note a dot on the edge reads as
        ///     something standing at the edge of the village.
        /// </summary>
        private static string Label(MapPin pin)
        {
            var label = pin.Label;
            if (pin.OffFrame) label += "\n<color=#9FB4C7>(off the map, this way)</color>";
            if (pin.Clickable) label += "\n<color=#9FB4C7>Left click: open its row</color>";
            if (pin.OpensOrder) label += "\n<color=#9FB4C7>Right click: edit this order</color>";
            return label;
        }

        /// <summary>
        ///     Attach Valheim's UITooltip by reflection — the type lives in assembly_guiutils,
        ///     which the mod does not reference directly.
        /// </summary>
        private static void SetTooltip(GameObject go, string text)
        {
            if (s_tooltipType == null)
            {
                // Say so once rather than shipping pins that quietly do nothing on hover.
                if (!s_warnedNoTooltip)
                {
                    s_warnedNoTooltip = true;
                    Plugin.Log?.LogWarning(
                        "[MapPinOverlay] UITooltip type not found in assembly_guiutils — map " +
                        "pins will have no hover text. Did the game move or rename it?");
                }

                return;
            }

            // UITooltip instantiates m_tooltipPrefab on hover. AddComponent leaves that field
            // NULL, so a bare component throws ArgumentException ("The Object you want to
            // instantiate is null") on EVERY pointer-enter and floods the log. Valheim's own
            // tooltips avoid it by being cloned from objects that already carry the reference;
            // a freshly added one has to borrow it.
            var donorPrefab = TooltipPrefab();
            if (donorPrefab == null)
            {
                if (!s_warnedNoTooltipPrefab)
                {
                    s_warnedNoTooltipPrefab = true;
                    Plugin.Log?.LogWarning(
                        "[MapPinOverlay] no existing UITooltip to borrow m_tooltipPrefab from — " +
                        "map pins will have no hover text. Adding one anyway would throw on " +
                        "every hover.");
                }

                return;
            }

            var tt = go.AddComponent(s_tooltipType);
            s_tooltipType.GetField("m_tooltipPrefab", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(tt, donorPrefab);
            s_tooltipType.GetField("m_text", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(tt, text);
            s_tooltipType.GetField("m_topic", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(tt, "");
        }
    }
}
