using UnityEngine;

namespace ValheimVillages.Behaviors.Forestry
{
    /// <summary>
    ///     Felling a tree the way the engine does it, rather than reimplementing it.
    ///
    ///     <para><c>TreeBase</c> exposes only <c>Damage(HitData)</c>, which routes to
    ///     <c>RPC_Damage</c> on the ZDO OWNER. Everything that makes a felled tree a felled
    ///     tree — the log spawn, the stub, the drop table — lives inside that private
    ///     handler, so the alternative (zeroing <c>s_health</c> and destroying the view
    ///     ourselves) would mean duplicating engine internals and silently losing the wood.
    ///     We claim ownership first so the handler runs here, on the server, where the
    ///     villager is.</para>
    ///
    ///     <para><b>The hit is aimed.</b> <c>SpawnLog</c> applies its impulse along
    ///     <c>hit.m_dir</c>, so hitting from the villager's side drops the trunk directly
    ///     away from the villager. The caller approaches from the village side, which is
    ///     what keeps a falling trunk off the walls — the planting distance is the backstop,
    ///     not the only defence.</para>
    /// </summary>
    public static class TreeFelling
    {
        /// <summary>
        ///     One blow, always lethal. A villager does not swing an axe repeatedly (there
        ///     is no chop animation on the Dvergr rig), so partial tree damage would only
        ///     ever be a state we have to carry and resume.
        /// </summary>
        private const float ChopDamage = 1_000_000f;

        /// <summary>
        ///     Above every tree's <c>m_minToolTier</c>. <c>CheckToolTier</c> compares this
        ///     against the tree's requirement, which is what stops a flint axe felling an
        ///     oak; the villager's competence is decided by its role, not by simulated gear.
        /// </summary>
        private const short ToolTier = 100;

        /// <summary>
        ///     Nearest live tree to <paramref name="pos" /> within <paramref name="radius" />, or
        ///     null.
        /// </summary>
        /// <param name="skip">
        ///     Optional veto, so a caller can pass over one it cannot currently work and take
        ///     the NEXT tree rather than losing the errand. Without it, "not that one" and
        ///     "none at all" are the same answer — which starved a Lumberjack's whole woodlot,
        ///     60 trees deep, because the single nearest one was unusable.
        /// </param>
        public static TreeBase FindNearest(
            Vector3 pos, float radius, System.Func<Object, bool> skip = null)
        {
            TreeBase best = null;
            var bestSq = float.MaxValue;
            foreach (var tree in PhysicsHelper.GetAllInRadius<TreeBase>(pos, radius))
            {
                if (!IsFellable(tree)) continue;
                if (skip != null && skip(tree)) continue;
                var d = (tree.transform.position - pos).sqrMagnitude;
                if (d >= bestSq) continue;
                bestSq = d;
                best = tree;
            }

            return best;
        }

        /// <summary>
        ///     Nearest fallen log within <paramref name="radius" />, or null.
        ///
        ///     <para>Felling a tree does not yield Wood — it yields a <c>TreeLog</c> plus the
        ///     tree's own drop table (seeds/cones). The Wood is in the log's drop table, and
        ///     a big log breaks into sub-logs that must each be broken in turn. So "chop down
        ///     a tree" is really a small loop: fell, then clear every log it left.</para>
        /// </summary>
        /// <param name="skip">See <see cref="FindNearest" /> — pass over one, take the next.</param>
        public static TreeLog FindNearestLog(
            Vector3 pos, float radius, System.Func<Object, bool> skip = null)
        {
            TreeLog best = null;
            var bestSq = float.MaxValue;
            foreach (var log in PhysicsHelper.GetAllInRadius<TreeLog>(pos, radius))
            {
                if (!IsBreakable(log)) continue;
                if (skip != null && skip(log)) continue;
                var d = (log.transform.position - pos).sqrMagnitude;
                if (d >= bestSq) continue;
                bestSq = d;
                best = log;
            }

            return best;
        }

        /// <summary>
        ///     Speed below which a log counts as come to rest. Not zero — a log settled against
        ///     a slope keeps a little jitter indefinitely, and waiting for a true zero would
        ///     wait forever.
        /// </summary>
        private const float SettledSpeed = 0.2f;

