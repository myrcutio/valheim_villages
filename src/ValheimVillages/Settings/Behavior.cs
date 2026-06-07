namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Configuration settings for villager behavior.
    /// </summary>
    public static class VillagerSettings
    {
        /// <summary>Maximum distance from bed that NPC will wander.</summary>
        public const float MaxWanderRange = 200f;

        /// <summary>Radius to check for nearby POIs while wandering.</summary>
        public const float DiscoveryRadius = 20f;

        /// <summary>How often to re-evaluate behavior (seconds).</summary>
        public const float UpdateInterval = 15f;

        /// <summary>
        ///     Cooldown (s) between consecutive behavior-selection passes.
        ///     The villager's path-follow inner loop still runs every tick
        ///     for smooth movement; this only gates the "which task do I
        ///     want?" fan-out across registered behaviors. A short cooldown
        ///     prevents the visible thrash where two Workflows take turns
        ///     winning WantsControl every Update tick (cooking station vs
        ///     work site, observed in incident timelines as TargetSet/
        ///     PathRecompute pairs every ~40ms). 2s is short enough that
        ///     player input (work orders, manual relocate) still feels
        ///     responsive but long enough to absorb tick-rate evaluation
        ///     jitter.
        /// </summary>
        public const float BehaviorReselectIntervalSec = 2f;

        /// <summary>
        ///     How long (s) a villager should linger near the spot where it
        ///     just finished a work step before Explore is allowed to drag
        ///     it back to the village fire. Bridges the gap between "polled
        ///     smelter, dropped one batch off, came back" and "smelter has
        ///     finished the next batch" — without this, the villager walks
        ///     to the fire and 30s later walks back, repeating endlessly.
        ///     <para>Tuned short: 15s is enough that if a new work order is
        ///     pending it'll preempt before the linger expires, but short
        ///     enough that the villager doesn't visibly hover for an
        ///     unnatural duration when there's genuinely nothing to do.
        ///     The follow-up walk to a known location uses
        ///     <see cref="IsCasualTravel"/>-flagged movement (walk, not
        ///     run) so even the expired-linger transition reads as relaxed.</para>
        /// </summary>
        public const float PostWorkLingerSec = 15f;

        /// <summary>
        ///     Random jitter range (seconds) applied to each NPC's initial behavior tick.
        ///     Prime number so tick offsets rarely re-align over time.
        /// </summary>
        public const float BehaviorTickJitter = 11f;

        /// <summary>How close to target before considering arrived (meters).</summary>
        /// <remarks>
        ///     Final-arrival radius used by VillagerAI's OnArrivedAtTarget gate
        ///     and the explore adapter's "do I need to travel?" check. This is
        ///     the moment a work interaction fires (roast/smelt/deposit), so it
        ///     doubles as the "allowable distance" from a station: the approach
        ///     already sits ~1.5m off the prefab pivot (MinApproachStandoffXZ),
        ///     and the villager interacts as soon as it's within this radius of
        ///     that approach. At 2m a villager would roast/deposit from up to
        ///     ~3.5-4m out; 1m keeps it right at the station (~1.5-2.5m). The
        ///     NavMesh agent drives to within its 0.3m stopping distance of the
        ///     approach, so 1m still registers arrival reliably. NOT used for
        ///     intermediate path-node popping — that uses the tighter
        ///     <see cref="PathNodePopThreshold"/> so routing corners around
        ///     obstacles don't get prematurely eaten.
        /// </remarks>
        public const float ArrivalThreshold = 1f;

        /// <summary>
        ///     Distance threshold for popping intermediate path nodes from
        ///     <see cref="UnityEngine.AI.NavMeshPath.corners"/> as the agent
        ///     advances along the path. Much tighter than
        ///     <see cref="ArrivalThreshold"/> because routing corners
        ///     placed by NavMesh.CalculatePath to navigate around obstacles
        ///     (column corners, narrow doorways) sit close together — a 2m
        ///     pop window eats them before the agent has actually maneuvered
        ///     past them, leaving the agent stuck against the obstacle the
        ///     corners were supposed to route around. 1.0m is the smallest
        ///     window that still lets the agent register "arrived at node"
        ///     when geometry pads the approach: agent radius (~0.5m) plus a
        ///     typical piece collider radius (~0.4m for chests / beds / small
        ///     columns) means the agent's CENTER physically can't get closer
        ///     than ~0.9m to a node placed at the obstacle's position. A
        ///     tighter threshold leaves the node unpoppable; 1.0m gives a
        ///     small margin. Routing corners ≥1.4m apart (typical for
        ///     CalculatePath turns around column-sized obstacles) still
        ///     survive until the agent actually walks within range.
        /// </summary>
        public const float PathNodePopThreshold = 0.5f;

        /// <summary>
        ///     Additive buffer (meters) applied to the slot-31 agent radius
        ///     when baking the villager NavMesh. The bake voxelizer carves
        ///     walkable surface away from obstacles by the agent radius —
        ///     adding a buffer pushes the NavMesh edge further from walls
        ///     so path corners around columns / pillars / piece edges sit
        ///     with extra clearance, giving the agent room to maneuver
        ///     through turns without scraping the geometry.
        ///
        ///     Affects bake only: the slot-31 agent's REGISTERED radius is
        ///     unchanged (still drives SamplePosition / NavMesh queries /
        ///     CalculatePath cost). The character's actual capsule radius
        ///     is independent and unaffected.
        ///
        ///     Tradeoff: large buffers can cause narrow passages to be
        ///     carved out entirely (1m doorways at 0.5m base radius become
        ///     unreachable past +0.0m buffer — but door NavMeshLinks bridge
        ///     them, so this is fine in practice). Keep modest (≤0.2m).
        ///
        ///     Empirically: 0.12m was wide enough to delete slot-31 polygons
        ///     from a narrow upper-level balcony (probe @ (-2256.94, 43.38,
        ///     1292.88) returned 0 polys within 5m for slot 31 while
        ///     Humanoid hit at 0.94m — same colliders, same bounds). Dropped
        ///     to 0.0m to let the slot-31 bake match Humanoid's coverage;
        ///     re-evaluate if villagers start scraping piece edges on
        ///     turns. The original "extra clearance on turns" justification
        ///     is now mostly redundant with the NavMeshLink corner-offset
        ///     (RegionGraphWaypoints.WallClearance=0.15m) that the corridor
        ///     planner injects at every direction change.
        /// </summary>
        public const float NavMeshBakeRadiusBuffer = 0.025f;

        /// <summary>
        ///     Hard timeout: seconds since last successful waypoint arrival before
        ///     the guard is teleported back to their bed as a safety reset.
        /// </summary>
        public const float PatrolHardStuckTimeoutSeconds = 60f;

        /// <summary>
        ///     Seconds of zero physical movement (despite a non-empty path)
        ///     before VillagerAI dumps diagnostics, clears the cached path,
        ///     and lets the next tick re-evaluate via TryFindCompletePath
        ///     (which will trigger the recovery flow if the target is truly
        ///     unreachable). Larger than DoorSettings.MovementStallThreshold
        ///     and StepJump's 1.5s so the door / step-jump heuristics get
        ///     to run first.
        /// </summary>
        public const float PathStallEscapeSeconds = 10f;

        /// <summary>
        ///     Maximum recovery attempts when FindPath returns an incomplete
        ///     path (target unreachable). After this many failures the AI
        ///     fires <c>IPathUnreachableHandler.OnPathUnreachable</c> so the
        ///     behavior can AbandonWork or pick a different target.
        /// </summary>
        public const int MaxRecoveryAttempts = 3;

        /// <summary>
        ///     Base seconds to wait after retreating to a known location before
        ///     re-attempting the unreachable target. Exponential backoff per
        ///     attempt: 5s, 10s, 20s, capped by <see cref="RecoveryBackoffMaxSeconds" />.
        /// </summary>
        public const float RecoveryBackoffBaseSeconds = 5f;

        /// <summary>
        ///     Upper bound on the recovery backoff to keep the retreat-retry
        ///     loop from stretching unreasonably long under pathological
        ///     attempt counts.
        /// </summary>
        public const float RecoveryBackoffMaxSeconds = 30f;

        /// <summary>
        ///     Fraction of the character's normal m_jumpForce used for automatic step-up
        ///     jumps when stuck against raised geometry. 1.0 = full jump, 0.5 = half height.
        /// </summary>
        public const float StepJumpForceFraction = 0.6f;

        /// <summary>
        ///     Master switch for the automatic step-up jump when a villager
        ///     stalls against raised geometry (path[0].y > transform.y).
        ///     Disabled by default: the jump masks broken-path scenarios by
        ///     refreshing m_lastRealMoveTime, which in turn prevents
        ///     <see cref="PathStallEscapeSeconds"/> and other "no movement"
        ///     timers from ever firing. With this off, a stuck villager
        ///     stays visibly stuck and the diagnostic logs reflect reality.
        /// </summary>
        public const bool StepJumpEnabled = false;

        /// <summary>
        ///     Master switch for the path-stall recovery flow (retreat to a
        ///     known POI with exponential backoff, eventually firing
        ///     <see cref="ValheimVillages.Interfaces.IPathUnreachableHandler.OnPathUnreachable"/>).
        ///     Disabled by default while investigating broken-path causes —
        ///     the recovery shuffles villagers around the village in a way
        ///     that hides the underlying NavMesh / pathing failure. With
        ///     this off, a villager whose TryFindCompletePath fails (or
        ///     whose path stalls past <see cref="PathStallEscapeSeconds"/>)
        ///     stays put with diagnostic logs, no automatic retreat or
        ///     AbandonWork.
        /// </summary>
        public const bool AutoPathRecoveryEnabled = false;

        /// <summary>
        ///     Master switch for AUTOMATIC diagnostic captures — the orchestrated
        ///     screenshot + sidecar JSON taken on every repartition and by the
        ///     stuck-villager incident recorder. Disabled by default: the
        ///     orchestration teleports the player to a top-down anchor to snap
        ///     the frame and restores afterward, and a frame/restore hiccup can
        ///     leave the character stranded in the sky. With this off, the
        ///     player is never moved for a capture. The explicit <c>vv_capture</c>
        ///     console command is unaffected — it still captures on demand.
        /// </summary>
        public const bool AutoDiagnosticCaptureEnabled = false;

        // Time boundaries as day fraction (0-1 where 0.5 = noon)
        // Valheim: 0.25 = 6am, 0.5 = noon, 0.75 = 6pm
        public const float NightStart = 0.875f; // ~9pm
        public const float MorningStart = 0.25f; // 6am
        public const float DayStart = 0.417f; // ~10am
        public const float EveningStart = 0.708f; // ~5pm
    }

    /// <summary>
    ///     Configuration settings for door interaction.
    /// </summary>
    public static class DoorSettings
    {
        /// <summary>Delay before closing a door after NPC passes through (seconds).</summary>
        public const float DoorCloseDelay = 1.5f;

        /// <summary>Radius to detect nearby doors (meters).</summary>
        public const float DoorDetectionRadius = 3f;

        /// <summary>Time without movement progress before checking for blocking doors (seconds).</summary>
        public const float MovementStallThreshold = 1f;

        /// <summary>Minimum distance NPC must move to be considered making progress (meters).</summary>
        public const float MovementProgressThreshold = 0.5f;
    }

    /// <summary>
    ///     Configuration settings for exploration behavior.
    ///     NPCs will explore when they lack variety in their known locations.
    /// </summary>
    public static class ExplorationSettings
    {
        /// <summary>
        ///     Minimum number of different location types before NPC stops exploring.
        ///     With 8 location types total (Bed, Shelter, Fire, Chair, Table, Farm, Animals, Patrol),
        ///     requiring 4 means NPC should know about half of them before being satisfied.
        /// </summary>
        public const int MinDesiredVariety = 4;

        /// <summary>
        ///     Probability (0-1) that an NPC will choose to explore instead of going to a known location
        ///     when their location variety is below the minimum.
        /// </summary>
        public const float ExplorationChance = 0.4f;

        /// <summary>
        ///     Minimum distance from all known locations when picking an exploration target.
        ///     This encourages exploring truly new areas.
        /// </summary>
        public const float MinDistanceFromKnown = 25f;

        /// <summary>
        ///     Maximum distance from bed when picking an exploration target.
        ///     Should be less than MaxWanderRange to stay safe.
        /// </summary>
        public const float MaxExplorationRange = 250f;

        /// <summary>
        ///     How long to wander at an exploration point before giving up (seconds).
        /// </summary>
        public const float ExplorationDuration = 5f;

        /// <summary>
        ///     Random movement range while exploring an area.
        /// </summary>
        public const float ExplorationWanderRadius = 25f;
    }

    /// <summary>
    ///     Configuration settings for NPC work order crafting behavior.
    /// </summary>
    public static class WorkSettings
    {
        /// <summary>Radius to scan for chests containing work orders (meters).</summary>
        public const float ChestScanRadius = 20f;

        /// <summary>Radius (from a villager's bed) to scan for stray ground
        /// drops to haul into chests (meters).</summary>
        public const float HaulScanRadius = 32f;

        /// <summary>Time spent at crafting station per craft cycle (seconds).</summary>
        public const float CraftDuration = 5f;

        /// <summary>How often to re-scan for work when idle (seconds).</summary>
        public const float WorkScanInterval = 30f;

        /// <summary>
        ///     Arrival distance for work destinations (chests, stations).
        ///     Wider than the normal ArrivalThreshold because physical colliders on
        ///     furniture can prevent NPCs from getting within 1m.
        /// </summary>
        public const float WorkArrivalThreshold = 2.5f;

        /// <summary>How often to poll the cooking station when waiting for food to finish (seconds).</summary>
        public const float WaitingPollInterval = 3f;

        /// <summary>Extra seconds to wait after cook time before checking/removing (buffer for game to set Done).</summary>
        public const float CookingDoneGraceSeconds = 1.5f;

        /// <summary>Seconds without reaching the work destination (within 2m 3D) before giving up and trying something else.</summary>
        public const float WorkStuckTimeoutSeconds = 30f;
    }

    /// <summary>
    ///     Configuration settings for villager combat (CombatBehavior).
    ///     Engagement model: defend-on-detection. A combat-capable villager
    ///     engages hostiles that enter its guard zone (a radius around its home
    ///     post / bed) and disengages — returning to patrol — once the zone is
    ///     clear or the target flees past the leash.
    /// </summary>
    public static class CombatSettings
    {
        /// <summary>
        ///     How far from the villager a hostile is noticed. The villager only
        ///     ACTS on hostiles that are also within <see cref="LeashRadius"/> of
        ///     its home post, so this is the "line of sight" radius, not the
        ///     guard zone size.
        /// </summary>
        public const float DetectionRadius = 25f;

        /// <summary>
        ///     Guard zone radius, measured from the villager's bed (home post).
        ///     Hostiles outside this radius are ignored, and an engaged target
        ///     that moves beyond it is dropped — the villager will not be lured
        ///     across the map. Keep comfortably larger than a typical patrol
        ///     route so the guard covers its whole village.
        /// </summary>
        public const float LeashRadius = 40f;

        /// <summary>
        ///     Distance at which a melee villager stops closing and swings. Uses
        ///     the larger of this and the weapon's own <c>m_aiAttackRange</c> so
        ///     long-reach weapons still work, but doesn't depend on player
        ///     weapons having AI ranges tuned.
        /// </summary>
        public const float MeleeAttackRange = 2.2f;

        /// <summary>
        ///     Distance at which a ranged (crossbow) villager will open fire.
        ///     Decoupled from the weapon's <c>m_aiAttackRange</c> (player weapons
        ///     leave it at the ~2m default, which would make the "ranged"
        ///     villager close to melee distance before shooting).
        /// </summary>
        public const float RangedEngageRange = 22f;

        /// <summary>
        ///     Ranged villagers back away (kite) when a target closes nearer than
        ///     this, to keep the crossbow useful instead of being swarmed.
        /// </summary>
        public const float RangedMinStandoff = 8f;

        /// <summary>How often (s) to re-scan for / re-validate a target while idle or engaged.</summary>
        public const float TargetRescanInterval = 1f;

        /// <summary>How often (s) to refresh the chase destination toward a moving target.</summary>
        public const float ChaseRepathInterval = 0.35f;

        /// <summary>Bolt stack size kept topped up in a ranged villager's inventory (infinite ammo).</summary>
        public const int AmmoTopUpStack = 20;

        /// <summary>
        ///     A hostile within this distance of ANY fellow villager counts as
        ///     "threatening the village" — a guard will move to engage it even if
        ///     it isn't near the guard itself (as long as it's within the guard's
        ///     <see cref="LeashRadius"/>). This is what makes guards defend other
        ///     NPCs rather than only reacting to threats in their own face.
        /// </summary>
        public const float VillageThreatRadius = 18f;

        /// <summary>
        ///     Non-combatant villagers flee when a hostile comes within this
        ///     distance. Smaller than the guard detection radius so guards react
        ///     first / from further out.
        /// </summary>
        public const float FleeDangerRadius = 16f;

        /// <summary>
        ///     Hysteresis: a fleeing non-combatant only calms once the nearest
        ///     hostile is beyond this (larger than <see cref="FleeDangerRadius"/>),
        ///     so it doesn't flicker in/out of panic at the boundary.
        /// </summary>
        public const float FleeClearRadius = 24f;

        /// <summary>
        ///     How far a non-combatant runs from the threat when there is no guard
        ///     on the roster to flee toward.
        /// </summary>
        public const float FleeDistance = 14f;

        /// <summary>Eye/muzzle height (m) the guard's line-of-sight ray is cast from / to.</summary>
        public const float LosEyeHeight = 1.5f;

        /// <summary>
        ///     Minimum seconds between line-of-sight reposition searches (sampling
        ///     navmesh cells around the target for one with a clear shot). The cheap
        ///     per-tick LOS raycast runs every tick; only the heavier
        ///     "find a firing position" search is throttled.
        /// </summary>
        public const float LosSearchInterval = 0.4f;

        /// <summary>
        ///     Consecutive failed reposition searches before the guard concludes no
        ///     navmesh cell can see the target, ignores it, and returns to patrol.
        /// </summary>
        public const int LosMaxSearchFails = 3;

        /// <summary>
        ///     After giving up on an unreachable-by-sight target, ignore it for this
        ///     long so the guard doesn't immediately re-aggro and shoot the wall again.
        /// </summary>
        public const float LosIgnoreSeconds = 8f;

        /// <summary>Number of candidate firing positions sampled around the target per search.</summary>
        public const int LosSampleCount = 12;
    }
}