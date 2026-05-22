using System.Collections;
using System.Reflection;
using UnityEngine.AI;

namespace ValheimVillages.Villager.AI.Pathfinding
{
    /// <summary>
    /// Agent type registration and resolution for Valheim's NavMesh system.
    /// Registers a custom AgentType (31) as an exact clone of Valheim's
    /// Humanoid agent settings. The slot exists so the mod can own a
    /// separately-baked NavMesh surface (see <see cref="Navigation.NavMeshBakeManager"/>)
    /// without competing with Valheim's Humanoid bake. Slope/climb tuning
    /// matches Humanoid exactly so the only variable when debugging is
    /// "did our bake produce data."
    /// </summary>
    public static class VillagerAgentType
    {
        private static int s_humanoidAgentTypeID = -1;
        /// <summary>
        /// Index into Pathfinding.m_agentSettings. High enough to avoid collision with
        /// Valheim's built-in AgentType enum (max value = 13 as of Valheim 0.219).
        /// </summary>
        private const int AgentTypeValue = 31;

        private static bool s_registered;
        private static int s_unityAgentTypeID;

        public static global::Pathfinding.AgentType AgentType =>
            (global::Pathfinding.AgentType)AgentTypeValue;

        /// <summary>
        /// Whether the custom agent type has been successfully registered with Valheim's
        /// Pathfinding singleton. When false, villagers fall back to the default Humanoid type.
        /// </summary>
        public static bool IsRegistered => s_registered;

        /// <summary>
        /// The Unity NavMesh agentTypeID assigned when settings were created.
        /// Only valid after <see cref="IsRegistered"/> is true.
        /// </summary>
        public static int UnityAgentTypeID => s_unityAgentTypeID;

        /// <summary>
        /// Maximum walkable slope in degrees, read live from the registered
        /// agent settings (or Humanoid as fallback before registration).
        /// </summary>
        public static float Slope => GetEffectiveSettings().agentSlope;

        /// <summary>
        /// Maximum climbable step height in meters, read live from the
        /// registered agent settings (or Humanoid as fallback).
        /// </summary>
        public static float Climb => GetEffectiveSettings().agentClimb;

        private static NavMeshBuildSettings GetEffectiveSettings()
        {
            int id = s_registered ? s_unityAgentTypeID : ResolveValheimHumanoidAgentTypeID();
            if (id == 0)
            {
                // Pathfinding singleton not yet available — Unity defaults are safe
                // because the actual queries that consume these values don't run
                // until well after Pathfinding initialization.
                return new NavMeshBuildSettings { agentSlope = 45f, agentClimb = 0.4f };
            }
            return NavMesh.GetSettingsByID(id);
        }

        /// <summary>
        /// Registers the villager agent type with Valheim's Pathfinding singleton by
        /// calling the private AddAgent method via reflection. Copies Humanoid build
        /// settings verbatim — no slope/climb override. Safe to call multiple times
        /// (no-ops after first success).
        /// </summary>
        public static bool EnsureRegistered()
        {
            if (s_registered) return true;

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
                    Plugin.Log?.LogError("[VillagerAgentType] Could not find m_agentSettings or AddAgent via reflection");
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
                    s_registered = true;
                    Plugin.Log?.LogInfo(
                        $"[VillagerAgentType] Re-captured agent slot {AgentTypeValue} " +
                        $"(agentTypeID={s_unityAgentTypeID}, settings cloned from Humanoid)");
                    return true;
                }

                var humanoidSettings = list[1]; // Pathfinding.AgentType.Humanoid = 1
                var result = addAgentMethod.Invoke(pf, new object[]
                {
                    (global::Pathfinding.AgentType)AgentTypeValue,
                    humanoidSettings
                });

                if (result == null)
                {
                    Plugin.Log?.LogError("[VillagerAgentType] AddAgent returned null");
                    return false;
                }

                CaptureAgentSlot(result);
                s_registered = true;

                Plugin.Log?.LogInfo(
                    $"[VillagerAgentType] Registered custom agent type {AgentTypeValue} " +
                    $"(agentTypeID={s_unityAgentTypeID}, settings cloned from Humanoid verbatim)");
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogError($"[VillagerAgentType] Registration failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Capture the Unity NavMesh agentTypeID from a (possibly newly created)
        /// agent settings object. Does NOT mutate <c>m_build</c> — slot 31's
        /// settings stay an exact clone of Humanoid.
        /// </summary>
        private static void CaptureAgentSlot(object agentSettings)
        {
            var buildField = agentSettings.GetType().GetField("m_build");
            if (buildField == null) return;
            var build = (NavMeshBuildSettings)buildField.GetValue(agentSettings);
            s_unityAgentTypeID = build.agentTypeID;
        }

        /// <summary>
        /// Resolves Valheim's Humanoid agent type ID from the Pathfinding singleton via reflection.
        /// Caches the result after the first successful lookup. Falls back to 0 if resolution fails.
        /// Used by patrol/boundary systems that query the humanoid NavMesh (as opposed to the
        /// villager-specific NavMesh built with <see cref="AgentType"/>).
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
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[VillagerAgentType] Failed to resolve Humanoid agentTypeID: {ex.Message}");
            }

            return 0;
        }
    }
}
