using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Behaviors.Combat
{
    /// <summary>
    ///     Non-combatant reaction to danger. When a hostile that is aware of someone and can
    ///     reach this villager (line of sight, or already inside on the village graph — see
    ///     IsRealThreat) comes within <see cref="CombatSettings.FleeDangerRadius"/>, the
    ///     villager panics and runs — toward the nearest guard (any villager with a
    ///     <see cref="CombatBehavior"/>) if one is on the roster, otherwise directly
    ///     away from the threat.
    ///
    ///     <para>Once the threat passes <see cref="CombatSettings.FleeClearRadius"/> the
    ///     villager does NOT resume work immediately — it holds position and sweeps
    ///     <see cref="CombatSettings.FleeAllClearRadius"/> every
    ///     <see cref="CombatSettings.FleeAllClearCheckInterval"/> seconds, yielding back to the
    ///     scheduler only when that sweep comes back empty. "The one chasing me left" is not
    ///     "the area is safe": a raid arrives as a pack, and resuming on the first clear
    ///     reading walked villagers straight back into the next wolf.</para>
    ///
    ///     <para>Auto-added by <c>VillagerAI.RegisterBehaviors</c> to every villager
    ///     that is NOT itself a combatant, so "any non-guard flees" without each
    ///     definition having to opt in. Tag: "flee", Priority: 100 — preempts work
    ///     while in danger.</para>
    /// </summary>
    [RegisterBehavior("flee")]
    public class FleeBehavior : IBehavior
    {
        private const float FleeTickSec = 0.1f;

        private readonly VillagerAI m_ai;

        private float m_lastRepathTime;
        private float m_lastScanTime;
        private bool m_runningToGuard;
        private Character m_threat;

        // The threat that drove us here is gone, but we are holding position until an
        // all-clear sweep says the area is actually empty. See BeginAllClearWatch.
        private bool m_watchingForAllClear;
        private float m_nextAllClearAt;

        // When the current flee episode began (0 = none). Bounded by FleeMaxSeconds.
        private float m_episodeStartedAt;

        public FleeBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "flee";

        // Preempts work/patrol while the villager is in danger.
        public int Priority => 100;

        public bool WantsControl(BehaviorContext ctx)
        {
            // One episode may hold control for at most FleeMaxSeconds. Release to the
            // scheduler; a hostile still inside the danger radius re-triggers a fresh episode
            // on the next scan, so this only ends panics that have outlived their threat.
            if (m_episodeStartedAt > 0f &&
                Time.time - m_episodeStartedAt > CombatSettings.FleeMaxSeconds)
            {
                Plugin.Log?.LogInfo(
                    $"[Flee:{m_ai.NpcName}] flee timed out after " +
                    $"{Time.time - m_episodeStartedAt:F0}s — returning to work");
                Calm();
                return false;
            }

            // Keep fleeing while the current threat is still within the (larger)
            // clear radius — hysteresis so panic doesn't flicker at the boundary.
            if (IsStillDangerous(m_threat))
            {
                m_watchingForAllClear = false;
                return true;
            }

            // The hostile we were running from just cleared. Do NOT hand straight back to
            // the scheduler: hold control and watch first (see BeginAllClearWatch).
            if (m_threat != null)
            {
                m_threat = null;
                BeginAllClearWatch();
                return true;
            }

            // Normal danger scan. Runs while watching too, so a NEW hostile arriving during
            // the watch re-triggers flight at the fast cadence rather than waiting out the
            // (deliberately slow) all-clear interval.
            if (Time.time - m_lastScanTime >= CombatSettings.TargetRescanInterval)
            {
                m_lastScanTime = Time.time;
                m_threat = FindNearestThreat(CombatSettings.FleeDangerRadius);
                if (m_threat != null)
                {
                    LogTrigger(m_threat);
                    if (m_episodeStartedAt <= 0f) m_episodeStartedAt = Time.time;
                    m_watchingForAllClear = false;
                    return true;
                }
            }

            return m_watchingForAllClear;
        }

        public void Update(float dt)
        {
            // Holding position after the threat cleared — the only thing left to decide is
            // when it is safe to go back to work.
            if (m_watchingForAllClear)
            {
                TickAllClearWatch();
                return;
            }

            if (!IsStillDangerous(m_threat))
            {
                // WantsControl owns the threat-cleared transition (into the watch); nothing
                // to drive this tick.
                return;
            }

            m_ai.RequestFastReselect(FleeTickSec);

            // Repath — and the non-trivial work that feeds it (village resolve, guard scope,
            // graph clamp) — only on the repath cadence, not on every 0.1s reselect tick.
            // During a raid many non-combatants flee at once; doing this 10x/s each was a
            // measurable hot path. Threat-clear (Calm, above) is still checked at 10Hz, so
            // reaction to safety stays snappy; only the pathing work is throttled.
            if (Time.time - m_lastRepathTime < CombatSettings.ChaseRepathInterval)
                return;
            m_lastRepathTime = Time.time;

            var myPos = m_ai.Position;
            var threatPos = m_threat.transform.position;

            // Resolve the fleer's OWN village once: scopes the guard search to this village
            // AND provides the graph the destination is clamped onto, so flee can never send
            // the villager toward another village's guard or to an off-graph SafeSpot, where
            // the agent would path across the unioned multi-village navmesh and strand it.
            var myVillage = Villages.Entity.VillageRegistry.GetVillageAt(m_ai.HomeAnchor);
            var graph = myVillage?.Graph;

            var guard = FindNearestGuard(graph);

            // Only run TO the guard when the guard is FARTHER from the enemy than we
            // are — then running to it moves us away from danger and behind its line.
            // If the guard is closer to the enemy than we are (it's already moving in
            // to engage), running to it would carry us toward the threat — so instead
            // retreat to a safe spot (home / directly away) and sit tight.
            Vector3 dest;
            if (guard != null &&
                (guard.Position - threatPos).sqrMagnitude > (myPos - threatPos).sqrMagnitude)
            {
                m_runningToGuard = true;
                dest = guard.Position;
            }
            else
            {
                m_runningToGuard = false;
                dest = SafeSpot(myPos, threatPos);
            }

            // Clamp the destination onto the fleer's village graph so the agent never paths
            // off-village. A same-village guard is already on-graph (no-op); SafeSpot may not
            // be. If the desired dest is too far from the graph, snap to a cell near the
            // villager's current position instead — anything on THIS graph beats an off-graph run.
            if (graph != null)
            {
                // Scope the clamp to anchor-REACHABLE cells so flee can never target a
                // disconnected far limb of the raw bake. Validator is null (unconstrained)
                // when the graph carries no committed classification — e.g. a blob-hydrated
                // client — so client-side behavior is unchanged.
                var reachable = graph.HasAnchorReachableClassification
                    ? (System.Func<Vector3, bool>)graph.IsAnchorReachableCell
                    : null;
                if (graph.TryFindNearestLookupCell(dest, reachable, out var onGraph, out _,
                        CombatSettings.FleeGraphClampRadius))
                    dest = onGraph;
                else if (graph.TryFindNearestLookupCell(myPos, reachable, out var hereCell, out _,
                             CombatSettings.FleeGraphClampRadius))
                    dest = hereCell;
            }

            m_ai.NavTo(dest, BehaviorState.Alarmed, "flee", snapToApproach: false);
        }

        public void OnArrival(float dt)
        {
            // No-op: fleeing is re-evaluated every tick against a moving threat.
        }

        public string GetStatusText()
        {
            if (m_watchingForAllClear) return "Waiting for the coast to clear...";
            if (m_threat == null) return "";
            return m_runningToGuard ? "Fleeing to a guard!" : "Hiding from danger!";
        }

        /// <summary>
        ///     Stop running and start watching. Entered the moment the hostile we fled from
        ///     leaves <see cref="CombatSettings.FleeClearRadius" />. Control is deliberately
        ///     RETAINED here: releasing it would let the scheduler dispatch work immediately
        ///     and march the villager back across the village while the rest of the pack is
        ///     still standing in it.
        /// </summary>
        private void BeginAllClearWatch()
        {
            m_watchingForAllClear = true;
            m_runningToGuard = false;
            m_nextAllClearAt = Time.time + CombatSettings.FleeAllClearCheckInterval;
            if (m_ai.CurrentState != BehaviorState.Idle)
                m_ai.SetState(BehaviorState.Idle);
        }

        /// <summary>
        ///     One tick of the all-clear watch. Sweeps for ANY hostile within
        ///     <see cref="CombatSettings.FleeAllClearRadius" /> on the
        ///     <see cref="CombatSettings.FleeAllClearCheckInterval" /> cadence; when the sweep
        ///     comes back empty the behavior releases control and the scheduler puts the
        ///     villager back to work on the next reselect.
        /// </summary>
        private void TickAllClearWatch()
        {
            if (Time.time < m_nextAllClearAt) return;
            m_nextAllClearAt = Time.time + CombatSettings.FleeAllClearCheckInterval;

            if (FindNearestThreat(CombatSettings.FleeAllClearRadius) != null)
            {
                // Still something out there. Stay put and re-check next interval; if it comes
                // close enough to be an actual danger, the scan in WantsControl picks it up
                // first and we go back to running.
                return;
            }

            m_watchingForAllClear = false;
            Calm();
            Plugin.Log?.LogInfo(
                $"[Flee:{m_ai.NpcName}] all clear (nothing within " +
                $"{CombatSettings.FleeAllClearRadius:F0}m) — returning to work");
        }

        // --- helpers -------------------------------------------------------

        private static int s_losMask;

        /// <summary>
        ///     Telemetry: what set off this flee and whether it could actually threaten the
        ///     villager — line of sight through solid geometry (walls, terrain), whether it
        ///     stands on the village graph (inside the walls), and whether it is even aware of
        ///     anyone. Throttled per villager + threat.
        /// </summary>
        private void LogTrigger(Character threat)
        {
            if (!Settings.LogSettings.VerboseFlee) return;
            var los = HasLineOfSight(threat, out var blockedBy);
            var ai = threat.GetComponent<BaseAI>();
            var monster = ai as MonsterAI;
            var targetCreature = monster != null ? monster.GetTargetCreature() : null;

            DebugLog.ThrottledWindow(
                $"flee_trigger:{m_ai.NpcName}:{threat.GetZDOID()}", System.TimeSpan.FromSeconds(10f),
                "Flee", "triggered",
                ("villager", m_ai.NpcName), ("threat", threat.name.Replace("(Clone)", "")),
                ("dist", Vector3.Distance(m_ai.Position, threat.transform.position)),
                ("line_of_sight", los),
                ("blocked_by", blockedBy ?? "none"),
                ("threat_on_village_graph", IsOnVillageGraph(threat)),
                ("threat_alerted", ai != null && ai.IsAlerted()),
                ("threat_target", targetCreature != null ? targetCreature.name.Replace("(Clone)", "") : "none"),
                ("villager_pos", m_ai.Position), ("threat_pos", threat.transform.position));
        }

        /// <summary>
        ///     A hostile is only a threat if it could actually get at this villager AND knows
        ///     anyone is there. Distance alone sent villagers running from a greyling on the
        ///     far side of the palisade — 7 m away, behind a closed door, unaware of anyone.
        ///     Reach = clear line of sight through solid geometry (walls, doors, terrain), or
        ///     standing on the village's walkable graph (i.e. already inside). Aware = alerted,
        ///     or has a target.
        /// </summary>
        private bool IsRealThreat(Character c)
        {
            var ai = c.GetComponent<BaseAI>();
            if (ai == null) return false;
            var monster = ai as MonsterAI;
            var aware = ai.IsAlerted() || (monster != null && monster.GetTargetCreature() != null);
            if (!aware) return false;

            return HasLineOfSight(c, out _) || IsOnVillageGraph(c);
        }

        private bool HasLineOfSight(Character threat, out string blockedBy)
        {
            if (s_losMask == 0)
                s_losMask = LayerMask.GetMask(
                    "Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");

            var eye = m_ai.Position + Vector3.up * 1.5f;
            var blocked = Physics.Linecast(eye, threat.GetCenterPoint(), out var hit, s_losMask,
                QueryTriggerInteraction.Ignore);
            blockedBy = blocked ? hit.collider.name : null;
            return !blocked;
        }

        private bool IsOnVillageGraph(Character threat)
        {
            var graph = Villages.Entity.VillageRegistry.GraphAt(m_ai.HomeAnchor);
            return graph != null && graph.PointToRegionId(threat.transform.position) != null;
        }

        private bool IsStillDangerous(Character c)
        {
            if (c == null || c.IsDead()) return false;
            var clearSq = CombatSettings.FleeClearRadius * CombatSettings.FleeClearRadius;
            if ((c.transform.position - m_ai.Position).sqrMagnitude > clearSq) return false;
            // Same test as the trigger: one that lost interest or went behind a wall is over.
            return IsRealThreat(c);
        }

        private Character FindNearestThreat(float radius)
        {
            var me = m_ai.Character;
            var myPos = m_ai.Position;
            var radiusSq = radius * radius;

            Character best = null;
            var bestSq = float.MaxValue;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == me || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(me, c)) continue;
                var dsq = (c.transform.position - myPos).sqrMagnitude;
                if (dsq > radiusSq) continue;
                if (!IsRealThreat(c)) continue;
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        ///     Nearest active combatant villager (one with a <see cref="CombatBehavior"/>) in
        ///     the SAME village as the fleer, or null. Village-scoped so a panicked villager
        ///     never flees toward a guard in another village (which would path it off its own
        ///     graph). A guard is in-village when its stable home anchor resolves to a region
        ///     on the fleer's own <paramref name="graph"/>. If the fleer's graph can't be
        ///     resolved (<paramref name="graph"/> null), falls back to nearest-overall rather
        ///     than excluding every guard.
        /// </summary>
        private VillagerAI FindNearestGuard(RegionGraph graph)
        {
            var myPos = m_ai.Position;
            VillagerAI best = null;
            var bestSq = float.MaxValue;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var v = kv.Value;
                if (v == null || v == m_ai) continue;
                if (v.GetBehavior<CombatBehavior>() == null) continue;
                // Same-village only: a guard counts when its (stable) home anchor resolves to
                // a region on the fleer's OWN graph — one O(1) lookup-grid hit, vs re-scanning
                // all world ZDOs per guard (GetVillageAt). When the fleer's graph is
                // unresolved, fall back to nearest-overall rather than excluding every guard.
                if (graph != null && graph.PointToRegionId(v.HomeAnchor) == null) continue;

                var dsq = (v.Position - myPos).sqrMagnitude;
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    best = v;
                }
            }

            return best;
        }

        /// <summary>
        ///     A spot to retreat to when running to the guard isn't safe. Prefers
        ///     home (typically indoors) when that moves us AWAY from the threat;
        ///     otherwise backs directly away from it.
        /// </summary>
        private Vector3 SafeSpot(Vector3 myPos, Vector3 threatPos)
        {
            var myDistSq = (myPos - threatPos).sqrMagnitude;
            var anchor = m_ai.HomeAnchor;
            if (anchor != Vector3.zero && (anchor - threatPos).sqrMagnitude > myDistSq)
                return anchor; // home is farther from the threat — hole up indoors

            var away = myPos - threatPos;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = m_ai.Character.transform.forward;
            return myPos + away.normalized * CombatSettings.FleeDistance;
        }

        private void Calm()
        {
            m_threat = null;
            m_runningToGuard = false;
            m_watchingForAllClear = false;
            m_episodeStartedAt = 0f;
            if (m_ai.CurrentState != BehaviorState.Idle)
                m_ai.SetState(BehaviorState.Idle);
        }
    }
}
