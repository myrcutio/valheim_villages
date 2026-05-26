using UnityEngine;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Villager
{
    public class Villager : MonoBehaviour
    {
        public string villagerName = "Villager";
        public string uid;
        public string villagerType;
        public ZNetView nView;
        public VillagerAI villagerAI;

        private Vector3 m_bedPosition;
        private ZDO m_zdo;

        public Vector3 BedPosition
        {
            get => m_bedPosition;
            set
            {
                m_bedPosition = value;
                nView.GetZDO().Set("vv_bed_position", m_bedPosition);
            }
        }

        // TODO: move the prefab and bed tracking to this holder instead
        public void Awake()
        {
            nView = GetComponent<ZNetView>();
            if (nView != null)
                m_zdo = nView.GetZDO();

            // Load identity from ZDO before adding VillagerAI so Awake can register with correct UniqueId
            if (m_zdo != null)
            {
                uid = m_zdo.GetString("vv_villager_id");
                m_bedPosition = m_zdo.GetVec3("vv_bed_position", Vector3.zero);
                villagerType = m_zdo.GetString("vv_villager_type", "villager");
                villagerName = m_zdo.GetString("vv_villager_name", villagerName);
            }

            if (villagerAI == null)
                villagerAI = gameObject.AddComponent<VillagerAI>();

            if (m_zdo != null && villagerAI != null)
                villagerAI.LoadMemories(m_zdo);
        }


        private void LoadFromZDO()
        {
            if (m_zdo == null) return;
            uid = m_zdo.GetString("vv_villager_id");
            m_bedPosition = m_zdo.GetVec3("vv_bed_position", Vector3.zero);
            villagerType = m_zdo.GetString("vv_villager_type", "villager");
            villagerName = m_zdo.GetString("vv_villager_name", villagerName);
            if (villagerAI != null)
                villagerAI.LoadMemories(m_zdo);
        }

        private void SaveToZDO()
        {
            villagerAI.SaveMemories(m_zdo);
        }
    }
}