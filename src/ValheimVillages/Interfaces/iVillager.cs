using UnityEngine;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    /// Interface for composable NPC behaviors. Each behavior has a tag (e.g. "patrol", "craft"),
    /// a priority (higher wins when multiple behaviors want control), and lifecycle hooks
    /// called by VillagerAI's dispatch loop.
    /// 
    /// Save/Load are defined in the mod assembly via IBehaviorPersistence (ZDO-dependent).
    /// </summary>
    public interface IVillager
    {
        string VillagerName { get; }
        string VillagerType { get; }
        string UniqueID { get; }
        /// <summary>Bed position for task attributes (e.g. work order scan).</summary>
        Vector3 BedPosition { get; }
    }
}
