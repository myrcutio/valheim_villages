using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.NPCs.AI;
using ValheimVillages.NPCs.AI.Work;

namespace ValheimVillages.NPCs
{
    /// <summary>
    /// Handles using pawn items to spawn villagers at unclaimed beds.
    /// Supports vv_pawn (random type), vv_guard_pawn, and vv_mountaineer_pawn.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
    public static class VillagerPawnPatch
    {
        private static readonly string[] DvergrPrefabs = {
            "Dverger",
            "DvergerMage",
            "DvergerMageFire",
            "DvergerMageIce"
        };

        /// <summary>Map of pawn item names to their forced NPC type (null = random).</summary>
        private static readonly Dictionary<string, NpcType?> PawnItems = new()
        {
            { "vv_pawn", null },
            { "vv_farmer_pawn", NpcType.Farmer },
            { "vv_miner_pawn", NpcType.Miner },
            { "vv_blacksmith_pawn", NpcType.Blacksmith },
            { "vv_carpenter_pawn", NpcType.Carpenter },
            { "vv_scout_pawn", NpcType.Scout },
            { "vv_trader_pawn", NpcType.Trader },
            { "vv_guard_pawn", NpcType.Guard },
            { "vv_mountaineer_pawn", NpcType.Mountaineer },
            { "vv_shipwright_pawn", NpcType.Shipwright },
            { "vv_tavernkeeper_pawn", NpcType.TavernKeeper }
        };

