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
    ///     Uses a two-tier cache to avoid recompositing on every UI open:
    ///     Tier 1 (base): parchment + item overlay — expensive, cached permanently.
    ///     Tier 2 (final): base + status badge — cheap to stamp, keyed by status.
    /// </summary>
    public static class WorkOrderIconCompositor
    {
        // Item overlay centered on the 64×64 parchment, large enough to
        // recognize the item at a glance.
        private const int OverlaySize = 42;
        private const int OverlayX = 11; // (64 - 42) / 2
        private const int OverlayY = 11;

        /// <summary>
        ///     Tier 1: base composite pixels (parchment + item, no status).
        ///     Key: "{workOrderPrefab}_{itemPrefab}"
        ///     Value: RGBA32 pixel array + dimensions. Never cleared during play.
        /// </summary>
        private static readonly Dictionary<string, CachedBase> _baseCache = new();

        /// <summary>
        ///     Tier 2: final sprites with status badge stamped on.
        ///     Key: "{workOrderPrefab}_{itemPrefab}_{status}"
        ///     All status variants coexist; no need to clear on status change.
        /// </summary>
        private static readonly Dictionary<string, Sprite> _spriteCache = new();

        /// <summary>
        ///     Clear both cache tiers (hot reload / world unload).
        /// </summary>
        public static void ClearCache()
        {
            _baseCache.Clear();
            _spriteCache.Clear();
        }

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

            var baseIcons = itemData.m_shared.m_icons;
            if (baseIcons == null || baseIcons.Length == 0) return;

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
                baseCached = BuildBaseComposite(baseIcons[0], itemPrefab);
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
                var baseTex = MakeReadable(parchment.texture);
                var itemTex = MakeReadable(prodSprite);

                BlitScaled(baseTex, itemTex, prodSprite.rect,
                    OverlayX, OverlayY, OverlaySize);

                var pixels = baseTex.GetPixels32();
                var result = new CachedBase
                {
                    Pixels = pixels,
                    Width = baseTex.width,
                    Height = baseTex.height,
                };

                Object.Destroy(baseTex);
                Object.Destroy(itemTex);
                return result;
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
                var tex = new Texture2D(
                    baseCached.Width, baseCached.Height,
                    TextureFormat.RGBA32, false);

                // Bulk-copy base pixels (fast array copy, no GPU involved)
                tex.SetPixels32(baseCached.Pixels);

                // Draw the lightweight status badge on top
                WorkOrderStatusOverlay.Draw(tex, status);

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

        private static Sprite GetItemSprite(string prefabName)
        {
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null) return null;
            var drop = prefab.GetComponent<ItemDrop>();
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }

        /// <summary>
        ///     Blit a sprite region onto a destination texture with alpha blending.
        ///     Only draws on pixels where the destination already has alpha.
        /// </summary>
        private static void BlitScaled(
            Texture2D dst, Texture2D src, Rect srcRect,
            int dstX, int dstY, int dstSize)
        {
            for (var dy = 0; dy < dstSize; dy++)
            for (var dx = 0; dx < dstSize; dx++)
            {
                var srcPx = (int)srcRect.x +
                            (int)(dx * srcRect.width / dstSize);
                var srcPy = (int)srcRect.y +
                            (int)(dy * srcRect.height / dstSize);
                srcPx = Mathf.Clamp(srcPx, 0, src.width - 1);
                srcPy = Mathf.Clamp(srcPy, 0, src.height - 1);

                var ic = src.GetPixel(srcPx, srcPy);
                if (ic.a < 0.1f) continue;

                var px = dstX + dx;
                var py = dstY + dy;
                if (px >= dst.width || py >= dst.height) continue;

                var bc = dst.GetPixel(px, py);
                if (bc.a < 0.1f) continue;

                var a = ic.a;
                dst.SetPixel(px, py, new Color(
                    bc.r * (1 - a) + ic.r * a,
                    bc.g * (1 - a) + ic.g * a,
                    bc.b * (1 - a) + ic.b * a,
                    Mathf.Max(bc.a, ic.a)));
            }
        }

        /// <summary>
        ///     GPU-copy a texture to a new readable Texture2D (works even if
        ///     the source is compressed / non-readable).
        /// </summary>
        private static Texture2D MakeReadable(Texture source)
        {
            var rt = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var tex = new Texture2D(
                source.width, source.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(
                new Rect(0, 0, source.width, source.height), 0, 0);
            tex.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private static Texture2D MakeReadable(Sprite itemIcon)
        {
            return MakeReadable(itemIcon.texture);
        }

        #endregion
    }
}