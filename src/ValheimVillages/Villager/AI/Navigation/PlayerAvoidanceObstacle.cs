using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Makes villager NavMeshAgents steer around the local player. The player
    ///     is a Character, NOT a NavMeshAgent, so it's invisible to the agents'
    ///     reciprocal (RVO) avoidance — villagers would walk straight into the
    ///     player in hallways/doorways. We give the player a non-carving
    ///     <see cref="NavMeshObstacle" /> (a moving cylinder the agents avoid) and
    ///     feed it the player's velocity each frame for predictive avoidance.
    ///     <para>NON-CARVING is deliberate: carving would cut a hole in the
    ///     navmesh and perturb every NavMesh path query — including Valheim's own
    ///     monster pathing. Velocity-based obstacle avoidance is consumed ONLY by
    ///     NavMeshAgents doing RVO (our slot-31 villagers); Valheim monsters move
    ///     their Characters manually and ignore it, so combat/aggro is unaffected.</para>
    ///     <para>Driven from Plugin.Update each frame. Stateless across hot
    ///     reloads: NavMeshObstacle is a Unity type, so GetComponent reliably
    ///     re-finds the existing component (no duplicate, no stale-instance bug).</para>
    /// </summary>
    internal static class PlayerAvoidanceObstacle
    {
        // Match the player capsule (~0.5m radius, ~1.8m tall — see vv_bake_audit
        // 'Player(Clone)' CapsuleCollider size(0.98,1.85,0.98)).
        private const float Radius = 0.5f;
        private const float Height = 1.8f;

        private static NavMeshObstacle s_obstacle;
        private static Player s_player;

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                s_obstacle = null;
                s_player = null;
                return;
            }

            if (s_obstacle == null || s_player != player)
            {
                s_player = player;
                s_obstacle = player.GetComponent<NavMeshObstacle>()
                             ?? player.gameObject.AddComponent<NavMeshObstacle>();
                s_obstacle.carving = false; // velocity-avoidance only; do NOT cut the navmesh
                s_obstacle.shape = NavMeshObstacleShape.Capsule;
                s_obstacle.radius = Radius;
                s_obstacle.height = Height;
                s_obstacle.center = new Vector3(0f, Height * 0.5f, 0f); // base at feet
            }

            // Feed the player's horizontal velocity so agent RVO predicts where
            // the player is heading and steers around, not just where they are.
            var vel = player.GetVelocity();
            vel.y = 0f;
            s_obstacle.velocity = vel;

            // TODO(player-interference): RVO only softly steers around the
            // player — it doesn't cover the player actively interfering. Edge
            // cases to handle later:
            //   - Player body-blocks a villager in a 1-wide corridor (RVO can't
            //     resolve a head-on; villager needs to yield/wait or re-route).
            //   - Player physically shoves a villager off the navmesh / off its
            //     path mid-traverse (character push), throwing it off course —
            //     should detect the displacement and re-path (or off-mesh rescue)
            //     rather than continuing to steer toward a now-stale corner.
            //   - Player standing on the villager's exact target/approach cell.
        }
    }
}
