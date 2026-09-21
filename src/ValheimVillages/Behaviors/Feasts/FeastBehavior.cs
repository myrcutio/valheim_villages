using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Scheduling;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Work;
using Object = UnityEngine.Object;

namespace ValheimVillages.Behaviors.Feasts
{
    /// <summary>
    ///     Lays out a fresh feast where an eaten one stands.
    ///
    ///     <para><b>Only an EMPTY feast is touched.</b> A feast the player put down is theirs:
    ///     it is a placed piece, it decorates the hall, and it keeps feeding people until its
    ///     last serving is gone. The one thing a villager can usefully do is swap the empty
    ///     platter for a full one when the village has another in store — so this behavior
    ///     looks for <c>Feast.GetStack() &lt;= 0</c> and nothing else.</para>
    ///
    ///     <para>The replacement is paid for: a placed feast costs the same item the player
    ///     builds it from (<c>Piece.m_resources</c>, e.g. <c>FeastMeadows_Material</c>), taken
    ///     out of a village chest. No stock, no swap — the empty one simply stays.</para>
    ///
    ///     <para>Tag: "feast", Priority: 55 — alongside tidy(60), above craft(50): it is a
    ///     short errand that restores comfort for everyone in the hall.</para>
    /// </summary>
    [RegisterBehavior("feast")]
    public class FeastBehavior : IBehavior, IDirectedBehavior
    {
        /// <summary>How close the producer's row has to be to the feast it named.</summary>
        internal const float SearchRadius = 8f;

        private const float MaxLegSeconds = 45f;
        private const float ReachRange = 3.5f;

        private readonly VillagerAI m_ai;
        private bool m_active;
        private Feast m_feast;
        private float m_legDeadline;
        private IngredientSource m_material;
        private bool m_navIssued;
        private Vector3 m_target;

        public FeastBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "feast";

        public int Priority => 55;

        public bool WantsControl(BehaviorContext ctx) => AssignmentActive;

        public bool CanExecute(TaskKind kind) => kind == TaskKind.FeastRefresh;

        public bool AssignmentActive => m_active;

        public AssignmentResult BeginAssignment(CandidateTask task)
        {
            var feast = FindEmptyFeast(task.Position, SearchRadius);
            if (feast == null) return AssignmentResult.NotActionable;

            var containers = ContainerScanner.FindVillageContainers(
                m_ai.HomeAnchor, WorkSettings.HaulScanRadius);
            if (!TryFindMaterial(feast, containers, out var material))
                return AssignmentResult.NotActionable;

            if (!VillagerMovement.TryResolveApproach(
                    feast.transform.position, m_ai.Position, null, out var approach))
                return AssignmentResult.Unreachable;

            m_feast = feast;
            m_material = material;
            m_target = approach;
            m_active = true;
            m_navIssued = false;
            m_legDeadline = Time.time + MaxLegSeconds;
            return AssignmentResult.Accepted;
        }

        public void Update(float dt)
        {
            if (!m_active) return;

            if (Time.time > m_legDeadline)
            {
                Plugin.Log?.LogWarning($"[Feast:{m_ai.NpcName}] gave up on the swap (leg timed out)");
                Reset();
                return;
            }

            m_ai.RequestFastReselect(0.25f);

            // Proximity, not a PathComplete arrival — cross-region routes are link-stitched
            // and never report one.
            if ((m_ai.Position - m_target).sqrMagnitude <= ReachRange * ReachRange)
            {
                Replace();
                Reset();
                return;
            }

            if (m_navIssued) return;
            if (!m_ai.NavTo(m_target, BehaviorState.Traveling, "feast: replace an empty platter",
                    snapToApproach: false))
            {
                Plugin.Log?.LogWarning($"[Feast:{m_ai.NpcName}] cannot path to the feast");
                Reset();
                return;
            }

            m_navIssued = true;
        }

