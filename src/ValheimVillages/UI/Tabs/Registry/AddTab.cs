using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.UI.Tabs.Registry
{
    /// <summary>
    ///     Lists the villager types that can be recruited at this registry (loaded from
    ///     <see cref="VillagerRegistry" />). Recruiting spawns the chosen type at the
    ///     registry station (its home anchor becomes the registry — no bed required) and
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
            m_types = VillagerRegistry.Definitions.Values
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
            // record whose home (vv_bed_position / record.BedPosition) is the seed below, so
            // the villager belongs to this village with no bed involved.
            //
            // The registry anchor sits inside the station's own colliders — using it
            // verbatim spawns the villager inside the tabletop AND stores a home that
            // the slot-31 flood can't seed from. Resolve a walkable seed just outside the
            // station footprint first; that point becomes both the spawn position and the
            // persisted home (and therefore the navmesh-discovery flood seed).
            var spawnPos = context.RegistryPosition;
            if (RegistrySeedResolver.TryResolveWalkableSeed(context.RegistryPosition, out var seed))
                spawnPos = seed;
            else
                Plugin.Log?.LogWarning(
                    $"[AddTab] No walkable seed near registry {context.RegistryPosition}; " +
                    "spawning at the anchor (region graph may degenerate).");

            VillagerRecord record = null;
            var prefab = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : "DvergerMage";
            var npc = VillagerPawnPatch.SpawnVillagerNpc(def, def.type, prefab, spawnPos, ref record);

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
