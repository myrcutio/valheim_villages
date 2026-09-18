using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Foraging support: harvesting a <see cref="Pickable" /> that already stands in the
    ///     village — a blueberry or raspberry bush, a mushroom, a thistle — to fill a work order
    ///     for what it yields.
    ///
    ///     <para>A pickable is not a crafting station: it has no inputs, no fuel and no
    ///     conversion queue. It ripens on its own and picking is a single interact that dumps
    ///     item-drops on the ground beside it. So a forage order is modelled exactly like the
    ///     beekeeping one — a zero-ingredient recipe whose physical station is
    ///     <see cref="PhysicalStation" /> — and the workflow is: walk to a ripe pickable that
    ///     yields this order's item → pick → sweep the drops off the ground → carry them to a
    ///     chest.</para>
    ///
    ///     <para>Scope is the VILLAGE FOOTPRINT, not a radius around the asking villager. A
    ///     radius is centred on whoever happens to ask, so the far half of a real settlement
    ///     falls outside it — the same failure that had a Farmer report "missing ingredients"
    ///     for deer meat sitting in a chest 23.5m away against a 20m radius. "Any pickable in
    ///     the village" is the actual rule, so the footprint is what we ask.</para>
    ///
    ///     <para>Enumeration is <c>FindObjectsOfType</c>, NOT an <c>OverlapSphere</c>: collider
    ///     state diverges from the real object set on a headless dedicated server, which is what
    ///     made the host read phantom chests while clients saw the true set. Results are filtered
    ///     to live ZDO-backed objects and deduped by ZDO id, so one networked bush counts once on
    ///     every peer.</para>
    /// </summary>
    public static class ForageHelper
    {
        /// <summary>physicalStation value that routes a recipe to this harvester.</summary>
        public const string PhysicalStation = "pickable";

        /// <summary>How far from a pickable the villager may stand to work it.</summary>
        private const float HarvestReach = 3f;

        /// <summary>The item this pickable yields when picked, or null.</summary>
        public static string YieldOf(Pickable pickable)
        {
            return pickable != null && pickable.m_itemPrefab != null
                ? pickable.m_itemPrefab.name
                : null;
        }

        /// <summary>
        ///     Where a pickable's drops land — the engine offsets them up by
        ///     <c>m_spawnOffset</c> from the plant's own position and scatters them inside a
        ///     small circle, which is what the ground sweep must search around.
        /// </summary>
        public static Vector3 OutputPoint(Pickable pickable)
        {
            if (pickable == null) return Vector3.zero;
            return pickable.transform.position + Vector3.up * pickable.m_spawnOffset;
        }

        /// <summary>
        ///     True when this pickable is RIPE: grown/regrown and not already picked. For a
        ///     berry bush that is its "has fruit" visual being live again after the respawn
        ///     timer; for a grown crop it is the <c>Pickable_X</c> simply existing.
        /// </summary>
        public static bool IsRipe(Pickable pickable)
        {
            return pickable != null && !pickable.GetPicked() && pickable.CanBePicked();
        }

        /// <summary>
        ///     Every ripe pickable inside the village at <paramref name="anchorPos" /> that
        ///     yields <paramref name="expectedOutput" /> (null = any yield). Falls back to
        ///     <paramref name="fallbackRadius" /> around the anchor when no village resolves or
        ///     it has no footprint yet (no partition since world load), so a village that hasn't
        ///     been mapped yet still forages instead of silently finding nothing.
        /// </summary>
        public static List<Pickable> FindRipeInVillage(
            Vector3 anchorPos, float fallbackRadius, string expectedOutput)
        {
            var results = new List<Pickable>();

            var village = VillageRegistry.GetVillageAt(anchorPos)
                          ?? VillageRegistry.FindNearAnchor(anchorPos);
            float minX = 0f, minZ = 0f, maxX = 0f, maxZ = 0f;
            var hasFootprint = village != null
                               && village.TryGetFootprint(out minX, out minZ, out maxX, out maxZ);
            var sqrRadius = fallbackRadius * fallbackRadius;

            var seen = new HashSet<ZDOID>();
            foreach (var pickable in Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
            {
                if (pickable == null) continue;

                // No live ZDO means the pick RPC has nowhere to go — Interact bails on exactly
                // this check, so such an object is not work, it's a mirage.
                var nview = pickable.GetComponent<ZNetView>();
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null) continue;

                var p = pickable.transform.position;
                if (hasFootprint)
                {
                    if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
                }
                else if ((p - anchorPos).sqrMagnitude > sqrRadius)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(expectedOutput) && YieldOf(pickable) != expectedOutput) continue;
                if (!IsRipe(pickable)) continue;
                if (!seen.Add(zdo.m_uid)) continue;

                results.Add(pickable);
            }

            return results;
        }

        /// <summary>
        ///     The ripe pickable nearest <paramref name="fromPos" /> that yields
        ///     <paramref name="expectedOutput" />, or null. Village-scoped via
        ///     <see cref="FindRipeInVillage" />; reachability is NOT considered, so callers that
        ///     need the villager to actually get there want
        ///     <see cref="TryFindHarvestable" /> instead.
        /// </summary>
        public static Pickable FindNearestRipe(
            Vector3 anchorPos, Vector3 fromPos, float fallbackRadius, string expectedOutput)
        {
            Pickable nearest = null;
            var best = float.MaxValue;

            foreach (var pickable in FindRipeInVillage(anchorPos, fallbackRadius, expectedOutput))
            {
                var d = (pickable.transform.position - fromPos).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                nearest = pickable;
            }

            return nearest;
        }

        /// <summary>
        ///     Nearest ripe pickable to <paramref name="anchorPos" /> that yields
        ///     <paramref name="expectedOutput" /> AND that the villager can reach a standing spot
        ///     beside. Unreachable ones are skipped rather than offered: a bush on the far side
        ///     of a wall is not work, and advertising it would have the villager walk at
        ///     geometry it can never touch.
        /// </summary>
        public static bool TryFindHarvestable(
            Vector3 anchorPos, float fallbackRadius, string expectedOutput,
            out Pickable pickable, out Vector3 approach)
        {
            pickable = null;
            approach = Vector3.zero;

            var best = float.MaxValue;
            foreach (var candidate in FindRipeInVillage(anchorPos, fallbackRadius, expectedOutput))
            {
                var d = (candidate.transform.position - anchorPos).sqrMagnitude;
                if (d >= best) continue;
                if (!PieceApproachResolver.TryResolve(
                        anchorPos, candidate.transform.position, HarvestReach, out var stand))
                    continue;

                best = d;
                pickable = candidate;
                approach = stand;
            }

            return pickable != null;
        }

        /// <summary>
        ///     Pick it. Returns false when the pickable went un-pickable between being chosen
        ///     and being reached (someone else got there first, the respawn state flipped) so
        ///     the caller can abandon rather than wait on drops that are never coming.
        ///
        ///     <para>The drops do NOT exist when this returns: <c>Interact</c> dispatches
        ///     <c>RPC_Pick</c> to the object's owner through the network pump, so the caller
        ///     must poll the ground afterwards. Note that <c>Interact</c>'s own return value is
        ///     the engine's "play the interact animation" flag, NOT success — it is false for
        ///     most pickables even when the pick lands, so it must not be used as one.</para>
        /// </summary>
        public static bool Pick(Pickable pickable, Humanoid npc)
        {
            if (pickable == null || npc == null) return false;
            if (!IsRipe(pickable)) return false;

            Plugin.Log?.LogInfo(
                $"[Forage] Picking {YieldOf(pickable) ?? "?"} at {pickable.transform.position}");

            pickable.Interact(npc, false, false);
            return true;
        }
    }
}
