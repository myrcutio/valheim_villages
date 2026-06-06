using UnityEngine;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.Interaction
{
    /// <summary>
    ///     Makes the placed Village Registry piece interactable. Mirrors
    ///     <see cref="VillagerInteract" />: implements <c>Hoverable</c> for the hover
    ///     prompt and <c>Interactable</c> for E-key use, opening Valheim's crafting
    ///     GUI with the registry's custom tabs (<see cref="RegistryTabManager" />).
    ///
    ///     <para>Added to the prefab <b>before</b> the <c>CraftingStation</c> (see
    ///     <c>PieceFactory.ConfigureInteraction</c>) so <c>GetComponentInParent&lt;Interactable&gt;()</c>
    ///     resolves to this component and not the native station-open path.</para>
    /// </summary>
    public class RegistryInteract : MonoBehaviour, Hoverable, Interactable
    {
        private Piece m_piece;

        /// <summary>
        ///     The registry whose UI is currently open, or null. Lets the
        ///     <c>InventoryGui.Hide</c> patch tear down the right manager.
        /// </summary>
        public static RegistryInteract ActiveRegistry { get; private set; }

        private void Awake()
        {
            m_piece = GetComponent<Piece>();
        }

        public string GetHoverName()
        {
            return m_piece != null && !string.IsNullOrEmpty(m_piece.m_name)
                ? m_piece.m_name
                : "Village Registry";
        }

        public string GetHoverText()
        {
            // $KEY_Use resolves to the bound Use key (E) on keyboard or the gamepad
            // glyph once Localized — same as native station prompts.
            return Localization.instance.Localize(
                $"{GetHoverName()}\n[<color=yellow><b>$KEY_Use</b></color>] Manage");
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (user is not Player player) return false;

            var station = GetComponent<CraftingStation>();
            if (station == null)
            {
                Plugin.Log?.LogWarning("RegistryInteract: no CraftingStation on registry piece");
                return false;
            }

            if (InventoryGui.instance == null) return false;

            // Open the crafting GUI scoped to the registry station, then inject the
            // Roster/Add/Revive tabs. activeGroup 3 is the crafting tab.
            player.SetCraftingStation(station);
            InventoryGui.instance.Show(null, 3);
            RegistryTabManager.Activate(new RegistryContext(transform.position));
            ActiveRegistry = this;
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        /// <summary>
        ///     Called from the <c>InventoryGui.Hide</c> patch. No-ops unless a
        ///     registry session is open, so it can safely share the hook with the
        ///     villager UI's close handler.
        /// </summary>
        public static void OnCraftingUIClosed()
        {
            if (ActiveRegistry == null) return;
            RegistryTabManager.Deactivate();
            ActiveRegistry = null;
        }
    }
}
