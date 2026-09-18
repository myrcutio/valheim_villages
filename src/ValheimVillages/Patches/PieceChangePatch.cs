using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Detects structural changes (piece placement/removal/destruction and terrain
    ///     edits) and records WHERE each one happened, so <c>Plugin.Update</c> can rebuild
    ///     only the villages a change could actually have affected. The bake is combined
    ///     terrain+piece, so terrain edits must dirty it too.
    ///     <para>
    ///     This used to be a single <c>bool IsDirty</c>. A repartition costs ~700ms per
    ///     village (measured: PartitionProfile), and every loaded village paid it for every
    ///     change anywhere in the world — including a shack built kilometres from any
    ///     village. Each patch site already has the change position in hand, so the only
    ///     thing the bool bought was throwing that away.
    ///     </para>
    /// </summary>
    [HarmonyPatch]
    public static class PieceChangePatch
    {
        /// <summary>
        ///     Beyond this many pending regions the set collapses to its own bounding box.
        ///     Collapsing is a SUPERSET of the real dirty area, so it can only ever cause an
        ///     extra rebuild, never a missed one — unlike dropping entries, which would.
        /// </summary>
        private const int MaxPendingRegions = 64;

        /// <summary>
        ///     Half-extent used for a changed object that carries no collider. Such an object
        ///     contributes no geometry to the bake, so the only reason to record it at all is
        ///     that its ZDO may still gate village membership; a cell-sized box is the honest
        ///     extent, not a guess at one.
        /// </summary>
        private const float PointHalfExtent = 1f;

        /// <summary>One structural change's XZ footprint. Y is irrelevant: villages scope by XZ.</summary>
        internal readonly struct DirtyRegion
        {
            public readonly float MinX, MinZ, MaxX, MaxZ;

            public DirtyRegion(float minX, float minZ, float maxX, float maxZ)
            {
                MinX = minX;
                MinZ = minZ;
                MaxX = maxX;
                MaxZ = maxZ;
            }

            public bool Intersects(float oMinX, float oMinZ, float oMaxX, float oMaxZ)
            {
                return MinX <= oMaxX && MaxX >= oMinX && MinZ <= oMaxZ && MaxZ >= oMinZ;
            }
        }

        private static readonly List<DirtyRegion> s_pending = new();

        /// <summary>Realtime timestamp of the last structural change.</summary>
        internal static float LastStructureChangeTime { get; private set; }

        /// <summary>True when a structural change has occurred but the graph hasn't been rebuilt yet.</summary>
        internal static bool IsDirty => s_pending.Count > 0;

        /// <summary>The pending change footprints, for matching against village footprints.</summary>
        internal static IReadOnlyList<DirtyRegion> PendingRegions => s_pending;

        /// <summary>
        ///     True if any pending change lands inside the given XZ box grown by
        ///     <paramref name="margin" />. The margin matters because a piece placed just
        ///     OUTSIDE a village's current footprint is exactly the change that should grow
        ///     it — matching the bare footprint would ignore every extension wing.
        /// </summary>
        internal static bool Affects(
            float minX, float minZ, float maxX, float maxZ, float margin)
        {
            for (var i = 0; i < s_pending.Count; i++)
                if (s_pending[i].Intersects(
                        minX - margin, minZ - margin, maxX + margin, maxZ + margin))
                    return true;
            return false;
        }

        /// <summary>
        ///     Union of the pending changes that fall inside the given XZ box grown by
        ///     <paramref name="margin" /> — the rectangle a partition of that village must
        ///     re-probe. Only call when <see cref="Affects" /> returned true; it throws
        ///     otherwise rather than return an empty rect that would read as "nothing
        ///     changed" and skip the very geometry that did.
        /// </summary>
        internal static DirtyRegion UnionAffecting(
            float minX, float minZ, float maxX, float maxZ, float margin)
        {
            var gMinX = minX - margin;
            var gMinZ = minZ - margin;
            var gMaxX = maxX + margin;
            var gMaxZ = maxZ + margin;

            var found = false;
            float uMinX = 0f, uMinZ = 0f, uMaxX = 0f, uMaxZ = 0f;
            for (var i = 0; i < s_pending.Count; i++)
            {
                var r = s_pending[i];
                if (!r.Intersects(gMinX, gMinZ, gMaxX, gMaxZ)) continue;
                if (!found)
                {
                    uMinX = r.MinX; uMinZ = r.MinZ; uMaxX = r.MaxX; uMaxZ = r.MaxZ;
                    found = true;
                    continue;
                }

                if (r.MinX < uMinX) uMinX = r.MinX;
                if (r.MinZ < uMinZ) uMinZ = r.MinZ;
                if (r.MaxX > uMaxX) uMaxX = r.MaxX;
                if (r.MaxZ > uMaxZ) uMaxZ = r.MaxZ;
            }

            if (!found)
                throw new System.InvalidOperationException(
                    $"UnionAffecting found no pending change inside x[{gMinX:F1}..{gMaxX:F1}] " +
                    $"z[{gMinZ:F1}..{gMaxZ:F1}] — caller did not check Affects first.");

            return new DirtyRegion(uMinX, uMinZ, uMaxX, uMaxZ);
        }

        /// <summary>Drop every pending change. Called once the rebuild sweep has consumed them.</summary>
        internal static void ClearPending()
        {
            s_pending.Clear();
        }

        [RegisterCleanup]
        public static void Reset()
        {
            s_pending.Clear();
            LastStructureChangeTime = 0f;
        }

        private static void MarkDirty(string source, float minX, float minZ, float maxX, float maxZ)
        {
            if (s_pending.Count >= MaxPendingRegions)
            {
                // Long build session: fold everything into one box rather than grow without
                // bound. Conservative by construction (see MaxPendingRegions).
                var cMinX = minX;
                var cMinZ = minZ;
                var cMaxX = maxX;
                var cMaxZ = maxZ;
                for (var i = 0; i < s_pending.Count; i++)
                {
                    var r = s_pending[i];
                    if (r.MinX < cMinX) cMinX = r.MinX;
                    if (r.MinZ < cMinZ) cMinZ = r.MinZ;
                    if (r.MaxX > cMaxX) cMaxX = r.MaxX;
                    if (r.MaxZ > cMaxZ) cMaxZ = r.MaxZ;
                }

                s_pending.Clear();
                s_pending.Add(new DirtyRegion(cMinX, cMinZ, cMaxX, cMaxZ));
            }
            else
            {
                s_pending.Add(new DirtyRegion(minX, minZ, maxX, maxZ));
            }

            LastStructureChangeTime = Time.realtimeSinceStartup;
            Plugin.Log?.LogInfo(
                $"[PieceChange] dirty via {source} at x[{minX:F0}..{maxX:F0}] z[{minZ:F0}..{maxZ:F0}]; " +
                $"rebake will enqueue after settle ({s_pending.Count} pending)");
        }

        /// <summary>
        ///     Record a changed GameObject by the XZ extent of its colliders — the geometry
        ///     the bake actually voxelizes. A 8m longhouse wall dirties 8m, not a point.
        /// </summary>
        private static void MarkDirtyObject(string source, GameObject go)
        {
            if (go == null) return;

            var colliders = go.GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.Length == 0)
            {
                var p = go.transform.position;
                MarkDirty(source, p.x - PointHalfExtent, p.z - PointHalfExtent,
                    p.x + PointHalfExtent, p.z + PointHalfExtent);
                return;
            }

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (var i = 0; i < colliders.Length; i++)
            {
                var b = colliders[i].bounds;
                if (b.min.x < minX) minX = b.min.x;
                if (b.min.z < minZ) minZ = b.min.z;
                if (b.max.x > maxX) maxX = b.max.x;
                if (b.max.z > maxZ) maxZ = b.max.z;
            }

            MarkDirty(source, minX, minZ, maxX, maxZ);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Piece), "SetCreator")]
        private static void OnPiecePlaced(Piece __instance)
        {
            MarkDirtyObject("place", __instance != null ? __instance.gameObject : null);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WearNTear), "Remove")]
        private static void OnPieceRemoved(WearNTear __instance)
        {
            MarkDirtyObject("hammer-remove", __instance != null ? __instance.gameObject : null);
            Villages.VillageCleanupRpc.OnRegistryRemoved(__instance);
        }

        // Pieces destroyed by damage/decay go through the private WearNTear.Destroy
        // (→ m_onDestroyed), NOT Remove — so this is needed for boar-smashed walls etc.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        private static void OnPieceDestroyed(WearNTear __instance)
        {
            MarkDirtyObject("destroyed", __instance != null ? __instance.gameObject : null);
            Villages.VillageCleanupRpc.OnRegistryRemoved(__instance);
        }

        // Terrain edits (hoe/pickaxe/cultivator/raise/level) — the combined bake includes
        // terrain, so these must dirty the graph too. ApplyOperation runs once per edit.
        // TerrainOp.GetRadius() is the game's own answer for how far the op reaches.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyOperation))]
        private static void OnTerrainModified(TerrainOp modifier)
        {
            if (modifier == null) return;
            var p = modifier.transform.position;
            var r = modifier.GetRadius();
            MarkDirty("terrain", p.x - r, p.z - r, p.x + r, p.z + r);
        }
    }
}
