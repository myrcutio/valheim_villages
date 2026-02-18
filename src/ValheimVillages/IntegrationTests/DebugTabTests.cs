using System.Linq;
using UnityEngine;
using ValheimVillages.Core.Attributes;
using ValheimVillages.Core.Testing;
using ValheimVillages.NPCs;
using ValheimVillages.Tags;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.IntegrationTests
{
    /// <summary>
    /// Integration tests for the Debug tab's panel registration and rendering.
    /// Validates end-to-end: definition tags -> panel registration -> tab rendering.
    /// </summary>
    public static class DebugTabTests
    {
        [ModTest(Name = "Guard_DebugTab_HasVillageMapPanel", Order = 100)]
        public static void Guard_DebugTab_HasVillageMapPanel()
        {
            // 1. Guard definition declares the right tags
            var def = NpcTypeRegistry.Get(NpcType.Guard);
            ModAssert.NotNull(def, "Guard NpcTypeDefinition should exist in NpcTypeRegistry");

            ModAssert.True(
                TagParser.HasTag(def.tags, "tab", "debug"),
                "Guard definition should have tab:debug tag");

            ModAssert.True(
                TagParser.HasTag(def.tags, "listpanel", "villagemap"),
                "Guard definition should have listpanel:villagemap tag");

            // 2. Panel is registered with AttributeScanner
            var debugPanels = AttributeScanner.GetListPanels("debug");
            var villageMapPanel = debugPanels.FirstOrDefault(p => p.Tag == "villagemap");
            ModAssert.NotNull(villageMapPanel,
                "A villagemap panel should be registered via [RegisterListPanel]");

            // 3. DebugTab implements IVillagerTabUI so the renderer can display items
            var debugTab = AttributeScanner.GetRegisteredTabs()
                .FirstOrDefault(t => t.Name == "Debug");
            ModAssert.NotNull(debugTab, "Debug tab should be registered");
            ModAssert.True(debugTab is IVillagerTabUI,
                "DebugTab must implement IVillagerTabUI for the renderer to display items");

            // 4. Find a guard NPC and verify Village Map appears in the tab's items
#pragma warning disable CS0618
            var guards = Object.FindObjectsOfType<VillagerBehaviorBridge>()
                .Where(v => v.NpcType == NpcType.Guard)
                .ToArray();
#pragma warning restore CS0618

            if (guards.Length == 0)
            {
                Plugin.Log?.LogWarning(
                    "[ModTest] No guard NPC in world — skipping live item check");
                return;
            }

            var guard = guards[0];
            ModAssert.NotNull(guard.AI,
                "Guard NPC exists but has no VillagerAI — hot reload or tagging bug");

            var tabUI = (IVillagerTabUI)debugTab;
            tabUI.OnSelected(guard);
            var items = tabUI.GetListItems(guard);

            ModAssert.True(items.Any(i => i.Name == "Village Map"),
                "Guard debug tab items should include 'Village Map'");
        }
    }
}
