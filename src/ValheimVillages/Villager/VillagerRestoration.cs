using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Registry;
using ValheimVillages.Villager.Station;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.Villager
{
    /// <summary>
    /// Restores villager state after hot reload or zone load.
    /// Strips native Dvergr components (MonsterAI, NpcTalk, Tameable)
    /// and ensures all mod components are present.
    /// </summary>
    public static class VillagerRestoration
    {
        private static readonly HashSet<string> s_restoredIds = new();

        /// <summary>
        /// Restore villager state from ZDO using VillagerRegistry for definition lookup.
        /// Strips native AI/talk/tame components and ensures all mod components are present.
        /// </summary>
        public static bool Restore(GameObject go, ZDO zdo)
        {
            if (go == null || zdo == null) return false;

            string uniqueId = zdo.GetString("vv_villager_id", "");
            if (string.IsNullOrEmpty(uniqueId)) return false;

            if (s_restoredIds.Contains(uniqueId)) return false;

            string typeStr = zdo.GetString("vv_villager_type", "");
            if (string.IsNullOrEmpty(typeStr))
            {
                Plugin.Log?.LogWarning($"[VillagerRestoration] NPC {uniqueId} has no vv_villager_type, skipping");
                return false;
            }

            var def = VillagerRegistry.Get(typeStr);
            if (def == null)
            {
                Plugin.Log?.LogWarning($"[VillagerRestoration] No definition for type '{typeStr}', skipping");
                return false;
            }

            StripNativeComponents(go);
            RestoreIdentity(go, def);
            RestoreComponents(go, uniqueId, typeStr);
            Dialog.ConfigureDialog(go, def);

            s_restoredIds.Add(uniqueId);

            Plugin.Log?.LogInfo(
                $"[VillagerRestoration] Restored {def.displayName} ({typeStr}) at {go.transform.position}");

            return true;
        }

        [RegisterCleanup]
        public static void ClearTracking()
        {
            s_restoredIds.Clear();
        }

        /// <summary>
        /// Remove native Dvergr prefab components that conflict with our AI/talk/interaction.
        /// Safe to call even if they've already been destroyed (e.g. after initial spawn).
        /// </summary>
        private static void StripNativeComponents(GameObject go)
        {
            var monsterAI = go.GetComponent<MonsterAI>();
            if (monsterAI != null)
                Object.DestroyImmediate(monsterAI);

            var npcTalk = go.GetComponent<NpcTalk>();
            if (npcTalk != null)
                Object.DestroyImmediate(npcTalk);

            var tameable = go.GetComponent<Tameable>();
            if (tameable != null)
                Object.DestroyImmediate(tameable);
        }

        private static void RestoreIdentity(GameObject go, VillagerDef definition)
        {
            var humanoid = go.GetComponent<Humanoid>();
            if (humanoid != null && !string.IsNullOrEmpty(definition?.displayName))
                humanoid.m_name = definition.displayName;

            var character = go.GetComponent<Character>();
            if (character != null)
                character.m_faction = Character.Faction.Players;
        }

        /// <summary>
        /// Ensure all mod components are present on the NPC GameObject.
        /// Idempotent -- skips components that already exist.
        /// </summary>
        private static void RestoreComponents(GameObject go, string uniqueId, string typeStr)
        {
            if (go.GetComponent<Villager>() == null)
                go.AddComponent<Villager>();

            if (go.GetComponent<VillagerTalk>() == null)
                go.AddComponent<VillagerTalk>();

            if (go.GetComponent<DoorHandler>() == null)
                go.AddComponent<DoorHandler>();

            var bridge = go.GetComponent<VillagerBehaviorBridge>();
            if (bridge == null)
            {
                bridge = go.AddComponent<VillagerBehaviorBridge>();
                bridge.villagerInstance = go.GetComponent<Villager>();
                bridge.Initialize(uniqueId);
            }

            if (go.GetComponent<VillagerInteract>() == null)
                go.AddComponent<VillagerInteract>();

            if (go.GetComponent<VillagerStation>() == null)
            {
                var vs = go.AddComponent<VillagerStation>();
                vs.Initialize(typeStr);
            }
        }
    }
}
