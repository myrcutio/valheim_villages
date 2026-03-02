using UnityEngine;
using ValheimVillages.Enums;

namespace ValheimVillages.Schemas
{
    /// <summary>
    /// Current environmental context for behavior decisions.
    /// </summary>
    public struct BehaviorContext
    {
        public bool IsRaining;
        public TimeOfDay TimeOfDay;
        public bool InShelter;
        public float CurrentComfort;
        public Vector3 CurrentPosition;
    }
}
