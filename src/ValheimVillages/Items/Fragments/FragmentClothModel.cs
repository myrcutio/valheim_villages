using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Replaces a fragment's world model (cloned from DragonEgg) with a small cloth scrap
    ///     simulated by Unity's <see cref="Cloth" />: a subdivided grid on a SkinnedMeshRenderer,
    ///     pinned along its top row with a graded drape below, gravity-driven, so it crumples and
    ///     flutters like a cape when the item is dropped. EXPERIMENTAL — cloth on a dropped item
    ///     can't be verified without running the game; the constants below are the tuning knobs.
    /// </summary>
    internal static class FragmentClothModel
    {
        // Scrap geometry / drape tuning.
        private const int Cols = 5;
        private const int Rows = 7;
        private const float Width = 0.26f;
        private const float Height = 0.36f;
        private const float MaxDrape = 0.5f; // how far free vertices may stray from rest (cloth flutter range)

        private const string ChildName = "vv_fragment_cloth";

        public static void Apply(GameObject prefab, Texture2D tex)
        {
            if (prefab == null) return;

            // Re-apply safe (hot reload): drop any prior scrap before rebuilding.
            var prior = prefab.transform.Find(ChildName);
            if (prior != null) UnityEngine.Object.DestroyImmediate(prior.gameObject);

            // Clone a base item material for a known-good Valheim shader, then swap the albedo to
            // the parchment texture and make it an alpha-cut, two-sided sheet (best effort on cull).
            var donor = prefab.GetComponentInChildren<MeshRenderer>(true);
            var mat = donor != null && donor.sharedMaterial != null
                ? new Material(donor.sharedMaterial)
                : new Material(Shader.Find("Standard"));
            mat.name = "vv_fragment_mat";
            mat.mainTexture = tex;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty("_Color")) mat.color = Color.white;
            if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
            mat.DisableKeyword("_NORMALMAP");
            mat.EnableKeyword("_ALPHATEST_ON");
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.4f);
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", 0); // two-sided if the shader honours it
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            // Hide the base egg visuals + glow; the scrap stands in for them.
            foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = false;
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.enabled = false;
            foreach (var lt in prefab.GetComponentsInChildren<Light>(true)) lt.enabled = false;

            var mesh = BuildGrid();

            var go = new GameObject(ChildName);
            go.transform.SetParent(prefab.transform, false);
            go.transform.localPosition = new Vector3(0f, 0.05f, 0f);

            var render = go.AddComponent<SkinnedMeshRenderer>();
            render.sharedMesh = mesh;
            render.sharedMaterial = mat;
            render.updateWhenOffscreen = true;
            render.localBounds = new Bounds(Vector3.zero, new Vector3(Width * 2f, Height * 2f, Height));

            var cloth = go.AddComponent<Cloth>();
            // Build coefficients ourselves (rather than reading cloth.coefficients, which isn't
            // populated on the inactive template): the grid has no coincident vertices so Cloth
            // won't weld them, making coefficient index == mesh vertex index (row-major).
            var coeffs = new ClothSkinningCoefficient[mesh.vertexCount];
            for (var i = 0; i < coeffs.Length; i++)
            {
                var row = i / Cols; // 0 = pinned top, increasing downward = freer
                coeffs[i].maxDistance = row == 0 ? 0f : row / (float)(Rows - 1) * MaxDrape;
                coeffs[i].collisionSphereDistance = float.MaxValue;
            }

            cloth.coefficients = coeffs;
            cloth.useGravity = true;
            cloth.damping = 0.25f;
            cloth.stretchingStiffness = 0.6f;
            cloth.bendingStiffness = 0.4f;
            cloth.worldVelocityScale = 0.5f;
            cloth.worldAccelerationScale = 1.0f;
            cloth.friction = 0.5f;
        }

        // Flat subdivided grid in the local XY plane, top row at y=0 hanging down to -Height.
        private static Mesh BuildGrid()
        {
            var vc = Cols * Rows;
            var verts = new Vector3[vc];
            var uv = new Vector2[vc];
            var normals = new Vector3[vc];

            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var i = r * Cols + c;
                var fx = (c / (float)(Cols - 1) - 0.5f) * Width;
                var fy = -(r / (float)(Rows - 1)) * Height;
                verts[i] = new Vector3(fx, fy, 0f);
                uv[i] = new Vector2(c / (float)(Cols - 1), 1f - r / (float)(Rows - 1));
                normals[i] = Vector3.forward;
            }

            var tris = new List<int>();
            for (var r = 0; r < Rows - 1; r++)
            for (var c = 0; c < Cols - 1; c++)
            {
                var i = r * Cols + c;
                tris.Add(i);
                tris.Add(i + Cols);
                tris.Add(i + 1);
                tris.Add(i + 1);
                tris.Add(i + Cols);
                tris.Add(i + Cols + 1);
            }

            var mesh = new Mesh { name = "vv_fragment_scrap" };
            mesh.vertices = verts;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
