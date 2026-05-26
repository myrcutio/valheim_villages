using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Tags;
using ValheimVillages.Testing;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.IntegrationTests
{
    /// <summary>
    ///     Integration tests for the Debug tab's panel registration and rendering.
    ///     Validates end-to-end: definition tags -> panel registration -> tab rendering.
    /// </summary>
    public static class DebugTabTests
    {
        [ModTest(Name = "Patroller_DebugTab_HasVillageMapPanel", Order = 100)]
        public static void Patroller_DebugTab_HasVillageMapPanel()
        {
            // 1. Guard definition declares the right tags (any villager with patrol works)
            var def = VillagerRegistry.Get("Guard");
            ModAssert.NotNull(def, "Guard VillagerDef should exist in VillagerRegistry");

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
                .FirstOrDefault(t => t.TabName == "Debug");
            ModAssert.NotNull(debugTab, "Debug tab should be registered");
            ModAssert.True(debugTab is IVillagerTabUI,
                "DebugTab must implement IVillagerTabUI for the renderer to display items");

            // 4. Find a patroller NPC (guard or scout) and verify Village Map appears
#pragma warning disable CS0618
            var patrollers = Object.FindObjectsOfType<VillagerBehaviorBridge>()
                .Where(v => v.AI?.GetBehavior<PerimeterPatrolBehavior>() != null)
                .ToArray();
#pragma warning restore CS0618

            if (patrollers.Length == 0)
            {
                Plugin.Log?.LogWarning(
                    "[ModTest] No patroller NPC in world — skipping live item check");
                return;
            }

            var patroller = patrollers[0];
            ModAssert.NotNull(patroller.AI,
                "Patroller NPC exists but has no VillagerAI — hot reload or tagging bug");

            var tabUI = (IVillagerTabUI)debugTab;
            tabUI.OnSelected(patroller);
            var items = tabUI.GetListItems(patroller);

            ModAssert.True(items.Any(i => i.TabName == "Village Map"),
                "Patroller debug tab items should include 'Village Map'");
        }
    }
}