        public void OnArrival(float dt)
        {
            if (!m_active) return;
            Replace();
            Reset();
        }

        public string GetStatusText() => m_active ? "Laying out a fresh feast" : "";

        /// <summary>
        ///     Swap the platter: pay, take the empty one away, set the full one down in its
        ///     place — same spot, same facing, so a feast the player lined up with their table
        ///     stays lined up.
        /// </summary>
        private void Replace()
        {
            if (m_feast == null || !IsEmpty(m_feast)) return;

            var nview = m_feast.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            var pieceName = PieceName(m_feast);
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(pieceName) : null;
            if (prefab == null)
            {
                Plugin.Log?.LogError($"[Feast:{m_ai.NpcName}] no prefab named '{pieceName}' to set down");
                return;
            }

            // Resolved BEFORE the old one is destroyed: once it is gone there is nothing left
            // to read the position and rotation from.
            var position = m_feast.transform.position;
            var rotation = m_feast.transform.rotation;

            if (!ContainerScanner.RemoveIngredients(new List<IngredientSource> { m_material }))
            {
                Plugin.Log?.LogInfo(
                    $"[Feast:{m_ai.NpcName}] the {m_material.PrefabName} was gone before the swap");
                return;
            }

            nview.ClaimOwnership();
            nview.Destroy();
            Object.Instantiate(prefab, position, rotation);

            Plugin.Log?.LogInfo(
                $"[Feast:{m_ai.NpcName}] laid out a fresh {pieceName} (from {m_material.PrefabName}) " +
                $"at ({position.x:F1},{position.y:F1},{position.z:F1})");
        }

        /// <summary>Nearest feast with nothing left on it.</summary>
        internal static Feast FindEmptyFeast(Vector3 center, float radius)
        {
            Feast best = null;
            var bestSqr = float.MaxValue;
            foreach (var feast in PhysicsHelper.GetAllInRadius<Feast>(center, radius))
            {
                if (feast == null || !IsEmpty(feast)) continue;

                var sqr = (feast.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                best = feast;
                bestSqr = sqr;
            }

            return best;
        }

        /// <summary>
        ///     Eaten out. <c>Feast</c> counts DOWN from <c>m_eatStacks</c> and writes -1 on the
        ///     last serving, so "empty" is any stack at or below zero — never a positive one,
        ///     which is a feast still feeding people.
        /// </summary>
        internal static bool IsEmpty(Feast feast)
        {
            var nview = feast != null ? feast.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() && feast.GetStack() <= 0;
        }

        internal static string PieceName(Feast feast)
        {
            return feast.gameObject.name.Replace("(Clone)", "").Trim();
        }

        /// <summary>
        ///     The item a placed feast is built from, and a village chest holding one. This is
        ///     the piece's own build cost, so it stays correct for every feast in the game and
        ///     for any a mod adds.
        /// </summary>
        internal static bool TryFindMaterial(
            Feast feast, List<Container> containers, out IngredientSource source)
        {
            source = null;

            var piece = feast.GetComponent<Piece>();
            if (piece?.m_resources == null) return false;

            foreach (var requirement in piece.m_resources)
            {
                if (requirement?.m_resItem == null) continue;
                var prefabName = Utils.GetPrefabName(requirement.m_resItem.name);
                var amount = Mathf.Max(1, requirement.m_amount);

                foreach (var container in containers)
                {
                    var inv = container?.GetInventory();
                    if (inv == null) continue;
                    if (ContainerScanner.CountByPrefab(inv, prefabName) < amount) continue;

                    source = new IngredientSource
                    {
                        Container = container,
                        PrefabName = prefabName,
                        Amount = amount,
                    };
                    return true;
                }
            }

            return false;
        }

        private void Reset()
        {
            m_active = false;
            m_feast = null;
            m_material = null;
            m_navIssued = false;
            m_ai.SetState(BehaviorState.Idle);
        }
    }
}
