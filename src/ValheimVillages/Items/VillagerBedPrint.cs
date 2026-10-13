using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Stamps a villager's station icon INTO the deer hide's texture, so the blanket's own
    ///     albedo carries the print.
    ///
    ///     <para><b>This replaces a second mesh hovering over the blanket.</b> That approach had
    ///     to win a depth fight against a lofted, draping surface it sat only millimetres above,
    ///     and it lost in both directions: too low and the icon's edges sank into the hide and
    ///     vanished, too high and it read as a sticker floating over the bed. The height had to be
    ///     re-tuned against the loft curve every time the blanket moved. A texture composite has
    ///     no height, so none of that exists — and it cannot be occluded, z-fight, or shadow
    ///     itself.</para>
    ///
    ///     <para>The composite runs once per graft on the CPU. Both source textures are read
    ///     through a RenderTexture rather than <c>GetPixels</c> directly, because neither is
    ///     CPU-readable: Valheim's shipped textures are compressed with
    ///     <c>Texture.isReadable == false</c>, and a sprite icon is additionally atlased.</para>
    /// </summary>
    internal static class VillagerBedPrint
    {
        /// <summary>
        ///     How strongly the print takes over the hide where the icon is opaque. Below 1 so
        ///     the pelt's grain still reads through it — the point of printing into the texture
        ///     rather than over it is that the motif can be genuinely translucent, which the old
        ///     alpha-TESTED plane could not be at any setting.
        /// </summary>
        private const float PrintOpacity = 0.85f;

        /// <summary>
        ///     How dark the mark is relative to the blanket it sits on, and how much its
        ///     saturation is pushed. The mark is dyed in a DEEPER SHADE OF THE BLANKET'S OWN
        ///     COLOUR rather than a fixed brown.
        ///
        ///     <para>A fixed dark brown was tried and read as "a vague brown smear": brown on a
        ///     violet or green blanket is a muddy, unrelated colour, so the eye sees a stain
        ///     rather than a mark. Tone-on-tone is both higher contrast in VALUE — which is what
        ///     actually defines a silhouette — and looks deliberate, like cloth dyed twice.</para>
        /// </summary>
        private const float DyeValue = 0.22f;

        private const float DyeSaturation = 1.25f;

        /// <summary>
        ///     Alpha below this is treated as empty and above <see cref="DyeEdgeHigh" /> as
        ///     solid, with a short ramp between.
        ///
        ///     <para>Station icons are 64x64 and get upscaled roughly 3.7x onto the blanket, so
        ///     their anti-aliased fringe stretches into a wide soft gradient — which is most of
        ///     why the mark looked blurred rather than stamped. Re-cutting the edge keeps a
        ///     silhouette crisp at any upscale without aliasing it into stairsteps.</para>
        /// </summary>
        private const float DyeEdgeLow = 0.30f;

        private const float DyeEdgeHigh = 0.62f;

        /// <summary>
        ///     How far the blanket is pushed toward its owner's theme colour. Short of 1 so the
        ///     hide's own warmth still shows through and it reads as DYED leather rather than
        ///     painted canvas.
        /// </summary>
        private const float TintStrength = 0.97f;

        /// <summary>
        ///     How far the hide's own light and shade may swing the dye's brightness, as a
        ///     fraction either side of the theme colour's own value.
        ///
        ///     <para>Without this the brightness was <c>themeValue * (luminance / mean)</c>
        ///     unbounded, so every hide pixel above the mean drove V to its ceiling. A colour at
        ///     full value and middling saturation is a PASTEL, which is why royal purple arrived
        ///     as pink. Confining the swing keeps the authored colour recognisable while the
        ///     grain and folds still read, because they only ever needed a few percent.</para>
        /// </summary>
        private const float ValueVariation = 0.40f;

        /// <summary>
        ///     Baked blankets, keyed by villager type.
        ///
        ///     <para>Keyed by TYPE, not by cot, because the mark is a station icon and the tint a
        ///     type colour — two Farmers' cots are pixel-identical. That matters once cots are
        ///     individually assigned: without this, a barracks row would hold one full-size
        ///     texture per bed instead of one per type.</para>
        ///
        ///     <para>Hot reload resets the dictionary and orphans its textures, which
        ///     <c>Resources.UnloadUnusedAssets</c> reclaims on the next scene change.</para>
        /// </summary>
        private static readonly Dictionary<string, Texture2D> s_baked = new();

        /// <summary>
        ///     Replace <paramref name="hideMaterial" />'s albedo with the same hide carrying
        ///     <paramref name="villagerType" />'s station icon.
        /// </summary>
        /// <param name="sheetSize">Blanket mesh extents in bed units: width along X, length along Z.</param>
        /// <param name="uvInset">The blanket's per-axis UV inset, as handed to VillagerBedCloth.</param>
        /// <param name="uvOffset">The blanket's UV offset, as handed to VillagerBedCloth.</param>
        /// <param name="printCentre">Print centre in BLANKET-local X,Z — bed units, not UV.</param>
        /// <param name="printSize">Edge of the (square, in bed units) print.</param>
        /// <param name="yawDegrees">Rotation of the icon on the bed, about the bed's up axis.</param>
        public static bool Bake(Material hideMaterial, string villagerType, Vector2 sheetSize,
            Vector2 uvInset, Vector2 uvOffset, Vector2 printCentre, float printSize,
            float yawDegrees)
        {
            if (hideMaterial == null || string.IsNullOrEmpty(villagerType)) return false;

            var hide = hideMaterial.mainTexture;
            if (hide == null)
            {
                Plugin.Log?.LogWarning("[VillagerBed] blanket has no albedo to print onto");
                return false;
            }

            var sprite = FindStationIcon(villagerType);
            if (sprite == null) return false;

            // --- Bed units -> texture pixels -------------------------------------------------
            // The blanket does not sample its whole texture. VillagerBedCloth maps the mesh onto
            // the window [inset + offset, inset + offset + (1 - 2*inset)] per axis, so one bed
            // unit is worth (1 - 2*inset) * texSize / meshExtent pixels. The two axes disagree:
            // the window is square but the blanket is 0.82 x 1.56, so a print that must come out
            // SQUARE ON THE BED has to be drawn as a WIDE rectangle in the texture. Drawing it
            // square in UV space is how a round motif arrives as an ellipse.
            var span = new Vector2(1f - uvInset.x * 2f, 1f - uvInset.y * 2f);
            var pxPerBedX = span.x * hide.width / sheetSize.x;
            var pxPerBedZ = span.y * hide.height / sheetSize.y;

            // Mesh UV runs the same direction as both texture axes (+X -> +U, +Z -> +V) and
            // GetPixels' row 0 is V = 0, so pixel space needs no flip on either axis.
            var centreUv = new Vector2(
                uvInset.x + uvOffset.x + (printCentre.x / sheetSize.x + 0.5f) * span.x,
                uvInset.y + uvOffset.y + (printCentre.y / sheetSize.y + 0.5f) * span.y);
            var cx = centreUv.x * hide.width;
            var cy = centreUv.y * hide.height;

            // Rotating the print grows its axis-aligned footprint by |cos|+|sin|.
            var yaw = yawDegrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(yaw);
            var sin = Mathf.Sin(yaw);
            var reach = printSize * 0.5f * (Mathf.Abs(cos) + Mathf.Abs(sin));
            var minX = Mathf.FloorToInt(cx - reach * pxPerBedX);
            var maxX = Mathf.CeilToInt(cx + reach * pxPerBedX);
            var minY = Mathf.FloorToInt(cy - reach * pxPerBedZ);
            var maxY = Mathf.CeilToInt(cy + reach * pxPerBedZ);

            // Say so loudly if the print has outgrown the window the blanket samples: the part
            // outside is simply not on the bed, which looks like a cropped icon rather than a bug.
            if (minX < 0 || minY < 0 || maxX >= hide.width || maxY >= hide.height)
                Plugin.Log?.LogWarning(
                    $"[VillagerBed] print of {villagerType} runs off the hide texture " +
                    $"(px {minX}..{maxX} x {minY}..{maxY} against {hide.width}x{hide.height}); " +
                    "reduce printSize or move printCentre");

            minX = Mathf.Clamp(minX, 0, hide.width - 1);
            maxX = Mathf.Clamp(maxX, 0, hide.width - 1);
            minY = Mathf.Clamp(minY, 0, hide.height - 1);
            maxY = Mathf.Clamp(maxY, 0, hide.height - 1);

            // --- Composite -------------------------------------------------------------------
            if (s_baked.TryGetValue(villagerType, out var cached) && cached != null)
            {
                hideMaterial.mainTexture = cached;
                if (hideMaterial.HasProperty("_MainTex")) hideMaterial.SetTexture("_MainTex", cached);
                return true;
            }

            var iconRect = sprite.textureRect;
            var iconW = Mathf.Max(1, Mathf.RoundToInt(iconRect.width));
            var iconH = Mathf.Max(1, Mathf.RoundToInt(iconRect.height));
            var icon = ReadableCopy(sprite.texture, iconW, iconH, false,
                new Vector2(iconRect.width / sprite.texture.width, iconRect.height / sprite.texture.height),
                new Vector2(iconRect.x / sprite.texture.width, iconRect.y / sprite.texture.height));

            // Mip-chained: the blanket is seen from across a room as often as from beside it, and
            // an un-mipped albedo on a sloped surface shimmers.
            var composite = ReadableCopy(hide, hide.width, hide.height, true, Vector2.one, Vector2.zero);
            composite.name = $"VV_bedprint_{villagerType}";
            composite.wrapMode = hide.wrapMode;
            composite.filterMode = hide.filterMode;

            // Dyed in a deeper shade of the blanket's own colour, computed from the same theme.
            var theme = Villager.Registry.VillagerTheme.ColorFor(villagerType);
            Color.RGBToHSV(theme, out var dyeHue, out var dyeSat, out var dyeVal);
            var dye = Color.HSVToRGB(dyeHue, Mathf.Clamp01(dyeSat * DyeSaturation), dyeVal * DyeValue);

            Recolour(composite, theme);

            // Every icon is authored with its own margin — a piece's hammer-menu icon sits in
            // noticeably more empty space than an item's inventory icon — so stamping the raw
            // 0..1 sprite makes some marks arrive much smaller than others for no reason the
            // player can see. Fit each icon's OPAQUE extent to the print instead, preserving
            // aspect so nothing is stretched.
            var fit = OpaqueExtent(icon);

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            var block = composite.GetPixels(minX, minY, bw, bh);
            var half = printSize * 0.5f;

            for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
            {
                // Pixel -> bed units -> un-yaw into the icon's own frame. The icon is square in
                // BED space, so the inside test has to happen after the anisotropic pixel-to-bed
                // division, never on raw pixel offsets.
                var bx = (minX + x + 0.5f - cx) / pxPerBedX;
                var bz = (minY + y + 0.5f - cy) / pxPerBedZ;
                var ix = bx * cos - bz * sin;
                var iz = bx * sin + bz * cos;
                if (ix < -half || ix > half || iz < -half || iz > half) continue;

                // Alpha ONLY. The icon's colour is discarded and the dye substituted, which is
                // what turns a rendered tool into a stamped silhouette. Bilinear sampling of the
                // alpha gives the mark a soft edge, as a dye bleeding into fabric has.
                var coverage = icon.GetPixelBilinear(
                    fit.center.x + (ix / printSize) * fit.size.x,
                    fit.center.y + (iz / printSize) * fit.size.y).a;
                var a = Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(DyeEdgeLow, DyeEdgeHigh, coverage)) * PrintOpacity;
                if (a <= 0f) continue;

                var i = y * bw + x;
                var dst = block[i];
                block[i] = new Color(
                    Mathf.Lerp(dst.r, dye.r, a),
                    Mathf.Lerp(dst.g, dye.g, a),
                    Mathf.Lerp(dst.b, dye.b, a),
                    dst.a);
            }

            composite.SetPixels(minX, minY, bw, bh, block);
            composite.Apply(true, false);
            Object.Destroy(icon);

            hideMaterial.mainTexture = composite;
            if (hideMaterial.HasProperty("_MainTex")) hideMaterial.SetTexture("_MainTex", composite);
            s_baked[villagerType] = composite;
            return true;
        }

        /// <summary>
        ///     The icon's opaque region as a sampling window, in normalised texture coordinates:
        ///     <c>center</c> is where the print's middle lands and <c>size</c> is how far one
        ///     print-width reaches.
        ///
        ///     <para>Square in TEXTURE terms only when the artwork is — the window takes the
        ///     larger of the two opaque dimensions on both axes, so a tall icon keeps its
        ///     proportions and simply leaves space either side instead of being stretched to
        ///     fill a square print.</para>
        /// </summary>
        private static Rect OpaqueExtent(Texture2D icon)
        {
            var pixels = icon.GetPixels32();
            int minX = icon.width, minY = icon.height, maxX = -1, maxY = -1;

            for (var y = 0; y < icon.height; y++)
            for (var x = 0; x < icon.width; x++)
            {
                // A low cut, not zero: icon art is anti-aliased and its faint fringe is part of
                // the shape. Only fully-transparent padding should be trimmed.
                if (pixels[y * icon.width + x].a < 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            if (maxX < 0)
                throw new System.InvalidOperationException(
                    "Station icon is fully transparent; nothing to silhouette");

            var centreX = (minX + maxX + 1) * 0.5f / icon.width;
            var centreY = (minY + maxY + 1) * 0.5f / icon.height;
            var spanX = (maxX - minX + 1) / (float)icon.width;
            var spanY = (maxY - minY + 1) / (float)icon.height;
            var span = Mathf.Max(spanX, spanY);

            return new Rect(centreX - span * 0.5f, centreY - span * 0.5f, span, span);
        }

        /// <summary>
        ///     Push the whole hide toward <paramref name="theme" /> while keeping its own light
        ///     and shade.
        ///
        ///     <para>A plain MULTIPLY (which is what setting the material's <c>_Color</c> would
        ///     do) cannot work here: the deer hide is a saturated orange-tan, and multiplying it
        ///     by grey yields a darker orange, never grey. Multiply can darken a hue but cannot
        ///     DESATURATE one, so "grey for mountaineer" is unreachable that way.</para>
        ///
        ///     <para>Instead the theme's hue and saturation are held fixed and only its VALUE
        ///     tracks the hide, normalised by the hide's MEAN luminance so a dark theme colour
        ///     does not also make the blanket dark. The pelt's grain, folds and worn patches all
        ///     survive, because every one of them is a luminance difference.</para>
        /// </summary>
        private static void Recolour(Texture2D texture, Color theme)
        {
            var pixels = texture.GetPixels32();

            var total = 0.0;
            for (var i = 0; i < pixels.Length; i++)
                total += (pixels[i].r * 0.299 + pixels[i].g * 0.587 + pixels[i].b * 0.114) / 255.0;

            var mean = (float)(total / pixels.Length);
            if (mean <= 0.001f)
                throw new System.InvalidOperationException(
                    "Blanket hide texture is black; nothing to re-tint (wrong donor node?)");

            // Vary VALUE only, holding the theme's hue and saturation fixed.
            //
            // Scaling r, g and b independently (the obvious way) is what turned royal purple
            // into PASTEL PINK: wherever the hide is brighter than its mean, the largest channel
            // clamps at 1 while the others keep climbing, so the colour slides toward white and
            // the hue washes out. The deer hide is full of bright patches, so most of the blanket
            // washed out. Pushing the brightness through V instead means a highlight becomes a
            // BRIGHT PURPLE rather than a pale one, and the authored colour survives intact.
            Color.RGBToHSV(theme, out var hue, out var saturation, out var themeValue);

            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                var r = p.r / 255f;
                var g = p.g / 255f;
                var b = p.b / 255f;
                var luminance = r * 0.299f + g * 0.587f + b * 0.114f;

                var dyed = Color.HSVToRGB(hue, saturation,
                    Mathf.Clamp01(themeValue * (1f + ValueVariation * (luminance / mean - 1f))));

                pixels[i] = new Color32(
                    (byte)(Mathf.Clamp01(Mathf.Lerp(r, dyed.r, TintStrength)) * 255f),
                    (byte)(Mathf.Clamp01(Mathf.Lerp(g, dyed.g, TintStrength)) * 255f),
                    (byte)(Mathf.Clamp01(Mathf.Lerp(b, dyed.b, TintStrength)) * 255f),
                    p.a);
            }

            texture.SetPixels32(pixels);
        }

        /// <summary>
        ///     The icon a villager type's station is drawn with, or null (logged) if it has none.
        ///
        ///     <para><c>stationIcon</c> names EITHER an inventory item or a build piece, and both
        ///     already own an authored sprite — an item's <c>m_shared.m_icons[0]</c> or a piece's
        ///     hammer-menu <c>m_icon</c>. Accepting both matters because the best silhouettes are
        ///     often pieces rather than items: the Trader's only item options were a flat pile of
        ///     coins and a coin seen face-on, which both stencil down to a featureless circle,
        ///     while the <c>treasure_stack</c> PIECE is an unmistakable stack.</para>
        /// </summary>
        internal static Sprite FindStationIcon(string villagerType)
        {
            return FindStationIcon(villagerType, out _);
        }

        /// <param name="source">"item", "piece", or "" when nothing resolved. For the audit.</param>
        private static Sprite FindStationIcon(string villagerType, out string source)
        {
            source = "";
            var def = Villager.Registry.VillagerRegistry.Get(villagerType);
            if (string.IsNullOrEmpty(def?.stationIcon))
            {
                Plugin.Log?.LogWarning($"[VillagerBed] '{villagerType}' has no stationIcon to print");
                return null;
            }

            var itemPrefab = ObjectDB.instance?.GetItemPrefab(def.stationIcon);
            var icons = itemPrefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_icons;
            if (icons != null && icons.Length > 0 && icons[0] != null && icons[0].texture != null)
            {
                source = "item";
                return icons[0];
            }

            var piece = ZNetScene.instance?.GetPrefab(def.stationIcon)?.GetComponent<Piece>();
            if (piece?.m_icon != null && piece.m_icon.texture != null)
            {
                source = "piece";
                return piece.m_icon;
            }

            Plugin.Log?.LogWarning(
                $"[VillagerBed] '{def.stationIcon}' for {villagerType} is neither an item with an " +
                "icon nor a build piece with one");
            return null;
        }

        /// <summary>
        ///     Drop every baked blanket so the next graft re-composites from scratch.
        ///
        ///     <para>Called by <c>vv_bedprint</c>, whose entire purpose is to SEE a change. The
        ///     cache is keyed by villager type only, so after a tint or icon edit it would
        ///     happily serve the blanket baked by the previous build and the edit would look
        ///     like it had done nothing — which is exactly what happened while the tint was
        ///     being tuned.</para>
        /// </summary>
        public static void ClearCache()
        {
            foreach (var texture in s_baked.Values)
                if (texture != null)
                    Object.Destroy(texture);
            s_baked.Clear();
        }

        /// <summary>
        ///     Check every villager definition can actually produce a blanket, without needing a
        ///     cot per type. Resolving a station icon is a three-step lookup (def -> stationIcon
        ///     name -> ObjectDB item prefab -> sprite) and any step can come back empty for a
        ///     type nobody has placed yet, so this exists to catch that from one call.
        /// </summary>
        [DevCommand("Audit every villager type's blanket theme + station icon. " +
                    "Usage: vv_bedprint_audit", Name = "vv_bedprint_audit")]
        public static void Audit(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder("[vv_bedprint_audit]");
            var iconOwners = new Dictionary<string, List<string>>();
            var failures = 0;

            foreach (var entry in Villager.Registry.VillagerRegistry.Definitions)
            {
                var type = entry.Key;
                var def = entry.Value;
                var themeOk = Villager.Registry.VillagerTheme.TryParse(def.themeColor, out var theme);
                var sprite = FindStationIcon(type, out var source);

                if (!themeOk || sprite == null) failures++;
                if (sprite != null)
                {
                    if (!iconOwners.TryGetValue(def.stationIcon, out var owners))
                        iconOwners[def.stationIcon] = owners = new List<string>();
                    owners.Add(type);
                }

                sb.Append($"\n  {type,-13} {(def.disabled ? "off" : "on ")} " +
                          $"theme={(themeOk ? $"({theme.r:F2},{theme.g:F2},{theme.b:F2})" : $"BAD '{def.themeColor}'")} " +
                          $"icon={def.stationIcon,-12} " +
                          $"sprite={(sprite != null ? $"{sprite.textureRect.width:F0}x{sprite.textureRect.height:F0} ({source})" : "MISSING")}");
            }

            // Three types share the Hammer icon, so the silhouette alone cannot identify them —
            // the theme colour is what tells those blankets apart, not decoration.
            foreach (var pair in iconOwners)
                if (pair.Value.Count > 1)
                    sb.Append($"\n  NOTE  icon '{pair.Key}' is shared by {string.Join(", ", pair.Value)} " +
                              "- only the tint distinguishes them");

            sb.Append($"\n  {(failures == 0 ? "all types resolved" : $"{failures} TYPE(S) FAILED")}");
            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }

        /// <summary>
        ///     A CPU-readable RGBA32 copy of any texture, optionally of just a sub-rect.
        ///
        ///     <para>Goes via a RenderTexture because the shipped textures are compressed and
        ///     report <c>isReadable == false</c>, so <c>GetPixels</c> on them throws. The blit
        ///     also resolves a sprite's atlas sub-rect, via the scale/offset pair.</para>
        ///
        ///     <para>sRGB on BOTH ends — an sRGB render target and a non-linear Texture2D — so
        ///     the round trip is byte-for-byte. Mixing one linear end in silently regrades the
        ///     whole hide.</para>
        /// </summary>
        private static Texture2D ReadableCopy(Texture source, int width, int height, bool mipChain,
            Vector2 scale, Vector2 offset)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;

            Graphics.Blit(source, rt, scale, offset);
            RenderTexture.active = rt;

            var copy = new Texture2D(width, height, TextureFormat.RGBA32, mipChain, false);
            copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            copy.Apply(mipChain, false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return copy;
        }
    }
}
