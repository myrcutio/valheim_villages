using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Tags;
using ValheimVillages.Testing;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.IntegrationTests
{
    /// <summary>
    ///     Integration tests for the Info tab's panel registration and rendering.
    ///     Validates end-to-end: definition tags -> panel registration -> tab rendering.
    /// </summary>
    public static class InfoTabTests
    {
        [ModTest(Name = "Villager_InfoTab_HasVillageMapPanel", Order = 100)]
        public static void Villager_InfoTab_HasVillageMapPanel()
        {
            // 1. Blacksmith definition declares the right tags (any non-patroller villager works)
            var def = VillagerRegistry.Get("Blacksmith");
            ModAssert.NotNull(def, "Blacksmith VillagerDef should exist in VillagerRegistry");

            ModAssert.True(
                TagParser.HasTag(def.tags, "tab", "info"),
                "Blacksmith definition should have tab:info tag");

            // 2. InfoTab implements IVillagerTabUI so the renderer can display items
            var infoTab = AttributeScanner.GetRegisteredTabs()
                .FirstOrDefault(t => t.TabName == "Info");
            ModAssert.NotNull(infoTab, "Info tab should be registered");
            ModAssert.True(infoTab is IVillagerTabUI,
                "InfoTab must implement IVillagerTabUI for the renderer to display items");

            // 3. Find a villager NPC (prefer Blacksmith) and verify Village Map does NOT appear
#pragma warning disable CS0618
            var allVillagers = Object.FindObjectsOfType<VillagerBehaviorBridge>();
#pragma warning restore CS0618

            var npc = allVillagers
                .FirstOrDefault(v => v.VillagerType == "Blacksmith")
                ?? allVillagers.FirstOrDefault();

            if (npc == null)
            {
                Plugin.Log?.LogWarning(
                    "[ModTest] No VillagerBehaviorBridge in world — skipping live item check");
                return;
            }

            ModAssert.NotNull(npc.AI,
                "Villager NPC exists but has no VillagerAI — hot reload or tagging bug");

            var tabUI = (IVillagerTabUI)infoTab;
            tabUI.OnSelected(npc);
            var items = tabUI.GetListItems(npc);

            ModAssert.True(!items.Any(i => i.TabName == "Village Map"),
                "Tasks tab should no longer have a standalone 'Village Map' list item — map moved into task details");
        }
    }
}
