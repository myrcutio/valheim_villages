using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     The cot's bedroll: a subdivided grid with a gentle sag and creasing baked into its
    ///     vertices, so it reads as a used sheet rather than a painted-on rectangle.
    ///
    ///     <para><b>Unity <c>Cloth</c> was tried and abandoned here.</b> It is the right tool for
    ///     the hanging map-fragment scrap, which drapes from a pinned top row, but a bedroll
    ///     lies flat and must stay flat: <c>Cloth</c> only collides with capsule and sphere
    ///     colliders it is handed, so the sheet has nothing to rest ON and gravity pulls it
    ///     through the frame. Pinning the interior with <c>maxDistance = 0</c>, binding an
    ///     identity bone for a defined rest pose, and re-applying the coefficients at runtime
    ///     (they do not survive activation from an inactive prefab) each failed to hold it — the
    ///     sheet collapsed to a line along one rail every time.</para>
    ///
    ///     <para>A further trap worth recording: the banner's own material is on
    ///     <c>Custom/Vegetation</c>, Valheim's WIND shader. It displaces vertices so hanging
    ///     cloth flutters, which on a bed looks like a sheet caught in a breeze and fights any
    ///     simulation underneath it. The sheet wears the banner's TEXTURE on an opaque piece
    ///     shader instead.</para>
    /// </summary>
    internal static class VillagerBedCloth
    {
        // Sheet geometry. Length runs along Z to match the chassis.
        private const int Cols = 9;   // across the bed (X)
        private const int Rows = 15;  // along the bed (Z)
        /// <summary>
        ///     Shapes the loft curve. Below 1 broadens the top and tightens the fall-off at the
        ///     edges, which is what makes it read as a stuffed pillow rather than a tent.
        /// </summary>
        private const float LoftFalloff = 0.55f;

        /// <summary>
        ///     Fraction of the way to the edge at which a draping layer starts folding down.
        ///     Below this the hide lies flat on the mattress.
        /// </summary>
        private const float DrapeStart = 0.78f;

        // Creasing was removed deliberately. It was sin(r*1.7) * cos(c*2.3) — two incommensurate
        // oscillations multiplied, which is a DIAGONAL interference pattern. At only +/-1cm it
        // still drove RecalculateNormals into diagonal shading bands across the blanket, and
        // diagonal banding on a rectangle reads as the rectangle being skewed. The geometry was
        // square the whole time (measured: all four corners pair exactly on both axes); the
        // skew was shading. If creasing is wanted again, it must be symmetric about both centre
        // lines, not a product of two different frequencies.


        /// <summary>
        ///     Build the bedroll onto <paramref name="prefab" />, taking its look from
        ///     <paramref name="donorMaterial" /> (the yellow banner's material).
        /// </summary>
        public static void Apply(GameObject prefab, string childName, Material material,
            Vector3 localPosition, float width, float length, float loft, Vector2 uvInset,
            Vector2 uvOffset, float drape = 0f, float yaw = 0f)
        {
            if (prefab == null) return;

            // Re-apply safe (hot reload): drop any prior layer of this name before rebuilding.
            var prior = prefab.transform.Find(childName);
            if (prior != null) Object.DestroyImmediate(prior.gameObject);

            var go = new GameObject(childName);
            go.transform.SetParent(prefab.transform, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            go.AddComponent<MeshFilter>().sharedMesh = BuildGrid(childName, width, length, loft, uvInset, uvOffset, drape);
            var render = go.AddComponent<MeshRenderer>();
            if (material != null) render.sharedMaterial = material;
            render.enabled = true;
        }

        /// <summary>Flat subdivided grid in the local XZ plane, centred on the origin.</summary>
        private static Mesh BuildGrid(string name, float width, float length, float loft, Vector2 uvInset,
            Vector2 uvOffset, float drape)
        {
            var vc = Cols * Rows;
            var verts = new Vector3[vc];
            var uv = new Vector2[vc];

            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var i = r * Cols + c;
                var fx = (c / (float)(Cols - 1) - 0.5f) * width;
                var fz = (r / (float)(Rows - 1) - 0.5f) * length;
                // Bulge UPWARD, not down: a bedroll is stuffed, so it stands proud in the
                // middle and tucks away at the edges. Raised to a power below 1 so the top is
                // broad and the fall-off is near the rim — a flat dome reads as a pillow, a
                // linear one reads as a tent. Deterministic because a placed cot must look the
                // same to every player and after every reload.
                var edgeX = Mathf.Abs(c / (float)(Cols - 1) - 0.5f) * 2f;
                var edgeZ = Mathf.Abs(r / (float)(Rows - 1) - 0.5f) * 2f;
                var inset = Mathf.Clamp01((1f - edgeX * edgeX) * (1f - edgeZ * edgeZ));
                var fy = loft * Mathf.Pow(inset, LoftFalloff);

                // Drape: past DrapeStart the hide has run out of mattress to lie on, so it folds
                // down over the rail instead of continuing flat. Squared so the fold starts
                // gently and steepens, which is how a hide actually breaks over an edge rather
                // than snapping to a bevel.
                if (drape > 0f)
                {
                    var over = Mathf.Max(
                        (edgeX - DrapeStart) / (1f - DrapeStart),
                        (edgeZ - DrapeStart) / (1f - DrapeStart));
                    if (over > 0f) fy -= drape * over * over;
                }

                verts[i] = new Vector3(fx, fy, fz);
                // uvInset pulls the sampled region in from the texture's edges. Hide art is a
                // silhouette with transparent margin around it, so a layer that must reach the
                // bed frame has to sample only the solid middle — otherwise its own corners
                // come out see-through however large the mesh is.
                // Inset per axis, not uniformly: the donor rugs are not square (rug_wolf's quad
                // is 2.55 x 3.46 and carries its own non-uniform scale), so one shared inset
                // frames the pelt off-centre and the layer reads as skewed.
                // uvOffset slides the sampled window. A symmetric inset assumes the hide sits
                // centred in its texture; when it does not, the visible pelt lands off to one
                // side — which reads as the layer being skewed and poking past one rail while
                // the other side looks right, even though the MESH is perfectly symmetric.
                uv[i] = new Vector2(
                    uvInset.x + uvOffset.x + c / (float)(Cols - 1) * (1f - uvInset.x * 2f),
                    uvInset.y + uvOffset.y + r / (float)(Rows - 1) * (1f - uvInset.y * 2f));

            }

            var tris = new List<int>();
            for (var r = 0; r < Rows - 1; r++)
            for (var c = 0; c < Cols - 1; c++)
            {
                var i = r * Cols + c;
                // i+1 steps along +X and i+Cols along +Z, and X cross Z is -Y — so THIS is the
                // winding whose normals face up. The other order renders a sheet that is
                // invisible from above and perfectly fine from underneath.
                tris.Add(i);
                tris.Add(i + Cols);
                tris.Add(i + 1);
                tris.Add(i + 1);
                tris.Add(i + Cols);
                tris.Add(i + Cols + 1);
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.uv = uv;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();


            mesh.RecalculateBounds();
            return mesh;
        }
    }

}
