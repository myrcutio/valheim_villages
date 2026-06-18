using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Interaction;
using Object = UnityEngine.Object;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Builds and registers the custom buildable <b>Village Registry</b> piece.
    ///     Unlike <see cref="ItemFactory" /> (which clones ItemDrops), this clones a
    ///     vanilla <c>Piece</c> prefab (the wooden table) so we inherit a fully working
    ///     Piece / ZNetView / WearNTear / collider / destruction setup for free, then
    ///     grafts vanilla prop meshes (work-order scroll, candle, feather) onto the
    ///     tabletop as pure visual children. No AssetBundle, no shader fix-up: every
    ///     grafted mesh keeps its source material, which is already a live game material.
    ///     The piece is added to ZNetScene (so it can be spawned / networked) and to the
    ///     Hammer's build PieceTable (so it appears in the build menu).
    /// </summary>
    public static class PieceFactory
    {
        public const string RegistryPrefabName = "vv_village_registry";

        // Vanilla source prefabs composed into the registry.
        private const string BaseTable = "piece_table"; // wood dining table: 2.5 x 1.25, top ~0.83
        private const string CandleSource = "Candle_resin"; // candle wax mesh at "full/stack"
        private const string FeatherSource = "CelestialFeather"; // single valkyrie feather (attach/default)
        private const string CurtainSource = "piece_cloth_hanging_door_blue2"; // draping blue curtain (rod + cloth)
        private const string ShelfSource = "dvergrprops_shelf"; // dvergr shelf unit, used as a desktop letter tray
        private const string StoolSource = "dvergrprops_stool"; // stool placed on the ground in front of the desk

        private const float TopY = 0.83f; // tabletop surface height in piece-local space

        private static GameObject _registryPrefab;

        /// <summary>
        ///     Deferred entry point: registers the registry piece once ObjectDB is alive.
        ///     <see cref="AddToHammerTable" /> needs the Hammer item (and its build PieceTable)
        ///     to exist in ObjectDB, and <see cref="ConfigurePiece" /> reads Wood/Resin for the
        ///     build cost — none of which are present when ZNetScene.Awake wins the race against
        ///     ObjectDB.Awake. Annotating this [RequireObjectDB] defers it until both are ready,
        ///     so the recipe always lands in the build menu (was silently dropped before).
        /// </summary>
        [RequireObjectDB]
        public static void RegisterDeferred()
        {
            RegisterAllInZNetScene(ZNetScene.instance);
        }

        /// <summary>
        ///     Ensure the registry prefab exists, then register it in ZNetScene and the
        ///     Hammer build table. Idempotent — safe to call again on hot reload.
        /// </summary>
        public static void RegisterAllInZNetScene(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            EnsureRegistryPrefab(zNetScene);
            if (_registryPrefab == null) return;

            if (!zNetScene.m_prefabs.Contains(_registryPrefab))
                zNetScene.m_prefabs.Add(_registryPrefab);

            var named = GetPrivateDictionary(zNetScene, "m_namedPrefabs");
            if (named != null)
                named[RegistryPrefabName.GetStableHashCode()] = _registryPrefab;

            AddToHammerTable(_registryPrefab);

            Plugin.Log?.LogInfo($"[PieceFactory] Registered '{RegistryPrefabName}' in ZNetScene + Hammer table");
        }

        private static void EnsureRegistryPrefab(ZNetScene zNetScene)
        {
            if (_registryPrefab != null) return;

            // Hot reload: a prior assembly may have left the prefab registered. Re-apply the
            // composition so prop tweaks take effect without restarting the game. (Already-placed
            // world instances are clones and won't update — spawn/place a fresh one to view.)
            var existing = zNetScene.GetPrefab(RegistryPrefabName);
            if (existing != null)
            {
                StripGraftedChildren(existing);
                ConfigurePiece(existing);
                ConfigureInteraction(existing);
                GraftProps(existing, zNetScene);
                _registryPrefab = existing;
                Plugin.Log?.LogInfo("[PieceFactory] Re-grafted registry prefab after hot reload");
                return;
            }

            var baseTable = zNetScene.GetPrefab(BaseTable);
            if (baseTable == null)
            {
                Plugin.Log?.LogError($"[PieceFactory] Base prefab '{BaseTable}' not found; cannot build registry");
                return;
            }

            var prefab = ClonePrefab(baseTable, RegistryPrefabName);

            ConfigurePiece(prefab);
            ConfigureInteraction(prefab);
            GraftProps(prefab, zNetScene);

            _registryPrefab = prefab;
            Plugin.Log?.LogInfo($"[PieceFactory] Built registry prefab from '{BaseTable}'");
        }

        private static void ConfigurePiece(GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            if (piece == null) return;

            piece.m_name = "Village Registry";
            piece.m_description = "A scribe's desk where villagers are enrolled, revived, and recorded.";
            piece.m_category = Piece.PieceCategory.Crafting;

            var wood = ObjectDB.instance?.GetItemPrefab("Wood")?.GetComponent<ItemDrop>();
            var resin = ObjectDB.instance?.GetItemPrefab("Resin")?.GetComponent<ItemDrop>();
            var requirements = new List<Piece.Requirement>();
            if (wood != null)
                requirements.Add(new Piece.Requirement { m_resItem = wood, m_amount = 10, m_recover = true });
            if (resin != null)
                requirements.Add(new Piece.Requirement { m_resItem = resin, m_amount = 4, m_recover = true });
            if (requirements.Count > 0)
                piece.m_resources = requirements.ToArray();
        }

        /// <summary>
        ///     Make the registry interactable: pressing E opens the crafting GUI with
        ///     the registry's custom tabs. Adds a <see cref="RegistryInteract" />
        ///     (the Interactable) and a UI-only <see cref="CraftingStation" />, in
        ///     that order so <c>GetComponentInParent&lt;Interactable&gt;()</c> resolves
        ///     to RegistryInteract — the same add-order invariant the villager spawn
        ///     relies on (VillagerInteract before VillagerStation). Idempotent: the
        ///     hot-reload branch reuses the existing prefab, so never double-add.
        /// </summary>
        private static void ConfigureInteraction(GameObject prefab)
        {
            // The Interactable must come BEFORE the CraftingStation in component
            // order: Valheim resolves interaction via GetComponentInParent<Interactable>(),
            // which returns the lowest-index match. If RegistryInteract isn't first,
            // pressing E would open the native (tab-less) crafting menu instead of
            // our registry tabs. The prefab is a persistent template mutated across
            // hot reloads, so add-order alone isn't reliable — normalise it here.
            var interact = prefab.GetComponent<RegistryInteract>()
                           ?? prefab.AddComponent<RegistryInteract>();

            var station = prefab.GetComponent<CraftingStation>();
            if (station != null &&
                ComponentIndex(prefab, station) < ComponentIndex(prefab, interact))
            {
                // Out of order (station before interact) — rebuild it after interact.
                Object.DestroyImmediate(station);
                station = null;
            }

            if (station == null)
            {
                // UI-only crafting station (mirrors VillagerStation.Initialize): no
                // roof/fire requirement (opens anywhere, no NRE on m_roofCheckPoint),
                // no discovery/build range, no effects. "$vv_" marks it virtual.
                station = prefab.AddComponent<CraftingStation>();
                station.m_name = "$vv_village_registry";
                station.m_discoverRange = 0f;
                station.m_rangeBuild = 0f;
                station.m_craftRequireRoof = false;
                station.m_craftRequireFire = false;
                station.m_showBasicRecipies = false;
                station.m_useDistance = 10f;
                station.m_useAnimation = 0;
                station.m_areaMarker = null;
                station.m_inUseObject = null;
                station.m_haveFireObject = null;
                station.m_craftItemEffects = new EffectList();
                station.m_craftItemDoneEffects = new EffectList();
                station.m_repairItemDoneEffects = new EffectList();
            }

            Plugin.Log?.LogInfo(
                $"[PieceFactory] Registry interaction ready: RegistryInteract@{ComponentIndex(prefab, interact)}, " +
                $"CraftingStation@{ComponentIndex(prefab, station)} (interact must precede station)");
        }

        /// <summary>
        ///     Re-attach interaction to an already-placed registry world instance after a
        ///     hot reload. The stale-component sweep (<see cref="HotReloadHelper" />.
        ///     DestroyStaleComponents) destroys the mod-owned <see cref="RegistryInteract" />
        ///     on placed pieces, but — unlike villagers (FixupExistingNPCs) — nothing re-adds
        ///     it. The vanilla <c>CraftingStation</c> survives (an engine type, so the sweep
        ///     ignores it) and, being itself <c>Interactable</c>, takes over E-interaction:
        ///     pressing E then opens the native, tab-less crafting menu (with only the
        ///     work-order Order button), which looks like the registry "lost its tabs".
        ///
        ///     <para>We <see cref="Object.DestroyImmediate(Object)" /> any existing
        ///     RegistryInteract / CraftingStation before rebuilding, rather than reusing
        ///     <see cref="ConfigureInteraction" />'s <c>?? AddComponent</c> path: a component
        ///     killed by deferred <c>Object.Destroy</c> is still a live managed reference until
        ///     end-of-frame, and <c>??</c> does NOT honour Unity's lifetime-aware null — so it
        ///     would latch onto the dying stale component and skip the re-add.</para>
        /// </summary>
        public static void ReapplyInteractionToInstance(GameObject instance)
        {
            if (instance == null) return;

            foreach (var ri in instance.GetComponents<RegistryInteract>())
                Object.DestroyImmediate(ri);
            foreach (var cs in instance.GetComponents<CraftingStation>())
                Object.DestroyImmediate(cs);

            ConfigureInteraction(instance);
        }

        private static int ComponentIndex(GameObject prefab, Component target)
        {
            var comps = prefab.GetComponents<Component>();
            for (var i = 0; i < comps.Length; i++)
                if (ReferenceEquals(comps[i], target))
                    return i;
            return -1;
        }

        private static void GraftProps(GameObject prefab, ZNetScene zNetScene)
        {
            // Messy stack of papers across the desktop. Based on a matte cloth material
            // (the jute carpet) so they read as flat cream paper, not glossy gold like the
            // wood. Slight y stagger avoids z-fighting.
            var curtain = zNetScene.GetPrefab(CurtainSource);
            // Base paper on the work-order scroll's material: that shader renders a flat sheet
            // as matte paper (the scroll's icon shows crisply), unlike the wood/cloth shaders
            // which reflected blue sky on the flat quads.
            var paperMat = BuildPaperMaterial(GetItemChildMaterial("vv_workorder_workbench", "log (1)"));
            var sheet = BuildSheetMesh(0.28f, 0.38f);
            var papers = new[]
            {
                (pos: new Vector3(-0.30f, TopY + 0.005f, 0.00f), yaw: 18f, scale: 1.00f),
                (pos: new Vector3(-0.14f, TopY + 0.010f, -0.18f), yaw: -28f, scale: 0.92f),
                (pos: new Vector3(-0.40f, TopY + 0.014f, 0.16f), yaw: 42f, scale: 0.85f),
                (pos: new Vector3(0.06f, TopY + 0.008f, 0.16f), yaw: -8f, scale: 1.00f),
                (pos: new Vector3(0.20f, TopY + 0.016f, -0.04f), yaw: 62f, scale: 0.80f),
            };
            for (var i = 0; i < papers.Length; i++)
                AddPaper(prefab, sheet, paperMat, $"vv_paper_{i}", papers[i].pos, papers[i].yaw, papers[i].scale);

            // Two candles side by side, each with a warm point light. The second is taller.
            var candle = zNetScene.GetPrefab(CandleSource);
            GraftMesh(prefab, candle, "full/stack", "vv_candle",
                new Vector3(0.84f, TopY, 0.34f), Vector3.zero, Vector3.one);
            AddCandleLight(prefab, new Vector3(0.84f, TopY + 0.28f, 0.34f));
            GraftMesh(prefab, candle, "full/stack", "vv_candle2",
                new Vector3(0.99f, TopY, 0.20f), new Vector3(0f, 0f, 14f), new Vector3(1f, 1.6f, 1f));
            AddCandleLight(prefab, new Vector3(0.99f, TopY + 0.65f, 0.20f));
            
            // Celestial feather quill laid flat across the papers.
            var feather = zNetScene.GetPrefab(FeatherSource);
            GraftMesh(prefab, feather, "attach/default", "vv_feather",
                new Vector3(0.4f, TopY + 0.03f, -0.02f), new Vector3(0f, -40f, 0f), Vector3.one * 0.62f);

            // Draping blue curtain along the far edge of the desk (local +Z long side), spanning
            // the width. Graft the whole "new" subtree (rod + cloth panels) preserving its internal
            // layout so the cloth keeps its authored drape; rotate to run along the long edge and
            // face outward. The source prefab root downscales its meshes ~0.25; GraftMeshGroup only
            // copies immediate local scale, so bake that in (here ~0.33 = 0.25 * 1.3 for +30%).
            var curtainNew = curtain != null ? curtain.transform.Find("new")?.gameObject : null;
            GraftMeshGroup(prefab, curtainNew, "vv_curtain",
                new Vector3(-1.18f, 2.05f, 0.61f), new Vector3(0f, -90f, 0f), 0.28f);

            // Dvergr shelf as a letter tray / document organiser on the desktop — sized so its
            // top footprint reads like a sheet of paper (wider than deep).
            var shelf = zNetScene.GetPrefab(ShelfSource);
            GraftMesh(prefab, shelf, "new/shelf_high", "vv_shelf",
                new Vector3(-0.90f, TopY, 0.34f), Vector3.zero, new Vector3(0.34f, 0.32f, 0.80f));

            // Stool on the ground in front of the desk (local -Z front side).
            var stool = zNetScene.GetPrefab(StoolSource);
            GraftMesh(prefab, stool, "new/high", "vv_stool",
                new Vector3(0f, 0f, -0.73f), Vector3.zero, Vector3.one);
        }

        /// <summary>Return the sharedMaterial of a renderer at a child path on a source prefab.</summary>
        private static Material GetChildMaterial(GameObject source, string childPath)
        {
            var t = source != null ? source.transform.Find(childPath) : null;
            var mr = t != null ? t.GetComponent<MeshRenderer>() : null;
            return mr != null ? mr.sharedMaterial : null;
        }

        /// <summary>Return the sharedMaterial of a renderer at a child path on an ObjectDB item prefab.</summary>
        private static Material GetItemChildMaterial(string itemName, string childPath)
        {
            return GetChildMaterial(ObjectDB.instance?.GetItemPrefab(itemName), childPath);
        }

        /// <summary>A flat cream paper material. Uses an UNLIT shader so the thin flat sheets
        /// show their cream albedo directly instead of mirroring the blue sky — Valheim's lit
        /// shaders derive smoothness from albedo alpha, making the flat quads reflective.</summary>
        private static Material BuildPaperMaterial(Material baseMat)
        {
            var cream = new Color(0.88f, 0.82f, 0.67f);
            var unlit = Shader.Find("Unlit/Texture") ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default");
            var m = unlit != null
                ? new Material(unlit)
                : baseMat != null ? new Material(baseMat) : new Material(Shader.Find("Standard"));
            m.name = "vv_paper_mat";
            m.mainTexture = BuildSolidTexture(cream);
            if (m.HasProperty("_Color")) m.color = cream;
            return m;
        }

        private static void AddPaper(GameObject parent, Mesh sheet, Material mat, string name,
            Vector3 localPos, float yaw, float scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = sheet;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        /// <summary>A 2x2 solid-color albedo texture, so a material's surface colour is unambiguous.</summary>
        private static Texture2D BuildSolidTexture(Color c)
        {
            var tex = new Texture2D(2, 2) { name = "vv_solid_tex" };
            tex.SetPixels(new[] { c, c, c, c });
            tex.Apply();
            return tex;
        }

        /// <summary>Flat, double-sided sheet in the local XZ plane (lies flat on the desk), UV 0..1.</summary>
        private static Mesh BuildSheetMesh(float width, float height)
        {
            var x = width / 2f;
            var z = height / 2f;
            return new Mesh
            {
                name = "vv_paper_sheet",
                vertices = new[]
                {
                    new Vector3(-x, 0, -z), new Vector3(x, 0, -z), new Vector3(x, 0, z), new Vector3(-x, 0, z),
                    new Vector3(-x, 0, -z), new Vector3(x, 0, -z), new Vector3(x, 0, z), new Vector3(-x, 0, z),
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                },
                normals = new[]
                {
                    Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                    Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 },
            };
        }

        /// <summary>Copy a single mesh node from a source prefab onto the registry as a visual-only child.</summary>
        private static void GraftMesh(GameObject parent, GameObject source, string childPath, string name,
            Vector3 localPos, Vector3 localEuler, Vector3 scale)
        {
            if (source == null)
            {
                Plugin.Log?.LogWarning($"[PieceFactory] Graft '{name}' skipped: source prefab missing");
                return;
            }

            var srcT = source.transform.Find(childPath);
            if (srcT == null)
            {
                Plugin.Log?.LogWarning($"[PieceFactory] Graft '{name}' skipped: child '{childPath}' not found on '{source.name}'");
                return;
            }

            var srcMf = srcT.GetComponent<MeshFilter>();
            var srcMr = srcT.GetComponent<MeshRenderer>();
            if (srcMf == null || srcMr == null)
            {
                Plugin.Log?.LogWarning($"[PieceFactory] Graft '{name}' skipped: no MeshFilter/MeshRenderer at '{childPath}'");
                return;
            }

            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = srcMf.sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = srcMr.sharedMaterials;
        }

        /// <summary>Copy every mesh node from a source prefab into one scaled container (preserves arrangement).</summary>
        private static void GraftMeshGroup(GameObject parent, GameObject source, string name,
            Vector3 localPos, Vector3 localEuler, float scale)
        {
            if (source == null)
            {
                Plugin.Log?.LogWarning($"[PieceFactory] Graft group '{name}' skipped: source prefab missing");
                return;
            }

            var container = new GameObject(name);
            container.transform.SetParent(parent.transform, false);
            container.transform.localPosition = localPos;
            container.transform.localRotation = Quaternion.Euler(localEuler);
            container.transform.localScale = Vector3.one * scale;

            var copied = 0;
            foreach (var srcMf in source.GetComponentsInChildren<MeshFilter>(true))
            {
                var srcMr = srcMf.GetComponent<MeshRenderer>();
                if (srcMr == null) continue;

                var go = new GameObject(srcMf.name);
                go.transform.SetParent(container.transform, false);
                go.transform.localPosition = srcMf.transform.localPosition;
                go.transform.localRotation = srcMf.transform.localRotation;
                go.transform.localScale = srcMf.transform.localScale;
                go.AddComponent<MeshFilter>().sharedMesh = srcMf.sharedMesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = srcMr.sharedMaterials;
                copied++;
            }

            if (copied == 0)
                Plugin.Log?.LogWarning($"[PieceFactory] Graft group '{name}': no meshes copied from '{source.name}'");
        }

        private static void AddCandleLight(GameObject parent, Vector3 localPos)
        {
            var go = new GameObject("vv_candle_light");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.72f, 0.36f);
            light.range = 3.5f;
            light.intensity = 1.3f;
            light.shadows = LightShadows.None;
        }

        private static void AddToHammerTable(GameObject piece)
        {
            var hammer = ObjectDB.instance?.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>();
            var table = hammer?.m_itemData?.m_shared?.m_buildPieces;
            if (table == null)
            {
                Plugin.Log?.LogWarning("[PieceFactory] Hammer build PieceTable not available; piece not added to build menu");
                return;
            }

            if (!table.m_pieces.Contains(piece))
                table.m_pieces.Add(piece);
        }

        /// <summary>Remove previously grafted prop children (named "vv_*") before re-grafting on hot reload.</summary>
        private static void StripGraftedChildren(GameObject prefab)
        {
            var toRemove = new List<Transform>();
            foreach (Transform child in prefab.transform)
                if (child.name.StartsWith("vv_"))
                    toRemove.Add(child);
            foreach (var t in toRemove)
                Object.DestroyImmediate(t.gameObject);
        }

        private static GameObject ClonePrefab(GameObject basePrefab, string newName)
        {
            var wasActive = basePrefab.activeSelf;
            basePrefab.SetActive(false);

            var prefab = Object.Instantiate(basePrefab);

            basePrefab.SetActive(wasActive);
            prefab.name = newName;
            // Park the template under the shared inactive root so it never renders/awakes at
            // world origin; activeSelf stays true so ZNetScene/placement clones come out active.
            prefab.transform.SetParent(PrefabTemplates.Root, false);
            prefab.SetActive(true);

            return prefab;
        }

        private static Dictionary<int, GameObject> GetPrivateDictionary<T>(T instance, string fieldName)
        {
            var field = typeof(T).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(instance) as Dictionary<int, GameObject>;
        }
    }
}
