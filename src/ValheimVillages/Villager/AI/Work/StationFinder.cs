using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Shared logic for resolving crafting/cooking stations from an NPC's known locations.
    ///     Used by WorkOrderScanHandler and available for CraftingBehavior if needed.
    /// </summary>
    public static class StationFinder
    {
        private const float StationLookupRadius = 2f;

        private static readonly MethodInfo s_isFireLit = typeof(CookingStation)
            .GetMethod("IsFireLit", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo s_getFuel = typeof(CookingStation)
            .GetMethod("GetFuel", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo s_smelterGetFuel = typeof(Smelter)
            .GetMethod("GetFuel", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        ///     Raw call to CookingStation.IsFireLit() via reflection.
        ///     Returns false if reflection fails or fire is not lit.
        /// </summary>
        private static bool InvokeIsFireLit(CookingStation station)
        {
            if (station == null || s_isFireLit == null) return false;
            try
            {
                return (bool)s_isFireLit.Invoke(station, null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Returns true when the fire-underneath requirement is satisfied:
        ///     either fire is not required, or it is required and IsFireLit() returns true.
        ///     Used by StationFuelHelper.DiagnoseFuelNeed to check the fire condition independently.
        /// </summary>
        /// <summary>Radius to look for the Fireplace heating a cooking station.</summary>
        private const float FireplaceProbeRadius = 3f;

        private static readonly Collider[] s_fireProbe = new Collider[16];

        public static bool IsCookingStationFireLit(CookingStation station)
        {
            if (station == null) return false;
            if (!station.m_requireFire) return true;

            // Prefer the fireplace's OWN state over CookingStation.IsFireLit().
            //
            // IsFireLit() only asks "is there a Burning EffectArea at my fire-check points",
            // and that area outlives the flame: a fire_pit at fuel=0 with IsBurning()=false
            // still reads as lit. The engine's UpdateCooking uses the same check, so vanilla
            // will happily cook food over a dead fire — observed directly: fireLit=True while
            // the adjacent fire_pit reported IsBurning=False fuel=0.00.
            //
            // Fireplace.IsBurning() reads state/fuel off the fireplace ZDO, which is what the
            // player sees. Deferring to it keeps villagers honest: they will not load or wait
            // on a fire that is actually out, and will fetch fuel instead.
            if (TryFindHeatSource(station, out var fireplace))
                return fireplace.IsBurning();

            // No Fireplace found — the heat may come from something else entirely, so fall
            // back to the engine's own answer rather than declaring the station cold.
            return InvokeIsFireLit(station);
        }

        /// <summary>Nearest Fireplace to the station, if one is heating it.</summary>
        private static bool TryFindHeatSource(CookingStation station, out Fireplace fireplace)
        {
            fireplace = null;
            var best = float.MaxValue;

            var count = Physics.OverlapSphereNonAlloc(
                station.transform.position, FireplaceProbeRadius, s_fireProbe,
                ~0, QueryTriggerInteraction.Collide);

            for (var i = 0; i < count; i++)
            {
                var fp = s_fireProbe[i] != null
                    ? s_fireProbe[i].GetComponentInParent<Fireplace>()
                    : null;
                if (fp == null) continue;

                var d = (fp.transform.position - station.transform.position).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                fireplace = fp;
            }

            return fireplace != null;
        }

        /// <summary>
        ///     Whether the station would ACCEPT an item right now — mirroring the engine's
        ///     player-facing gate in <c>CookingStation.OnUseItem</c>:
        ///     <code>
        ///     if (m_requireFire &amp;&amp; !IsFireLit())  -> refuse ("$msg_needfire")
        ///     if (GetFreeSlot() == -1)             -> refuse ("$msg_nocookroom")
        ///     </code>
        ///
        ///     <para>This is deliberately STRICTER than <see cref="IsCookingStationReady" />, and
        ///     the two are not interchangeable. The engine has two doors into a station: the
        ///     player's <c>Interact/UseItem</c> path, which runs this gate, and the internal
        ///     <c>RPC_AddItem</c>, which checks only "is this a valid input" and "is there room" —
        ///     because it is the trusted call the gated path makes after passing. The mod invokes
        ///     that inner RPC directly, so without mirroring the gate here a villager could load
        ///     food onto a cold station that the engine would refuse a player, leaving raw meat
        ///     sitting on an unlit fire forever.</para>
        ///
        ///     <para>Note the fire term is NOT "fire OR fuel": a station that requires fire and
        ///     has none is refused by the engine even when it holds internal fuel.</para>
        /// </summary>
        public static bool CanAcceptItem(CookingStation station)
        {
            if (station == null) return false;
            if (station.m_requireFire && !IsCookingStationFireLit(station)) return false;
            return HasFreeSlot(station);
        }

        /// <summary>
        ///     Whether cooking would PROGRESS right now — mirroring the engine's
        ///     <c>CookingStation.UpdateCooking</c> gate:
        ///     <code>
        ///     (m_requireFire &amp;&amp; IsFireLit())
        ///       || (m_useFuel &amp;&amp; GetFuel() > 0 &amp;&amp; (m_useFueldWhileEmpty || HaveUncookedItem()))
        ///     </code>
        ///
        ///     <para>Used for polling an in-flight cook, NOT for deciding whether to add an item —
        ///     see <see cref="CanAcceptItem" /> for that. The <c>HaveUncookedItem()</c> term is
        ///     private in the engine; it only narrows the fuel arm when the station is empty, and
        ///     an empty station has no cook to poll, so omitting it cannot make this return true
        ///     where the engine returns false.</para>
        /// </summary>
        public static bool IsCookingStationReady(CookingStation station)
        {
            if (station == null) return false;
            if (station.m_requireFire && IsCookingStationFireLit(station)) return true;
            if (station.m_useFuel && station.m_useFueldWhileEmpty && GetCookingStationFuel(station) > 0)
                return true;
            return false;
        }

        /// <summary>
        ///     Returns the Smelter component on the ZNetScene prefab with the given name, or null if the prefab
        ///     doesn't exist or doesn't carry a Smelter (e.g. it's a CraftingStation prefab like piece_forge).
        /// </summary>
        public static Smelter GetSmelterPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return null;
            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null || go.name != prefabName) continue;
                return go.GetComponent<Smelter>();
            }
            return null;
        }

        /// <summary>
        ///     Smelter is ready when it has no fuel requirement (m_fuelItem null) OR currently has fuel.
        ///     Mirrors the cooking-station ready check at the smelter level.
        /// </summary>
        public static bool IsSmelterReady(Smelter station)
        {
            if (station == null) return false;
            if (station.m_fuelItem == null) return true;
            return GetSmelterFuel(station) > 0f;
        }

        /// <summary>
        ///     Returns the current fuel level of a Smelter via reflection (GetFuel is private).
        ///     Returns 0 on failure.
        /// </summary>
        public static float GetSmelterFuel(Smelter station)
        {
            if (station == null || s_smelterGetFuel == null) return 0f;
            try
            {
                return (float)s_smelterGetFuel.Invoke(station, null);
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        ///     Returns the current fuel level of a CookingStation via reflection (GetFuel is private).
        ///     Returns 0 on failure.
        /// </summary>
        public static float GetCookingStationFuel(CookingStation station)
        {
            if (station == null || s_getFuel == null) return 0f;
            try
            {
                return (float)s_getFuel.Invoke(station, null);
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        ///     Returns true if the cooking station has at least one empty slot.
        ///     Reads slot data from ZDO, matching the server's GetFreeSlot() logic.
        /// </summary>
        public static bool HasFreeSlot(CookingStation station)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var zdo = nview.GetZDO();
            for (var i = 0; i < slotCount; i++)
                if (string.IsNullOrEmpty(zdo.GetString("slot" + i)))
                    return true;
            return false;
        }

        /// <summary>
        ///     Number of occupied (non-empty) slots on a cooking station — items currently
        ///     cooking, each of which becomes one finished output. Reads ZDO slot data,
        ///     mirroring <see cref="HasFreeSlot" />. Counted as pending output toward a
        ///     cooking work order's quota so the production pipeline can't overshoot.
        /// </summary>
        public static int CountOccupiedSlots(CookingStation station)
        {
            if (station == null) return 0;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return 0;

            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var zdo = nview.GetZDO();
            var occupied = 0;
            for (var i = 0; i < slotCount; i++)
                if (!string.IsNullOrEmpty(zdo.GetString("slot" + i)))
                    occupied++;
            return occupied;
        }
    }
}