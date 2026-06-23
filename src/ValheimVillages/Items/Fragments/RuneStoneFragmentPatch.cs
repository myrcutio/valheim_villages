using HarmonyLib;
using UnityEngine;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Grants a biome-matched ransom fragment the first time each player reads a given
    ///     runestone — both RuneStone lore stones and Vegvisir map stones — making runestones a
    ///     reliable reason to explore. One-time per player per stone (a per-player unique key
    ///     keyed to the stone's position), so re-reading can't farm fragments. Interact runs
    ///     only on the interacting player's client, so the grant lands on the right inventory
    ///     with no cross-peer duplication.
    /// </summary>
    [HarmonyPatch]
    public static class RuneStoneFragmentPatch
    {
        [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.Interact))]
        [HarmonyPostfix]
        public static void RuneStonePostfix(RuneStone __instance, Humanoid character, bool hold)
        {
            TryGrant(__instance.transform.position, character, hold);
        }

        [HarmonyPatch(typeof(Vegvisir), nameof(Vegvisir.Interact))]
        [HarmonyPostfix]
        public static void VegvisirPostfix(Vegvisir __instance, Humanoid character, bool hold)
        {
            TryGrant(__instance.transform.position, character, hold);
        }

        private static void TryGrant(Vector3 stonePos, Humanoid character, bool hold)
        {
            if (hold) return; // a press, not a hold (mirrors the stone's own Interact guard)
            if (character is not Player player) return;

            var key = UniqueKey(stonePos);
            if (player.HaveUniqueKey(key)) return; // this player already claimed this stone's fragment

            var fragmentName = BiomeFragments.NameForPosition(stonePos);
            if (fragmentName == null) return; // biome not mapped to a fragment (or world not ready)

            var prefab = BiomeFragments.Prefab(fragmentName);
            if (prefab == null) return;

            var inv = player.GetInventory();
            if (inv == null) return;

            if (!inv.AddItem(prefab, 1))
            {
                // Don't claim the key on a full inventory, so the player can come back for it.
                player.Message(MessageHud.MessageType.Center,
                    "A ransom fragment is wedged here, but your inventory is full.");
                return;
            }

            player.AddUniqueKey(key);
            player.Message(MessageHud.MessageType.Center, "You pry a ransom fragment loose from the runestone.");
        }

        private static string UniqueKey(Vector3 pos)
        {
            return $"vv_runefrag_{Mathf.RoundToInt(pos.x)}_{Mathf.RoundToInt(pos.y)}_{Mathf.RoundToInt(pos.z)}";
        }
    }
}
