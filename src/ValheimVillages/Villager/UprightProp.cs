using UnityEngine;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Keeps a carried prop hanging vertically however the hand holding it is posed.
    ///
    ///     <para>A lantern's attach model is authored for a player's grip, so the same local
    ///     rotation reads upright on one body and flat on its side on another — the Dverger and
    ///     DvergerMage rest their <c>l.hand</c> bone differently. Correcting that with a
    ///     per-villager angle would need a new number for every body and every animation; a real
    ///     hanging lantern just stays level, so this holds the mesh level and lets it swing with
    ///     the arm instead.</para>
    /// </summary>
    internal class UprightProp : MonoBehaviour
    {
        private Transform m_mesh;
        private Quaternion m_meshToSelf;

        /// <summary>
        ///     Bind the prop's mesh. The mesh's rotation RELATIVE to this object is captured once,
        ///     so the correction can target the mesh while only ever rotating this object.
        /// </summary>
        public void Bind(Transform mesh)
        {
            m_mesh = mesh;
            m_meshToSelf = Quaternion.Inverse(transform.rotation) * mesh.rotation;
        }

        /// <summary>
        ///     Runs after the animator has posed the bones, or the arm would overwrite this every
        ///     frame.
        /// </summary>
        private void LateUpdate()
        {
            if (m_mesh == null) return;

            // Level, keeping whichever way the prop is already facing. The lantern model is
            // authored +Y up, so a rotation with no pitch or roll is exactly "hanging down".
            var upright = Quaternion.Euler(0f, m_mesh.eulerAngles.y, 0f);
            transform.rotation = upright * Quaternion.Inverse(m_meshToSelf);
        }
    }
}
