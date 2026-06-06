using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     A durable village, backed by a free-standing persistent ZDO (the
    ///     <c>vv_village</c> carrier). Identified by a stable GUID minted when a
    ///     registry station is placed; the registry piece and every villager carry the
    ///     same <see cref="IdKey" /> as a back-reference. The village OWNS its HNA
    ///     region graph: the ZDO stores the serialized blob, this wrapper holds the one
    ///     live <see cref="RegionGraph" /> for the id. Graph persistence is therefore
    ///     1-to-1 with the village and independent of any in-world GameObject.
    ///
    ///     <para>Always obtain a <see cref="Village" /> through <see cref="VillageRegistry" />
    ///     so the live graph is cached per id — never <c>new Village(zdo)</c> elsewhere.</para>
    /// </summary>
    public sealed class Village
    {
        public const string IdKey = "vv_village_id"; // string GUID — durable village identity + back-ref key
        public const string AnchorKey = "vv_village_anchor"; // Vector3 — registry placement position
        public const string GraphKey = "vv_village_graph"; // string — v4 serialized RegionGraph blob

        private readonly ZDO m_zdo;
        private RegionGraph m_graph;
        private bool m_hydrated;

        public Village(ZDO zdo)
        {
            m_zdo = zdo;
        }

        public ZDO Zdo => m_zdo;
        public bool IsValid => m_zdo != null && !string.IsNullOrEmpty(VillageId);

        public string VillageId => m_zdo.GetString(IdKey);

        public Vector3 Anchor
        {
            get => m_zdo.GetVec3(AnchorKey, Vector3.zero);
            set
            {
                m_zdo.Set(AnchorKey, value);
                m_zdo.Persistent = true;
            }
        }

        /// <summary>
        ///     The live region graph for this village, or null if none is built yet.
        ///     Lazily hydrates from the ZDO blob on first access; an empty/absent graph
        ///     stays null (callers must treat that as "no graph", never fabricate one).
        /// </summary>
        public RegionGraph Graph
        {
            get
            {
                if (!m_hydrated) HydrateGraphFromZdo();
                return m_graph;
            }
        }

        public bool HasGraph => Graph != null && Graph.IsAvailable;

        /// <summary>
        ///     Return the live graph, creating an empty one if none exists yet. Used by
        ///     the partition handler before it populates regions. An empty graph is
        ///     <see cref="RegionGraph.IsAvailable" /> == false, so it is never handed
        ///     out by <see cref="VillageRegistry.GetVillageAt" /> as covering a point.
        /// </summary>
        public RegionGraph GetOrCreateGraph()
        {
            if (Graph == null)
            {
                m_graph = new RegionGraph { RegisteredVillageKey = VillageId };
                m_hydrated = true;
            }

            return m_graph;
        }

        /// <summary>Deserialize the stored graph blob into the live graph (idempotent).</summary>
        public void HydrateGraphFromZdo()
        {
            m_hydrated = true;
            var data = m_zdo.GetString(GraphKey);
            if (string.IsNullOrEmpty(data)) return;

            var graph = new RegionGraph { RegisteredVillageKey = VillageId };
            if (RegionGraphPersistence.Restore(graph, data))
            {
                m_graph = graph;
                return;
            }

            // Blob present but unparseable (legacy/corrupt). Clear it so we don't
            // re-parse it every load; the next partition rebuilds + re-saves.
            Plugin.Log?.LogInfo(
                $"[Village] Wiping unparseable graph blob for village {VillageId} " +
                $"(bytes={data.Length}); will rebuild on next partition");
            m_zdo.Set(GraphKey, "");
            m_zdo.Persistent = true;
        }

        /// <summary>Serialize the live graph onto the ZDO. No-op if the graph is empty.</summary>
        public void SaveGraph()
        {
            if (m_graph == null || !m_graph.IsAvailable) return;
            m_zdo.Set(GraphKey, RegionGraphPersistence.Serialize(m_graph));
            m_zdo.Persistent = true;
        }
    }
}
