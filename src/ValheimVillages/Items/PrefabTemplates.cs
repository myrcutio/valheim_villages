using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     A persistent, inactive parent that holds every runtime-built prefab template
    ///     (registry station, custom items, …) so a template never renders or runs Awake
    ///     at world origin.
    ///     <para>
    ///         Templates must keep <see cref="GameObject.activeSelf" /> = true so that
    ///         <c>ZNetScene</c>/placement clones — created with
    ///         <c>Object.Instantiate(template, pos, rot)</c> at the scene root — come out
    ///         active and run <c>ZNetView.Awake</c> synchronously to bind their ZDO.
    ///         Parenting the template under this <b>inactive</b> root makes it
    ///         inactive-<i>in-hierarchy</i> (so it doesn't draw the desk/item mesh at
    ///         spawn) without changing the clones, whose parent is the scene root, not this.
    ///     </para>
    /// </summary>
    internal static class PrefabTemplates
    {
        private static GameObject s_root;

        /// <summary>The shared inactive, DontDestroyOnLoad template parent (created on first use).</summary>
        public static Transform Root
        {
            get
            {
                if (s_root == null)
                {
                    // vv_ prefix so the standard hot-reload cleanup can reclaim it.
                    s_root = new GameObject("vv_prefab_templates");
                    s_root.SetActive(false);
                    Object.DontDestroyOnLoad(s_root);
                }

                return s_root.transform;
            }
        }
    }
}
