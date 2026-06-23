using System.Collections.Generic;
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
        public const string AnchorKey = "vv_village_anchor"; // Vector3 — legacy registry placement position
        public const string GraphKey = "vv_village_graph"; // string — v4 serialized RegionGraph blob
        public const string AnchorsKey = "vv_village_anchors"; // string — serialized named-anchor list
        public const string StationsKey = "vv_village_stations"; // string — serialized station metadata (reserved for WS3)
        public const string WorkOrdersKey = "vv_village_workorders"; // string — serialized work-order config list (Fix C)
        public const string InvalidKey = "vv_village_invalid"; // int 0/1 — village failed triad validation (recruit blocked)
        public const string NeedsWallKey = "vv_village_needs_wall"; // int 0/1 — outside flood reaches the registry (no perimeter wall)

        private readonly ZDO m_zdo;
        private RegionGraph m_graph;
        private bool m_hydrated;

        private readonly List<VillageAnchor> m_anchors = new();
        private bool m_anchorsHydrated;

        private readonly List<WorkOrderEntry> m_workOrders = new();
        private bool m_workOrdersHydrated;
        private uint m_workOrdersRevision;

        public Village(ZDO zdo)
        {
            m_zdo = zdo;
        }

        public ZDO Zdo => m_zdo;
        public bool IsValid => m_zdo != null && !string.IsNullOrEmpty(VillageId);

        /// <summary>
        ///     True only on the peer that OWNS this village's carrier ZDO — the host
        ///     (see <c>VillageOwnership</c>). Authoritative graph/anchor writes are gated on
        ///     this: a write via <c>ZDO.Set</c> bumps the carrier's DataRevision and, on a
        ///     client, is pushed to the host where a DataRevision-newer blob would clobber the
        ///     host's authoritative graph. The ownership patch keeps the host the sole owner,
        ///     so clients read/hydrate the blob locally but never persist over it. Reads are
        ///     unaffected; the partition that produces the graph is also enqueued host-only.
        /// </summary>
        private bool CanPersist => m_zdo != null && m_zdo.IsOwner();

        public string VillageId => m_zdo.GetString(IdKey);

        public Vector3 Anchor
        {
            get => m_zdo.GetVec3(AnchorKey, Vector3.zero);
            set
            {
                if (!CanPersist) return;
                m_zdo.Set(AnchorKey, value);
                m_zdo.Persistent = true;
            }
        }

        /// <summary>
        ///     Whether this village failed anchor-triad validation (fewer than 3 mutually
        ///     connected, founder-reachable anchors near the registry). A loud, persistent
        ///     fail flag — downstream spawn/recruit/seed callers refuse an invalid village
        ///     rather than silently land on the disconnected registry island. Persisted as
        ///     ZDO int 0/1; set/cleared only by <see cref="VillageRegistry.EnsureAnchorTriad" />.
        /// </summary>
        public bool IsInvalid
        {
            get => m_zdo.GetInt(InvalidKey, 0) != 0;
            set
            {
                if (!CanPersist) return;
                m_zdo.Set(InvalidKey, value ? 1 : 0);
                m_zdo.Persistent = true;
            }
        }

        /// <summary>
        ///     True when the pre-bake outside flood reaches the registry cell — i.e. the
        ///     village is NOT sealed by a perimeter wall. Computed each partition from the
        ///     bake's raw outside-cell set (<see cref="RegionPartitionHandler" />) and read at
        ///     registry-interact time to refuse opening the panel until the player walls the
        ///     village in (mirrors a workbench needing a roof). Persisted as ZDO int 0/1;
        ///     host-only writer, all peers read. A rebake fires on every piece change, so this
        ///     clears on its own once the perimeter is closed.
        /// </summary>
        public bool NeedsPerimeterWall
        {
            get => m_zdo.GetInt(NeedsWallKey, 0) != 0;
            set
            {
                if (!CanPersist) return;
                m_zdo.Set(NeedsWallKey, value ? 1 : 0);
                m_zdo.Persistent = true;
            }
        }

        /// <summary>
        ///     The currently-set triad anchor positions (those of triad0/1/2 that resolve
        ///     via <see cref="TryGetAnchor" />), 0..3 entries in triad-slot order. The
        ///     connectivity source for villager spawn snap, patrol seed, and partition seeds.
        /// </summary>
        public IReadOnlyList<Vector3> TriadAnchors
        {
            get
            {
                var list = new List<Vector3>(VillageAnchor.Triad.Length);
                foreach (var name in VillageAnchor.Triad)
                    if (TryGetAnchor(name, out var pos))
                        list.Add(pos);
                return list;
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
            // re-parse it every load; the next partition rebuilds + re-saves. Only the
            // owning host persists — a client just leaves its local graph null and waits
            // for the host's rebuilt blob to replicate.
            if (!CanPersist) return;
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
            if (!CanPersist) return; // only the owning host persists the authoritative graph blob
            m_zdo.Set(GraphKey, RegionGraphPersistence.Serialize(m_graph));
            m_zdo.Persistent = true;
        }

        /// <summary>
        ///     The village's named anchors (registry, founder, …), lazily hydrated from
        ///     the ZDO blob on first access — same pattern as <see cref="Graph" />. Durable
        ///     truth; never fabricated.
        /// </summary>
        public IReadOnlyList<VillageAnchor> Anchors
        {
            get
            {
                if (!m_anchorsHydrated) HydrateAnchorsFromZdo();
                return m_anchors;
            }
        }

        /// <summary>Positions of every named anchor.</summary>
        public IEnumerable<Vector3> AnchorPositions
        {
            get
            {
                foreach (var a in Anchors) yield return a.Position;
            }
        }

        public bool TryGetAnchor(string name, out Vector3 pos)
        {
            foreach (var a in Anchors)
                if (a.Name == name)
                {
                    pos = a.Position;
                    return true;
                }

            pos = Vector3.zero;
            return false;
        }

        /// <summary>Upsert an anchor by name and persist the whole list to the ZDO.</summary>
        public void SetAnchor(string name, Vector3 pos)
        {
            if (!CanPersist) return; // host-authoritative; clients receive anchors via replication
            if (!m_anchorsHydrated) HydrateAnchorsFromZdo();

            var entry = new VillageAnchor(name, pos);
            var found = false;
            for (var i = 0; i < m_anchors.Count; i++)
                if (m_anchors[i].Name == name)
                {
                    m_anchors[i] = entry;
                    found = true;
                    break;
                }

            if (!found) m_anchors.Add(entry);

            m_zdo.Set(AnchorsKey, VillageAnchorPersistence.Serialize(m_anchors));
            m_zdo.Persistent = true;
        }

        /// <summary>
        ///     Deserialize the stored anchor blob into the live list (idempotent).
        ///     MIGRATION: legacy villages have no anchor blob but do carry a non-zero
        ///     <see cref="Anchor" /> (the old single registry position); backfill a
        ///     <see cref="VillageAnchor.Registry" /> anchor from it and persist on first
        ///     hydrate. Legacy villages have no founder anchor — that is acceptable.
        /// </summary>
        public void HydrateAnchorsFromZdo()
        {
            m_anchorsHydrated = true;
            m_anchors.Clear();

            var data = m_zdo.GetString(AnchorsKey);
            if (VillageAnchorPersistence.Restore(data, m_anchors)) return;

            var legacy = Anchor;
            if (legacy == Vector3.zero) return;

            m_anchors.Add(new VillageAnchor(VillageAnchor.Registry, legacy));
            // Keep the migrated anchor in memory on every peer (so reads resolve it), but
            // only the owning host persists it back — clients receive it via replication.
            if (!CanPersist) return;
            m_zdo.Set(AnchorsKey, VillageAnchorPersistence.Serialize(m_anchors));
            m_zdo.Persistent = true;
            Plugin.Log?.LogInfo(
                $"[Village] Migrated legacy anchor -> '{VillageAnchor.Registry}' for village {VillageId}");
        }

        /// <summary>
        ///     The village's work-order config records (Fix C), lazily hydrated from the ZDO
        ///     blob on first access — same pattern as <see cref="Anchors" />. This is the
        ///     authoritative quota source; the chest token is now only a UI handle. Reads are
        ///     owner-agnostic; mutations are host-only.
        /// </summary>
        public IReadOnlyList<WorkOrderEntry> WorkOrders
        {
            get
            {
                EnsureWorkOrdersFresh();
                return m_workOrders;
            }
        }

        // Re-hydrate when the carrier's DataRevision has advanced since our last read. The
        // work-order blob changes whenever the host applies an edit (the RPC), so a one-shot
        // cache on a CLIENT (and on the host right after its own write) goes stale — the editor
        // would then read the pre-edit value and re-send it on close, reverting the player's edit.
        private void EnsureWorkOrdersFresh()
        {
            if (m_workOrdersHydrated && (m_zdo == null || m_zdo.DataRevision == m_workOrdersRevision))
                return;
            HydrateWorkOrdersFromZdo();
        }

        private void HydrateWorkOrdersFromZdo()
        {
            m_workOrdersHydrated = true;
            m_workOrdersRevision = m_zdo != null ? m_zdo.DataRevision : 0u;
            m_workOrders.Clear();
            WorkOrderPersistence.Restore(m_zdo.GetString(WorkOrdersKey), m_workOrders);
        }

        public bool TryGetWorkOrder(string station, string item, out WorkOrderEntry entry)
        {
            foreach (var o in WorkOrders)
                if (o.Station == station && o.Item == item)
                {
                    entry = o;
                    return true;
                }

            entry = default;
            return false;
        }

        /// <summary>Host-only: upsert a work order by (station, item) and persist the list.</summary>
        public void UpsertWorkOrder(WorkOrderEntry entry)
        {
            if (!CanPersist) return; // host-authoritative; clients route edits through the RPC
            EnsureWorkOrdersFresh();

            var found = false;
            for (var i = 0; i < m_workOrders.Count; i++)
                if (m_workOrders[i].Station == entry.Station && m_workOrders[i].Item == entry.Item)
                {
                    m_workOrders[i] = entry;
                    found = true;
                    break;
                }

            if (!found) m_workOrders.Add(entry);

            m_zdo.Set(WorkOrdersKey, WorkOrderPersistence.Serialize(m_workOrders));
            m_zdo.Persistent = true;
        }

        /// <summary>Host-only: remove a work order by (station, item); returns true if one was removed.</summary>
        public bool RemoveWorkOrder(string station, string item)
        {
            if (!CanPersist) return false;
            EnsureWorkOrdersFresh();

            var removed = m_workOrders.RemoveAll(o => o.Station == station && o.Item == item) > 0;
            if (removed)
            {
                m_zdo.Set(WorkOrdersKey, WorkOrderPersistence.Serialize(m_workOrders));
                m_zdo.Persistent = true;
            }

            return removed;
        }

        /// <summary>Generic ZDO string read (for WS3 station blob, etc.).</summary>
        public string GetBlob(string key) => m_zdo.GetString(key);

        /// <summary>Generic ZDO string write; marks the ZDO persistent. Host-authoritative.</summary>
        public void SetBlob(string key, string value)
        {
            if (!CanPersist) return;
            m_zdo.Set(key, value);
            m_zdo.Persistent = true;
        }
    }
}
