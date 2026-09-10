using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Renders the Village Registry's build-menu icon from the actual prefab, so the button
    ///     shows the scribe's desk — papers, candles, quill, shelf — instead of the bare dining
    ///     table it was cloned from.
    ///
    ///     <para>The registry inherits <c>piece_table</c>'s <c>m_icon</c> along with its mesh, and
    ///     nothing about grafting props onto the prefab updates that sprite. Compositing 2D marks
    ///     onto the table icon (the trick <see cref="WorkOrderIconCompositor" /> uses) would only
    ///     approximate it; photographing the prefab keeps the icon honest, and it re-renders on
    ///     every hot reload so prop tweaks show up without any second asset to maintain.</para>
    ///
    ///     <para>The model handed to the camera is a MESH-ONLY rebuild, not an instance of the
    ///     prefab: a real instance would run <c>ZNetView.Awake</c> and try to bind a ZDO the moment
    ///     it activated. Copying MeshFilter/MeshRenderer pairs into plain GameObjects sidesteps
    ///     every MonoBehaviour, the same approach <c>PieceFactory.GraftMesh</c> already takes.</para>
    /// </summary>
    public static class RegistryIconRenderer
    {
        /// <summary>Rendered icon resolution. Larger than the 64px item icons — build-menu
        /// buttons draw bigger than inventory slots, and the desk has fine detail.</summary>
        private const int IconSize = 128;

        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
        private static readonly int Color1 = Shader.PropertyToID("_Color");
        private static readonly int Glossiness = Shader.PropertyToID("_Glossiness");

        /// <summary>
        ///     Staged far above the world so no scenery can wander into frame. The camera only
        ///     renders the icon layer anyway; this is belt-and-braces against a stray skybox or
        ///     terrain chunk at the origin.
        /// </summary>
        private static readonly Vector3 StagePosition = new(0f, 12000f, 0f);

        /// <summary>
        ///     Top-level branches of the prefab left out of the shot.
        ///
        ///     <para>Worn / Broken / Destruction are <c>WearNTear</c>'s alternate states — hidden
        ///     in-world, and drawing them would overlay three tables at once. floor_2x2_snow is a
        ///     ground decal. vv_curtain is the 2m backdrop: it more than doubles the vertical
        ///     extent, which would shrink the desk (and the papers and candles that are the point
        ///     of the icon) to about a third of the frame.</para>
        /// </summary>
        private static readonly HashSet<string> ExcludedBranches = new()
        {
            "Worn",
            "Broken",
            "Destruction",
            "floor_2x2_snow",
            "vv_curtain",
        };

        /// <summary>
        ///     Where to stand the model while it is photographed. Beside the player when there is
        ///     one: Valheim's piece shaders read world position for wetness/snow coverage, so a
        ///     model parked in the stratosphere does not shade the way the same piece does in the
        ///     world. Layer isolation, not distance, is what keeps the scenery out of frame.
        /// </summary>
        private static Vector3 StagePos()
        {
            var player = Player.m_localPlayer;
            return player != null
                ? player.transform.position + Vector3.up * 2f
                : StagePosition;
        }

        /// <summary>
        ///     Photograph <paramref name="prefab" /> and return the sprite, or null when no free
        ///     layer is available to render on (logged — the piece then keeps its inherited icon).
        /// </summary>
        public static Sprite Render(
            GameObject prefab, int size = IconSize, string onlyNode = null,
            bool substituteMaterial = false)
        {
            if (prefab == null) return null;

            var layer = FindFreeLayer();
            if (layer < 0)
            {
                Plugin.Log?.LogError(
                    "[RegistryIcon] No unused layer to render on; keeping the inherited icon.");
                return null;
            }

            var model = BuildMeshOnlyModel(prefab, layer, onlyNode, substituteMaterial);
            if (model == null)
            {
                Plugin.Log?.LogError("[RegistryIcon] Prefab has no renderable meshes.");
                return null;
            }

            var rig = new GameObject("vv_icon_rig");
            rig.transform.position = StagePos();
            model.transform.SetParent(rig.transform, false);

            try
            {
                var camera = BuildCamera(rig, layer, model);
                AddLights(rig, layer);
                return Capture(camera, size);
            }
            finally
            {
                Object.DestroyImmediate(rig);
            }
        }

        /// <summary>
        ///     Copy every wanted MeshFilter/MeshRenderer pair into bare GameObjects, laid out in
        ///     the prefab's own local space. No MonoBehaviours, no colliders, nothing that wakes.
        /// </summary>
        private static GameObject BuildMeshOnlyModel(
            GameObject prefab, int layer, string onlyNode, bool substituteMaterial)
        {
            var root = new GameObject("vv_icon_model") { layer = layer };
            var copied = 0;
            var litShader = FindLitShader(prefab);

            // (true) — the prefab template lives under an inactive parent, so its renderers are
            // inactive-in-hierarchy and an active-only search would find nothing.
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (onlyNode != null && mf.name != onlyNode) continue;
                if (onlyNode == null && IsExcluded(mf.transform, prefab.transform)) continue;

                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;

                var go = new GameObject(mf.name) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = prefab.transform.InverseTransformPoint(mf.transform.position);
                go.transform.localRotation =
                    Quaternion.Inverse(prefab.transform.rotation) * mf.transform.rotation;
                go.transform.localScale = mf.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var copy = go.AddComponent<MeshRenderer>();
                // Diagnostic: a flat material separates "this mesh will not draw" from "this
                // material will not draw in our rig".
                copy.sharedMaterials = substituteMaterial
                    ? new[] { DebugMaterial() }
                    : RigSafeMaterials(mr.sharedMaterials, litShader);
                copied++;

            }

            if (copied != 0) return root;

            Object.DestroyImmediate(root);
            return null;
        }

        /// <summary>
        ///     Valheim's own <c>Custom/Piece</c> materials draw nothing in an offscreen rig —
        ///     measured: with a substitute material the table mesh renders in full, with
        ///     <c>Table_Mat</c> it is entirely absent, while every prop on Standard,
        ///     Sprites/Default or Custom/Creature draws normally. Matching the game camera's
        ///     rendering path does not change it, and the shader depends on game-wide state this
        ///     rig has no way to reproduce.
        ///
        ///     <para>So for the icon shot only, re-shade those materials onto a shader that does
        ///     work, carrying the albedo across so the wood still reads as wood. The in-world
        ///     piece is untouched — this substitution exists solely inside the photograph.</para>
        /// </summary>
        private static Material[] RigSafeMaterials(Material[] source, Shader litShader)
        {
            if (source == null || litShader == null) return source;

            Material[] result = null;
            for (var i = 0; i < source.Length; i++)
            {
                var mat = source[i];
                if (mat == null || mat.shader == null || mat.shader.name != "Custom/Piece") continue;

                result ??= (Material[])source.Clone();

                var swapped = new Material(litShader) { name = $"vv_icon_{mat.name}" };
                if (mat.HasProperty(MainTex) && swapped.HasProperty(MainTex))
                    swapped.SetTexture(MainTex, mat.GetTexture(MainTex));
                if (mat.HasProperty(BumpMap) && swapped.HasProperty(BumpMap))
                    swapped.SetTexture(BumpMap, mat.GetTexture(BumpMap));
                if (mat.HasProperty(Color1) && swapped.HasProperty(Color1))
                    swapped.SetColor(Color1, mat.GetColor(Color1));
                if (swapped.HasProperty(Glossiness))
                    swapped.SetFloat(Glossiness, 0.15f); // furniture, not polished stone

                result[i] = swapped;
            }

            return result ?? source;
        }

        /// <summary>
        ///     A shader that demonstrably renders in this rig, harvested from the prefab's own
        ///     props. <c>Shader.Find("Standard")</c> returns null in Valheim — the shader is only
        ///     reachable through a material that already references it.
        /// </summary>
        private static Shader FindLitShader(GameObject prefab)
        {
            foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null || mr.sharedMaterials == null) continue;
                foreach (var mat in mr.sharedMaterials)
                    if (mat?.shader != null && mat.shader.name == "Standard")
                        return mat.shader;
            }

            Plugin.Log?.LogWarning(
                "[RegistryIcon] No Standard-shader material on the prefab to re-shade with; " +
                "Custom/Piece meshes will be missing from the icon.");
            return null;
        }

        /// <summary>
        ///     Flat diagnostic material. "Standard" is not present in Valheim's shipped shader
        ///     set, so it is built from one the game demonstrably has loaded.
        /// </summary>
        private static Material DebugMaterial()
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                Plugin.Log?.LogError("[RegistryIcon] No usable diagnostic shader found");
                return null;
            }

            return new Material(shader) { name = "vv_icon_debug_mat", color = Color.magenta };
        }

        /// <summary>
        ///     True when this node sits under an excluded top-level branch, or is a LOD "low"
        ///     mesh (the prefab carries high/low pairs; the icon always wants high).
        /// </summary>
        private static bool IsExcluded(Transform node, Transform prefabRoot)
        {
            if (node.name == "low") return true;

            for (var t = node; t != null && t != prefabRoot; t = t.parent)
            {
                if (ExcludedBranches.Contains(t.name)) return true;
                if (t.name == "low") return true;
            }

            return false;
        }

        /// <summary>
        ///     Orthographic three-quarter view, framed tight: the model's bounding box corners are
        ///     projected into camera space and the half-extents taken from that, so the desk fills
        ///     the icon instead of floating inside the slack a bounding-sphere fit would leave.
        /// </summary>
        private static Camera BuildCamera(GameObject rig, int layer, GameObject model)
        {
            var bounds = new Bounds(model.transform.position, Vector3.zero);
            var first = true;
            foreach (var mr in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (first)
                {
                    bounds = mr.bounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(mr.bounds);
                }
            }

            var go = new GameObject("vv_icon_camera");
            go.transform.SetParent(rig.transform, false);

            var camera = go.AddComponent<Camera>();
            camera.enabled = false; // rendered explicitly, never by the main loop
            camera.orthographic = true;
            camera.cullingMask = 1 << layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.nearClipPlane = 0.01f;
            camera.aspect = 1f; // square target; half-width then equals orthographicSize

            // Match the game camera's rendering path. Valheim's own piece shaders ship only the
            // passes that path needs, so a camera left on the default renders every Custom/Piece
            // material as nothing at all — the table vanished while the props (Standard,
            // Sprites/Default, Custom/Creature) drew normally.
            var main = Camera.main;
            camera.renderingPath = main != null
                ? main.actualRenderingPath
                : RenderingPath.DeferredShading;


            // Slightly above and to the side, the angle Valheim's own piece icons read at.
            //
            // The yaw MUST keep the camera on the desk's front (local -Z) side — the side the
            // stool faces and the curtain hangs opposite. Viewing from +Z instead mirrors the
            // desktop: candles (local +X) land on the left and the shelf (local -X) on the right,
            // the reverse of what the player sees walking up to the placed piece.
            var direction = Quaternion.Euler(28f, 45f, 0f) * Vector3.forward;
            var radius = bounds.extents.magnitude;
            go.transform.position = bounds.center - direction * (radius * 3f);
            go.transform.rotation = Quaternion.LookRotation(direction);
            camera.farClipPlane = radius * 6f;

            var halfWidth = 0f;
            var halfHeight = 0f;
            foreach (var corner in Corners(bounds))
            {
                var local = go.transform.InverseTransformPoint(corner);
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(local.x));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(local.y));
            }

            // Square icon, so the larger half-extent decides. 1.06 keeps a hair of margin so the
            // silhouette doesn't touch the button edge.
            camera.orthographicSize = Mathf.Max(halfWidth, halfHeight) * 1.02f;

            Plugin.Log?.LogInfo(
                $"[RegistryIcon] {model.transform.childCount} mesh(es), bounds={bounds.size}, " +
                $"orthoSize={camera.orthographicSize:F2}, path={camera.renderingPath}");
            return camera;
        }

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            var c = b.center;
            var e = b.extents;
            for (var i = 0; i < 8; i++)
                yield return c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
        }

        /// <summary>
        ///     Key light plus a dim fill from the opposite side. Both are masked to the icon layer
        ///     so they cannot spill onto the real world, and the fill keeps the shadowed face from
        ///     reading as a black cut-out at icon size.
        /// </summary>
        private static void AddLights(GameObject rig, int layer)
        {
            var key = new GameObject("vv_icon_key") { layer = layer };
            key.transform.SetParent(rig.transform, false);
            key.transform.rotation = Quaternion.Euler(38f, 20f, 0f);
            var keyLight = key.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.cullingMask = 1 << layer;
            keyLight.intensity = 1.25f;
            keyLight.color = new Color(1f, 0.96f, 0.88f);
            keyLight.shadows = LightShadows.None;

            var fill = new GameObject("vv_icon_fill") { layer = layer };
            fill.transform.SetParent(rig.transform, false);
            fill.transform.rotation = Quaternion.Euler(20f, 210f, 0f);
            var fillLight = fill.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.cullingMask = 1 << layer;
            fillLight.intensity = 0.55f;
            fillLight.color = new Color(0.78f, 0.84f, 1f);
            fillLight.shadows = LightShadows.None;
        }

        private static Sprite Capture(Camera camera, int size)
        {
            var rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;

            camera.targetTexture = rt;
            camera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "vv_registry_icon_tex",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();

            RenderTexture.active = previous;
            camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);

            var sprite = Sprite.Create(
                tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            sprite.name = "vv_registry_icon";
            return sprite;
        }

        /// <summary>
        ///     Highest unnamed layer. An unnamed layer is one nothing in the game or another mod
        ///     has claimed, which is what makes it safe to render on in isolation.
        /// </summary>
        private static int FindFreeLayer()
        {
            for (var i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    return i;
            return -1;
        }
    }
}
