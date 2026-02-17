namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Current environmental context for behavior decisions.
    /// Uses Vec3 instead of UnityEngine.Vector3 for Core compatibility.
    /// </summary>
    public struct BehaviorContext
    {
        public bool IsRaining;
        public TimeOfDay TimeOfDay;
        public bool InShelter;
        public float CurrentComfort;
        public Vec3 CurrentPosition;
    }
}
