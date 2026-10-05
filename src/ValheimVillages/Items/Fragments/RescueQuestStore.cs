using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Networking;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     A pending rescue quest as read from its world-save carrier ZDO.
    /// </summary>
    public readonly struct RescueQuest
    {
        public readonly ZDOID Id;
        public readonly Vector3 Position;
        public readonly string VillagerType;
        public readonly string Biome;
        public readonly bool IsInterior;

        public RescueQuest(ZDO zdo)
        {
            Id = zdo.m_uid;
            Position = zdo.GetPosition();
            VillagerType = zdo.GetString(RescueQuestStore.TypeKey);
            Biome = zdo.GetString(RescueQuestStore.BiomeKey);
            IsInterior = zdo.GetBool(RescueQuestStore.InteriorKey);
        }
    }

    /// <summary>
    ///     Pending rescue quests live in the WORLD SAVE, one <c>vv_rescue_quest</c> carrier ZDO
    ///     per quest, positioned at the quest site. They used to be a static list on the client
    ///     that combined the fragments, so a game restart, relog or hot reload lost the quest
    ///     while the saved map pin survived — the player reached the site and nothing spawned.
    ///     As world state, a quest survives restarts and ANY player who reaches the site can
    ///     complete it.
    ///     <para>Completion is server-arbitrated (<see cref="TryClaim" /> via
    ///     <see cref="FragmentQuestRpc" />) so two players arriving together can't both collect
    ///     the reward.</para>
    /// </summary>
    public static class RescueQuestStore
    {
        public const string PrefabName = "vv_rescue_quest";

        public static readonly int PrefabHash = PrefabName.GetStableHashCode();

        internal const string TypeKey = "vv_quest_type";
        internal const string BiomeKey = "vv_quest_biome";
        internal const string InteriorKey = "vv_quest_interior";
        internal const string LocationKey = "vv_quest_location";

        // Set by the host the moment it grants a claim. DestroyZDO only queues the removal
        // (it's sent out next update), so the ZDO is still findable for the rest of the frame —
        // this flag is what refuses a second claim arriving in that window.
        private const string ClaimedKey = "vv_quest_claimed";

        private static readonly List<ZDO> s_nearby = new();

        private static GameObject _prefab;

        public static void RegisterInZNetScene(ZNetScene zNetScene)
        {
            _prefab = CarrierPrefab.Register(zNetScene, PrefabName, _prefab, "RescueQuestStore");
        }

        /// <summary>Mints the persistent carrier ZDO for a new quest at <paramref name="position" />.</summary>
        public static RescueQuest Create(Vector3 position, string villagerType, string biome, bool isInterior,
            string locationName)
        {
            var zdo = ZDOMan.instance.CreateNewZDO(position, PrefabHash);
            // CreateNewZDO does NOT persist the prefab hash onto the ZDO — set it explicitly so
            // GetPrefab() identifies the carrier and ZNetScene can instantiate its clone.
            zdo.SetPrefab(PrefabHash);
            zdo.Persistent = true;
            zdo.Set(TypeKey, villagerType);
            zdo.Set(BiomeKey, biome);
            zdo.Set(InteriorKey, isInterior);
            zdo.Set(LocationKey, locationName ?? "");
            return new RescueQuest(zdo);
        }

        /// <summary>
        ///     Unclaimed quests in the sectors around <paramref name="position" /> — the same
        ///     near-simulation area a peer is sent ZDOs for, so on a client this is every quest
        ///     it can see. Sectors are 2D, so a dungeon interior (thousands of metres up) still
        ///     finds the quest marked at its surface entrance.
        /// </summary>
        public static List<RescueQuest> Near(Vector3 position)
        {
            var result = new List<RescueQuest>();
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZNet.instance == null) return result;

            var simulation = ZNet.instance.GetSyncedSimulationDistance();
            var nearOnly = new SimulationDistance(simulation.NearSimulationDistance, 0, simulation.IsClassic);
            s_nearby.Clear();
            zdoMan.FindSectorObjects(ZoneSystem.GetZone(position), nearOnly, s_nearby, null);

            foreach (var zdo in s_nearby)
                if (zdo != null && zdo.GetPrefab() == PrefabHash && !zdo.GetBool(ClaimedKey))
                    result.Add(new RescueQuest(zdo));
            return result;
        }

        /// <summary>
        ///     HOST ONLY. Grants the quest <paramref name="id" /> to one claimant and removes it
        ///     from the world. False if it no longer exists or was already claimed.
        /// </summary>
        public static bool TryClaim(ZDOID id, out RescueQuest quest)
        {
            quest = default;
            var zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != PrefabHash || zdo.GetBool(ClaimedKey))
                return false;

            quest = new RescueQuest(zdo);
            // The creating client usually still owns the ZDO; take it so the host may destroy it.
            zdo.SetOwner(ZDOMan.GetSessionID());
            zdo.Set(ClaimedKey, true);
            ZDOMan.instance.DestroyZDO(zdo);
            return true;
        }
    }
}
