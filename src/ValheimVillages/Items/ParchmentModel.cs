using UnityEngine;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Reskins an item prefab's world model into a flat, double-sided parchment
    ///     sheet textured with the work order icon. Mutates the base prefab's mesh and
    ///     material in place so all the ItemDrop plumbing (collider, rigidbody,
    ///     ZNetView) is preserved — only the visible geometry changes.
    /// </summary>
    internal static class ParchmentModel
    {
        public static void Apply(GameObject prefab, Texture2D tex)
        {
            if (prefab == null || tex == null) return;

            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
            {
                Plugin.Log?.LogWarning($"ParchmentModel: no MeshRenderer on '{prefab.name}'");
                return;
            }

            var sheet = BuildSheet();

            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = sheet;

                // Clone the base material so we inherit a known-good Valheim shader
                // (correct lighting/fog), then swap the albedo to the parchment and
                // drop the base normal map so it reads as flat paper, not wood grain.
                var mat = new Material(r.sharedMaterial) { name = "vv_parchment_mat" };
                mat.mainTexture = tex;
                if (mat.HasProperty("_Color")) mat.color = Color.white;
                if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
                mat.DisableKeyword("_NORMALMAP");

                // The icon PNG has a transparent background; render the scroll
                // silhouette via alpha cutout instead of an opaque black square.
                mat.EnableKeyword("_ALPHATEST_ON");
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                r.sharedMaterials = new[] { mat };
            }

            // Neutralise any glow light carried by the base prefab (e.g. DragonEgg).
            foreach (var light in prefab.GetComponentsInChildren<Light>(true))
                light.enabled = false;
        }

        /// <summary>
        ///     A flat, double-sided quad in the local XZ plane, UV-mapped 0..1. Two
        ///     vertex sets with opposite winding so the sheet is visible from both
        ///     faces without relying on the shader disabling backface culling.
        /// </summary>
        private static Mesh BuildSheet(float width = 0.3f, float height = 0.4f)
        {
            var x = width / 2f;
            var z = height / 2f;
            var up = Vector3.up;
            var down = Vector3.down;

            var mesh = new Mesh
            {
                name = "vv_parchment",
                vertices = new[]
                {
                    new Vector3(-x, 0, -z), new Vector3(x, 0, -z),
                    new Vector3(x, 0, z), new Vector3(-x, 0, z),   // front face
                    new Vector3(-x, 0, -z), new Vector3(x, 0, -z),
                    new Vector3(x, 0, z), new Vector3(-x, 0, z),   // back face
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                },
                normals = new[] { up, up, up, up, down, down, down, down },
                triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 },
            };

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
