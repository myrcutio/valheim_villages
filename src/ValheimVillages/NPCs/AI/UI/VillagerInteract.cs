using UnityEngine;
using ValheimVillages.NPCs.AI.UI.Tabs;
using ValheimVillages.NPCs.AI.Work;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Enables player interaction with villager NPCs.
    /// Implements Hoverable for hover text and Interactable for E-key interaction.
    /// Opens the crafting UI for NPC types with virtual stations (Farmer, TavernKeeper),
    /// or the dialog menu for other types.
    /// </summary>
    public class VillagerInteract : MonoBehaviour, Hoverable, Interactable
    {
        private VillagerBehaviorBridge m_bridge;
        private Character m_character;
        private Humanoid m_humanoid;
        private bool m_resolved;

        /// <summary>
        /// The villager currently being interacted with via the crafting UI.
        /// Used by the InventoryGui.Hide patch to resume the NPC.
        /// </summary>
        public static VillagerInteract ActiveCraftingVillager { get; private set; }

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_humanoid = GetComponent<Humanoid>();
        }

        /// <summary>
        /// Lazily resolve the bridge in case it was added after this component.
        /// </summary>
        private VillagerBehaviorBridge Bridge
        {
            get
            {
                if (m_bridge == null && !m_resolved)
                {
                    m_bridge = GetComponent<VillagerBehaviorBridge>();
                    m_resolved = true;
                }
                return m_bridge;
            }
        }

        /// <summary>
        /// Gets the hover name displayed when looking at the NPC.
        /// </summary>
        public string GetHoverName()
        {
            if (m_humanoid != null)
            {
                return m_humanoid.m_name;
            }
            if (m_character != null)
            {
                return m_character.GetHoverName();
            }
            return "Villager";
        }

        /// <summary>
        /// Gets the full hover text with interaction prompt.
        /// </summary>
        public string GetHoverText()
        {
            string name = GetHoverName();

            // Sleeping: show sleep state, no interaction prompt
            if (IsSleeping())
            {
                return $"{name}\n<color=grey>Sleeping</color>";
            }

            string stateInfo = GetStateInfo();

            // Guard breach alert
            if (IsGuardAlarmed())
            {
                return $"{name}\n<color=red><b>There is a breach in the walls!</b></color>\n{stateInfo}\n[<color=yellow><b>E</b></color>] Talk";
            }
            
            return $"{name}\n{stateInfo}\n[<color=yellow><b>E</b></color>] Talk";
        }

        /// <summary>
        /// Gets a short description of the NPC's current state.
        /// </summary>
        private string GetStateInfo()
        {
            if (Bridge == null) return "";

            var ai = Bridge.AI;
            if (ai == null) return "";

            // Guard-specific state info
            if (ai.IsGuard && ai.GuardBehavior != null)
                return GetGuardStateInfo(ai);

            // Worker crafting state info
            if (ai.CraftingBehavior != null && ai.CraftingBehavior.IsWorking)
                return GetWorkStateInfo(ai.CraftingBehavior);

            // General villager state
            return GetGeneralStateInfo(ai);
        }

        private string GetGeneralStateInfo(VillagerAI ai)
        {
            return ai.CurrentState switch
            {
                BehaviorState.Idle => "<color=grey>Idle</color>",
                BehaviorState.Traveling => "<color=grey>Walking somewhere...</color>",
                BehaviorState.Wandering => "<color=grey>Wandering around</color>",
                BehaviorState.Exploring => "<color=grey>Exploring the area</color>",
                BehaviorState.Sleeping => "<color=grey>Sleeping</color>",
                BehaviorState.Working => GetWorkStateInfo(ai.CraftingBehavior),
                _ => $"<color=grey>{ai.CurrentState}</color>"
            };
        }

        private string GetWorkStateInfo(CraftingBehavior craft)
        {
            if (craft == null) return "<color=grey>Working</color>";

            return craft.SubState switch
            {
                WorkSubState.GatheringIngredients =>
                    "<color=grey>Gathering ingredients...</color>",
                WorkSubState.TravelingToStation =>
                    "<color=grey>Heading to crafting station...</color>",
                WorkSubState.Crafting =>
                    "<color=grey>Crafting...</color>",
                WorkSubState.ReturningToChest =>
                    "<color=grey>Returning with goods...</color>",
                WorkSubState.Depositing =>
                    "<color=grey>Storing crafted items...</color>",
                _ => "<color=grey>Working</color>"
            };
        }

        private string GetGuardStateInfo(VillagerAI ai)
        {
            var guard = ai.GuardBehavior;
            var state = ai.CurrentState;

            return state switch
            {
                BehaviorState.Scouting => "<color=grey>Scouting the perimeter...</color>",
                BehaviorState.CircuitTracing => "<color=grey>Mapping the village boundary...</color>",
                BehaviorState.Patrolling => $"<color=grey>Patrolling ({guard.WaypointCount} waypoints)</color>",
                BehaviorState.Alarmed => "<color=red>BREACH DETECTED</color>",
                BehaviorState.Sleeping => "<color=grey>Sleeping</color>",
                _ => "<color=grey>On duty</color>"
            };
        }

        private bool IsGuardAlarmed()
        {
            var ai = Bridge?.AI;
            return ai != null && ai.IsGuard && ai.GuardBehavior?.IsAlarmed == true;
        }

        private bool IsSleeping()
        {
            var ai = Bridge?.AI;
            return ai != null && ai.CurrentState == BehaviorState.Sleeping && ai.IsSleepAnimationActive;
        }

        /// <summary>
        /// Called when the player interacts with this NPC.
        /// Opens the crafting UI for NPC types with virtual stations,
        /// or the dialog menu for others.
        /// </summary>
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (user is not Player player) return false;

            // Don't allow interaction while sleeping
            if (IsSleeping())
            {
                return false;
            }

            if (Bridge == null)
            {
                Plugin.Log?.LogWarning("VillagerInteract: No VillagerBehaviorBridge component found");
                player.Message(MessageHud.MessageType.Center, "This villager cannot be inspected");
                return false;
            }

            // Open the crafting/tab UI for all NPC types
            var villagerStation = GetComponent<VillagerStation>();
            if (villagerStation?.Station != null)
            {
                return OpenCraftingUI(player, villagerStation);
            }

            // Shouldn't reach here, but log if it does
            Plugin.Log?.LogWarning(
                $"VillagerInteract: {GetHoverName()} has no VillagerStation");
            return false;
        }

        /// <summary>
        /// Opens Valheim's crafting UI with this NPC's virtual station.
        /// For NPC types with recipes (Farmer, TavernKeeper), shows the full
        /// crafting panel with Tasks/Upgrade/Info/Debug tabs. For others,
        /// hides the native tabs and shows only Info/Debug.
        /// </summary>
        private bool OpenCraftingUI(Player player, VillagerStation villagerStation)
        {
            var station = villagerStation.Station;

            // Pause the NPC while the UI is open
            Bridge?.SetPaused(true);
            ActiveCraftingVillager = this;

            // Set the player's crafting station to our virtual station
            player.SetCraftingStation(station);

            // Open the inventory GUI (activeGroup=3 is the crafting tab)
            InventoryGui.instance.Show(null, 3);

            // Determine if this NPC type has crafting recipes
            var bridge = Bridge;
            bool hasCrafting = bridge?.NpcType != null &&
                VillagerStation.HasCraftingRecipes(bridge.NpcType.Value);

            // Activate the tab system
            VillagerTabManager.Activate(Bridge, hasCrafting);

            Plugin.Log?.LogInfo(
                $"VillagerInteract: Opened UI for {GetHoverName()} " +
                $"(station: {station.m_name}, crafting: {hasCrafting})");

            return true;
        }

        /// <summary>
        /// Called when the crafting UI is closed. Resumes the NPC.
        /// </summary>
        public static void OnCraftingUIClosed()
        {
            if (ActiveCraftingVillager == null) return;

            VillagerTabManager.Deactivate();
            ActiveCraftingVillager.Bridge?.SetPaused(false);
            Plugin.Log?.LogInfo(
                $"VillagerInteract: Crafting UI closed, resuming NPC");
            ActiveCraftingVillager = null;
        }

        /// <summary>
        /// Called when the player uses an item on this NPC.
        /// </summary>
        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

    }
}
