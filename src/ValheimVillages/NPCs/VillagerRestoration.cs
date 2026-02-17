using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs.AI;
using ValheimVillages.NPCs.AI.Work;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.NPCs
{
    /// <summary>
    /// Restores villager runtime state from ZDO data after a game reload.
    /// On reload, NPCs lose their custom components, name, faction, equipment config,
    /// dialog lines, and passive AI settings. This class rehydrates all of that
    /// using the NPC type stored in the ZDO to look up the JSON definition.
    /// </summary>
    public static class VillagerRestoration
    {
        /// <summary>Track which NPCs have already been restored to prevent double-restoration.</summary>
        private static readonly HashSet<string> s_restoredIds = new();

        /// <summary>
        /// Restore all runtime state for a villager NPC that was loaded from a save.
        /// Reads the NPC type from ZDO, looks up the JSON definition, and reapplies:
        /// name, faction, equipment config, dialog, passive AI, and custom components.
        /// </summary>
        /// <param name="monsterAI">The MonsterAI component on the NPC GameObject.</param>
        /// <param name="zdo">The NPC's ZDO containing persisted villager data.</param>
        /// <returns>True if restoration was performed, false if skipped.</returns>
        public static bool Restore(MonsterAI monsterAI, ZDO zdo)
        {
            if (monsterAI == null || zdo == null) return false;

            string uniqueId = zdo.GetString("vv_villager_id", "");
            if (string.IsNullOrEmpty(uniqueId)) return false;

            // Skip if already restored this session
            if (s_restoredIds.Contains(uniqueId)) return false;

            int typeInt = zdo.GetInt("vv_npc_type", -1);
            if (typeInt < 0)
            {
                Plugin.Log?.LogWarning($"[Restore] NPC {uniqueId} has no vv_npc_type, skipping");
                return false;
            }

            var npcType = (NpcType)typeInt;
            var definition = NpcTypeRegistry.Get(npcType);
            if (definition == null)
            {
                Plugin.Log?.LogWarning($"[Restore] No definition found for NPC type {npcType}, skipping");
                return false;
            }

            var go = monsterAI.gameObject;

            // Restore name and faction
            RestoreIdentity(go, definition);

            // Restore equipment configuration (clears random arrays, sets default items)
            RestoreEquipment(go, definition);

            // Restore dialog lines
            NpcEquipment.ConfigureDialog(go, definition);

            // Restore weapon rotation fix
            RestoreWeaponRotationFix(go, definition);

            // Restore passive MonsterAI settings
            ConfigurePassiveAI(monsterAI);

            // Add missing custom components
            RestoreComponents(go, uniqueId, npcType);

            s_restoredIds.Add(uniqueId);

            Plugin.Log?.LogInfo(
                $"[Restore] Restored {definition.displayName} ({npcType}) at {go.transform.position}");

            return true;
        }

        /// <summary>
        /// Configure MonsterAI for passive, player-friendly behavior.
        /// Shared between initial spawn (VillagerPawnPatch) and restoration.
        /// </summary>
        public static void ConfigurePassiveAI(MonsterAI ai)
        {
            if (ai == null) return;

            // Passive behavior - never attack players
            ai.m_attackPlayerObjects = false;
            ai.m_aggravatable = false;
            ai.m_passiveAggresive = false;

            // Wandering behavior (VillagerAI takes over via Harmony patch)
            ai.m_randomMoveInterval = 10f;
            ai.m_randomMoveRange = 10f;
            ai.m_smoothMovement = true;

            // Environmental awareness
            ai.m_avoidFire = true;
            ai.m_afraidOfFire = false;
            ai.m_avoidWater = true;
        }

        /// <summary>
        /// Add missing custom MonoBehaviour components to the NPC GameObject.
        /// Safe to call multiple times; checks for existing components.
        /// </summary>
        public static void RestoreComponents(GameObject go, string uniqueId, NpcType npcType)
        {
            if (go.GetComponent<DoorHandler>() == null)
                go.AddComponent<DoorHandler>();

            var bridge = go.GetComponent<VillagerBehaviorBridge>();
            if (bridge == null)
            {
                bridge = go.AddComponent<VillagerBehaviorBridge>();
                bridge.Initialize(uniqueId);
            }

            if (go.GetComponent<VillagerInteract>() == null)
                go.AddComponent<VillagerInteract>();

            if (go.GetComponent<VillagerLookBehavior>() == null)
                go.AddComponent<VillagerLookBehavior>();

            // Add virtual crafting station for NPC types that support it
            if (go.GetComponent<VillagerStation>() == null &&
                VillagerStation.HasVirtualStation(npcType))
            {
                var vs = go.AddComponent<VillagerStation>();
                vs.Initialize(npcType);
            }
        }

        /// <summary>
        /// Clear the restored-IDs tracking set. Called on hot reload
        /// so NPCs can be re-restored with fresh component types.
        /// </summary>
        [RegisterCleanup]
        public static void ClearTracking()
        {
            s_restoredIds.Clear();
        }

        private static void RestoreIdentity(GameObject go, NpcTypeDefinition definition)
        {
            var humanoid = go.GetComponent<Humanoid>();
            if (humanoid != null)
                humanoid.m_name = definition.displayName;

            var character = go.GetComponent<Character>();
            if (character != null)
                character.m_faction = Character.Faction.Players;
        }

        private static void RestoreEquipment(GameObject go, NpcTypeDefinition definition)
        {
            var humanoid = go.GetComponent<Humanoid>();
            if (humanoid == null) return;

            // Clear random equipment arrays so the Dvergr doesn't re-roll defaults
            NpcEquipment.Configure(humanoid, definition);
        }

        private static void RestoreWeaponRotationFix(GameObject go, NpcTypeDefinition definition)
        {
            // Only add if not already present and definition specifies one
            if (string.IsNullOrEmpty(definition?.weaponRotationFix)) return;
            if (go.GetComponent<NpcVisualFix>() != null) return;

            NpcEquipment.ApplyWeaponRotationFix(go, definition);
        }
    }
}
