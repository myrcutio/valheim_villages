using UnityEngine;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     The subject the Village Registry tabs (Roster / Add / Revive) render for.
    ///     Identifies one village by the registry station's world position and the
    ///     durable village id (<c>vv_village_id</c>) stamped on the registry ZDO.
    ///
    ///     <para>This is the single seam where the Villager Record table plugs in: the
    ///     tabs only ever read from this context, so roster accessors key off
    ///     <see cref="VillageId" />.</para>
    /// </summary>
    public sealed class RegistryContext
    {
        public RegistryContext(Vector3 registryPosition, string villageId)
        {
            RegistryPosition = registryPosition;
            VillageId = villageId;
        }

        /// <summary>World position of the registry station that was interacted with.</summary>
        public Vector3 RegistryPosition { get; }

        /// <summary>Durable id of the village this registry anchors.</summary>
        public string VillageId { get; }
    }
}
