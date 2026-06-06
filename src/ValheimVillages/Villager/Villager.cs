using UnityEngine;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.Records;

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

            // Load identity AND home anchor from the authoritative record (the NPC carries
            // only a vv_record_id back-reference) before adding VillagerAI so it registers
            // with the correct UniqueId and home. The record is minted on spawn / migrated
            // on restore before this component is added, so it must exist here.
            if (m_zdo != null)
                LoadIdentityFromRecord();

            if (villagerAI == null)
                villagerAI = gameObject.AddComponent<VillagerAI>();

            if (m_zdo != null && villagerAI != null)
                villagerAI.LoadMemories(m_zdo);
        }


        /// <summary>
        ///     Populate identity from the villager's record via its vv_record_id
        ///     back-reference. Logs loudly if no record is found — that means the
        ///     mint-on-spawn / migrate-on-restore invariant was violated upstream.
        /// </summary>
        private void LoadIdentityFromRecord()
        {
            var recordId = m_zdo.GetString("vv_record_id");
            var record = VillagerRecordTable.FindById(recordId);
            if (record == null)
            {
                Plugin.Log?.LogError(
                    $"[Villager] No record found for vv_record_id '{recordId}' on '{villagerName}' — " +
                    "identity unresolved (mint/migrate invariant violated)");
                return;
            }

            uid = record.RecordId;
            villagerType = record.Type;
            villagerName = record.Name;
            // Home anchor comes from the record too. The freshly-instantiated NPC ZDO's
            // vv_bed_position is unreliable at spawn time (reads back zero), whereas the
            // record's field is authoritative and sticks. Keeps the BedPosition setter
            // mirroring vv_bed_position for nav/legacy readers.
            m_bedPosition = record.BedPosition;
            Plugin.Log?.LogDebug($"[Villager] '{villagerName}' home from record={record.BedPosition}");
        }

        private void LoadFromZDO()
        {
            if (m_zdo == null) return;
            LoadIdentityFromRecord();
            if (villagerAI != null)
                villagerAI.LoadMemories(m_zdo);
        }

        private void SaveToZDO()
        {
            villagerAI.SaveMemories(m_zdo);
        }
    }
}