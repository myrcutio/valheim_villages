using System;
using System.Collections;
using System.Reflection;
using UnityEngine.AI;

namespace ValheimVillages.Villager.AI.Pathfinding
{
    /// <summary>
    ///     Agent type registration and resolution for Valheim's NavMesh system.
    ///     Registers a custom AgentType (31) as an exact clone of Valheim's
    ///     Humanoid agent settings. The slot exists so the mod can own a
    ///     separately-baked NavMesh surface (see <see cref="Navigation.NavMeshBakeManager" />)
    ///     without competing with Valheim's Humanoid bake. Slope/climb tuning
    ///     matches Humanoid exactly so the only variable when debugging is
    ///     "did our bake produce data."
    /// </summary>
    public static class VillagerAgentType
    {
        /// <summary>
        ///     Index into Pathfinding.m_agentSettings. High enough to avoid collision with
        ///     Valheim's built-in AgentType enum (max value = 13 as of Valheim 0.219).
        /// </summary>
        private const int AgentTypeValue = 31;

        private static int s_humanoidAgentTypeID = -1;

        public static global::Pathfinding.AgentType AgentType =>
            (global::Pathfinding.AgentType)AgentTypeValue;

        /// <summary>
        ///     Whether the custom agent type has been successfully registered with Valheim's
        ///     Pathfinding singleton. When false, villagers fall back to the default Humanoid type.
        /// </summary>
        public static bool IsRegistered { get; private set; }

        /// <summary>
        ///     The Unity NavMesh agentTypeID assigned when settings were created.
        ///     Only valid after <see cref="IsRegistered" /> is true.
        /// </summary>
        public static int UnityAgentTypeID { get; private set; }

        /// <summary>
        ///     The agent's actual build settings — the Humanoid clone stored in
        ///     Valheim's Pathfinding (radius 0.40, etc.), carrying our agentTypeID.
        ///     This is the authoritative source for baking: <see cref="NavMesh.GetSettingsByID" />
        ///     returns Unity's DEFAULT dimensions (radius 0.50), which Valheim never
        ///     syncs — baking with those over-widens the agent and over-erodes the
        ///     walkable surface. Only valid after <see cref="IsRegistered" /> is true.
        /// </summary>
        public static NavMeshBuildSettings BuildSettings { get; private set; }

        /// <summary>
        ///     Maximum walkable slope in degrees, read live from the registered
        ///     agent settings. Returns false if the villager agent slot has not
        ///     been registered yet (Pathfinding singleton not alive) — callers
        ///     MUST handle the false case rather than substituting a default.
        /// </summary>
        public static bool TryGetSlope(out float slope)
        {
            if (!TryGetEffectiveSettings(out var settings))
            {
                slope = 0f;
                return false;
            }

            slope = settings.agentSlope;
            return true;
        }

        /// <summary>
        ///     Maximum climbable step height in meters, read live from the
        ///     registered agent settings. Returns false if the villager agent slot
        ///     has not been registered yet — callers MUST handle the false case
        ///     rather than substituting a default.
        /// </summary>
        public static bool TryGetClimb(out float climb)
        {
            if (!TryGetEffectiveSettings(out var settings))
            {
                climb = 0f;
                return false;
            }

            climb = settings.agentClimb;
            return true;
        }

        /// <summary>
        ///     Returns the live NavMesh build settings for the villager agent slot,
        ///     or false if the slot has not been registered yet. No fallback values
        ///     are ever synthesised — this is the contract that lets pathfinding
        ///     consumers fail loudly during the registration race window instead
        ///     of silently using Unity defaults that don't match the villager
        ///     agent's actual slope/climb (27°/0.3m cloned from Humanoid).
        /// </summary>
        public static bool TryGetEffectiveSettings(out NavMeshBuildSettings settings)
        {
            var id = IsRegistered ? UnityAgentTypeID : ResolveValheimHumanoidAgentTypeID();
            if (id == 0)
            {
                settings = default;
                return false;
            }

            settings = NavMesh.GetSettingsByID(id);
            return true;
        }

