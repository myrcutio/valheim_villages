using UnityEngine;

namespace ValheimVillages.NPCs.AI
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
