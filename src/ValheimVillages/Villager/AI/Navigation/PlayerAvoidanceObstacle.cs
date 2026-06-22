using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Makes villager NavMeshAgents steer around every player. A player is a
    ///     Character, NOT a NavMeshAgent, so it's invisible to the agents'
    ///     reciprocal (RVO) avoidance — villagers would walk straight into the
    ///     player in hallways/doorways. We give each player a non-carving
    ///     <see cref="NavMeshObstacle" /> (a moving cylinder the agents avoid) and
    ///     feed it the player's velocity each frame for predictive avoidance.
    ///     <para>CRITICAL — must cover ALL players, not just the local one. Our
    ///     villagers are server-authoritative: the dedicated server owns and
    ///     simulates every villager agent, but a dedicated server has NO
    ///     <c>Player.m_localPlayer</c>. An obstacle built only on the local player
    ///     therefore never exists on the peer that runs the agents, so server-side
    ///     villagers had nothing representing the player and walked through them.
    ///     We iterate <see cref="Player.GetAllPlayers" /> (on the server this is
    ///     the connected players' replicated ghosts) and keep an obstacle on each.</para>
    ///     <para>Velocity is derived from the player's per-frame position delta, NOT
    ///     <c>Character.GetVelocity()</c>: a remote player on the server is an
    ///     interpolated ghost whose rigidbody velocity reads ~0/stale, which would
    ///     make RVO treat a sprinting player as stationary.</para>
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

        // Clamp the position-delta velocity so a teleport / respawn / zone-load
        // jump doesn't emit a one-frame hypervelocity that flings RVO. ~12 m/s is
        // comfortably above a sprinting player but far below a teleport jump.
        private const float MaxSpeed = 12f;

        private sealed class Tracked
        {
            internal NavMeshObstacle Obstacle;
            internal Vector3 LastPos;
            internal int LastSeenFrame;
        }

        private static readonly Dictionary<Player, Tracked> s_tracked = new();
        private static readonly List<Player> s_prune = new();

        internal static void Tick()
        {
            var players = Player.GetAllPlayers();
            if (players == null) return;

            var frame = Time.frameCount;
            var dt = Time.deltaTime;

            foreach (var player in players)
            {
                if (player == null) continue;

                if (!s_tracked.TryGetValue(player, out var t))
                {
                    t = new Tracked { LastPos = player.transform.position };
                    s_tracked[player] = t;
                }

                if (t.Obstacle == null)
                {
                    t.Obstacle = player.GetComponent<NavMeshObstacle>()
                                 ?? player.gameObject.AddComponent<NavMeshObstacle>();
                    t.Obstacle.carving = false; // velocity-avoidance only; do NOT cut the navmesh
                    t.Obstacle.shape = NavMeshObstacleShape.Capsule;
                    t.Obstacle.radius = Radius;
                    t.Obstacle.height = Height;
                    t.Obstacle.center = new Vector3(0f, Height * 0.5f, 0f); // base at feet
                }

                // Predictive avoidance: feed the player's horizontal velocity so agent
                // RVO steers around where the player is HEADING, not just where it is.
                // Derive it from the position delta (robust for replicated ghosts) and
                // clamp away teleport spikes.
                var pos = player.transform.position;
                var vel = dt > 1e-4f ? (pos - t.LastPos) / dt : Vector3.zero;
                vel.y = 0f;
                if (vel.sqrMagnitude > MaxSpeed * MaxSpeed)
                    vel = vel.normalized * MaxSpeed;
                t.Obstacle.velocity = vel;

                t.LastPos = pos;
                t.LastSeenFrame = frame;
            }

            // Drop entries for players that vanished this frame (disconnect / death).
            // The NavMeshObstacle is destroyed with the player GameObject; we only
            // need to release our reference so a reused Player slot re-initialises.
            s_prune.Clear();
            foreach (var kv in s_tracked)
                if (kv.Key == null || kv.Value.LastSeenFrame != frame)
                    s_prune.Add(kv.Key);
            foreach (var p in s_prune)
                s_tracked.Remove(p);

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