        /// <summary>
        /// Logs all prefabs containing "dvergr" (case insensitive) for debugging.
        /// </summary>
        public static void LogAvailableDvergrPrefabs()
        {
            if (ZNetScene.instance == null)
            {
                Plugin.Log?.LogWarning("ZNetScene not available for prefab listing");
                return;
            }

            var dvergrPrefabs = ZNetScene.instance.m_prefabs
                .Where(p => p != null && p.name.IndexOf("dvergr", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => p.name)
                .OrderBy(n => n)
                .ToList();

            Plugin.Log?.LogInfo($"Found {dvergrPrefabs.Count} Dvergr prefabs:");
            foreach (var name in dvergrPrefabs)
            {
                Plugin.Log?.LogInfo($"  - {name}");
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui)
        {
            // Only works for players
            if (__instance is not Player player)
                return true;

            // Only intercept our pawn items
            string itemName = item?.m_dropPrefab?.name;
            if (itemName == null || !PawnItems.TryGetValue(itemName, out var forcedType))
                return true;

            // Must be looking at something
            var hoverObject = player.GetHoverObject();
            if (hoverObject == null)
                return true;

            // Check if it's a bed
            var bed = hoverObject.GetComponentInParent<Bed>();
            if (bed == null)
                return true;

            // Check if the bed is unclaimed (owner stored in ZDO)
            var bedZdoId = bed.GetComponent<ZNetView>()?.GetZDO()?.GetLong("owner", 0L) ?? 0L;
            if (bedZdoId != 0L)
            {
                player.Message(MessageHud.MessageType.Center, "This bed is already claimed");
                return false;
            }

            // Spawn a villager NPC at the bed (handles bed assignment and item consumption)
            SpawnVillagerAtBed(player, bed, item, inventory, forcedType);

            return false;
        }

        private static bool SpawnVillagerAtBed(Player player, Bed bed, ItemDrop.ItemData item, Inventory inventory, NpcType? forcedType = null)
        {
            // Use forced type or pick random
            NpcType chosenType;
            if (forcedType.HasValue)
            {
                chosenType = forcedType.Value;
            }
            else
            {
                var npcTypes = Enum.GetValues(typeof(NpcType)).Cast<NpcType>().ToArray();
                chosenType = npcTypes[UnityEngine.Random.Range(0, npcTypes.Length)];
            }
            var definition = NpcTypeRegistry.Get(chosenType);

            if (definition == null)
            {
                Plugin.Log?.LogError($"Failed to get definition for NPC type {chosenType}");
                return false;
            }

            // Pick a dvergr prefab based on NPC definition (preferredPrefab or random)
            var prefabName = NpcEquipment.SelectPrefab(definition, DvergrPrefabs);
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);

            if (prefab == null)
            {
                Plugin.Log?.LogError($"Failed to find prefab {prefabName}");
                player.Message(MessageHud.MessageType.Center, "Failed to spawn villager (prefab not found)");
                return false;
            }

            // Spawn position at the bed
            var spawnPos = bed.transform.position + Vector3.up * 0.5f;
            var spawnRot = bed.transform.rotation;

            // Instantiate the NPC
            var npcObject = UnityEngine.Object.Instantiate(prefab, spawnPos, spawnRot);
            if (npcObject == null)
            {
                Plugin.Log?.LogError("Failed to instantiate NPC");
                return false;
            }

            // Set up the NPC
            var humanoid = npcObject.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                humanoid.m_name = definition.displayName;

                var character = npcObject.GetComponent<Character>();
                if (character != null)
                    character.m_faction = Character.Faction.Players;

                // Configure equipment from JSON definition before Humanoid.Start() runs
                NpcEquipment.Configure(humanoid, definition);

                // Apply skin color (persists via ZDO)
                var visEquip = npcObject.GetComponent<VisEquipment>();
                NpcEquipment.ApplySkinColor(visEquip, definition);

                // Apply weapon rotation fix if defined (corrects Dverger hand bone orientation)
                NpcEquipment.ApplyWeaponRotationFix(npcObject, definition);
            }

            // Override Dverger dialog lines with type-specific lines from JSON
            NpcEquipment.ConfigureDialog(npcObject, definition);

            // Set up MonsterAI to be passive and never turn hostile against players
            var monsterAI = npcObject.GetComponent<MonsterAI>();
            if (monsterAI != null)
            {
                // Set their spawn point as patrol center
                monsterAI.SetPatrolPoint();

                // Configure passive behavior, wandering, and environmental awareness
                VillagerRestoration.ConfigurePassiveAI(monsterAI);
            }
            
            // Mark as tamed via ZDO so they don't become hostile
            var characterZnetView = npcObject.GetComponent<ZNetView>();
            if (characterZnetView != null && characterZnetView.GetZDO() != null)
            {
                characterZnetView.GetZDO().Set("tame", true);
                characterZnetView.GetZDO().Set("TamedTime", ZNet.instance.GetTime().Ticks);
            }
            
            // Add door handler component for pathfinding through doors
            npcObject.AddComponent<DoorHandler>();
            
            // Add bridge component for UI access to AI (must be added before VillagerInteract)
            var bridge = npcObject.AddComponent<VillagerBehaviorBridge>();
            
            // Add interaction component for dialog menu
            npcObject.AddComponent<VillagerInteract>();

            npcObject.AddComponent<VillagerLookBehavior>();

            // Add virtual station for the crafting/tab UI panel
            var villagerStation = npcObject.AddComponent<VillagerStation>();
            villagerStation.Initialize(chosenType);

            // Get NPC's ZNetView for bed assignment and NPC data storage
            var npcZnetView = npcObject.GetComponent<ZNetView>();
            if (npcZnetView != null && npcZnetView.GetZDO() != null)
            {
                var npcZdoId = npcZnetView.GetZDO().m_uid;
                
                // Generate a unique ID for the villager AI system
                string uniqueId = System.Guid.NewGuid().ToString();
                npcZnetView.GetZDO().Set("vv_villager_id", uniqueId);
                
                // Store the NPC type in ZDO for persistence
                npcZnetView.GetZDO().Set("vv_npc_type", (int)chosenType);
                
                // Store bed position for AI initialization
                npcZnetView.GetZDO().Set("vv_bed_position", bed.transform.position);
                
                // Register with the AI manager (AI instance created on first UpdateAI tick)
                VillagerAIManager.Register(uniqueId, bed.transform.position);
                bridge.Initialize(uniqueId);
                
                // Store the bed's ZDO ID on the NPC for reference
                var bedZnetView = bed.GetComponent<ZNetView>();
                if (bedZnetView != null && bedZnetView.GetZDO() != null)
                {
                    var bedZdoId = bedZnetView.GetZDO().m_uid;
                    npcZnetView.GetZDO().Set("vv_assigned_bed", bedZdoId != ZDOID.None ? bedZdoId.ID : 0L);
                    
                    // Assign the NPC as the owner of the bed using Valheim's bed ownership system
                    bedZnetView.GetZDO().Set("owner", npcZdoId != ZDOID.None ? npcZdoId.ID : 0L);
                    bedZnetView.GetZDO().Set("ownerName", definition.displayName);
                    Plugin.Log?.LogInfo($"Assigned bed to NPC '{definition.displayName}' with ZDO ID: {npcZdoId}");
                }
            }

            // Consume the pawn item from player's inventory
            try
            {
                var playerInventory = player.GetInventory();
                if (playerInventory != null && item != null)
                {
                    if (playerInventory.RemoveOneItem(item))
                    {
                        Plugin.Log?.LogInfo("Consumed vv_pawn item from inventory");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("Failed to remove vv_pawn item from inventory");
                    }
                }
                else
                {
                    Plugin.Log?.LogWarning($"Cannot remove item: playerInventory={playerInventory != null}, item={item != null}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Exception while removing item: {ex.Message}");
            }

            Plugin.Log?.LogInfo($"Spawned {definition.displayName} ({definition.category}) at bed");
            player.Message(MessageHud.MessageType.Center, $"Assigned {definition.displayName} to bed");

            return true;
        }

    }
}
