using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
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
    ///     mints a fresh Alive record. No cost for now; the egg/material flow is later.
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
            m_types = VillagerRegistry.EnabledDefinitions
                .OrderBy(d => d.displayName)
                .ToList();
        }

        public List<TabListItemUI> GetListItems(RegistryContext context)
        {
            if (m_types.Count == 0) RefreshTypes();
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
            if (selectedIndex < 0 || selectedIndex >= m_types.Count)
                return null;

            var def = m_types[selectedIndex];
            var name = string.IsNullOrEmpty(def.displayName) ? def.type : def.displayName;
            return new TabDetailDataUI
            {
                Title = name,
                Icon = ResolveItemIcon(def.stationIcon),
                Description = string.IsNullOrEmpty(def.description)
                    ? $"Recruit a {name} to this village. They will spawn at the registry."
                    : def.description,
                ActionText = "Recruit",
                OnAction = () => Recruit(def, name, context),
            };
        }

        private static void Recruit(VillagerDef def, string name, RegistryContext context)
        {
            if (context == null) return;

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
                    village, context.RegistryPosition, out var spawnPos))
            {
                Plugin.Log?.LogError(
                    "[AddTab] No reachable spawn location near registry " +
                    $"({context.RegistryPosition.x:F1},{context.RegistryPosition.y:F1},{context.RegistryPosition.z:F1}); " +
                    $"aborting recruit of {name}.");
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center, $"Failed to recruit {name} (no reachable spot)");
                return;
            }

            VillagerRecord record = null;
            var prefab = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : "DvergerMage";
            // Recruited AT this registry → belongs to this registry's village (already
            // minted at placement). Pass its id so the spawn resolves it without minting.
            var npc = VillagerSpawner.SpawnVillagerNpc(def, def.type, prefab, spawnPos, ref record, context.VillageId);

            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                npc != null ? $"Recruited {name}" : $"Failed to recruit {name}");
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
