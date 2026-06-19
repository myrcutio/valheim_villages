using UnityEngine;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling.Producers
{
    /// <summary>
    ///     Produces <see cref="TaskKind.CookRescue" /> tasks from live cooking stations:
    ///     food that will overcook into coal unless pulled off in time.
    ///
    ///     <para>
    ///     Cooking mechanics (from decompiled <c>CookingStation</c>): each slot stores
    ///     <c>slot{i}</c> (item name, string), <c>slot{i}</c> (cookedTime, float) and
    ///     <c>slotstatus{i}</c> (0 NotDone / 1 Done / 2 Burnt) on the station ZDO.
    ///     <c>cookedTime</c> accumulates only while the fire is lit; the item burns to
    ///     coal once <c>cookedTime > m_cookTime * 2</c>. So time-to-burn =
    ///     <c>m_cookTime*2 − cookedTime</c>, which becomes the task deadline.
    ///     </para>
    /// </summary>
    public static class CookRescueProducer
    {
        private const float ScanRadius = 40f;
        private const string Capability = "tidy"; // TidyBehavior pulls done/burnt food off spits

        // Slot status ints (CookingStation.Status order).
        private const int StatusDone = 1;
        private const int StatusBurnt = 2;

        public static void Scan(Village village, Vector3 center, float now)
        {
            if (village == null) return;
            var villageId = village.VillageId;

            foreach (var station in PhysicsHelper.GetAllInRadius<CookingStation>(center, ScanRadius))
            {
                var nview = station != null ? station.GetComponent<ZNetView>() : null;
                if (nview == null || !nview.IsValid()) continue;
                var zdo = nview.GetZDO();
                if (zdo == null) continue;

                var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
                for (var i = 0; i < slotCount; i++)
                {
                    var sourceId = zdo.m_uid + ":" + i;
                    var item = zdo.GetString("slot" + i);

                    // Only DONE food is rescuable — the burn window is exactly the
                    // Done→Burnt phase (cookTime .. cookTime*2). Still-cooking food
                    // isn't actionable yet; burnt food is already coal.
                    var status = zdo.GetInt("slotstatus" + i);
                    if (string.IsNullOrEmpty(item) || status != StatusDone)
                    {
                        TaskBoard.Remove(villageId, sourceId);
                        continue;
                    }

                    var cookTime = CookTimeFor(station, item);
                    if (cookTime <= 0f) continue; // not a known conversion

                    var burnAt = cookTime * 2f;
                    var timeToBurn = burnAt - zdo.GetFloat("slot" + i);
                    if (timeToBurn <= 0f) continue;

                    TaskBoard.Upsert(villageId, new CandidateTask
                    {
                        SourceId = sourceId,
                        Kind = TaskKind.CookRescue,
                        Position = station.transform.position,
                        Priority = 1f, // ready-to-collect, will be wasted if it burns
                        ExpiresAt = now + timeToBurn, // burns at cookTime*2
                        RequiredCapability = Capability,
                    });
                }
            }
        }

        /// <summary>
        ///     Cook time for the item currently in a slot. The slot name is the raw item
        ///     while cooking (<c>m_from</c>) and the cooked item once done (<c>m_to</c>),
        ///     so match either end of the conversion — mirrors CookingStation.GetItemConversion.
        /// </summary>
        private static float CookTimeFor(CookingStation station, string itemName)
        {
            if (station.m_conversion == null) return 0f;
            foreach (var c in station.m_conversion)
            {
                if (c?.m_from == null || c.m_to == null) continue;
                if (c.m_from.gameObject.name == itemName || c.m_to.gameObject.name == itemName)
                    return c.m_cookTime;
            }

            return 0f;
        }
    }
}
