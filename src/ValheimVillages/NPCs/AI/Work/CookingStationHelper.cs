using System;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Uses reflection to call CookingStation and ZNetView methods so the farmer
    /// can add raw food, wait for it to cook, and collect the result (real station visuals).
    /// Valheim's CookingStation has GetFreeSlot, GetSlot, GetItemConversion, m_slots as private;
    /// reflection is required. Members are cached in static fields to avoid repeated GetMethod/GetField in hot paths.
    /// </summary>
    public static class CookingStationHelper
    {
        public const int StatusNotDone = 0;
        public const int StatusDone = 1;
        public const int StatusBurnt = 2;

        private const BindingFlags InstanceAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static MethodInfo s_getFreeSlot;
        private static MethodInfo s_getSlot;
        private static MethodInfo s_getItemConversion;
        private static FieldInfo s_mSlots;
        private static FieldInfo s_itemConversionCookTime;
        private static MethodInfo s_rpcRemoveDoneItem;

        private static void EnsureCached(Type stationType)
        {
            if (s_getFreeSlot != null) return;
            s_getFreeSlot = stationType.GetMethod("GetFreeSlot", InstanceAny);
            s_getSlot = stationType.GetMethod("GetSlot", InstanceAny);
            s_getItemConversion = stationType.GetMethod("GetItemConversion", InstanceAny, null, new[] { typeof(string) }, null);
            s_mSlots = stationType.GetField("m_slots", InstanceAny);
            s_rpcRemoveDoneItem = stationType.GetMethod("RPC_RemoveDoneItem", InstanceAny, null, new[] { typeof(long), typeof(Vector3), typeof(int) }, null);
            var itemConversionType = stationType.GetNestedType("ItemConversion", BindingFlags.Public | BindingFlags.NonPublic);
            s_itemConversionCookTime = itemConversionType?.GetField("m_cookTime", InstanceAny);
        }

        /// <summary>Returns a free slot index (0-based) or -1 if full.</summary>
        public static int GetFreeSlot(Component station)
        {
            if (station == null) return -1;
            EnsureCached(station.GetType());
            if (s_getFreeSlot == null) return -1;
            var r = s_getFreeSlot.Invoke(station, null);
            return r is int i ? i : -1;
        }

        /// <summary>Gets slot contents: itemName (prefab name), cookedTime (0..cookTime), status (0=NotDone, 1=Done, 2=Burnt).</summary>
        public static void GetSlot(Component station, int slot, out string itemName, out float cookedTime, out int status)
        {
            itemName = "";
            cookedTime = 0f;
            status = StatusNotDone;
            if (station == null) return;
            EnsureCached(station.GetType());
            if (s_getSlot == null) return;
            var args = new object[] { slot, null, null, null };
            s_getSlot.Invoke(station, args);
            itemName = args[1] as string ?? "";
            cookedTime = args[2] is float t ? t : 0f;
            status = args[3] != null && args[3].GetType().IsEnum ? (int)args[3] : StatusNotDone;
        }

        /// <summary>Cook time in seconds for an input item (e.g. RawMeat). Returns 0 if unknown.</summary>
        public static float GetCookTime(Component station, string inputItemName)
        {
            if (station == null || string.IsNullOrEmpty(inputItemName)) return 0f;
            EnsureCached(station.GetType());
            if (s_getItemConversion == null) return 0f;
            var conversion = s_getItemConversion.Invoke(station, new object[] { inputItemName });
            if (conversion == null || s_itemConversionCookTime == null) return 0f;
            return s_itemConversionCookTime.GetValue(conversion) is float t ? t : 0f;
        }

        /// <summary>Number of slots on the station.</summary>
        public static int GetSlotCount(Component station)
        {
            if (station == null) return 0;
            EnsureCached(station.GetType());
            if (s_mSlots == null) return 0;
            var arr = s_mSlots.GetValue(station) as Array;
            return arr?.Length ?? 0;
        }

        /// <summary>Add raw item to the station (finds free slot and invokes RPC_AddItem).
        /// Matches player flow: CookItem passes only (itemName) to InvokeRPC and claims ownership if needed.</summary>
        public static bool AddItem(Component station, string itemPrefabName)
        {
            if (station == null || string.IsNullOrEmpty(itemPrefabName)) return false;
            if (GetFreeSlot(station) < 0) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            // Player path (CookItem) claims ownership when station has no owner, then InvokeRPC with single param (item name)
            if (!nview.HasOwner())
                nview.ClaimOwnership();
            nview.InvokeRPC("RPC_AddItem", new object[] { itemPrefabName });
            return true;
        }

        /// <summary>Remove one done item from the station; spawns it in the world.
        /// Matches player flow: player calls InvokeRPC with (userPoint, amount) only.
        /// When we own the station we call RPC_RemoveDoneItem directly so it runs once (avoids duplicate spawns from RPC broadcast).</summary>
        public static bool RemoveDoneItem(Component station, Vector3 spawnAt, int amount = 1)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            // Same as player taking food: run the remove logic once. When we own the station, call the method directly
            // so it executes locally once (no RPC broadcast that can run on multiple peers and spawn multiple items).
            if (nview.IsOwner())
            {
                EnsureCached(station.GetType());
                if (s_rpcRemoveDoneItem != null)
                {
                    s_rpcRemoveDoneItem.Invoke(station, new object[] { 0L, spawnAt, amount });
                    return true;
                }
            }

            // Fallback: invoke RPC with same 2 args the player uses (position, amount); sender is injected by the engine.
            nview.InvokeRPC("RPC_RemoveDoneItem", new object[] { spawnAt, amount });
            return true;
        }
    }
}
