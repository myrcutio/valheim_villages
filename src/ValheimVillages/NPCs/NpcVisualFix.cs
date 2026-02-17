using System.Reflection;
using UnityEngine;

namespace ValheimVillages.NPCs
{
    /// <summary>
    /// Corrects weapon rotation on NPC models whose hand bone orientation
    /// differs from the player skeleton (e.g. Dverger). Applies a configurable
    /// euler offset to the right-hand weapon instance each time it changes.
    /// </summary>
    public class NpcVisualFix : MonoBehaviour
    {
        private static readonly FieldInfo RightItemField =
            typeof(VisEquipment).GetField("m_rightItemInstance",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private VisEquipment m_visEquipment;
        private Quaternion m_rotationOffset;
        private GameObject m_lastFixedInstance;

        public void Initialize(Vector3 eulerOffset)
        {
            m_rotationOffset = Quaternion.Euler(eulerOffset);
            m_visEquipment = GetComponent<VisEquipment>();

            if (m_visEquipment == null || RightItemField == null)
            {
                Plugin.Log?.LogWarning("[NpcVisualFix] Missing VisEquipment or reflection failed");
                Destroy(this);
            }
        }

        private void LateUpdate()
        {
            if (m_visEquipment == null) return;

            var instance = RightItemField.GetValue(m_visEquipment) as GameObject;
            if (instance == null || instance == m_lastFixedInstance) return;

            instance.transform.localRotation *= m_rotationOffset;
            m_lastFixedInstance = instance;
        }
    }
}
