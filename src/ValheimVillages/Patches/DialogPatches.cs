using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Patches to support the villager dialog menu:
    /// - Redirect hover text for villagers to VillagerInteract
    /// - Tell game systems a menu is visible (for cursor unlock)
    /// </summary>
    public static class DialogPatches
    {
        // Note: We don't block Player.TakeInput - player can move with WASD while menu is open
        // (like the inventory screen). Camera look is blocked via Menu.IsVisible patch.

        /// <summary>
        /// Patch Character.GetHoverText to use VillagerInteract's hover text for villagers.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
        public static class Character_GetHoverText_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(Character __instance, ref string __result)
            {
                // Check if this character has our VillagerInteract component
                var villagerInteract = __instance.GetComponent<VillagerInteract>();
                if (villagerInteract != null)
                {
                    // Use our custom hover text instead
                    __result = villagerInteract.GetHoverText();
                    return false; // Skip original method
                }
                return true; // Continue to original method
            }
        }

        /// <summary>
        /// Patch Tameable.Interact to skip the native pet/command interaction for villagers.
        /// Without this, pressing E fires both Tameable.Interact (pet effect, follow/stay)
        /// and our VillagerInteract.Interact (dialog menu) since both implement Interactable.
        /// </summary>
        [HarmonyPatch(typeof(Tameable), nameof(Tameable.Interact))]
        public static class Tameable_Interact_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(Tameable __instance, ref bool __result)
            {
                if (__instance.GetComponent<VillagerInteract>() != null)
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Patch TextInput.IsVisible to return true when our dialog is open.
        /// This tells the game's camera system that text input is active.
        /// </summary>
        [HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
        public static class TextInput_IsVisible_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ref bool __result)
            {
                if (VillagerDialogMenu.IsVisible)
                {
                    __result = true;
                }
            }
        }

        /// <summary>
        /// Patch Menu.IsVisible to return true when our dialog is open.
        /// This tells various game systems that a menu is active.
        /// </summary>
        [HarmonyPatch(typeof(Menu), nameof(Menu.IsVisible))]
        public static class Menu_IsVisible_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ref bool __result)
            {
                if (VillagerDialogMenu.IsVisible)
                {
                    __result = true;
                }
            }
        }

        /// <summary>
        /// Patch InventoryGui.Hide to resume the NPC when the crafting UI is closed.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        public static class InventoryGui_Hide_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                VillagerInteract.OnCraftingUIClosed();
            }
        }

        #region Sleep Dialog Patches

        private static readonly List<string> s_sleepTalkLines = new()
        {
            "...zzzzz...",
            "...zzzz...",
            "zzz...",
            "...zzz...zzz...",
            "...zzzzzzzz..."
        };

        private static readonly MethodInfo s_queueSayMethod =
            typeof(NpcTalk).GetMethod("QueueSay", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Check if an NpcTalk instance belongs to a sleeping villager.
        /// </summary>
        private static bool IsVillagerSleeping(NpcTalk npcTalk)
        {
            var monsterAI = npcTalk.GetComponent<MonsterAI>();
            if (monsterAI == null) return false;
            // Only apply to our villagers
            if (npcTalk.GetComponent<VillagerInteract>() == null) return false;
            return monsterAI.IsSleeping();
        }

        /// <summary>
        /// Patch NpcTalk.RandomTalk to emit "...zzzzz..." instead of normal dialog when sleeping.
        /// RandomTalk is invoked periodically via InvokeRepeating.
        /// </summary>
        [HarmonyPatch(typeof(NpcTalk), "RandomTalk")]
        public static class NpcTalk_RandomTalk_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(NpcTalk __instance)
            {
                if (!IsVillagerSleeping(__instance)) return true;

                // 30% chance to mutter in sleep each tick
                if (Random.value < 0.3f && s_queueSayMethod != null)
                {
                    s_queueSayMethod.Invoke(__instance,
                        new object[] { s_sleepTalkLines, "Sleep", null });
                }
                return false; // Skip normal random talk
            }
        }

        /// <summary>
        /// Patch NpcTalk.Update to suppress greet/goodbye when sleeping,
        /// while still processing queued zzz messages.
        /// </summary>
        [HarmonyPatch(typeof(NpcTalk), "Update")]
        public static class NpcTalk_Update_Patch
        {
            private static readonly MethodInfo s_updateSayQueueMethod =
                typeof(NpcTalk).GetMethod("UpdateSayQueue", BindingFlags.NonPublic | BindingFlags.Instance);

            [HarmonyPrefix]
            public static bool Prefix(NpcTalk __instance)
            {
                if (!IsVillagerSleeping(__instance)) return true;

                // Still process queued text (zzz messages) but skip greet/goodbye logic
                s_updateSayQueueMethod?.Invoke(__instance, null);
                return false;
            }
        }

        #endregion
    }
}
