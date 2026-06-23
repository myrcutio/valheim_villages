using UnityEngine;
using ValheimVillages.UI.Core;
using ValheimVillages.Villages.Entity;

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

            var villageId = ResolveVillageId();

            // Perimeter-wall requirement, mirroring a workbench refusing to open without a
            // roof: if the village isn't sealed (the outside flood reaches the registry),
            // flash a centered message and don't open the panel. Cleared automatically once
            // the player closes the perimeter (the next rebake re-evaluates the flag).
            var village = !string.IsNullOrEmpty(villageId)
                ? VillageRegistry.FindById(villageId)
                : null;
            if (village != null && village.NeedsPerimeterWall)
            {
                player.Message(MessageHud.MessageType.Center, "Village registry needs a perimeter wall");
                return false;
            }

            // Open the crafting GUI scoped to the registry station, then inject the
            // Roster/Add/Revive tabs. activeGroup 3 is the crafting tab.
            player.SetCraftingStation(station);
            InventoryGui.instance.Show(null, 3);
            RegistryTabManager.Activate(new RegistryContext(transform.position, villageId));
            ActiveRegistry = this;
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        /// <summary>
        ///     The durable village id stamped on this registry's ZDO at placement
        ///     (<see cref="Villages.StationBuildPatch" />). A missing id here is a true
        ///     anomaly, not a migration path — log and return null (the UI opens with no
        ///     village rather than minting one to paper over the inconsistency).
        /// </summary>
        private string ResolveVillageId()
        {
            var villageId = GetComponent<ZNetView>()?.GetZDO()?.GetString(Village.IdKey);
            if (!string.IsNullOrEmpty(villageId)) return villageId;

            Plugin.Log?.LogError(
                "[RegistryInteract] registry piece has no vv_village_id (it should be stamped at " +
                "placement); opening with no village. Village state is inconsistent — investigate.");
            return null;
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