        /// <summary>
        ///     Has this log finished falling?
        ///
        ///     <para>A felled trunk rolls, bounces and slides for a few seconds, and while it
        ///     does it is both a moving collider and a moving target: walking at one gets the
        ///     villager shoved, and the navmesh under it is about to change anyway. Waiting for
        ///     it to come to rest costs a couple of seconds and avoids both.</para>
        /// </summary>
        public static bool IsSettled(TreeLog log)
        {
            if (log == null) return false;
            var body = log.GetComponent<Rigidbody>();
            if (body == null) return true; // nothing to move it — treat as at rest
            if (body.isKinematic || body.IsSleeping()) return true;
            return body.linearVelocity.sqrMagnitude <= SettledSpeed * SettledSpeed
                   && body.angularVelocity.sqrMagnitude <= SettledSpeed * SettledSpeed;
        }

        /// <summary>
        ///     Is anything still falling near here? The woodlot pauses while the answer is yes,
        ///     rather than felling a second tree into the first one's path.
        /// </summary>
        public static bool AnyLogInMotion(Vector3 centre, float radius)
        {
            foreach (var log in PhysicsHelper.GetAllInRadius<TreeLog>(centre, radius))
            {
                if (log == null) continue;
                if (!IsSettled(log)) return true;
            }

            return false;
        }

        /// <summary>A log that exists and is past the frame it spawned on.</summary>
        public static bool IsBreakable(TreeLog log)
        {
            if (log == null) return false;
            var nview = log.GetComponent<ZNetView>();
            // TreeLog.Damage silently no-ops during m_firstFrame, so a log hit on its spawn
            // frame just absorbs the blow. The caller walks to it, which covers the gap.
            return nview != null && nview.IsValid() && nview.GetZDO() != null;
        }

        /// <summary>Break <paramref name="log" /> into its drops (and any sub-logs).</summary>
        public static bool TryBreakLog(TreeLog log, Vector3 fellerPos)
        {
            if (!IsBreakable(log)) return false;

            var nview = log.GetComponent<ZNetView>();
            nview.ClaimOwnership();

            var logPos = log.transform.position;
            var away = logPos - fellerPos;
            away.y = 0f;
            var dir = away.sqrMagnitude > 0.0001f ? away.normalized : Vector3.forward;

            var hit = new HitData
            {
                m_toolTier = ToolTier,
                m_point = logPos,
                m_dir = dir,
                m_hitType = HitData.HitType.Structural,
                m_itemWorldLevel = byte.MaxValue,
            };
            hit.m_damage.m_chop = ChopDamage;

            log.Damage(hit);
            return true;
        }

        /// <summary>A tree that still exists and whose ZDO we can act on.</summary>
        public static bool IsFellable(TreeBase tree)
        {
            if (tree == null) return false;
            var nview = tree.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() && nview.GetZDO() != null;
        }

        /// <summary>
        ///     Fell <paramref name="tree" />, dropping it away from <paramref name="fellerPos" />.
        ///     Returns false only when the tree is already gone.
        /// </summary>
        public static bool TryFell(TreeBase tree, Vector3 fellerPos)
        {
            if (!IsFellable(tree)) return false;

            var nview = tree.GetComponent<ZNetView>();
            // RPC_Damage early-returns unless it runs on the owner, so without this the
            // hit is a no-op whenever the tree is owned by a client or unowned.
            nview.ClaimOwnership();

            var treePos = tree.transform.position;
            var away = treePos - fellerPos;
            away.y = 0f;
            // Degenerate only if the villager is exactly inside the trunk; any direction
            // is as good as another there.
            var dir = away.sqrMagnitude > 0.0001f ? away.normalized : Vector3.forward;

            var hit = new HitData
            {
                m_toolTier = ToolTier,
                m_point = treePos + Vector3.up,
                m_dir = dir,
                m_hitType = HitData.HitType.Structural,
                // CheckToolTier also fails an under-levelled item when the
                // WorldLevelLockedTools global key is set; the villager's "axe" is
                // notional, so it is never the thing being level-gated.
                m_itemWorldLevel = byte.MaxValue,
            };
            hit.m_damage.m_chop = ChopDamage;

            tree.Damage(hit);
            return true;
        }
    }
}
