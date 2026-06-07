using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Combat
{
    /// <summary>
    ///     Basic villager combat — BASELINE. Defend-on-detection: engages a hostile
    ///     that enters the guard zone (a radius around the villager's home post),
    ///     closes to weapon range, faces it, and fires; disengages and yields back to
    ///     patrol when the zone is clear or the target flees past the leash.
    ///
    ///     <para>For the baseline it drives the Dvergr prefab's OWN default weapon
    ///     (its native crossbow) rather than a player weapon we equip. That weapon's
    ///     attack animation lives in the Dvergr animator and it is AI-tuned
    ///     (m_aiAttackRange / m_aiAttackInterval), so the standard
    ///     <c>Humanoid.StartAttack</c> path works — no animator mismatch, no
    ///     player-crossbow reload gate (<c>IsWeaponLoaded()</c> is always false for
    ///     non-Players, which blocks reload weapons). MonsterAI is stripped, but the
    ///     engine still ticks <c>UpdateAttack</c>/<c>OnAttackTrigger</c> on the owner,
    ///     so starting the attack is enough.</para>
    ///     Tag: "combat", Priority: 100 — preempts patrol/work while a target exists.
    /// </summary>
    [RegisterBehavior("combat")]
    public class CombatBehavior : IBehavior
    {
        // Fast tick cadence (s) requested while engaged so we re-aim / fire / repath
        // near frame-rate instead of the default 2s behavior-reselect interval.
        private const float EngagedTickSec = 0.1f;

        // Layers that block a shot: walls, floors, terrain. The target is on the
        // 'character' layer (not here), so any hit is genuine cover between us and it.
        private static readonly int s_losMask =
            LayerMask.GetMask("Default", "static_solid", "piece", "terrain");

        private readonly VillagerAI m_ai;

        private float m_lastAttackTime;
        private float m_lastRepathTime;
        private float m_lastScanTime;
        private Character m_target;

        // Line-of-sight reposition state: a navmesh cell with a clear shot we're
        // moving to when the target is behind cover, plus the throttle + give-up
        // counter for the (heavier) search that finds it.
        private Vector3? m_losDest;
        private float m_nextLosSearch;
        private int m_losSearchFails;

        // A target we gave up on (no navmesh cell can see it) and won't re-aggro
        // until m_ignoreUntil, so the guard doesn't keep camping a wall.
        private Character m_ignoredTarget;
        private float m_ignoreUntil;

        public CombatBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "combat";

        // Above patrol(30)/craft(50)/tidy(60): combat preempts everything while a
        // hostile is in the guard zone.
        public int Priority => 100;

        public bool WantsControl(BehaviorContext ctx)
        {
            if (!HasUsableWeapon())
                return false;

            // Keep control while a live target is still in the guard zone.
            if (IsValidTarget(m_target))
                return true;

            // Otherwise scan for a fresh target on a throttle.
            if (Time.time - m_lastScanTime < CombatSettings.TargetRescanInterval)
                return false;
            m_lastScanTime = Time.time;

            m_target = AcquireTarget();
            return m_target != null;
        }

        public void Update(float dt)
        {
            var humanoid = m_ai.Humanoid;
            if (humanoid == null || !IsValidTarget(m_target))
            {
                Disengage();
                return;
            }

            // Tick fast while engaged (one-shot — re-request every Update).
            m_ai.RequestFastReselect(EngagedTickSec);

            // Make sure the Dvergr's native weapon is in hand (default items add it
            // to the inventory but don't auto-equip it without MonsterAI).
            EnsureWeaponEquipped(humanoid);
            var weapon = humanoid.GetCurrentWeapon();
            if (weapon == null)
            {
                Disengage();
                return;
            }

            var myPos = m_ai.Position;
            var targetPos = m_target.transform.position;
            var dist = Vector3.Distance(myPos, targetPos);

            // Face the target so the attack lands / the projectile aims.
            var lookDir = m_target.GetCenterPoint() - myPos;
            if (lookDir.sqrMagnitude > 0.0001f)
                m_ai.Character.SetLookDir(lookDir.normalized);

            // Trust the native weapon's AI-tuned attack range (a real ranged value
            // for the crossbow), with a small floor.
            var range = Mathf.Max(2f, weapon.m_shared.m_aiAttackRange);
            if (dist > range)
            {
                Repath(targetPos); // out of range — close in (path routes around walls)
                return;
            }

            // In range. Only fire with a clear shot — never shoot through a wall.
            if (HasLineOfSight(humanoid))
            {
                m_losDest = null;
                m_losSearchFails = 0;
                m_ai.ClearWaypoint(); // stop and fire in place
                TryAttack(humanoid, weapon);
                return;
            }

            // Blocked by cover. Try to reposition to a navmesh cell with a clear shot.
            UpdateLosDestination(range);
            if (m_losDest.HasValue)
                Repath(m_losDest.Value);
            else if (m_losSearchFails >= CombatSettings.LosMaxSearchFails)
                // No reachable cell can see the target (e.g. it's sealed behind
                // walls). Ignore it and return to patrol instead of camping cover.
                IgnoreAndDisengage();
            else
                // Search is throttled and we have no firing spot yet — hold rather
                // than fire blind into the wall.
                m_ai.ClearWaypoint();
        }

        public void OnArrival(float dt)
        {
            // No-op: attacks are driven from Update against a (moving) target.
        }

        public string GetStatusText()
        {
            if (m_target == null) return "";
            var name = string.IsNullOrEmpty(m_target.m_name)
                ? "enemy"
                : Localization.instance.Localize(m_target.m_name);
            return $"Fighting {name}";
        }

        // --- target acquisition / validation -------------------------------

        /// <summary>
        ///     Closest live hostile inside the guard zone
        ///     (<see cref="CombatSettings.LeashRadius"/> of the home post) that is
        ///     EITHER within the guard's own sight
        ///     (<see cref="CombatSettings.DetectionRadius"/>) OR threatening a fellow
        ///     villager (<see cref="CombatSettings.VillageThreatRadius"/> of any
        ///     villager in this village) — so guards move to defend other NPCs, not
        ///     just react to threats in their own face. Faction uses the inherited
        ///     <see cref="BaseAI.IsEnemy"/> — villagers are Faction.Players, so
        ///     monsters resolve as enemies while players/other villagers do not.
        /// </summary>
        private Character AcquireTarget()
        {
            var me = m_ai.Character;
            if (me == null) return null;

            var myPos = m_ai.Position;
            var bed = m_ai.BedPosition;
            var detectSq = CombatSettings.DetectionRadius * CombatSettings.DetectionRadius;
            var leashSq = CombatSettings.LeashRadius * CombatSettings.LeashRadius;

            Character best = null;
            var bestSq = float.MaxValue;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == me || c.IsDead()) continue;
                if (c == m_ignoredTarget && Time.time < m_ignoreUntil) continue;
                if (!BaseAI.IsEnemy(me, c)) continue;

                var cp = c.transform.position;
                if ((cp - bed).sqrMagnitude > leashSq) continue; // stay near home

                var dsq = (cp - myPos).sqrMagnitude;
                var nearMe = dsq <= detectSq;
                if (!nearMe && !ThreatensVillage(cp, bed)) continue;

                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        ///     True if <paramref name="enemyPos"/> is within
        ///     <see cref="CombatSettings.VillageThreatRadius"/> of any active villager
        ///     whose home is in this guard's village (bed within the leash). Lets a
        ///     guard respond to a threat menacing another NPC.
        /// </summary>
        private static bool ThreatensVillage(Vector3 enemyPos, Vector3 guardBed)
        {
            var sameVillageSq = CombatSettings.LeashRadius * CombatSettings.LeashRadius;
            var threatSq = CombatSettings.VillageThreatRadius * CombatSettings.VillageThreatRadius;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var v = kv.Value;
                if (v == null) continue;
                if ((v.BedPosition - guardBed).sqrMagnitude > sameVillageSq) continue;
                if ((enemyPos - v.Position).sqrMagnitude <= threatSq) return true;
            }

            return false;
        }

        /// <summary>Target is alive and still inside the guard zone (leash from the home post).</summary>
        private bool IsValidTarget(Character c)
        {
            if (c == null || c.IsDead()) return false;
            var leashSq = CombatSettings.LeashRadius * CombatSettings.LeashRadius;
            return (c.transform.position - m_ai.BedPosition).sqrMagnitude <= leashSq;
        }

        private void Disengage()
        {
            m_target = null;
            // Hand back to lower-priority behavior (patrol) on the next tick.
            if (m_ai.CurrentState != BehaviorState.Idle)
                m_ai.SetState(BehaviorState.Idle);
        }

        // --- weapon / attack / movement ------------------------------------

        /// <summary>True if a weapon is in hand, or an AI "enemy" weapon is available to equip.</summary>
        private bool HasUsableWeapon()
        {
            var h = m_ai.Humanoid;
            if (h == null) return false;
            if (h.GetCurrentWeapon() != null) return true;
            return FindEnemyWeapon(h) != null;
        }

        /// <summary>Equip the Dvergr's native enemy weapon (its crossbow) if nothing is in hand.</summary>
        private static void EnsureWeaponEquipped(Humanoid h)
        {
            if (h.GetCurrentWeapon() != null) return;
            var weapon = FindEnemyWeapon(h);
            if (weapon != null)
                h.EquipItem(weapon, triggerEquipEffects: false);
        }

        /// <summary>First inventory weapon flagged for AI use against enemies.</summary>
        private static ItemDrop.ItemData FindEnemyWeapon(Humanoid h)
        {
            var items = h.GetInventory()?.GetAllItems();
            if (items == null) return null;
            foreach (var it in items)
                if (it != null && it.IsWeapon() &&
                    it.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Enemy)
                    return it;
            return null;
        }

        private void TryAttack(Humanoid humanoid, ItemDrop.ItemData weapon)
        {
            var interval = Mathf.Max(0.5f, weapon.m_shared.m_aiAttackInterval);
            if (Time.time - m_lastAttackTime < interval) return;
            if (!m_ai.CanUseAttack(weapon)) return;

            if (humanoid.StartAttack(m_target, false))
                m_lastAttackTime = Time.time;
        }

        private void Repath(Vector3 dest)
        {
            if (Time.time - m_lastRepathTime < CombatSettings.ChaseRepathInterval) return;
            m_lastRepathTime = Time.time;
            // snapToApproach:false — chase a moving target directly; NavTo still
            // lands the point on the agent navmesh so the mover can follow it.
            m_ai.NavTo(dest, BehaviorState.Alarmed, "engage", snapToApproach: false);
        }

        // --- line of sight -------------------------------------------------

        /// <summary>True when nothing solid sits between the guard's muzzle and the target.</summary>
        private bool HasLineOfSight(Humanoid humanoid)
        {
            var from = humanoid.transform.position + Vector3.up * CombatSettings.LosEyeHeight;
            return IsClear(from, m_target.GetCenterPoint());
        }

        private static bool IsClear(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            var dist = delta.magnitude;
            if (dist < 0.01f) return true;
            return !Physics.Raycast(from, delta / dist, dist, s_losMask,
                QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        ///     (Re)compute, on a throttle, the nearest reachable navmesh cell with a
        ///     clear shot at the target. Sets <see cref="m_losDest"/> on success;
        ///     leaves it null and bumps <see cref="m_losSearchFails"/> when no firing
        ///     position can be found this pass.
        /// </summary>
        private void UpdateLosDestination(float weaponRange)
        {
            if (Time.time < m_nextLosSearch) return; // keep current dest between searches
            m_nextLosSearch = Time.time + CombatSettings.LosSearchInterval;

            if (FindFiringPosition(weaponRange, out var spot))
            {
                m_losDest = spot;
                m_losSearchFails = 0;
            }
            else
            {
                m_losDest = null;
                m_losSearchFails++;
            }
        }

        /// <summary>
        ///     Sample a ring of cells around the target and return the nearest one
        ///     (to the guard) that the villager navmesh can reach AND that has a
        ///     clear line of sight to the target. Returns false if none — meaning the
        ///     target is effectively sealed off from every firing position.
        /// </summary>
        private bool FindFiringPosition(float weaponRange, out Vector3 result)
        {
            result = Vector3.zero;
            if (!VillagerAgentType.IsRegistered) return false;

            var filter = AgentFilter();
            var myPos = m_ai.Position;
            var targetPos = m_target.transform.position;
            var aimPoint = m_target.GetCenterPoint();

            // A cell that can see the target is one not separated from it by cover
            // (i.e. on the target's side). Sample at a comfortable firing radius.
            var radius = Mathf.Clamp(weaponRange * 0.6f, 4f, 12f);

            var haveFrom = NavMesh.SamplePosition(myPos, out var fromHit, 3f, filter);
            var fromMesh = haveFrom ? fromHit.position : myPos;

            var bestSq = float.MaxValue;
            var found = false;
            var n = CombatSettings.LosSampleCount;
            for (var i = 0; i < n; i++)
            {
                var ang = Mathf.PI * 2f / n * i;
                var cand = targetPos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
                if (!NavMesh.SamplePosition(cand, out var hit, 2.5f, filter)) continue;

                var dsq = (hit.position - myPos).sqrMagnitude;
                if (dsq >= bestSq) continue; // already have a nearer firing spot

                var eye = hit.position + Vector3.up * CombatSettings.LosEyeHeight;
                if (!IsClear(eye, aimPoint)) continue; // this cell can't see the target either

                if (haveFrom)
                {
                    var path = new NavMeshPath();
                    NavMesh.CalculatePath(fromMesh, hit.position, filter, path);
                    if (path.status != NavMeshPathStatus.PathComplete) continue; // unreachable
                }

                bestSq = dsq;
                result = hit.position;
                found = true;
            }

            return found;
        }

        private void IgnoreAndDisengage()
        {
            m_ignoredTarget = m_target;
            m_ignoreUntil = Time.time + CombatSettings.LosIgnoreSeconds;
            m_losDest = null;
            m_losSearchFails = 0;
            Disengage();
        }

        private static NavMeshQueryFilter AgentFilter()
        {
            return new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
        }
    }
}
