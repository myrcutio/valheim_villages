using UnityEngine;
using ValheimVillages.Items;
using ValheimVillages.UI.Core;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.UI.Interaction
{
    /// <summary>
    ///     Makes a placed Villager Cot interactable. Mirrors <see cref="RegistryInteract" />:
    ///     <c>Hoverable</c> for the prompt, <c>Interactable</c> for the E key, opening Valheim's
    ///     crafting GUI with the cot's Assign tab.
    ///
    ///     <para>Added to the prefab <b>before</b> the <c>CraftingStation</c> (see
    ///     <c>VillagerBedPiece.ConfigureInteraction</c>) so
    ///     <c>GetComponentInParent&lt;Interactable&gt;()</c> resolves here and not to the native
    ///     station-open path.</para>
    /// </summary>
    public class CotInteract : MonoBehaviour, Hoverable, Interactable
    {
        private Piece m_piece;

        /// <summary>
        ///     The cot whose UI is currently open, or null. Lets the <c>InventoryGui.Hide</c>
        ///     patch tear down the right manager.
        /// </summary>
        public static CotInteract ActiveCot { get; private set; }

        private void Awake()
        {
            m_piece = GetComponent<Piece>();
        }

        public string GetHoverName()
        {
            return m_piece != null && !string.IsNullOrEmpty(m_piece.m_name)
                ? m_piece.m_name
                : "Villager Cot";
        }

        /// <summary>
        ///     Extra interaction reach, not a vertical offset despite the name. Deferred to the
        ///     piece's own CraftingStation, and resolved lazily rather than cached in Awake —
        ///     this component is added to the prefab BEFORE the station exists.
        /// </summary>
        public float GetHoverOffset()
        {
            var station = GetComponent<CraftingStation>();
            return station != null ? station.GetHoverOffset() : 0f;
        }

        /// <summary>
        ///     Naming the occupant in the hover text is the whole point of assigning a cot: the
        ///     player can read the room without opening anything.
        /// </summary>
        public string GetHoverText()
        {
            var occupant = VillagerCotOccupant.ResolveRecord(gameObject);
            var who = occupant != null
                ? $"\n<color=#9FB4C7>{occupant.Name} — {occupant.Type}</color>"
                : "\n<color=#9FB4C7>unassigned</color>";

            return Localization.instance.Localize(
                $"{GetHoverName()}{who}\n[<color=yellow><b>$KEY_Use</b></color>] Assign");
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (user is not Player player) return false;

            var station = GetComponent<CraftingStation>();
            if (station == null)
            {
                Plugin.Log?.LogWarning("[CotInteract] no CraftingStation on the cot piece");
                return false;
            }

            if (InventoryGui.instance == null) return false;

            // A cot carries no village stamp of its own — unlike the registry, which is stamped
            // at placement — so the village is resolved from where it stands. Null is a normal
            // state (a cot placed out in the wild), and the tab says so rather than refusing.
            var village = VillageRegistry.GetVillageAt(transform.position);

            player.SetCraftingStation(station);
            InventoryGui.instance.Show(null, 3);
            CotTabManager.Activate(new CotContext(gameObject, village?.VillageId));
            ActiveCot = this;
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        /// <summary>
        ///     Called from the <c>InventoryGui.Hide</c> patch. No-ops unless a cot session is
        ///     open, so it safely shares the hook with the villager and registry close handlers.
        /// </summary>
        public static void OnCraftingUIClosed()
        {
            if (ActiveCot == null) return;
            CotTabManager.Deactivate();
            ActiveCot = null;
        }
    }
}
