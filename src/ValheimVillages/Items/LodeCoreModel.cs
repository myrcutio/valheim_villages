using UnityEngine;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Retints the Lode Core's world model. The item is cloned from the Surtling core
    ///     (a small glowing core, so the "brightly shining marble" silhouette comes for
    ///     free), but the Surtling core glows orange — we shift it to a vivid bright blue so
    ///     players don't confuse a Lode Core with a real Surtling core at a glance. Mutates
    ///     the cloned template in place; all ItemDrop plumbing is preserved.
    /// </summary>
    internal static class LodeCoreModel
    {
        // Vivid, cold blue — deliberately distinct from the ORANGE native surtling cores that
        // share these crypts, so the Lode Core reads as "ours" at a glance.
        private static readonly Color Glow = new(0.10f, 0.35f, 1f);
        private const float EmissionBoost = 8f; // HDR — bright enough that Valheim's bloom throws off rays
        private const float LightIntensity = 6f;
        private const float LightRange = 4f;

        public static void Apply(GameObject prefab)
        {
            if (prefab == null) return;

            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                // Clone the base material to keep the Surtling core's known-good shader, then
                // override the glow entirely: kill its ORANGE emission texture so only our blue
                // drives the emission, and push that emission HDR-bright for a strong bloom.
                var mat = new Material(r.sharedMaterial) { name = "vv_lodecore_mat" };
                if (mat.HasProperty("_Color")) mat.color = Glow;
                if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", null);
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Glow * EmissionBoost);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                r.sharedMaterials = new[] { mat };
            }

            // Make the core's own light a bright blue beacon (the cast light + bloom "rays").
            foreach (var light in prefab.GetComponentsInChildren<Light>(true))
            {
                light.color = Glow;
                light.intensity = LightIntensity;
                light.range = LightRange;
            }
        }
    }
}
