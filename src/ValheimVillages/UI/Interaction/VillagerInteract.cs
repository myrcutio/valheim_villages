using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.UI.Core;
using ValheimVillages.Villager.Station;

namespace ValheimVillages.UI.Interaction
{
    /// <summary>
    ///     Enables player interaction with villager NPCs.
    ///     Implements Hoverable for hover text and Interactable for E-key interaction.
    ///     Opens the crafting UI with work order tabs for NPCs tagged "tab:workorder",
    ///     or the dialog menu for other types.
    /// </summary>
    public class VillagerInteract : MonoBehaviour, Hoverable, Interactable
    {
        private VillagerBehaviorBridge m_bridge;
        private Character m_character;
        private Humanoid m_humanoid;
        private bool m_resolved;

        /// <summary>
        ///     The villager currently being interacted with via the crafting UI.
        ///     Used by the InventoryGui.Hide patch to resume the NPC.
        /// </summary>
        public static VillagerInteract ActiveCraftingVillager { get; private set; }

        /// <summary>
        ///     Lazily resolve the bridge in case it was added after this component.
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

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_humanoid = GetComponent<Humanoid>();
        }

        /// <summary>
        ///     Gets the hover name displayed when looking at the NPC.
        /// </summary>
        public string GetHoverName()
        {
            if (m_humanoid != null) return m_humanoid.m_name;
            if (m_character != null) return m_character.GetHoverName();
            return "Villager";
        }

        /// <summary>
        ///     Gets the full hover text with interaction prompt.
        /// </summary>
        public string GetHoverText()
        {
            var name = GetHoverName();

            var stateInfo = GetStateInfo();

            // $KEY_Use resolves to the bound Use key on keyboard (E) or the
            // gamepad glyph (A) once run through Localize — same as native
            // station hover prompts. A literal "E" never adapts to a controller.
            return Localization.instance.Localize(
                $"{name}\n{stateInfo}\n[<color=yellow><b>$KEY_Use</b></color>] Talk");
        }

        /// <summary>
        ///     Called when the player interacts with this NPC.
        ///     Opens the crafting UI for NPC types with virtual stations,
        ///     or the dialog menu for others.
        /// </summary>
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (user is not Player player) return false;

            if (Bridge == null)
            {
                Plugin.Log?.LogWarning("VillagerInteract: No VillagerBehaviorBridge component found");
                player.Message(MessageHud.MessageType.Center, "This villager cannot be inspected");
                return false;
            }

            // Open the crafting/tab UI for all NPC types
            var villagerStation = GetComponent<VillagerStation>();
            if (villagerStation?.Station != null) return OpenCraftingUI(player, villagerStation);

            // Shouldn't reach here, but log if it does
            Plugin.Log?.LogWarning(
                $"VillagerInteract: {GetHoverName()} has no VillagerStation");
            return false;
        }

        /// <summary>
        ///     Called when the player uses an item on this NPC.
        /// </summary>
        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        private string GetStateInfo()
        {
            if (Bridge == null) return "";

            var ai = Bridge.villagerInstance.villagerAI;
            if (ai == null) return "";

            var hasActiveWork = ai.CraftingBehavior?.Crafting?.IsWorking == true;

            var label = ai.CurrentState switch
            {
                BehaviorState.Traveling when !hasActiveWork => "Idle",
                BehaviorState.Wandering when !hasActiveWork => "Idle",
                _ => ai.CurrentState.ToString(),
            };

            return $"<color=grey>{label}</color>";
        }

        /// <summary>
        ///     Opens Valheim's crafting UI with this NPC's virtual station.
        ///     For NPC types with the "tab:workorder" tag, shows the full
        ///     crafting panel with Orders/Upgrade/Info/Debug tabs. For others,
        ///     hides the native tabs and shows only Info/Debug.
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
            var hasCrafting = bridge?.villagerInstance.villagerType != null &&
                              VillagerStation.HasCraftingRecipes(bridge.villagerInstance.villagerType);

            // Activate the tab system
            VillagerTabManager.Activate(Bridge, hasCrafting);

            Plugin.Log?.LogInfo(
                $"VillagerInteract: Opened UI for {GetHoverName()} " +
                $"(station: {station.m_name}, crafting: {hasCrafting})");

            return true;
        }

        /// <summary>
        ///     Called when the crafting UI is closed. Resumes the NPC.
        /// </summary>
        public static void OnCraftingUIClosed()
        {
            if (ActiveCraftingVillager == null) return;

            VillagerTabManager.Deactivate();
            ActiveCraftingVillager.Bridge?.SetPaused(false);
            Plugin.Log?.LogInfo(
                "VillagerInteract: Crafting UI closed, resuming NPC");
            ActiveCraftingVillager = null;
        }
    }
}