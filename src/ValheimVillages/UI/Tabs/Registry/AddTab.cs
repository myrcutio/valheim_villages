using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Items.Fragments;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villager.Registry;
using ValheimVillages.Villages;

namespace ValheimVillages.UI.Tabs.Registry
{
    /// <summary>
    ///     Lists the villager types that can be recruited at this registry (loaded from
    ///     <see cref="VillagerRegistry" />). Recruiting spawns the chosen type at the
    ///     registry station (its home anchor becomes the registry — no anchor required) and
    ///     mints a fresh Alive record. Recruiting consumes a Lode Core and requires the type's
    ///     recruit recipe (unlocked via fragment maps); only unlocked types are listed here.
    /// </summary>
    [RegisterRegistryTab("add", Order = 1)]
    public class AddTab : IRegistryTabUI
    {
        private List<VillagerDef> m_types = new();

        public string TabName => "Add";

        public void OnSelected(RegistryContext context)
        {
            RefreshTypes();
        }

        public void OnDeselected()
        {
            m_types.Clear();
        }

        public void OnUpdate(RegistryContext context)
        {
            RefreshTypes();
        }

        private void RefreshTypes()
        {
            // Only types the local player has UNLOCKED (via fragment maps) appear at all —
            // locked recipes are hidden entirely, not shown greyed/tagged.
            m_types = VillagerRegistry.EnabledDefinitions
                .Where(d => RecruitUnlocks.IsUnlockedLocal(d.type))
                .OrderBy(d => d.displayName)
                .ToList();
        }

        public List<TabListItemUI> GetListItems(RegistryContext context)
        {
            if (!RegistryTabLoading.VillageReady(context)) return RegistryTabLoading.ListItems();

            RefreshTypes();
            if (m_types.Count == 0)
                return new List<TabListItemUI>
                {
                    new() { TabName = "(complete a biome map to unlock recruits)" },
                };

            var items = new List<TabListItemUI>();
            foreach (var def in m_types)
                items.Add(new TabListItemUI
                {
                    TabName = string.IsNullOrEmpty(def.displayName) ? def.type : def.displayName,
                    Icon = ResolveItemIcon(def.stationIcon),
                });
            return items;
        }

        public TabDetailDataUI GetDetail(int selectedIndex, RegistryContext context)
        {
            if (!RegistryTabLoading.VillageReady(context)) return RegistryTabLoading.Detail();

            if (m_types.Count == 0)
                return new TabDetailDataUI
                {
                    Title = "No recruits unlocked",
                    Description = "Find ransom-note fragments and combine three of one biome to learn how " +
                                  "to recruit that biome's villager. Only unlocked types appear here.",
                };

            if (selectedIndex < 0 || selectedIndex >= m_types.Count)
                return null;

            var def = m_types[selectedIndex];
            var name = string.IsNullOrEmpty(def.displayName) ? def.type : def.displayName;
            var icon = ResolveItemIcon(def.stationIcon);

            return new TabDetailDataUI
            {
                Title = name,
                Icon = icon,
                Description = string.IsNullOrEmpty(def.description)
                    ? $"Recruit a {name} to this village. They will spawn at the registry."
                    : def.description,
                ActionText = "Recruit",
                OnAction = () => Recruit(def, name, context),
                // Show the cost like the craft menu: the Lode Core ingredient icon + have/need
                // amount and a station-level star, instead of plain "Costs 1 Lode Core" text.
                Requirements = LodeCoreRequirement(),
                StationLevel = 1,
            };
        }

        /// <summary>The recruit cost as a native recipe requirement (1 Lode Core), or null.</summary>
        private static Piece.Requirement[] LodeCoreRequirement()
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(RecruitCost.ItemPrefab) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop != null
                ? new[] { new Piece.Requirement { m_resItem = drop, m_amount = 1 } }
                : null;
        }

