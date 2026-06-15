using UnityEngine;

namespace ValheimVillages.Villager.Records
{
    /// <summary>Lifecycle state of a villager, independent of any live NPC GameObject.</summary>
    public enum RecordStatus
    {
        Egg = 0,
        Alive = 1,
        Dead = 2,
    }

    /// <summary>
    ///     Authoritative identity/lifecycle record for one villager, backed by a
    ///     free-standing persistent ZDO (the <c>vv_villager_record</c> carrier). Owns
    ///     the villager's type/name/status/village/anchor/egg-prefab so identity survives
    ///     independently of the NPC GameObject (which carries only a <c>vv_record_id</c>
    ///     back-reference). This is the source of truth the registry roster reads from.
    /// </summary>
    public sealed class VillagerRecord
    {
        public const string IdKey = "vv_record_id"; // string guid; also the villager's UniqueId
        public const string TypeKey = "vv_record_type";
        public const string NameKey = "vv_record_name";
        public const string StatusKey = "vv_record_status"; // int (RecordStatus)
        public const string VillageTag = "vv_record_village"; // owning Village id (vv_village_id)
        public const string HomeKey = "vv_record_home"; // Vector3
        public const string EggPrefabKey = "vv_record_egg_prefab"; // string (empty until egg phase)
        public const string NpcKey = "vv_record_npc"; // ZDOID back-link to the live NPC (when alive)

        private readonly ZDO m_zdo;

        public VillagerRecord(ZDO zdo)
        {
            m_zdo = zdo;
        }

        public ZDO Zdo => m_zdo;
        public bool IsValid => m_zdo != null && !string.IsNullOrEmpty(RecordId);

        public string RecordId => m_zdo.GetString(IdKey);

        public string Type
        {
            get => m_zdo.GetString(TypeKey);
            set => SetString(TypeKey, value);
        }

        public string Name
        {
            get => m_zdo.GetString(NameKey);
            set => SetString(NameKey, value);
        }

        public RecordStatus Status
        {
            get => (RecordStatus)m_zdo.GetInt(StatusKey, (int)RecordStatus.Alive);
            set
            {
                m_zdo.Set(StatusKey, (int)value);
                m_zdo.Persistent = true;
            }
        }

        public string Village
        {
            get => m_zdo.GetString(VillageTag);
            set => SetString(VillageTag, value);
        }

        public Vector3 HomeAnchor
        {
            get => m_zdo.GetVec3(HomeKey, Vector3.zero);
            set
            {
                m_zdo.Set(HomeKey, value);
                m_zdo.Persistent = true;
            }
        }

        public string EggPrefab
        {
            get => m_zdo.GetString(EggPrefabKey);
            set => SetString(EggPrefabKey, value);
        }

        public ZDOID NpcZdoId
        {
            get => m_zdo.GetZDOID(NpcKey);
            set
            {
                m_zdo.Set(NpcKey, value);
                m_zdo.Persistent = true;
            }
        }

        private void SetString(string key, string value)
        {
            m_zdo.Set(key, value ?? "");
            m_zdo.Persistent = true;
        }
    }
}
