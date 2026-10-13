using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Behaviors.Forestry
{
    /// <summary>
    ///     Drops a village's woodlot anchors (<c>forester</c> + the <c>forester_link*</c>
    ///     corridor) once the Forester's Post that laid them is gone.
    ///     <para>
    ///     Nothing used to remove them: the post only ever SETS its anchors, so a deconstructed
    ///     woodlot kept baking navmesh out to an empty field, kept the Lumberjack picking a
    ///     Forestry task there, and — via a position lookup in AddForestryAnchors — leaked into
    ///     a brand-new village's first partition and stretched its footprint 1.7km.
    ///     </para>
    ///     <para>
    ///     Keyed off ZDO DESTRUCTION (<see cref="ZDOMan.m_onZDODestroyed" />), not the post's
    ///     OnDestroy: OnDestroy also fires when the zone simply unloads, which must not cost a
    ///     village its woodlot. Destruction fires on every peer for a hammer removal or any
    ///     other real delete. A one-time reconcile per village per session catches posts that
    ///     were removed before this existed (or while the host could not write).
    ///     </para>
    ///     <para>Host-only: village anchors are host-authoritative.</para>
    /// </summary>
    public static class ForesterAnchorJanitor
    {
        /// <summary>
        ///     How close a post must stand to a <c>forester</c> anchor to count as the one that
        ///     laid it. The anchor is the post's resolved standing seed, a few metres off the
        ///     post itself (<see cref="ForesterPost.SeedClearance" /> plus the resolver's search).
        /// </summary>
        private const float PostMatchRadius = 10f;

        private static ZDOMan s_subscribedTo;

        /// <summary>Villages already reconciled (anchor confirmed live, absent, or cleared).</summary>
        private static readonly HashSet<string> s_reconciled = new();

        /// <summary>Called from the host's periodic village sweep.</summary>
        public static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;

            if (s_subscribedTo != ZDOMan.instance)
            {
                if (s_subscribedTo != null) s_subscribedTo.m_onZDODestroyed -= OnZdoDestroyed;
                ZDOMan.instance.m_onZDODestroyed += OnZdoDestroyed;
                s_subscribedTo = ZDOMan.instance;
                s_reconciled.Clear();
            }

            foreach (var village in new List<Village>(VillageRegistry.EnumerateAll()))
            {
                var id = village.VillageId;
                if (string.IsNullOrEmpty(id) || s_reconciled.Contains(id)) continue;

                if (!village.TryGetAnchor(ForesterPost.AnchorName, out var anchor) ||
                    PostExistsNear(anchor, default))
                {
                    s_reconciled.Add(id);
                    continue;
                }

                // Not marked reconciled on failure: the host may not have claimed the village
                // ZDO yet, so the next sweep tries again.
                if (Clear(village, "no Forester's Post stands at its woodlot"))
                    s_reconciled.Add(id);
            }
        }

        private static void OnZdoDestroyed(ZDO zdo)
        {
            if (zdo == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (zdo.GetPrefab() != ForesterPost.PrefabName.GetStableHashCode()) return;

            var postPos = zdo.GetPosition();
            foreach (var village in new List<Village>(VillageRegistry.EnumerateAll()))
            {
                if (!village.TryGetAnchor(ForesterPost.AnchorName, out var anchor)) continue;
                if (XzDistance(anchor, postPos) > PostMatchRadius) continue;

                // The destroyed ZDO is still registered while this callback runs, so it has to
                // be excluded by id or it would vouch for itself.
                if (PostExistsNear(anchor, zdo.m_uid)) continue;

                if (!Clear(village, "its Forester's Post was removed"))
                    s_reconciled.Remove(village.VillageId); // let Tick retry the write
            }
        }

        /// <summary>
        ///     Remove the woodlot anchors and rebuild the village so its bake pulls back in.
        ///     Returns false when this peer could not write the village record.
        /// </summary>
        private static bool Clear(Village village, string reason)
        {
            // "forester" prefixes both the post anchor and its forester_link* corridor.
            var removed = village.RemoveAnchorsWithPrefix(ForesterPost.AnchorName);
            if (removed == 0) return !village.TryGetAnchor(ForesterPost.AnchorName, out _);

            Plugin.Log?.LogInfo(
                $"[ForesterPost] Cleared {removed} woodlot anchor(s) from village {village.VillageId}: " +
                $"{reason}");
            ForesterPostAnchor.RequestRepartition(village);
            return true;
        }

        private static bool PostExistsNear(Vector3 pos, ZDOID ignore)
        {
            var objectsByID = Traverse.Create(ZDOMan.instance)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null) return false;

            var postHash = ForesterPost.PrefabName.GetStableHashCode();
            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null || zdo.GetPrefab() != postHash || zdo.m_uid == ignore) continue;
                if (XzDistance(zdo.GetPosition(), pos) <= PostMatchRadius) return true;
            }

            return false;
        }

        private static float XzDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        [RegisterCleanup]
        public static void Cleanup()
        {
            if (s_subscribedTo != null) s_subscribedTo.m_onZDODestroyed -= OnZdoDestroyed;
            s_subscribedTo = null;
            s_reconciled.Clear();
        }
    }
}