        private static void Recruit(VillagerDef def, string name, RegistryContext context)
        {
            if (context == null) return;

            var player = Player.m_localPlayer;

            // Belt-and-suspenders: the UI already locks un-learned types, but guard the action
            // too. Fresh recruits require the per-player recipe (learned by completing the
            // type's biome map). Revive is NOT gated this way.
            if (!RecruitUnlocks.IsUnlocked(player, def.type))
            {
                var biomes = string.Join(" or ", FragmentCombiner.BiomesForType(def.type));
                player?.Message(MessageHud.MessageType.Center, string.IsNullOrEmpty(biomes)
                    ? $"You haven't learned to recruit {name}s."
                    : $"Combine 3 {biomes} ransom fragments to learn how to recruit a {name}.");
                return;
            }

            // Spawn the chosen type at the registry. SpawnVillagerNpc mints a fresh Alive
            // record whose home (vv_home_position / record.HomeAnchor) is the seed below, so
            // the villager belongs to this village with no anchor involved.
            //
            // The registry anchor sits inside the station's own colliders — using it
            // verbatim spawns the villager inside the tabletop AND stores a home that
            // the slot-31 flood can't seed from. Resolve a walkable seed just outside the
            // station footprint first; that point becomes both the spawn position and the
            // persisted home (and therefore the navmesh-discovery flood seed).
            // Spawn ON the village (slot-31) graph: resolve an HNA-valid, approachable
            // cell beside the registry — the surface the villager actually walks on,
            // Y-banded so a roofed registry resolves to its own floor and never the
            // structure above. The seed is resolved against the village's anchor triad
            // (founder-connected), NOT the registry island the registry anchor sits on.
            // No fallback by design: if the village is missing/invalid or the seed can't
            // be resolved (graph not yet settled, registry walled off, fewer than 3
            // triad anchors, etc.) abort loudly rather than dump the villager on the raw
            // anchor for the EnsureAgent warp to teleport upward.
            var village = Villages.Entity.VillageRegistry.FindById(context.VillageId);
            if (village == null || village.IsInvalid)
            {
                Plugin.Log?.LogError(
                    $"[AddTab] Village '{context.VillageId}' is missing or invalid; " +
                    $"aborting recruit of {name}.");
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center, $"Failed to recruit {name} (village invalid)");
                return;
            }

            if (!Villages.Entity.VillageRegistry.TryResolveVillagerSeed(
                    village, context.RegistryPosition, out _))
            {
                Plugin.Log?.LogError(
                    "[AddTab] No reachable spawn location near registry " +
                    $"({context.RegistryPosition.x:F1},{context.RegistryPosition.y:F1},{context.RegistryPosition.z:F1}); " +
                    $"aborting recruit of {name}.");
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center, $"Failed to recruit {name} (no reachable spot)");
                return;
            }

            // Spend one Lode Core from the recruiting player's own (client-owned) inventory,
            // right before the spawn RPC. No core → abort without spawning. (Spawn itself is
            // server-authoritative; on the rare host-side rejection after our client checks
            // the core is already spent — acceptable, the checks here mirror the host's.)
            if (!RecruitCost.TryConsumeOne(player))
            {
                player?.Message(MessageHud.MessageType.Center, $"You need a Lode Core to recruit {name}.");
                return;
            }

            // Authoritative spawn happens on the HOST so the villager is server-owned from
            // birth — a villager created client-side and handed to the server despawns. The
            // checks above are client-side UX gating; the host re-resolves the seed against
            // ITS navmesh near the registry. recordId empty = fresh recruit.
            VillagerRecruitRpc.RequestSpawn(def.type, context.VillageId, context.RegistryPosition, "", paid: true);

            player?.Message(MessageHud.MessageType.Center, $"Recruiting {name}… (−1 Lode Core)");
        }

        private static Sprite ResolveItemIcon(string itemPrefab)
        {
            if (string.IsNullOrEmpty(itemPrefab) || ObjectDB.instance == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab(itemPrefab);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }
    }
}
