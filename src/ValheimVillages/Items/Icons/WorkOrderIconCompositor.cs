using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Composites a production item's icon onto a work order's parchment icon
    ///     so players can tell at a glance which item the work order produces.
    ///     The item is drawn large and centered on the parchment. A status badge
    ///     (completed, in-progress, unworkable) is drawn in the top-right corner.
    ///
    ///     The item icon is downscaled on the GPU (bilinear, sampling only the
    ///     sprite's sub-rect) into a small render texture, so the only CPU readback
    ///     is the 42×42 overlay — never the item's full source atlas. All
    ///     compositing then runs as Color32[] array math, avoiding per-pixel
    ///     Texture2D.GetPixel/SetPixel calls.
    ///
    ///     Two-tier cache avoids recompositing on every UI open:
    ///     Tier 1 (base): parchment + item overlay — expensive, cached permanently.
    ///     Tier 2 (final): base + status badge — cheap to stamp, keyed by status.
    /// </summary>
    public static class WorkOrderIconCompositor
    {
        // Parchment icons are authored at 64×64.
        private const int IconSize = 64;

        // Item overlay centered on the parchment, large enough to recognize
        // the item at a glance.
        private const int OverlaySize = 42;
        private const int OverlayX = (IconSize - OverlaySize) / 2; // 11
        private const int OverlayY = (IconSize - OverlaySize) / 2; // 11

        /// <summary>
        ///     Tier 1: base composite pixels (parchment + item, no status).
        ///     Key: "{workOrderPrefab}_{itemPrefab}"
        /// </summary>
        private static readonly Dictionary<string, CachedBase> _baseCache = new();

        /// <summary>
        ///     Tier 2: final sprites with status badge stamped on.
        ///     Key: "{workOrderPrefab}_{itemPrefab}_{status}"
        /// </summary>
        private static readonly Dictionary<string, Sprite> _spriteCache = new();

        /// <summary>
        ///     Parchment pixel cache, keyed by sprite instance id. The parchment
        ///     PNGs are already readable RGBA32, so their pixels are read once.
        /// </summary>
        private static readonly Dictionary<int, Color32[]> _parchmentCache = new();

        /// <summary>
        ///     If the item is a configured work order (has wo_item custom data),
        ///     overlay the production item's icon onto the parchment, then draw
        ///     the status badge. Deep-copies SharedData so only this instance changes.
        /// </summary>
        public static void ApplyOverlay(
            ItemDrop.ItemData itemData,
            WorkOrderStatus status = WorkOrderStatus.Pending)
        {
            if (itemData?.m_shared == null) return;
            if (itemData.m_customData == null) return;
            if (!itemData.m_customData.TryGetValue("wo_item", out var itemPrefab))
                return;
            if (string.IsNullOrEmpty(itemPrefab)) return;

            // Always composite onto the clean parchment from the prefab, never
            // the instance's current icon (which may already be a stale composite,
            // or the clone base prefab's icon if the parchment failed to load).
            var parchment = ResolveParchment(itemData);
            if (parchment == null) return;

            var woPrefab = itemData.m_dropPrefab?.name ?? "wo";
            var spriteKey = $"{woPrefab}_{itemPrefab}_{status}";

            // Fast path: final sprite already cached for this exact status
            if (_spriteCache.TryGetValue(spriteKey, out var cached))
            {
                EnsureOwnSharedData(itemData);
                itemData.m_shared.m_icons = new[] { cached };
                return;
            }

            // Ensure the base composite (parchment + item) is cached
            var baseKey = $"{woPrefab}_{itemPrefab}";
            if (!_baseCache.TryGetValue(baseKey, out var baseCached))
            {
                baseCached = BuildBaseComposite(parchment, itemPrefab);
                if (baseCached == null) return;
                _baseCache[baseKey] = baseCached;
            }

            // Stamp status badge onto a copy of the base pixels
            var sprite = StampStatus(baseCached, status);
            if (sprite == null) return;

            _spriteCache[spriteKey] = sprite;
            EnsureOwnSharedData(itemData);
            itemData.m_shared.m_icons = new[] { sprite };
        }

        /// <summary>
        ///     Scan an inventory and apply overlays to every work order,
        ///     resolving status from a pre-scanned list of nearby containers.
        /// </summary>
        public static void EnsureOverlays(
            Inventory inventory,
            Vector3? containerPos = null,
            List<Container> containers = null)
        {
            if (inventory == null) return;

            foreach (var item in inventory.GetAllItems())
            {
                if (item.m_customData == null) continue;
                if (!item.m_customData.ContainsKey("wo_item")) continue;

                var status = WorkOrderStatusResolver.Resolve(
                    item, containerPos, containers);
                ApplyOverlay(item, status);
            }
        }

        #region Tier 1: base composite

        private static CachedBase BuildBaseComposite(
            Sprite parchment, string itemPrefab)
        {
            var prodSprite = GetItemSprite(itemPrefab);
            if (prodSprite == null) return null;

            try
            {
                // Parchment is a readable PNG: read its pixels directly (cached).
                var basePixels = GetParchmentPixels(parchment);
                if (basePixels == null) return null;

                // GPU-scale only the item's sprite rect down to the overlay size.
                var itemPixels = ScaleSpriteToBuffer(prodSprite, OverlaySize);
                if (itemPixels == null) return null;

                // Composite the item over a fresh copy of the parchment pixels,
                // clipped to the parchment's opaque (scroll) area.
                var pixels = (Color32[])basePixels.Clone();
                CompositeOver(
                    pixels, IconSize, IconSize,
                    itemPixels, OverlaySize, OverlaySize,
                    OverlayX, OverlayY, clipToDstAlpha: true);

                return new CachedBase
                {
                    Pixels = pixels,
                    Width = IconSize,
                    Height = IconSize,
                };
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError(
                    $"Failed to build base composite for {itemPrefab}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Tier 2: status stamp

        private static Sprite StampStatus(
            CachedBase baseCached, WorkOrderStatus status)
        {
            try
            {
                var pixels = (Color32[])baseCached.Pixels.Clone();
                WorkOrderStatusOverlay.Draw(
                    pixels, baseCached.Width, baseCached.Height, status);

                var tex = new Texture2D(
                    baseCached.Width, baseCached.Height,
                    TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply();

                return Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError(
                    $"Failed to stamp status {status}: {ex.Message}");
                return null;
            }
        }

        #endregion

        /// <summary>
        ///     Cached base composite: raw RGBA32 pixels from the parchment + item
        ///     overlay, before any status badge is applied.
        /// </summary>
        private class CachedBase
        {
            public int Height;
            public Color32[] Pixels;
            public int Width;
        }

        #region Helpers

        private static void EnsureOwnSharedData(ItemDrop.ItemData item)
        {
            var prefab = item.m_dropPrefab;
            if (prefab == null) return;

            var prefabDrop = prefab.GetComponent<ItemDrop>();
            if (prefabDrop != null &&
                ReferenceEquals(item.m_shared, prefabDrop.m_itemData.m_shared))
                item.m_shared = JsonUtility.FromJson<
                    ItemDrop.ItemData.SharedData>(
                    JsonUtility.ToJson(item.m_shared));
        }

        /// <summary>
        ///     Resolve the clean parchment scroll to composite onto. Prefers the
        ///     work order prefab's icon (always the raw station scroll, set at
        ///     registration and never mutated) over the instance icon, which may
        ///     already carry a stale composite.
        /// </summary>
        private static Sprite ResolveParchment(ItemDrop.ItemData itemData)
        {
            var prefabDrop = itemData.m_dropPrefab != null
                ? itemData.m_dropPrefab.GetComponent<ItemDrop>()
                : null;
            var prefabIcons = prefabDrop?.m_itemData?.m_shared?.m_icons;
            if (prefabIcons != null && prefabIcons.Length > 0 && prefabIcons[0] != null)
                return prefabIcons[0];

            var icons = itemData.m_shared?.m_icons;
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }

        private static Sprite GetItemSprite(string prefabName)
        {
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null) return null;
            var drop = prefab.GetComponent<ItemDrop>();
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }

        /// <summary>
        ///     Read (and cache) the parchment sprite's pixels. Parchment icons
        ///     come from embedded PNGs decoded as readable RGBA32, so a direct
        ///     GetPixels32 works without a GPU roundtrip.
        /// </summary>
        private static Color32[] GetParchmentPixels(Sprite parchment)
        {
            if (!(parchment?.texture is Texture2D tex)) return null;

            var id = tex.GetInstanceID();
            if (_parchmentCache.TryGetValue(id, out var cached))
                return cached;

            var pixels = tex.GetPixels32();
            _parchmentCache[id] = pixels;
            return pixels;
        }

        /// <summary>
        ///     Downscale a sprite's sub-rect to a square buffer on the GPU using
        ///     bilinear filtering, then read back only that small buffer. Works
        ///     for compressed / non-readable source textures (atlases included)
        ///     and never reads back the full source.
        /// </summary>
        private static Color32[] ScaleSpriteToBuffer(Sprite sprite, int size)
        {
            var src = sprite.texture;
            if (src == null) return null;

            // textureRect is the sprite's region in its (possibly atlased) texture.
            var rect = sprite.textureRect;
            if (rect.width < 1 || rect.height < 1) rect = sprite.rect;

            float tw = src.width;
            float th = src.height;
            var scale = new Vector2(rect.width / tw, rect.height / th);
            var offset = new Vector2(rect.x / tw, rect.y / th);

            // Ensure the source samples bilinearly for a smooth downscale.
            var prevFilter = src.filterMode;
            src.filterMode = FilterMode.Bilinear;

            var rt = RenderTexture.GetTemporary(
                size, size, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;

            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt, scale, offset);
                RenderTexture.active = rt;

                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                var pixels = tex.GetPixels32();
                Object.Destroy(tex);
                return pixels;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                src.filterMode = prevFilter;
            }
        }

        /// <summary>
        ///     Alpha-blend a source buffer over a destination buffer at (dstX, dstY)
        ///     using straight src-over compositing. Both buffers are RGBA32 with a
        ///     bottom-left origin (GetPixels32 layout). When clipToDstAlpha is set,
        ///     source pixels are only drawn where the destination is already opaque
        ///     (keeps the item within the parchment silhouette).
        /// </summary>
        private static void CompositeOver(
            Color32[] dst, int dstW, int dstH,
            Color32[] src, int srcW, int srcH,
            int dstX, int dstY, bool clipToDstAlpha)
        {
            for (var sy = 0; sy < srcH; sy++)
            {
                var py = dstY + sy;
                if (py < 0 || py >= dstH) continue;

                for (var sx = 0; sx < srcW; sx++)
                {
                    var px = dstX + sx;
                    if (px < 0 || px >= dstW) continue;

                    var ic = src[sy * srcW + sx];
                    if (ic.a == 0) continue;

                    var di = py * dstW + px;
                    var bc = dst[di];
                    if (clipToDstAlpha && bc.a < 26) continue;

                    int a = ic.a;
                    int inv = 255 - a;
                    dst[di] = new Color32(
                        (byte)((bc.r * inv + ic.r * a) / 255),
                        (byte)((bc.g * inv + ic.g * a) / 255),
                        (byte)((bc.b * inv + ic.b * a) / 255),
                        Math.Max(bc.a, ic.a));
                }
            }
        }

        #endregion
    }
}
