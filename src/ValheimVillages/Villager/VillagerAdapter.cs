using UnityEngine;
using ValheimVillages.Interfaces;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager
{
    public class VillagerAdapter : IVillager
    {
        public VillagerAdapter(Villager villagerInstance)
        {
            VillagerName = villagerInstance.villagerName;
            VillagerType = villagerInstance.villagerType;
            UniqueID = villagerInstance.uid;
            BedLocation = villagerInstance.BedPosition;
            CurrentWaypoint = villagerInstance.villagerAI?.GetCurrentWaypoint();
        }

        public Vector3 BedLocation { get; }
        public VillagerWaypoint CurrentWaypoint { get; }
        public string VillagerName { get; }
        public string VillagerType { get; }
        public string UniqueID { get; }

        public Vector3 BedPosition => BedLocation;
    }
}