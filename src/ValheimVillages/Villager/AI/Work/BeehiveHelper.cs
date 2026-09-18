using UnityEngine;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Beekeeping support for the Farmer's Honey work order.
    ///
    ///     <para>A beehive is not a crafting station: it has no inputs, no fuel and no
    ///     conversion queue. It slowly accrues a honey "level" on its own, and harvesting is a
    ///     single extract that dumps that many Honey item-drops on the ground beside it. So the
    ///     Honey order is modelled as a zero-ingredient recipe whose physical station is
    ///     <see cref="PhysicalStation" />, and the workflow is: walk to a hive that has honey →
    ///     extract → pick the drops up off the ground → carry them to a chest. The pickup half
    ///     is the same ground-collection the smelter/kiln flow already uses, because those
    ///     stations spit their output out the same way.</para>
    ///
    ///     <para><see cref="Beehive" /> keeps its level and its extract entirely private, so
    ///     both are reached the way the engine itself stores them: the level is the ZDO int
    ///     <c>ZDOVars.s_level</c>, and extraction is the hive's own <c>RPC_Extract</c>.</para>
    /// </summary>
    public static class BeehiveHelper
    {
        /// <summary>physicalStation value that routes a recipe to this harvester.</summary>
        public const string PhysicalStation = "beehive";

        /// <summary>The hive's own extract RPC (registered in <c>Beehive.Awake</c>).</summary>
        private const string ExtractRpc = "RPC_Extract";

        /// <summary>How far from a hive the villager may stand to work it.</summary>
        private const float HarvestReach = 3f;

        /// <summary>
        ///     Honey currently sitting in the hive. Reads the ZDO directly because
        ///     <c>Beehive.GetHoneyLevel</c> is private; this is the exact field it reads.
        ///     0 for a hive with no valid ZDO — nothing to harvest either way.
        /// </summary>
        public static int GetHoneyLevel(Beehive hive)
        {
            var zdo = hive != null ? hive.GetComponent<ZNetView>()?.GetZDO() : null;
            return zdo == null ? 0 : zdo.GetInt(ZDOVars.s_level);
        }

        /// <summary>
        ///     Where a hive's honey lands when extracted — the drops spawn at the hive's
        ///     spawn point, which is what the ground sweep must search around.
        /// </summary>
        public static Vector3 OutputPoint(Beehive hive)
        {
            if (hive == null) return Vector3.zero;
            return hive.m_spawnPoint != null ? hive.m_spawnPoint.position : hive.transform.position;
        }

        /// <summary>The item this hive yields when extracted, or null.</summary>
        public static string YieldOf(Beehive hive)
        {
            return hive != null && hive.m_honeyItem != null
                ? hive.m_honeyItem.gameObject.name
                : null;
        }

        /// <summary>
        ///     Nearest hive to <paramref name="center" /> that yields
        ///     <paramref name="expectedOutput" />, actually has something in it, and that the
        ///     villager can reach a standing spot beside. Hives with nothing in them are
        ///     skipped rather than walked to: an empty hive is not work, and offering it would
        ///     have the villager cross the village to look at a box.
        ///
        ///     <para>The yield check matters because <see cref="Beehive" /> is NOT only used by
        ///     beehives — <c>piece_birdnest</c> carries the same component and yields Feathers.
        ///     Without it a Honey order would walk to the nearest nest, extract feathers, then
        ///     sit there waiting for honey drops that were never coming.</para>
        /// </summary>
        public static bool TryFindHarvestable(
            Vector3 center, float radius, string expectedOutput,
            out Beehive hive, out Vector3 approach)
        {
            hive = null;
            approach = Vector3.zero;

            var best = float.MaxValue;
            foreach (var h in PhysicsHelper.GetAllInRadius<Beehive>(center, radius))
            {
                if (h == null) continue;
                if (!string.IsNullOrEmpty(expectedOutput) && YieldOf(h) != expectedOutput) continue;
                if (GetHoneyLevel(h) <= 0) continue;

                var d = (h.transform.position - center).sqrMagnitude;
                if (d >= best) continue;
                if (!PieceApproachResolver.TryResolve(
                        center, h.transform.position, HarvestReach, out var stand)) continue;

                best = d;
                hive = h;
                approach = stand;
            }

            return hive != null;
        }

        /// <summary>
        ///     Ask the hive to dump its honey. Returns false when the hive can't be driven
        ///     (no live ZNetView) so the caller can abandon rather than wait on drops that
        ///     will never appear.
        ///
        ///     <para>The drops do NOT exist when this returns: the RPC is dispatched by the
        ///     network pump, so the caller must poll the ground afterwards.</para>
        /// </summary>
        public static bool Extract(Beehive hive)
        {
            var nview = hive != null ? hive.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) return false;
            nview.InvokeRPC(ExtractRpc);
            return true;
        }
    }
}
