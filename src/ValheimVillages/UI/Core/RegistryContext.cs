using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     The subject the Village Registry tabs (Roster / Add / Revive) render for.
    ///     Identifies one village by the registry station's world position and the
    ///     derived village key (the same 30 m bucket used by the nav graph).
    ///
    ///     <para>This is the single seam where the future Villager Record table
    ///     plugs in: the tabs only ever read from this context, so adding roster
    ///     accessors here (e.g. <c>GetMembers()</c>, <c>GetDead()</c>) is all that's
    ///     needed to swap the current placeholder tab content for real data.</para>
    /// </summary>
    public sealed class RegistryContext
    {
        public RegistryContext(Vector3 registryPosition)
        {
            RegistryPosition = registryPosition;
            VillageKey = RegionGraph.VillageKey(registryPosition);
        }

        /// <summary>World position of the registry station that was interacted with.</summary>
        public Vector3 RegistryPosition { get; }

        /// <summary>Village key (30 m bucket) this registry anchors, per <see cref="RegionGraph" />.</summary>
        public string VillageKey { get; }
    }
}
