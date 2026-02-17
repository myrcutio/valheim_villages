using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Positions and rotates an NPC on their bed to visually lie down while sleeping.
    /// Uses the bed's m_spawnPoint transform for positioning and rotates the character's
    /// visual model to simulate a lying-down pose.
    /// </summary>
    public static class NpcSleepPose
    {
        private struct SavedState
        {
            public Quaternion VisualLocalRotation;
            public Vector3 VisualLocalPosition;
            public float AnimatorSpeed;
            public bool WasKinematic;
            public bool WasGravity;
        }

        private static readonly Dictionary<int, SavedState> s_states = new();

        private static readonly FieldInfo s_visualField =
            typeof(Character).GetField("m_visual", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_bodyField =
            typeof(Character).GetField("m_body", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_animatorField =
            typeof(Character).GetField("m_animator", BindingFlags.NonPublic | BindingFlags.Instance);
        private static PropertyInfo s_animatorSpeedProp;

        /// <summary>
        /// Position the NPC on their bed in a lying-down pose.
        /// Call after the NPC has arrived at their bed position.
        /// </summary>
        public static void LieDown(MonsterAI npc, Vector3 bedPosition)
        {
            var bed = FindNearestBed(bedPosition);
            if (bed?.m_spawnPoint == null)
            {
                Plugin.Log?.LogDebug($"[SleepPose] No bed found near {bedPosition}");
                return;
            }

            var character = npc.GetComponent<Character>();
            if (character == null) return;

            var visual = s_visualField?.GetValue(character) as GameObject;
            var body = s_bodyField?.GetValue(character) as Rigidbody;

            if (visual == null)
            {
                Plugin.Log?.LogDebug("[SleepPose] No visual found on character");
                return;
            }

            int id = npc.GetInstanceID();

            s_states[id] = new SavedState
            {
                VisualLocalRotation = visual.transform.localRotation,
                VisualLocalPosition = visual.transform.localPosition,
                AnimatorSpeed = GetAnimatorSpeed(character),
                WasKinematic = body != null && body.isKinematic,
                WasGravity = body != null && body.useGravity
            };

            var spawnPoint = bed.m_spawnPoint;

            npc.transform.position = spawnPoint.position + new Vector3(0f, 0.2f, 0f);
            npc.transform.rotation = spawnPoint.rotation;

            if (body != null)
            {
                // Zero velocity before switching to kinematic to avoid Unity warning
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                body.isKinematic = true;
                body.useGravity = false;
            }

            SetAnimatorSpeed(character, 0f);

            // Tip the visual model -90° around local X axis: standing → lying face-up
            visual.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            Plugin.Log?.LogDebug($"[SleepPose] NPC lying down at bed {spawnPoint.position}");
        }

        /// <summary>
        /// Restore the NPC to standing pose when waking up.
        /// </summary>
        public static void StandUp(MonsterAI npc)
        {
            int id = npc.GetInstanceID();
            if (!s_states.TryGetValue(id, out var state))
                return;

            var character = npc.GetComponent<Character>();
            if (character == null)
            {
                s_states.Remove(id);
                return;
            }

            var visual = s_visualField?.GetValue(character) as GameObject;
            var body = s_bodyField?.GetValue(character) as Rigidbody;

            if (visual != null)
            {
                visual.transform.localRotation = state.VisualLocalRotation;
                visual.transform.localPosition = state.VisualLocalPosition;
            }

            SetAnimatorSpeed(character, state.AnimatorSpeed);

            if (body != null)
            {
                body.isKinematic = state.WasKinematic;
                body.useGravity = state.WasGravity;
            }

            s_states.Remove(id);
            Plugin.Log?.LogDebug("[SleepPose] NPC standing up from bed");
        }

        /// <summary>
        /// Clean up saved state for an NPC (call when unregistered/destroyed).
        /// </summary>
        public static void Cleanup(int instanceId) => s_states.Remove(instanceId);

        private static Bed FindNearestBed(Vector3 position, float maxDistance = 3f)
        {
            Bed nearest = null;
            float bestDist = maxDistance;

            foreach (var bed in Object.FindObjectsByType<Bed>(FindObjectsSortMode.None))
            {
                float dist = Vector3.Distance(bed.transform.position, position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = bed;
                }
            }

            return nearest;
        }

        private static float GetAnimatorSpeed(Character character)
        {
            var animatorObj = s_animatorField?.GetValue(character);
            if (animatorObj == null) return 1f;

            EnsureSpeedProp(animatorObj);
            return (float)(s_animatorSpeedProp?.GetValue(animatorObj, null) ?? 1f);
        }

        private static void SetAnimatorSpeed(Character character, float speed)
        {
            var animatorObj = s_animatorField?.GetValue(character);
            if (animatorObj == null) return;

            EnsureSpeedProp(animatorObj);
            s_animatorSpeedProp?.SetValue(animatorObj, speed, null);
        }

        private static void EnsureSpeedProp(object animator)
        {
            if (s_animatorSpeedProp == null)
                s_animatorSpeedProp = animator.GetType().GetProperty("speed");
        }
    }
}
