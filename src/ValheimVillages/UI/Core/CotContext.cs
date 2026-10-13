using UnityEngine;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     The subject the Villager Cot tab renders for: one placed cot, and the village whose
    ///     roster can be assigned to it.
    ///
    ///     <para>Holds the cot GameObject rather than a position, because assigning writes to
    ///     that cot's ZDO. The village is resolved at interact time from where the cot stands —
    ///     a cot carries no village stamp of its own, unlike the registry.</para>
    /// </summary>
    public sealed class CotContext
    {
        public CotContext(GameObject cot, string villageId)
        {
            Cot = cot;
            VillageId = villageId;
        }

        /// <summary>The placed cot that was interacted with.</summary>
        public GameObject Cot { get; }

        /// <summary>Durable id of the village the cot stands in, or null if it stands in none.</summary>
        public string VillageId { get; }
    }
}