        /// <summary>
        ///     Registers the villager agent type with Valheim's Pathfinding singleton by
        ///     calling the private AddAgent method via reflection. Copies Humanoid build
        ///     settings verbatim — no slope/climb override. Safe to call multiple times
        ///     (no-ops after first success).
        /// </summary>
        public static bool EnsureRegistered()
        {
            if (IsRegistered) return true;

            var pf = global::Pathfinding.instance;
            if (pf == null)
            {
                Plugin.Log?.LogWarning("[VillagerAgentType] Pathfinding.instance is null; deferring registration");
                return false;
            }

            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;

                var listField = typeof(global::Pathfinding).GetField("m_agentSettings", flags);
                var addAgentMethod = typeof(global::Pathfinding).GetMethod("AddAgent", flags);
                if (listField == null || addAgentMethod == null)
                {
                    Plugin.Log?.LogError(
                        "[VillagerAgentType] Could not find m_agentSettings or AddAgent via reflection");
                    return false;
                }

                var list = listField.GetValue(pf) as IList;
                if (list == null || list.Count < 2)
                {
                    Plugin.Log?.LogError("[VillagerAgentType] m_agentSettings is null or too small");
                    return false;
                }

                // If re-registering after hot reload, the slot may already exist.
                if (list.Count > AgentTypeValue && list[AgentTypeValue] != null)
                {
                    CaptureAgentSlot(list[AgentTypeValue]);
                    IsRegistered = true;
                    Plugin.Log?.LogInfo(
                        $"[VillagerAgentType] Re-captured agent slot {AgentTypeValue} " +
                        $"(agentTypeID={UnityAgentTypeID}, settings cloned from Humanoid)");
                    return true;
                }

                var humanoidSettings = list[1]; // Pathfinding.AgentType.Humanoid = 1
                var result = addAgentMethod.Invoke(pf, new[]
                {
                    (global::Pathfinding.AgentType)AgentTypeValue,
                    humanoidSettings,
                });

                if (result == null)
                {
                    Plugin.Log?.LogError("[VillagerAgentType] AddAgent returned null");
                    return false;
                }

                CaptureAgentSlot(result);
                IsRegistered = true;

                Plugin.Log?.LogInfo(
                    $"[VillagerAgentType] Registered custom agent type {AgentTypeValue} " +
                    $"(agentTypeID={UnityAgentTypeID}, settings cloned from Humanoid verbatim)");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[VillagerAgentType] Registration failed: {ex}");
                return false;
            }
        }

        /// <summary>
        ///     Capture the Unity NavMesh agentTypeID from a (possibly newly created)
        ///     agent settings object. Does NOT mutate <c>m_build</c> — slot 31's
        ///     settings stay an exact clone of Humanoid.
        /// </summary>
        private static void CaptureAgentSlot(object agentSettings)
        {
            var buildField = agentSettings.GetType().GetField("m_build");
            if (buildField == null) return;
            var build = (NavMeshBuildSettings)buildField.GetValue(agentSettings);
            UnityAgentTypeID = build.agentTypeID;
            BuildSettings = build;
        }

        /// <summary>
        ///     Resolves Valheim's Humanoid agent type ID from the Pathfinding singleton via reflection.
        ///     Caches the result after the first successful lookup. Falls back to 0 if resolution fails.
        ///     Used by patrol/boundary systems that query the humanoid NavMesh (as opposed to the
        ///     villager-specific NavMesh built with <see cref="AgentType" />).
        /// </summary>
        public static int ResolveValheimHumanoidAgentTypeID()
        {
            if (s_humanoidAgentTypeID >= 0) return s_humanoidAgentTypeID;

            try
            {
                var pf = global::Pathfinding.instance;
                if (pf == null) return 0;

                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var listField = typeof(global::Pathfinding).GetField("m_agentSettings", flags);
                if (listField == null) return 0;

                var list = listField.GetValue(pf) as IList;
                if (list == null) return 0;

                var asType = typeof(global::Pathfinding).GetNestedType("AgentSettings",
                    BindingFlags.NonPublic);
                var typeF = asType?.GetField("m_agentType");
                var buildF = asType?.GetField("m_build");
                if (typeF == null || buildF == null) return 0;

                foreach (var ag in list)
                {
                    if (ag == null) continue;
                    if ((int)typeF.GetValue(ag) == 1) // Pathfinding.AgentType.Humanoid
                    {
                        var bs = (NavMeshBuildSettings)buildF.GetValue(ag);
                        s_humanoidAgentTypeID = bs.agentTypeID;
                        return s_humanoidAgentTypeID;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[VillagerAgentType] Failed to resolve Humanoid agentTypeID: {ex.Message}");
            }

            return 0;
        }
    }
}