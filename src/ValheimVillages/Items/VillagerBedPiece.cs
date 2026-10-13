using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     The <b>Villager Cot</b>: a military-style camp bed, kitbashed from vanilla parts so it
    ///     reads as field kit rather than as the player's furniture.
    ///
    ///     <para>A yellow banner laid flat is the bedroll, four shrunken core-wood poles are the
    ///     legs, an ashwood decorative divider stands at the head, and an iron chest sits at the
    ///     foot as a footlocker. The vanilla <c>bed</c> underneath supplies Piece, WearNTear,
    ///     colliders, snap points and destruction; its own meshes are hidden.</para>
    ///
    ///     <para>Earlier attempts decorated a bed instead of building one — a lantern grafted to
    ///     a stock bed, a chair warped into a bed footprint, the bed mesh wearing chair
    ///     materials. All three read as a vanilla bed with something stuck on, which is the one
    ///     thing this piece must not be.</para>
    ///
    ///     <para>Deliberately NOT a home anchor. The village registry owns where a villager
    ///     lives; this piece only furnishes a room.</para>
    ///
    ///     <para>The <c>Bed</c> component is stripped. It grants only claim, sleep and respawn,
    ///     and sleeping runs through <c>Humanoid.AttachStart(..., isBed: true)</c> — the
    ///     Player-only attach pipeline that already stops villagers using chairs. Villagers lose
    ///     nothing they could have used, and the cot cannot become a player spawn point.</para>
    /// </summary>
    public static class VillagerBedPiece
    {
        public const string PrefabName = "vv_villager_bed";

        /// <summary>Chassis: a working buildable whose collider is already bed-shaped.</summary>
        private const string BasePrefab = "bed";

        /// <summary>
        ///     One grafted part. <paramref name="Node" /> names the donor child to copy, because
        ///     the donor's useful mesh is rarely its first — a banner's first mesh is the wooden
        ///     crossbar, not the cloth.
        /// </summary>
        private readonly struct Part
        {
            public Part(string name, string prefab, string node, Vector3 pos, Vector3 euler,
                Vector3 scale, Color? tint = null, string texPrefab = null, string texNode = null)
            {
                Name = name;
                Prefab = prefab;
                Node = node;
                Pos = pos;
                Euler = euler;
                Scale = scale;
                Tint = tint;
                TexPrefab = texPrefab;
                TexNode = texNode;
            }

            public string Name { get; }
            public string Prefab { get; }
            public string Node { get; }
            public Vector3 Pos { get; }
            public Vector3 Euler { get; }
            public Vector3 Scale { get; }

            /// <summary>Multiplied into the donor material's colour. Null keeps the donor look.</summary>
            public Color? Tint { get; }

            /// <summary>
            ///     Optional MATERIAL donor — keeps this part's MESH but wears another prefab's
            ///     whole material, so a shape can be borrowed from one piece and its surface
            ///     from another. Whole material, not just the texture: shading properties
            ///     (metallic, gloss, emission) belong with the look, and mixing a wood image
            ///     onto a polished-marble material made the headboard shinier than the rails.
            /// </summary>
            public string TexPrefab { get; }

            public string TexNode { get; }
        }

        /// <summary>
        ///     The cot, in the chassis' local space: length runs along Z with the head at -Z,
        ///     width along X, ground at y=0. Edit and run <c>vv_bedrebuild</c> to see a change —
        ///     placed pieces do not pick up template edits on their own.
        /// </summary>
        private static Part[] Parts =
        {
            // The bedroll is NOT here — it is a Cloth sim built by VillagerBedCloth. This table
            // is for rigid grafts only.

            // Frame: four milled beams. `wood_beam`'s `new` node is a box mesh
            // (Cube_Cube_Material), so a rail is straight however far it is stretched — the
            // previous `wood_pole_log` is a tree trunk whose natural taper and bend showed
            // plainly once drawn out to length, worst at the short foot rail.
            new("frame_l", "wood_beam", "new",
                new Vector3(-0.40f, 0.36f, 0f), new Vector3(0f, 90f, 0f), new Vector3(0.95f, 0.16f, 0.16f)),
            new("frame_r", "wood_beam", "new",
                new Vector3(0.40f, 0.36f, 0f), new Vector3(0f, 90f, 0f), new Vector3(0.95f, 0.16f, 0.16f)),
            new("frame_h", "wood_beam", "new",
                new Vector3(0f, 0.36f, -0.92f), Vector3.zero, new Vector3(0.42f, 0.16f, 0.16f)),
            new("frame_f", "wood_beam", "new",
                new Vector3(0f, 0.36f, 0.92f), Vector3.zero, new Vector3(0.42f, 0.16f, 0.16f)),

            // Head: the black marble bench, UPRIGHT, dropped low and pulled in along Z so it
            // clips INTO the frame — its feet run straight down past the bed's own legs and only
            // the top of the slab stands proud as the headboard's rail. Standing it off the end
            // at full height made it tower over the cot.
            //
            // The bench was rejected once for producing a "spiky crown" when flattened into a
            // panel; those spikes were its feet, and using them as posts is what makes it work.
            // It must stay the right way up — inverted, the carving reads as upside down.
            // It keeps the bench's CARVING but wears the frame's plank texture, so the
            // headboard belongs to the same piece of furniture as the rails instead of reading
            // as a slab of stone dropped on a wooden cot. NO tint at all: any multiplier above 1
            // made it brighter than the rails beside it, which is the whole thing being avoided.
            // The old heavy lift existed only to rescue very dark marble.
            new("head", "piece_blackmarble_bench", "high",
                new Vector3(0f, 0.26f, -0.88f), Vector3.zero,
                new Vector3(0.33f, 0.55f, 0.30f), tint: null,
                texPrefab: "wood_beam", texNode: "new"),

            // Yawed 180 so the lid hinges away from the bed. Facing the other way it opened
            // INTO the mattress, which is backwards for a footlocker.
            new("locker", "piece_chest", "ironchest",
                new Vector3(0f, 0f, 1.20f), new Vector3(0f, 180f, 0f), new Vector3(0.34f, 0.32f, 0.38f)),
            new("lockerlid", "piece_chest", "ironchesttop_closed",
                new Vector3(0f, 0f, 1.20f), new Vector3(0f, 180f, 0f), new Vector3(0.34f, 0.32f, 0.38f)),

            // Legs: core-wood poles shrunk to camp-cot spindles.
            // Euler (90,90,0) is the donor's OWN rotation, which is what stands the log on end.
            // Zeroing it laid all four legs flat — visible in a template dump as 0.12 x 0.08 x
            // 0.49, a log lying along Z rather than a 0.35m spindle.
            new("leg_hl", "wood_pole_log", "log (1)",
                new Vector3(-0.36f, 0.18f, -0.80f), new Vector3(90f, 90f, 0f), new Vector3(0.16f, 0.16f, 0.16f)),
            new("leg_hr", "wood_pole_log", "log (1)",
                new Vector3(0.36f, 0.18f, -0.80f), new Vector3(90f, 90f, 0f), new Vector3(0.16f, 0.16f, 0.16f)),
            new("leg_fl", "wood_pole_log", "log (1)",
                new Vector3(-0.36f, 0.18f, 0.80f), new Vector3(90f, 90f, 0f), new Vector3(0.16f, 0.16f, 0.16f)),
            new("leg_fr", "wood_pole_log", "log (1)",
                new Vector3(0.36f, 0.18f, 0.80f), new Vector3(90f, 90f, 0f), new Vector3(0.16f, 0.16f, 0.16f)),
        };

        /// <summary>
        ///     Uniform upsize applied to every grafted part and all three bedding layers. At
        ///     1.0 the cot read as child-sized next to a vanilla bed; 1.25 overshot and looked
        ///     oversized, so it sits ~10% back from there.
        /// </summary>
        private const float BedScale = 1.12f;

        private static GameObject _prefab;

        [RequireObjectDB]
        public static void RegisterDeferred()
        {
            Register(ZNetScene.instance);
        }

        public static void Register(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            EnsurePrefab(zNetScene);
            if (_prefab == null) return;

            if (!zNetScene.m_prefabs.Contains(_prefab))
                zNetScene.m_prefabs.Add(_prefab);

            var named = typeof(ZNetScene)
                .GetField("m_namedPrefabs", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(zNetScene) as Dictionary<int, GameObject>;
            if (named != null) named[PrefabName.GetStableHashCode()] = _prefab;

            PieceFactory.AddToHammerTable(_prefab);
            Plugin.Log?.LogInfo($"[VillagerBed] Registered '{PrefabName}' in ZNetScene + Hammer table");
        }

        private static void EnsurePrefab(ZNetScene zNetScene)
        {
            var existing = _prefab != null ? _prefab : zNetScene.GetPrefab(PrefabName);
            if (existing != null)
            {
                Configure(existing);
                _prefab = existing;
                return;
            }

            var basePrefab = zNetScene.GetPrefab(BasePrefab);
            if (basePrefab == null)
            {
                Plugin.Log?.LogError(
                    $"[VillagerBed] Base prefab '{BasePrefab}' not found; cannot build the cot");
                return;
            }

            _prefab = PieceFactory.ClonePrefab(basePrefab, PrefabName);
            Configure(_prefab);
        }

        /// <summary>
        ///     Idempotent: every step assigns an absolute value or destroys-then-adds, so a hot
        ///     reload re-running this cannot stack parts or double up components.
        /// </summary>
        public static void Configure(GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            if (piece != null)
            {
                piece.m_name = "Villager Cot";
                piece.m_description =
                    "A camp bed for a villager. Counts toward your villagers' comfort. " +
                    "You cannot sleep in it yourself.";
                piece.m_category = Piece.PieceCategory.Furniture;

                var wood = ObjectDB.instance?.GetItemPrefab("Wood")?.GetComponent<ItemDrop>();
                var scraps = ObjectDB.instance?.GetItemPrefab("LeatherScraps")?.GetComponent<ItemDrop>();
                var costs = new List<Piece.Requirement>();
                if (wood != null)
                    costs.Add(new Piece.Requirement { m_resItem = wood, m_amount = 10, m_recover = true });
                if (scraps != null)
                    costs.Add(new Piece.Requirement { m_resItem = scraps, m_amount = 4, m_recover = true });
                if (costs.Count > 0) piece.m_resources = costs.ToArray();
            }

            var bed = prefab.GetComponent<Bed>();
            if (bed != null) Object.DestroyImmediate(bed);

            HideChassisMeshes(prefab);
            GraftParts(prefab);
            GraftSheet(prefab);
            ConfigureInteraction(prefab);
            ApplyIcon(prefab);
        }

        /// <summary>
        ///     Make the cot openable, so a villager can be assigned to it.
        ///
        ///     <para>The <c>Interactable</c> must come BEFORE the <c>CraftingStation</c> in
        ///     component order: Valheim resolves interaction through
        ///     <c>GetComponentInParent&lt;Interactable&gt;()</c>, which returns the lowest-index
        ///     match. With the station first, E would open the native tab-less crafting menu
        ///     instead of the Assign tab. The prefab is a persistent template mutated across hot
        ///     reloads, so add-order alone is not reliable — it is normalised here.</para>
        /// </summary>
        public static void ConfigureInteraction(GameObject prefab)
        {
            var interact = prefab.GetComponent<UI.Interaction.CotInteract>()
                           ?? prefab.AddComponent<UI.Interaction.CotInteract>();

            var station = prefab.GetComponent<CraftingStation>();
            if (station != null && ComponentIndex(prefab, station) < ComponentIndex(prefab, interact))
            {
                Object.DestroyImmediate(station);
                station = null;
            }

            if (station == null) BuildStation(prefab);
        }

        /// <summary>
        ///     UI-only crafting station, the same shape the registry uses: it exists purely so
        ///     Valheim will open its crafting GUI for us. No roof or fire requirement (so it
        ///     opens anywhere and cannot NRE on a missing roof check point), no discovery or
        ///     build range, no effects. The "$vv_" name marks it virtual.
        /// </summary>
        private static void BuildStation(GameObject prefab)
        {
            var station = prefab.AddComponent<CraftingStation>();
            station.m_name = "$vv_villager_cot";
            station.m_discoverRange = 0f;
            station.m_rangeBuild = 0f;
            station.m_craftRequireRoof = false;
            station.m_craftRequireFire = false;
            station.m_showBasicRecipies = false;
            station.m_useDistance = 5f;
            station.m_useAnimation = 0;
            station.m_areaMarker = null;
            station.m_inUseObject = null;
            station.m_haveFireObject = null;
            station.m_craftItemEffects = new EffectList();
            station.m_craftItemDoneEffects = new EffectList();
            station.m_repairItemDoneEffects = new EffectList();
        }

        private static int ComponentIndex(GameObject prefab, Component target)
        {
            var all = prefab.GetComponents<Component>();
            for (var i = 0; i < all.Length; i++)
                if (ReferenceEquals(all[i], target))
                    return i;
            return -1;
        }

        /// <summary>
        ///     Rebuild interaction on an already-placed cot after a hot reload.
        ///
        ///     <para>The stale-component sweep destroys the mod-owned <c>CotInteract</c> on
        ///     placed pieces while the vanilla <c>CraftingStation</c> survives — and being itself
        ///     <c>Interactable</c>, it then hijacks E and opens the native tab-less menu, which
        ///     looks like the cot "lost its Assign tab". Both are destroyed and rebuilt in order
        ///     rather than patched, because a component killed by deferred <c>Object.Destroy</c>
        ///     is still a live managed reference this frame and would be found by
        ///     <c>GetComponent</c>.</para>
        /// </summary>
        public static void ReapplyInteractionToInstance(GameObject instance)
        {
            if (instance == null) return;

            var staleInteract = instance.GetComponent<UI.Interaction.CotInteract>();
            if (staleInteract != null) Object.DestroyImmediate(staleInteract);

            var staleStation = instance.GetComponent<CraftingStation>();
            if (staleStation != null) Object.DestroyImmediate(staleStation);

            instance.AddComponent<UI.Interaction.CotInteract>();
            BuildStation(instance);
        }

        /// <summary>The blanket child. One name, used by the graft, the icon and per-cot repaints.</summary>
        public const string SheetNode = "vv_sheet";

        /// <summary>
        ///     Blanket geometry. Fields rather than literals at the call site because the
        ///     occupant's mark is positioned in the BLANKET's frame — duplicating these into the
        ///     print call is how the two drift apart and the mark slides off the hide.
        /// </summary>
        private static readonly Vector3 SheetPos = new Vector3(0f, 0.46f, 0.19f) * BedScale;

        private const float SheetWidth = 0.82f * BedScale;
        private const float SheetLength = 1.56f * BedScale;

        /// <summary>
        ///     A blanket material for <paramref name="villagerType" />, or the plain hide when
        ///     that is empty.
        ///
        ///     <para>Shared by the prefab graft and by per-cot repaints, so an assigned cot and a
        ///     freshly placed one cannot disagree about where the mark sits. <paramref
        ///     name="source" /> is whatever carries the chassis materials — the template when
        ///     grafting, the placed cot when repainting one — because the shader is harvested
        ///     from it.</para>
        /// </summary>
        public static Material BuildBlanket(GameObject source, string villagerType)
        {
            var material = BuildHideMaterial(source, HideDonorPrefab, "rug (2)", forceOpaque: true);
            if (material == null || string.IsNullOrEmpty(villagerType)) return material;

            VillagerBedPrint.Bake(material, villagerType,
                new Vector2(SheetWidth, SheetLength), OverlayUvInset, s_overlayUv,
                printCentre: new Vector2(0f, PrintZ * BedScale - SheetPos.z),
                printSize: PrintSize * BedScale, yawDegrees: PrintYaw);
            return material;
        }

        /// <summary>
        ///     Photograph the cot for the build menu, always in its UNAFFILIATED state.
        ///
        ///     <para>The blanket is swapped back to a plain hide for the shot. The menu
        ///     advertises the piece the player is about to place, which is by definition
        ///     unclaimed — an icon wearing whichever villager happened to be selected would
        ///     misrepresent it, and would change under the player every time a cot was
        ///     assigned.</para>
        ///
        ///     <para>The donor bed's own meshes need no special handling: <see
        ///     cref="HideChassisMeshes" /> disables their renderers and the icon renderer skips
        ///     disabled ones, so only the grafted cot is in frame.</para>
        /// </summary>
        private static void ApplyIcon(GameObject prefab)
        {
            var sheet = prefab.transform.Find("vv_sheet")?.GetComponent<MeshRenderer>();
            var claimed = sheet != null ? sheet.sharedMaterial : null;

            if (sheet != null)
                sheet.sharedMaterial =
                    BuildHideMaterial(prefab, HideDonorPrefab, "rug (2)", forceOpaque: true);

            try
            {
                // 180: the cot's default facing puts the BACK of the headboard toward the
                // icon camera, which reads as an odd angular slab. Turned around, the camera
                // gets the headboard's front and the foot locker reads as a locker.
                PieceFactory.ApplyRenderedIcon(prefab, "VillagerBed", modelYaw: 180f);
            }
            finally
            {
                if (sheet != null) sheet.sharedMaterial = claimed;
            }
        }

        /// <summary>
        ///     Hide the donor bed's own meshes by disabling the RENDERERS, not the GameObjects.
        ///     WearNTear owns those objects and switches them on and off as damage state changes,
        ///     so deactivating them would simply be undone the first time the cot was hit.
        /// </summary>
        private static void HideChassisMeshes(GameObject prefab)
        {
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer == null) continue;
                if (renderer.transform.name.StartsWith("vv_")) continue; // our own parts
                renderer.enabled = false;
            }
        }

        private static void GraftParts(GameObject prefab)
        {
            // Hot reload re-runs Configure on the surviving template; drop the previous parts
            // first or they stack up one set per reload.
            var stale = new List<Transform>();
            foreach (Transform child in prefab.transform)
                if (child.name.StartsWith("vv_"))
                    stale.Add(child);
            foreach (var t in stale) Object.DestroyImmediate(t.gameObject);

            var scene = ZNetScene.instance;
            if (scene == null) return;

            var grafted = 0;
            foreach (var part in Parts)
            {
                var donor = scene.GetPrefab(part.Prefab);
                if (donor == null)
                {
                    Plugin.Log?.LogWarning($"[VillagerBed] part '{part.Name}': donor '{part.Prefab}' missing");
                    continue;
                }

                var src = FindNode(donor, part.Node);
                if (src == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[VillagerBed] part '{part.Name}': node '{part.Node}' not found on '{part.Prefab}'");
                    continue;
                }

                Graft(prefab, src, part);
                grafted++;
            }

            Plugin.Log?.LogInfo($"[VillagerBed] Grafted {grafted}/{Parts.Length} cot part(s)");
        }

        /// <summary>
        ///     Lay the cloth bedroll, wearing the yellow banner's material so it matches the
        ///     rigid parts' art without needing a texture of its own.
        /// </summary>
        /// <summary>
        ///     Lay the bedding in two layers: a wolf pelt filling the frame, and a deer hide
        ///     thrown over it. The wolf layer is the one that must reach the rails on every
        ///     side, so it is cut slightly OVERSIZE and samples only the solid middle of its
        ///     texture — a hide's art is a silhouette with transparent margin, so a layer sized
        ///     to the frame but sampling the full image arrives with see-through corners.
        /// </summary>
        private static void GraftSheet(GameObject prefab)
        {
            // Under-layer: wolf, sized to the frame interior — rails sit at x +/-0.40 and
            // z +/-0.92, so the inner span is ~0.73 x 1.84. Shrinking BOTH axes 10% fixed the
            // width but left the length at 1.73 against a 1.84 opening, which is what showed as
            // gaps at the head and foot while the sides looked right.
            VillagerBedCloth.Apply(prefab, "vv_underlay",
                BuildHideMaterial(prefab, WolfDonorPrefab, "rug (2)", forceOpaque: true),
                new Vector3(0f, 0.40f, 0f) * BedScale,
                width: 0.76f * BedScale, length: 1.86f * BedScale, loft: 0.045f * BedScale,
                uvInset: UnderlayUvInset, uvOffset: s_underlayUv);

            // Pillow: the bedding generator again, just small and steeply lofted — a short, wide
            // patch with a high dome IS a pillow, so it needs no donor mesh of its own. Wears
            // the wolf pelt so it matches the under-layer rather than introducing a third hide.
            VillagerBedCloth.Apply(prefab, "vv_pillow",
                BuildHideMaterial(prefab, WolfDonorPrefab, "rug (2)", forceOpaque: true),
                new Vector3(0f, 0.52f, -0.66f) * BedScale,
                // 25% up on all three axes, loft included — scaling only the footprint would
                // flatten it, and a wide flat patch stops reading as a pillow.
                width: 0.575f * BedScale, length: 0.325f * BedScale, loft: 0.10625f * BedScale,
                uvInset: UnderlayUvInset, uvOffset: s_underlayUv);

            // Over-layer: deer, still wider than the mattress so it breaks over the rails
            // instead of stopping on top of them, and pushed toward the FOOT so the pillow end
            // stays clear — a hide drawn up to the headboard reads as a dust sheet.
            //
            // The foot end is the fiddly one. From the constants: half-length is 1.56/2 = 0.78
            // and DrapeStart is 0.78, so the fold zone runs z+0.608 .. z+0.78. At z=0.19 that
            // brackets the foot rail at 0.92 (frame_f), which is the intent — the rail should
            // fall INSIDE the fold so the hide breaks over the timber rather than ending before
            // or after it.
            //
            // Reported by the user at z=0.24: the hide clipped through the frame. Consistent
            // with the arithmetic, since 0.24+0.78 = 1.02 puts its END past the 0.92 rail.
            // z=0.19 was not independently confirmed to be optimal — it is the value that
            // satisfies the bracket, nothing more.
            //
            // DRAPE IS CLEARANCE-BOUND. Rail tops sit at y = 0.36 + 0.032 = 0.392 (frame y plus
            // half the 0.064 cross-section) and the hide's base is y = 0.46, so it clears them
            // by 0.068. A drape deeper than that folds the hide's edges BELOW the rail tops —
            // i.e. straight through the timber, which is what 0.11 did. 0.05 keeps the edge at
            // y = 0.41, just proud of the rail, so it rests over the wood instead of inside it.
            VillagerBedCloth.Apply(prefab, SheetNode, BuildBlanket(prefab, s_printVillagerType),
                SheetPos, width: SheetWidth, length: SheetLength, loft: 0.095f * BedScale,
                uvInset: OverlayUvInset, uvOffset: s_overlayUv, drape: 0.05f * BedScale);

            // Hot-reload migration: the print used to be its own child mesh. The prefab is
            // grafted in place and survives a reload, so a stale one would sit there forever.
            var strayPrint = prefab.transform.Find("vv_print");
            if (strayPrint != null) Object.DestroyImmediate(strayPrint.gameObject);
        }

        /// <summary>
        ///     Edge of the occupant's mark on the blanket, in bed units before BedScale. Square
        ///     ON THE BED — VillagerBedPrint converts that into the non-square texture rectangle
        ///     the blanket's stretched UVs require.
        /// </summary>
        private const float PrintSize = 0.675f;

        /// <summary>
        ///     Where the mark sits along the bed, in cot-local Z — the blanket's own centre.
        ///
        ///     <para>Bounded at both ends by the drape rather than by the blanket's edges. The
        ///     hide only lies FLAT within DrapeStart (0.78) of half its length, i.e. cot-local
        ///     z -0.42 .. 0.80; past that it folds over the rails. A 0.675 mark yawed 120 deg
        ///     reaches 0.675/2 * (|cos|+|sin|) = 0.46 from its centre, so the centre has to stay
        ///     inside 0.04 .. 0.34 or part of the silhouette bends down over a rail. 0.35 was
        ///     just outside that at the foot end; 0.19 sits mid-bracket with room either side.</para>
        /// </summary>
        private const float PrintZ = 0.19f;

        /// <summary>
        ///     Rotation of the print on the blanket. Icons are authored upright for a UI slot,
        ///     which is not the orientation they want lying on a bed seen from the side.
        /// </summary>
        private const float PrintYaw = 120f;

        /// <summary>
        ///     Whose icon is printed. Empty = none. Set by hand for now: which villager a cot
        ///     belongs to is an open design question — see vv_bedprint.
        /// </summary>
        private static string s_printVillagerType = "";

        /// <summary>
        ///     How far in from the texture edge the underlay samples. Large enough to clear the
        ///     hide silhouette's transparent surround entirely, so the pelt is opaque right out
        ///     to the bed frame.
        /// </summary>
        /// <summary>
        ///     Per-axis, because the donor rug quad is not square (2.55 x 3.46) and carries its
        ///     own non-uniform scale — a single shared inset framed the pelt off-centre and the
        ///     underlay read as skewed.
        /// </summary>
        private static readonly Vector2 UnderlayUvInset = new(0.30f, 0.34f);

        /// <summary>
        ///     A modest inset for the deer hide, where the full texture was used before.
        ///
        ///     <para>The drape and the hide's ragged outline pull against each other: the mesh
        ///     folds down past the rails, but with full-texture UVs those outer vertices land on
        ///     the silhouette's transparent margin, so the fold happens on invisible geometry.
        ///     Insetting pushes solid hide out to where the fold is. Kept small so the edge still
        ///     reads as a pelt rather than a rectangle — the V inset is larger because the
        ///     texture's hide sits off-centre lengthwise.</para>
        /// </summary>
        private static readonly Vector2 OverlayUvInset = new(0.36f, 0.36f);

        /// <summary>Wolf pelt for the under-layer; its own rug stacks fur layers the same way.</summary>
        private const string WolfDonorPrefab = "rug_wolf";

        /// <summary>
        ///     The banner's TEXTURE on a non-swaying shader.
        ///
        ///     <para>Using the banner's own material put the sheet on <c>Custom/Vegetation</c>,
        ///     which is Valheim's wind shader: it displaces vertices so hanging cloth flutters.
        ///     On a bedroll that reads as a sheet flapping in the breeze, and the displacement
        ///     fights the Cloth sim rather than adding to it. Borrowing an opaque piece shader
        ///     and swapping its albedo keeps the yellow look and holds still.</para>
        /// </summary>
        private static Material BuildHideMaterial(GameObject prefab, string donorPrefab, string donorNode,
            bool forceOpaque = false)
        {
            // Deer hide, stretched edge to edge. The texture comes from `rug_deer`
            // (`deerhide_rug` / `hide01`), which is authored for a flat rug and so fills its UV
            // space — unlike the bed's own BedSimple_d, an atlas whose mattress occupies one
            // corner, and unlike the banner art, which is two banners with a transparent gap.
            //
            // Only the TEXTURE is borrowed. `rug_deer` is on shader `Custom/Rug`, and the last
            // time an unexamined shader was inherited here (`Valheim/Snow Mesh`) it did
            // world-projected vertex work and stretched this very grid into a sail. The shader
            // comes from a plain Custom/Piece material on the chassis instead.
            Material shaderDonor = null;
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || r.sharedMaterial == null) continue;
                if (r.transform.name.StartsWith("vv_")) continue;
                var shader = r.sharedMaterial.shader;
                if (shader == null || shader.name != "Custom/Piece") continue;
                shaderDonor = r.sharedMaterial;
                break;
            }

            if (shaderDonor == null)
            {
                Plugin.Log?.LogWarning(
                    "[VillagerBed] no Custom/Piece material on the chassis; bedroll left untextured");
                return null;
            }

            var mat = new Material(shaderDonor) { name = $"vv_bed_{donorPrefab}_mat" };

            var rug = ZNetScene.instance?.GetPrefab(donorPrefab);
            var rugNode = rug != null ? FindNode(rug, donorNode) : null;
            var hide = rugNode != null ? rugNode.GetComponent<MeshRenderer>()?.sharedMaterial?.mainTexture : null;

            if (hide != null)
            {
                mat.mainTexture = hide;
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", hide);
                if (mat.HasProperty("_Color")) mat.color = Color.white;
                // Tiling must be 1:1 or the hide repeats across the bedroll instead of
                // stretching over it; the donor material may carry its own scale.
                if (mat.HasProperty("_MainTex")) mat.SetTextureScale("_MainTex", Vector2.one);
                if (mat.HasProperty("_MainTex")) mat.SetTextureOffset("_MainTex", Vector2.zero);
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[VillagerBed] hide texture missing from '{donorPrefab}'; " +
                    "bedroll falls back to the chassis texture");
            }

            if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
            mat.DisableKeyword("_NORMALMAP");

            // Hide is matte. The shader donor is a chassis material and brings its own metallic
            // and gloss along with it, which made the blanket read as polished wood — the same
            // mistake as the headboard, where cloning a material and swapping only its texture
            // kept the donor's surface properties.
            // Property names taken from a shader dump, not guessed. Custom/Piece is a WOOD/STONE
            // shader and brings two surprises along with it:
            //   _AddRain      — wet-surface sheen. Outdoors in rain this is what made the hide
            //                   look polished; bedding should not slick over like a plank.
            //   _ValueNoise / _Ripple* — procedural WOOD-GRAIN ripple baked into the shader. On
            //                   a hide texture it reads as varnished timber.
            // Zeroing gloss alone did nothing because neither of those is gloss.
            if (mat.HasProperty("_MetalColor")) mat.SetColor("_MetalColor", Color.black);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
            if (mat.HasProperty("_MetallicAlphaGloss")) mat.SetFloat("_MetallicAlphaGloss", 0f);
            if (mat.HasProperty("_AddRain")) mat.SetFloat("_AddRain", 0f);
            if (mat.HasProperty("_ValueNoise")) mat.SetFloat("_ValueNoise", 0f);
            if (mat.HasProperty("_ValueNoiseVertex")) mat.SetFloat("_ValueNoiseVertex", 0f);
            if (mat.HasProperty("_RippleDistance")) mat.SetFloat("_RippleDistance", 0f);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");

            if (forceOpaque)
            {
                // Hide art carries alpha, and this shader path honours it — that is exactly why
                // the deer hide shows a ragged silhouette. Good for the top layer, fatal for the
                // one that has to be solid out to the bed frame: inset UVs alone cannot help if
                // the sampled texels are themselves transparent. Cut the alpha test out.
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0f);
                mat.SetOverrideTag("RenderType", "Opaque");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }

            return mat;
        }

        /// <summary>Deer-hide rug: a flat piece whose texture fills its UVs edge to edge.</summary>
        private const string HideDonorPrefab = "rug_deer";

        /// <summary>The yellow banner — `Banner_2_YellowBlackBorder_mat`, the yellow one of eleven.</summary>
        private const string SheetDonorPrefab = "piece_banner08";

        /// <summary>
        ///     First renderable child with this name. Searched rather than path-addressed
        ///     because donors bury their meshes under LOD and damage-state containers whose
        ///     names vary between prefabs.
        /// </summary>
        private static Transform FindNode(GameObject donor, string node)
        {
            if (donor == null || string.IsNullOrEmpty(node)) return null;
            foreach (var filter in donor.GetComponentsInChildren<MeshFilter>(true))
                if (filter != null && filter.sharedMesh != null && filter.name == node)
                    return filter.transform;
            return null;
        }

        /// <summary>
        ///     Copy one donor mesh and its materials onto the cot. Meshes only — no colliders
        ///     come across, so the parts cannot carve the navmesh or block the villager whose
        ///     bed it is.
        /// </summary>
        private static void Graft(GameObject parent, Transform src, Part part)
        {
            var srcFilter = src.GetComponent<MeshFilter>();
            var srcRenderer = src.GetComponent<MeshRenderer>();
            if (srcFilter?.sharedMesh == null || srcRenderer == null) return;

            var go = new GameObject("vv_" + part.Name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = part.Pos * BedScale;
            go.transform.localRotation = Quaternion.Euler(part.Euler);

            // Multiply in the donor's ACCUMULATED scale. Only the mesh is copied, not the chain
            // of scaled parents it hung under, so without this a part comes out at raw-mesh size
            // while every scale here was derived from the donor's APPARENT size in a dump.
            // The marble headboard sat under a node scaled 0.5 in Y and duly rendered exactly
            // twice as tall as specified. With this, a part's scale means "fraction of the
            // donor's apparent size", which is what the dumps report and what one can reason in.
            go.transform.localScale = Vector3.Scale(part.Scale * BedScale, src.lossyScale);

            go.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;
            var renderer = go.AddComponent<MeshRenderer>();

            if (part.Tint.HasValue || part.TexPrefab != null)
            {
                var custom = new Material(srcRenderer.sharedMaterial) { name = $"vv_{part.Name}_mat" };


                if (part.TexPrefab != null)
                {
                    var texDonorNode = FindNode(ZNetScene.instance?.GetPrefab(part.TexPrefab), part.TexNode);
                    var texMat = texDonorNode?.GetComponent<MeshRenderer>()?.sharedMaterial;
                    if (texMat != null)
                    {
                        // Take the donor's WHOLE material, not just its texture. Cloning the
                        // shape-donor's material and swapping the image left polished marble's
                        // metallic and gloss settings in place, so the headboard glistened like
                        // stone while wearing a wood texture and read as a different material
                        // from the rails beside it. Starting from the wood material means it
                        // shades exactly like the frame.
                        custom = new Material(texMat) { name = $"vv_{part.Name}_mat" };
                        if (custom.HasProperty("_MainTex"))
                        {
                            // The beam material carries its own tiling/offset for a 2m plank;
                            // the bench mesh has its own UVs, so sample straight.
                            custom.SetTextureScale("_MainTex", Vector2.one);
                            custom.SetTextureOffset("_MainTex", Vector2.zero);
                        }

                        if (custom.HasProperty("_EmissionColor")) custom.SetColor("_EmissionColor", Color.black);
                        custom.DisableKeyword("_EMISSION");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning(
                            $"[VillagerBed] part '{part.Name}': no material on " +
                            $"'{part.TexPrefab}/{part.TexNode}'; keeping the donor material");
                    }
                }

                if (part.Tint.HasValue && custom.HasProperty("_Color")) custom.color = part.Tint.Value;
                renderer.sharedMaterial = custom;
            }
            else
            {
                renderer.sharedMaterials = srcRenderer.sharedMaterials;
            }

            renderer.enabled = true;
        }

        /// <summary>
        ///     Every cot in the world, found by PREFAB NAME rather than a marker component. A hot
        ///     reload strips mod components off placed pieces, so a cot identified by a
        ///     <c>MonoBehaviour</c> quietly stops counting as a bed after the first reload —
        ///     observed exactly that way. The prefab name is on the ZDO and survives.
        /// </summary>
        public static IEnumerable<GameObject> PlacedCots()
        {
            foreach (var piece in Object.FindObjectsByType<Piece>(FindObjectsSortMode.None))
                if (piece != null && IsCot(piece.gameObject))
                    yield return piece.gameObject;
        }

        /// <summary>Is this GameObject a Villager Cot? Prefab-name based, so reload-proof.</summary>
        public static bool IsCot(GameObject go)
        {
            return go != null && Utils.GetPrefabName(go) == PrefabName;
        }

        /// <summary>
        ///     Destroy and re-place every cot where it stands, so a template edit becomes
        ///     visible. A placed piece keeps the grafted children it was instantiated with, and
        ///     hot reload does not rebuild them — without this, every look change means hunting
        ///     down and re-placing each cot by hand.
        /// </summary>
        [DevCommand("Rebuild placed Villager Cots in place so template edits take effect",
            Name = "vv_bedrebuild")]
        public static void Rebuild(Terminal.ConsoleEventArgs args)
        {
            var scene = ZNetScene.instance;
            if (scene == null)
            {
                Print("[vv_bedrebuild] ZNetScene not ready");
                return;
            }

            // Pin midday first. Every look change is judged by eye, and judging one made at
            // dusk against one made at night says nothing about the change.
            Diagnostics.FreezeTime.Noon();

            // Re-apply the current layout to the template first, so replacements are built from
            // it rather than from whatever the last reload left behind.
            Register(scene);
            if (_prefab == null)
            {
                Print("[vv_bedrebuild] no cot prefab");
                return;
            }

            var placements = new List<(Vector3 pos, Quaternion rot)>();
            foreach (var cot in PlacedCots())
                placements.Add((cot.transform.position, cot.transform.rotation));

            var old = new List<GameObject>(PlacedCots());

            // Place first, and only destroy an original once its replacement is verifiably
            // alive. Destroying unconditionally lost cots outright on a rapid second call
            // ("rebuilt 0 cot(s)" on a cot that was plainly there) — pairing each destroy to a
            // confirmed replacement means the worst case is a duplicate, which is visible and
            // removable, rather than silently nothing.
            var placed = new List<GameObject>();
            foreach (var (pos, rot) in placements)
            {
                var copy = Object.Instantiate(_prefab, pos, rot);
                var copyView = copy != null ? copy.GetComponent<ZNetView>() : null;
                if (copy != null && copyView != null && copyView.IsValid()) placed.Add(copy);
                else Plugin.Log?.LogWarning(
                    $"[VillagerBed] replacement at {pos} has no valid ZNetView; keeping the original");
            }

            var destroyed = 0;
            for (var i = 0; i < old.Count && i < placed.Count; i++)
            {
                var cot = old[i];
                if (cot == null) continue;
                var nview = cot.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid()) nview.ClaimOwnership();
                scene.Destroy(cot);
                destroyed++;
            }

            var where = new System.Text.StringBuilder();
            foreach (var (pos, _) in placements)
                where.Append($" ({pos.x:F1},{pos.y:F1},{pos.z:F1})");

            Print($"[vv_bedrebuild] rebuilt {placed.Count}/{placements.Count} cot(s), " +
                  $"{destroyed} old removed, at{where}");
        }

        /// <summary>
        ///     Move or resize one cot part in-game and rebuild every placed cot, so the layout
        ///     can be dialled by eye. Grafted-prop proportions are a judgement call, and the
        ///     edit/build/reload/re-place loop is far too slow to make it by.
        /// </summary>
        [DevCommand("Tune a cot part: vv_bedpart <name> <x> <y> <z> [scale]  (no args lists parts)",
            Name = "vv_bedpart")]
        public static void TunePart(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 5)
            {
                var sb = new System.Text.StringBuilder("[vv_bedpart] parts:");
                foreach (var p in Parts)
                {
                    // Report the RESULTING world size, not just the authored scale: a part can
                    // carry the right numbers and still be oriented wrong, which is how four
                    // legs ended up lying flat while their scale looked correct.
                    var size = "";
                    var node = _prefab != null ? _prefab.transform.Find("vv_" + p.Name) : null;
                    var renderer = node != null ? node.GetComponent<MeshRenderer>() : null;
                    if (renderer != null)
                    {
                        var b = renderer.bounds.size;
                        size = $" size=({b.x:F2},{b.y:F2},{b.z:F2})";
                    }

                    sb.Append($"\n  {p.Name,-10} pos=({p.Pos.x:F2},{p.Pos.y:F2},{p.Pos.z:F2})" +
                              $" rot=({p.Euler.x:F0},{p.Euler.y:F0},{p.Euler.z:F0}){size}");
                }

                // Bedding layers are generated meshes, not Parts, so they never appeared here —
                // which is why "is the blanket square to the frame?" kept being argued from
                // screenshots instead of measured. World-space corners settle rotation, shear
                // and offset in one line each.
                foreach (var layer in new[] { "vv_underlay", "vv_sheet", "vv_pillow" })
                {
                    var node = _prefab != null ? _prefab.transform.Find(layer) : null;
                    var mf = node != null ? node.GetComponent<MeshFilter>() : null;
                    if (mf?.sharedMesh == null) continue;

                    var v = mf.sharedMesh.vertices;
                    // Corner vertices of the row-major grid: first, last-of-first-row, first-of-
                    // last-row, last. If the quad is square to the bed these share x/z pairwise.
                    var n = v.Length;
                    var cols = 9;
                    var c0 = node.TransformPoint(v[0]);
                    var c1 = node.TransformPoint(v[cols - 1]);
                    var c2 = node.TransformPoint(v[n - cols]);
                    var c3 = node.TransformPoint(v[n - 1]);
                    sb.Append($"\n  {layer,-12} corners x[{c0.x:F2} {c1.x:F2} {c2.x:F2} {c3.x:F2}]" +
                              $" z[{c0.z:F2} {c1.z:F2} {c2.z:F2} {c3.z:F2}]");
                }

                sb.Append("\n  usage: vv_bedpart <name> <x> <y> <z> [scale]");
                Print(sb.ToString());
                return;
            }

            var name = args[1];
            if (!float.TryParse(args[2], out var x) ||
                !float.TryParse(args[3], out var y) ||
                !float.TryParse(args[4], out var z))
            {
                Print("[vv_bedpart] could not parse three numbers");
                return;
            }

            var found = false;
            for (var i = 0; i < Parts.Length; i++)
            {
                if (Parts[i].Name != name) continue;

                var scale = Parts[i].Scale;
                if (args.Length >= 6 && float.TryParse(args[5], out var uniform))
                    scale = new Vector3(uniform, uniform, uniform);

                Parts[i] = new Part(Parts[i].Name, Parts[i].Prefab, Parts[i].Node,
                    new Vector3(x, y, z), Parts[i].Euler, scale);
                found = true;
                break;
            }

            if (!found)
            {
                Print($"[vv_bedpart] no part named '{name}'");
                return;
            }

            Rebuild(null);
            Print($"[vv_bedpart] {name} -> ({x:F2},{y:F2},{z:F2})");
        }

        /// <summary>
        ///     Report where the cots are without touching them. Finding one should not require
        ///     rebuilding it — that is how a look-at turned into a lost cot more than once.
        /// </summary>
        /// <summary>
        ///     Re-run the placed-cot interaction repair by hand. Isolates "the repair is broken"
        ///     from "the repair never ran", which a log line alone cannot distinguish.
        /// </summary>
        [DevCommand("Re-attach interaction to placed Villager Cots", Name = "vv_cotfix")]
        public static void CotFix(Terminal.ConsoleEventArgs args)
        {
            var n = 0;
            foreach (var cot in PlacedCots())
            {
                ReapplyInteractionToInstance(cot);
                VillagerCotOccupant.RefreshBlanket(cot);
                n++;
            }

            Print($"[vv_cotfix] re-attached interaction on {n} cot(s)");
        }

        [DevCommand("List placed Villager Cots and their positions", Name = "vv_bedwhere")]
        public static void Where(Terminal.ConsoleEventArgs args)
        {
            var sb = new System.Text.StringBuilder("[vv_bedwhere]");
            var n = 0;
            foreach (var cot in PlacedCots())
            {
                var p = cot.transform.position;
                sb.Append($"\n  ({p.x:F1},{p.y:F1},{p.z:F1})");

                // What a placed cot is MADE OF, because that is what hot reload takes away.
                // A cot whose grafted children are gone still has a Piece and a ZDO, so every
                // count-based check says it is fine while the player sees an empty frame.
                var parts = new System.Text.StringBuilder();
                var grafted = 0;
                foreach (Transform child in cot.transform)
                {
                    if (!child.name.StartsWith("vv_")) continue;
                    grafted++;
                    if (parts.Length > 0) parts.Append(' ');
                    var r = child.GetComponent<Renderer>();
                    parts.Append(r == null ? $"{child.name}(no-renderer)"
                        : r.enabled ? child.name : $"{child.name}(off)");
                }

                // Chassis renderers are disabled on purpose; if they are ON, HideChassisMeshes
                // did not run on this instance and the donor bed is showing through.
                var chassisOn = 0;
                foreach (var r in cot.GetComponentsInChildren<MeshRenderer>(true))
                    if (r != null && r.enabled && !r.transform.name.StartsWith("vv_"))
                        chassisOn++;

                var occupant = VillagerCotOccupant.ResolveRecord(cot);
                sb.Append($"  occupant={(occupant != null ? occupant.Name : "none")}" +
                          $"  grafted={grafted}  chassisVisible={chassisOn}" +
                          $"  interact={(cot.GetComponent<UI.Interaction.CotInteract>() != null ? "y" : "N")}" +
                          $"  station={(cot.GetComponent<CraftingStation>() != null ? "y" : "N")}" +
                          $"  znv={(cot.GetComponent<ZNetView>()?.IsValid() == true ? "y" : "N")}");
                if (grafted > 0) sb.Append($"\n      parts: {parts}");
                n++;
            }

            if (n == 0) sb.Append(" no cots placed");
            Print(sb.ToString());
        }

        /// <summary>
        ///     Slide a bedding layer's texture window. Which part of a hide texture is solid is
        ///     not something that can be reasoned out from a prefab dump — it has to be seen —
        ///     and an off-centre window is what makes a symmetric mesh look skewed and overhang
        ///     one rail.
        /// </summary>
        [DevCommand("Nudge bedding texture: vv_bedhide <underlay|sheet> <u> <v>  (no args lists)",
            Name = "vv_bedhide")]
        public static void TuneHide(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 4)
            {
                Print($"[vv_bedhide] underlay=({s_underlayUv.x:F3},{s_underlayUv.y:F3}) " +
                      $"sheet=({s_overlayUv.x:F3},{s_overlayUv.y:F3}) — " +
                      "usage: vv_bedhide <underlay|sheet> <u> <v>");
                return;
            }

            if (!float.TryParse(args[2], out var u) || !float.TryParse(args[3], out var v))
            {
                Print("[vv_bedhide] could not parse two numbers");
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "underlay": s_underlayUv = new Vector2(u, v); break;
                case "sheet": s_overlayUv = new Vector2(u, v); break;
                default:
                    Print("[vv_bedhide] expected 'underlay' or 'sheet'");
                    return;
            }

            Rebuild(null);
            Print($"[vv_bedhide] underlay=({s_underlayUv.x:F3},{s_underlayUv.y:F3}) " +
                  $"sheet=({s_overlayUv.x:F3},{s_overlayUv.y:F3})");
        }

        private static Vector2 s_underlayUv = Vector2.zero;

        /// <summary>
        ///     Back to zero. The -0.06 nudge was dialled while this layer was still ALPHA-CUT,
        ///     to centre a visible silhouette inside the quad. Once the layer went opaque there is
        ///     no silhouette to centre — the whole quad draws — and the offset only decided which
        ///     side showed more pale margin, i.e. it created the skew it had previously cured.
        /// </summary>
        private static Vector2 s_overlayUv = Vector2.zero;

        /// <summary>
        ///     Choose whose station icon is printed on the blanket.
        ///
        ///     <para>Manual on purpose. Deciding this automatically needs an answer to "which
        ///     villager does this cot belong to", and the obvious route — storing an occupant on
        ///     the cot — is the bed/villager coupling that was deliberately removed once already.
        ///     A display-only link is defensible, but it is a decision to take rather than
        ///     assume.</para>
        /// </summary>
        [DevCommand("Print a villager's station icon on the cot blanket: " +
                    "vv_bedprint <VillagerType|none>", Name = "vv_bedprint")]
        public static void SetPrint(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                Print($"[vv_bedprint] current='{(s_printVillagerType == "" ? "none" : s_printVillagerType)}' " +
                      "— usage: vv_bedprint <VillagerType|none>");
                return;
            }

            var arg = args[1];
            if (string.Equals(arg, "none", System.StringComparison.OrdinalIgnoreCase))
            {
                s_printVillagerType = "";
            }
            else
            {
                if (Villager.Registry.VillagerRegistry.Get(arg) == null)
                {
                    Print($"[vv_bedprint] no villager type named '{arg}'");
                    return;
                }

                s_printVillagerType = arg;
            }

            // Drop the baked blankets first: the cache is keyed by villager type, so without
            // this a tint or icon edit would be invisible here and look like a no-op.
            VillagerBedPrint.ClearCache();
            Rebuild(null);
            Print($"[vv_bedprint] print='{(s_printVillagerType == "" ? "none" : s_printVillagerType)}'");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
