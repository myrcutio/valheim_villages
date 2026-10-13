using UnityEngine;
using ValheimVillages.Schemas;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Makes a villager look like its trade: the prop in its hand and the colour of its robe.
    ///
    ///     <para>Purely cosmetic, and deliberately NOT routed through
    ///     <see cref="VisEquipment" />. Equipping real items on a Dvergr body was tried and
    ///     removed — player weapons have no matching animator clip and no working attack path —
    ///     so this grafts a model onto the hand bone instead and leaves combat alone. A Guard
    ///     keeps its native crossbow because <c>heldTool</c> is a separate field from
    ///     <c>equipment</c>, not a reuse of it.</para>
    /// </summary>
    public static class VillagerAppearance
    {
        /// <summary>Bone the held prop hangs from, as named in the Dvergr armature.</summary>
        private const string HandBone = "r.hand";

        /// <summary>
        ///     Name of the grafted prop. The VV_ prefix is what the hot-reload sweep keys off to
        ///     clean the old assembly's children off a live NPC.
        /// </summary>
        private const string GraftName = "VV_HeldTool";

        /// <summary>Off-hand prop, grafted alongside the tool rather than replacing anything.</summary>
        private const string OffHandGraftName = "VV_OffHand";

        /// <summary>
        ///     Off-hand bone. Every villager carries in the hand, including the Guard: the Dvergr
        ///     body exposes NO holster mounts — VisEquipment's backShield/backMelee/backBow/
        ///     backTool are all null on it — so a hip or back carry could only ever be a guessed
        ///     offset, and the item's attach_back offsets are authored against a mount that does
        ///     not exist here, which planted the lantern inside the hip.
        /// </summary>
        private const string OffHandBone = "l.hand";

        /// <summary>
        ///     Off-hand props are carried a size down from the dropped item. At full size the
        ///     lantern swings low enough to clip through the ground as the villager walks.
        /// </summary>
        private const float OffHandScale = 0.7f;

        /// <summary>
        ///     Materials carrying the Dvergr's SKIN and hair share this texture name. The robe
        ///     materials do not, which is what separates "clothing" from "body" without having to
        ///     enumerate every mage variant by name.
        /// </summary>
        private const string SkinMaterialMarker = "Body";

        /// <summary>Only pixels this saturated contribute a hue; the rest are shadow and cloth weave.</summary>
        private const float HueSampleMinSaturation = 0.15f;

        private const int SampleSize = 16;

        /// <summary>
        ///     Surface character of thatch, measured off Valheim's own <c>beehive</c> material:
        ///     fully matte with strong normal relief. A robe left at cloth gloss reads as silk
        ///     however it is coloured, which is why colour alone did not make it look like straw.
        /// </summary>
        private const float ThatchGlossiness = 0f;

        private const float ThatchBumpScale = 2.08f;

        /// <summary>
        ///     Bounds on how far the tint may drag a texture's saturation and brightness.
        ///
        ///     <para>A garment texture averages dark — its shading is painted in — so steering
        ///     that average onto the theme colour's own value meant a +0.4 lift applied to EVERY
        ///     texel, which flattened the shading and came out glowing. The hue carries the
        ///     identity; value and saturation only nudge, so the cloth keeps its own light and
        ///     weave.</para>
        /// </summary>
        /// <summary>
        ///     How much of the distance to the target colour's own saturation and brightness to
        ///     actually travel.
        ///
        ///     <para>Compressed rather than clipped. A garment texture averages dark — its shading
        ///     is painted in — so steering that average all the way onto a theme's own value lifts
        ///     every texel and the cloth glows. But a hard ceiling is worse in a different way:
        ///     every theme here is brighter than the texture, so all of them pinned to the SAME
        ///     ceiling and came out identical, which turned a wood brown and a straw yellow into
        ///     the same colour — brown being, in the end, only a dark orange. Scaling keeps the
        ///     villagers distinguishable from each other while still pulling the overall lift
        ///     well short of washing the shading out.</para>
        /// </summary>
        private const float ValueResponse = 0.55f;

        /// <summary>
        ///     Theme brightness that means "leave this fabric as dark as the texture already is".
        ///
        ///     <para>The shader OFFSETS an existing texture rather than replacing its colour, so
        ///     aiming at a theme's absolute value is the wrong model: every theme is brighter than
        ///     the dress texture, so every robe got brightened, and a lightened saturated orange
        ///     under daylight is yellow — which is why a wood brown refused to look brown.</para>
        ///
        ///     <para>Calibrated from what was actually observed on screen, not derived: the dress
        ///     averages 0.28, rendering at 0.40 read as blonde and at 0.16 as pitch black. So the
        ///     neutral sits at the wood-brown theme's own value — that robe keeps the texture's
        ///     natural darkness, which IS brown once the hue is shifted, and brighter themes lift
        ///     from there. Re-calibrate this, not the clamps, if the set drifts light or dark
        ///     together.</para>
        /// </summary>
        private const float NeutralValue = 0.48f;

        private const float SaturationResponse = 0.6f;

        /// <summary>Backstop against a pathological theme, not the everyday limit.</summary>
        private const float MaxValueShift = 0.35f;

        private const float MaxSaturationShift = 0.30f;

        /// <summary>
        ///     Target skin tone. The Dvergr body texture is a cold blue-grey, which reads as
        ///     distinctly other-worldly next to the player; this pulls it to a warm human mid-tone.
        ///     Applied only to the engine's own <see cref="VisEquipment.m_bodyModel" />, so hair
        ///     and beard — which share the body's material — keep their own colour.
        /// </summary>
        private static readonly Color HumanSkin = new(0.78f, 0.60f, 0.47f);

        public static void Apply(GameObject npcObject, VillagerDef def)
        {
            if (npcObject == null || def == null) return;

            ApplyHeldTool(npcObject, def);
            ApplyOffHand(npcObject, def);
            ApplyRobeTint(npcObject, def);
            ApplySkinTone(npcObject, def);
            ApplyBeard(npcObject, def);
        }

        /// <summary>
        ///     Swap whatever the body came holding for the definition's <c>heldTool</c>, in the
        ///     same place and pose.
        /// </summary>
        private static void ApplyHeldTool(GameObject npcObject, VillagerDef def)
        {
            if (string.IsNullOrEmpty(def.heldTool)) return;

            var hand = FindDescendant(npcObject.transform, HandBone);
            if (hand == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Appearance] {def.type}: no '{HandBone}' bone — cannot hold {def.heldTool}");
                return;
            }

            // Attach exactly the way the engine attaches an item to a hand: identity pose on the
            // bone, letting the item's own authored grip do the rest. Inheriting the replaced
            // staff's attach-root pose instead left the cultivator sticking out at an angle,
            // because that root carries the STAFF's offsets, not a generic hand mount. The
            // off-hand lantern already uses this convention and sits correctly.
            var existing = FindAttachRoot(hand);
            var graft = GraftProp(hand, def.heldTool, "attach", GraftName, def.type, null);
            if (graft == null) return;

            // Only the ROTATION is overridden. The position the item prefab authors is its own
            // pivot offset — the staff's is a metre off the hand because its mesh origin sits at
            // one end — so inheriting that from the replaced prop would fling the tool sideways.
            var graftMesh = FindRenderedChild(graft.transform);
            if (graftMesh != null)
            {
                if (TryParseVector(def.heldToolRotation, "heldToolRotation", out var euler))
                    graftMesh.localRotation = Quaternion.Euler(euler);
                if (TryParseVector(def.heldToolPosition, "heldToolPosition", out var offset))
                    graftMesh.localPosition = offset;
            }

            // Hide, do not destroy: the attach root belongs to the body prefab's own setup, and
            // removing it would leave whatever created it holding a dead reference.
            //
            // Effects as well as meshes. Disabling only the renderers left the Dvergr staff
            // invisible but still running its smoke and impact particles, so the villager
            // trailed smoke from an empty hand.
            if (existing != null)
            {
                foreach (var renderer in existing.GetComponentsInChildren<Renderer>())
                    renderer.enabled = false;

                SilenceEffects(existing, null);
            }

            Plugin.Log?.LogInfo(
                $"[Appearance] {def.type}: holding {def.heldTool} " +
                $"(replaced '{(existing != null ? existing.name : "nothing")}')");
        }

        /// <summary>
        ///     Give the villager its off-hand prop — a lantern, so a village reads as inhabited
        ///     after dark.
        /// </summary>
        private static void ApplyOffHand(GameObject npcObject, VillagerDef def)
        {
            if (string.IsNullOrEmpty(def.offHandItem)) return;

            var bone = FindDescendant(npcObject.transform, OffHandBone);
            if (bone == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Appearance] {def.type}: no off-hand attach point for {def.offHandItem}");
                return;
            }

            var graft = GraftProp(
                bone, def.offHandItem, "attach", OffHandGraftName, def.type, null, OffHandScale);
            if (graft == null) return;

            // Hang it level rather than at whatever angle the hand bone rests at — the same
            // attach rotation reads upright on a DvergerMage and on its side on a Dverger.
            var mesh = FindRenderedChild(graft.transform);
            if (mesh != null) graft.AddComponent<UprightProp>().Bind(mesh);

            Plugin.Log?.LogInfo(
                $"[Appearance] {def.type}: carrying {def.offHandItem} on {bone.name}");
        }

        /// <summary>
        ///     Instantiate an item's attach model onto a bone, replacing any graft left by a
        ///     previous assembly.
        /// </summary>
        private static GameObject GraftProp(
            Transform bone, string itemName, string attachChild, string graftName,
            string label, Transform poseLike, float scale = 1f)
        {
            // Rebuild rather than skip: a graft from a previous assembly survives hot reload, so
            // returning early on finding one made every subsequent change silently do nothing.
            //
            // Searched from the whole body, not just the target bone, and ALL matches are
            // cleared rather than the first: moving a prop between bones leaves a copy on the
            // old one, and removing a single match just alternates between the two.
            DestroyAllNamed(bone.root, graftName);

            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            if (prefab == null)
            {
                Plugin.Log?.LogWarning($"[Appearance] {label}: no item prefab '{itemName}'");
                return null;
            }

            var attach = prefab.transform.Find(attachChild);
            if (attach == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Appearance] {label}: '{itemName}' has no '{attachChild}' child to graft");
                return null;
            }

            var graft = Object.Instantiate(attach.gameObject);
            graft.name = graftName;
            graft.transform.SetParent(bone, false);

            if (poseLike != null)
            {
                graft.transform.localPosition = poseLike.localPosition;
                graft.transform.localRotation = poseLike.localRotation;
                graft.transform.localScale = poseLike.localScale;
            }
            else
            {
                graft.transform.localPosition = Vector3.zero;
                graft.transform.localRotation = Quaternion.identity;

                // The Dvergr armature is scaled 100x, so a prop parented straight onto a bone
                // inherits that and arrives the size of a building. Items are authored at world
                // scale 1, so undo the bone's scale rather than guessing a factor. A prop posed
                // after an existing attachment skips this: it inherits that root's own
                // compensation, which is why the cultivator came out the right size and the
                // lantern did not.
                var lossy = bone.lossyScale;
                graft.transform.localScale = new Vector3(
                    Mathf.Approximately(lossy.x, 0f) ? scale : scale / lossy.x,
                    Mathf.Approximately(lossy.y, 0f) ? scale : scale / lossy.y,
                    Mathf.Approximately(lossy.z, 0f) ? scale : scale / lossy.z);
            }

            graft.SetActive(true);

            // Lamps and torches keep their light and sound under an 'equiped' child that ships
            // inactive; the engine turns it on when it attaches one, so this must too.
            var equipped = graft.transform.Find("equiped");
            if (equipped != null) equipped.gameObject.SetActive(true);

            // ...and nothing emissive OUTSIDE 'equiped' should run. An item's attach can carry
            // effects meant for other contexts — AxeIron ships an UpgraderGlow with a particle
            // system and a point light — and grafting the subtree wholesale lit the carpenter up
            // with a blue crafting glow. 'equiped' is the engine's own marker for "effects to
            // show while held", so everything emissive outside it goes quiet.
            SilenceEffects(graft.transform, equipped);

            // A worn prop must not collide with anything — the engine disables these on its own
            // attachments for the same reason.
            foreach (var collider in graft.GetComponentsInChildren<Collider>())
                collider.enabled = false;

            return graft;
        }

        /// <summary>
        ///     Warm the Dvergr's blue-grey skin toward a human tone.
        ///
        ///     <para>Targets <see cref="VisEquipment.m_bodyModel" /> specifically rather than
        ///     every material named like the body: hair and beard meshes draw with that same
        ///     material, and tinting them to skin colour would leave the villager bald-looking.</para>
        /// </summary>
        private static void ApplySkinTone(GameObject npcObject, VillagerDef def)
        {
            var vis = npcObject.GetComponent<VisEquipment>();
            var body = vis != null ? vis.m_bodyModel : null;
            if (body == null)
            {
                Plugin.Log?.LogWarning($"[Appearance] {def.type}: no body model to tone");
                return;
            }

            TintRenderer(body, def.type, HumanSkin, null, null, skinPass: true);
        }

        /// <summary>
        ///     Give every villager a beard.
        ///
        ///     <para>Valheim rolls one for an NPC with probability
        ///     <see cref="VisEquipment.m_npcBeardChance" /> — 0.2 — and records a refusal as
        ///     <c>noBeard</c> on the ZDO, which is why one villager has whiskers and the next
        ///     does not. The choice is re-derived from the villager's own id rather than rolled,
        ///     so a given villager keeps the same beard across loads instead of being reshuffled
        ///     every time it spawns.</para>
        /// </summary>
        private static void ApplyBeard(GameObject npcObject, VillagerDef def)
        {
            var vis = npcObject.GetComponent<VisEquipment>();
            if (vis == null) return;
            if (vis.m_beardPrefabCount <= 0 || string.IsNullOrWhiteSpace(vis.m_beardPrefabPrefix))
            {
                Plugin.Log?.LogWarning(
                    $"[Appearance] {def.type}: body defines no beards " +
                    $"(prefix='{vis.m_beardPrefabPrefix}' count={vis.m_beardPrefabCount})");
                return;
            }

            var view = npcObject.GetComponent<ZNetView>();
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;

            // The refusal is sticky: leaving it set means the engine re-applies "no beard"
            // on the next setup and undoes this.
            zdo?.Set(ZDOVars.s_noBeard, false);

            var seed = npcObject.name.GetStableHashCode();
            if (zdo != null) seed = zdo.m_uid.GetHashCode();

            var index = Mathf.Abs(seed) % vis.m_beardPrefabCount + 1;
            var beard = vis.m_beardPrefabPrefix + index;
            vis.SetBeardItem(beard.GetStableHashCode());

            Plugin.Log?.LogInfo($"[Appearance] {def.type}: beard '{beard}'");
        }

        /// <summary>
        ///     Tint the villager's clothing to its <c>themeColor</c> — the same identity colour
        ///     that dyes its cot blanket.
        /// </summary>
        private static void ApplyRobeTint(GameObject npcObject, VillagerDef def)
        {
            if (!Registry.VillagerTheme.TryParse(def.themeColor, out var theme)) return;

            TintRobe(npcObject, def.type, theme, ThatchGlossiness, ThatchBumpScale);
        }

        /// <summary>
        ///     Recolour and re-surface a villager's clothing. Public so a dev command can drive it
        ///     live, which is the only practical way to converge on a look.
        /// </summary>
        public static int TintRobe(
            GameObject npcObject, string label, Color theme, float glossiness, float bumpScale)
        {
            if (npcObject == null) return 0;

            var tinted = 0;
            foreach (var renderer in npcObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                tinted += TintRenderer(renderer, label, theme, glossiness, bumpScale);

            if (tinted == 0)
                Plugin.Log?.LogWarning(
                    $"[Appearance] {label}: nothing to tint — no cloth material " +
                    $"with HSV inputs found");

            return tinted;
        }

        /// <summary>
        ///     Apply a target colour to one renderer's materials, in bounded HSV offsets.
        ///
        ///     <para><paramref name="glossiness" /> and <paramref name="bumpScale" /> are optional:
        ///     skin keeps the surface the model shipped with, only cloth is re-surfaced.</para>
        /// </summary>
        private static int TintRenderer(
            Renderer renderer, string label, Color target,
            float? glossiness, float? bumpScale, bool skinPass = false)
        {
            Color.RGBToHSV(target, out var targetHue, out var targetSat, out var targetVal);

            var shared = renderer.sharedMaterials;
            if (shared == null) return 0;

            var instances = renderer.materials; // instantiates, so other Dvergr stay untouched
            var changed = false;
            var tinted = 0;

            for (var i = 0; i < instances.Length && i < shared.Length; i++)
            {
                var material = instances[i];
                if (material == null) continue;

                // Skin and hair share the body texture. When a skin tone is being applied this
                // IS the target; when clothing is, it must be left alone.
                var isSkin = shared[i] != null && shared[i].name.Contains(SkinMaterialMarker);
                if (isSkin != skinPass) continue;

                // Custom/Creature carries HSV OFFSETS, which is the only tint on this shader
                // that can both desaturate and lighten — a _Color multiply can do neither.
                if (!material.HasProperty("_Hue") ||
                    !material.HasProperty("_Saturation") ||
                    !material.HasProperty("_Value"))
                    continue;

                // Offsets are relative, so they need the robe's actual colour to aim from.
                if (!TrySampleAlbedo(material, out var source)) continue;

                Color.RGBToHSV(source, out var sourceHue, out var sourceSat, out var sourceVal);

                // Shortest way round the colour wheel, expressed in the shader's 0..1 hue.
                var hueShift = Mathf.DeltaAngle(sourceHue * 360f, targetHue * 360f) / 360f;

                var satShift = Mathf.Clamp(
                    (targetSat - sourceSat) * SaturationResponse,
                    -MaxSaturationShift, MaxSaturationShift);
                var valShift = Mathf.Clamp(
                    (targetVal - NeutralValue) * ValueResponse,
                    -MaxValueShift, MaxValueShift);

                material.SetFloat("_Hue", hueShift);
                material.SetFloat("_Saturation", satShift);
                material.SetFloat("_Value", valShift);

                // Surface, not just colour: straw is matte and coarse. Optional, because skin
                // keeps whatever surface the model shipped with.
                if (glossiness.HasValue && material.HasProperty("_Glossiness"))
                    material.SetFloat("_Glossiness", glossiness.Value);
                if (bumpScale.HasValue && material.HasProperty("_BumpScale"))
                    material.SetFloat("_BumpScale", bumpScale.Value);

                changed = true;
                tinted++;

                Plugin.Log?.LogInfo(
                    $"[Appearance] {label}: tinted '{shared[i].name}' " +
                    $"from HSV({sourceHue:F2},{sourceSat:F2},{sourceVal:F2}) " +
                    $"to HSV({targetHue:F2},{targetSat:F2},{targetVal:F2}) " +
                    $"offsets=({hueShift:F2},{satShift:F2},{valShift:F2})");
                }

            if (changed) renderer.materials = instances;
            return tinted;
        }

        /// <summary>
        ///     Average hue/saturation/value of a material's albedo, read back off the GPU because
        ///     textures shipped in asset bundles are not CPU-readable.
        ///
        ///     <para>Hue is a circular mean over the saturated pixels only: a flat average over
        ///     every pixel converges on grey and would report no hue to shift away from.</para>
        /// </summary>
        private static bool TrySampleAlbedo(Material material, out Color average)
        {
            average = Color.white;

            var texture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            if (texture == null) return false;

            var rt = RenderTexture.GetTemporary(
                SampleSize, SampleSize, 0, RenderTextureFormat.ARGB32);
            var readable = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;

            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readable.ReadPixels(new Rect(0, 0, SampleSize, SampleSize), 0, 0);
                readable.Apply();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }

            var pixels = readable.GetPixels();
            Object.Destroy(readable);
            if (pixels == null || pixels.Length == 0) return false;

            var hueX = 0f;
            var hueY = 0f;
            var satTotal = 0f;
            var values = new System.Collections.Generic.List<float>(pixels.Length);
            var hueCount = 0;
            var count = 0;

            foreach (var pixel in pixels)
            {
                if (pixel.a < 0.5f) continue;

                Color.RGBToHSV(pixel, out var h, out var s, out var v);
                values.Add(v);
                satTotal += s;
                count++;

                if (s < HueSampleMinSaturation) continue;

                var radians = h * Mathf.PI * 2f;
                hueX += Mathf.Cos(radians);
                hueY += Mathf.Sin(radians);
                hueCount++;
            }

            if (count == 0) return false;

            var hue = 0f;
            if (hueCount > 0)
            {
                hue = Mathf.Atan2(hueY, hueX) / (Mathf.PI * 2f);
                if (hue < 0f) hue += 1f;
            }

            var valTotal = 0f;
            foreach (var v in values) valTotal += v;

            average = Color.HSVToRGB(hue, satTotal / count, valTotal / count);
            return true;
        }

        /// <summary>Parse an "x,y,z" vector from a definition. Blank means "leave it alone".</summary>
        private static bool TryParseVector(string value, string field, out Vector3 vector)
        {
            vector = Vector3.zero;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var parts = value.Split(',');
            if (parts.Length != 3)
                throw new System.FormatException(
                    $"{field} must be 'x,y,z' but was '{value}'");

            vector = new Vector3(
                float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        ///     Switch off every particle system and light under <paramref name="root" />, except
        ///     those under <paramref name="keep" /> (the engine's "shown while equipped" subtree).
        /// </summary>
        private static void SilenceEffects(Transform root, Transform keep)
        {
            foreach (var particles in root.GetComponentsInChildren<ParticleSystem>(true))
                if (!IsUnder(particles.transform, keep))
                    particles.gameObject.SetActive(false);

            foreach (var light in root.GetComponentsInChildren<Light>(true))
                if (!IsUnder(light.transform, keep))
                    light.gameObject.SetActive(false);
        }

        /// <summary>True when <paramref name="t" /> sits under <paramref name="ancestor" />.</summary>
        private static bool IsUnder(Transform t, Transform ancestor)
        {
            if (ancestor == null) return false;

            for (var cursor = t; cursor != null; cursor = cursor.parent)
                if (cursor == ancestor) return true;

            return false;
        }

        /// <summary>Remove every descendant with this name, wherever it ended up.</summary>
        private static void DestroyAllNamed(Transform root, string name)
        {
            var doomed = new System.Collections.Generic.List<GameObject>();
            Collect(root, name, doomed);
            foreach (var go in doomed) Object.DestroyImmediate(go);
        }

        private static void Collect(
            Transform t, string name, System.Collections.Generic.List<GameObject> into)
        {
            if (t.name == name)
            {
                into.Add(t.gameObject);
                return; // nothing nested inside a graft is a separate graft
            }

            for (var i = 0; i < t.childCount; i++)
                Collect(t.GetChild(i), name, into);
        }

        /// <summary>
        ///     First descendant that draws GEOMETRY — the prop's mesh.
        ///
        ///     <para>Mesh renderers only, deliberately. Matching any <see cref="Renderer" /> also
        ///     matches a ParticleSystemRenderer, and an item's attach can lead with an effect:
        ///     AxeIron's first child is an UpgraderGlow whose particle system was being returned
        ///     instead of the axe, so posing the tool silently moved an invisible emitter.</para>
        /// </summary>
        public static Transform FindRenderedChild(Transform root)
        {
            if (root.GetComponent<MeshRenderer>() != null ||
                root.GetComponent<SkinnedMeshRenderer>() != null) return root;

            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindRenderedChild(root.GetChild(i));
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>The body's own prop root on a bone, as the engine names its attachments.</summary>
        private static Transform FindAttachRoot(Transform hand)
        {
            for (var i = 0; i < hand.childCount; i++)
            {
                var child = hand.GetChild(i);
                if (child.name.StartsWith("attach") && child.name != GraftName)
                    return child;
            }

            return null;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name) return root;

            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }

            return null;
        }
    }
}
