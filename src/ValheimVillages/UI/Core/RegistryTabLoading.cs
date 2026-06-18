using System.Collections.Generic;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Shared "is the village set up yet?" gate for the Village Registry tabs.
    ///     The registry piece can be opened the instant it's placed/loaded, but the
    ///     village it anchors is hydrated and its region graph built by DEFERRED tasks
    ///     (<c>village_index</c> + the HNA partition/bake). Until those complete, the
    ///     tabs would query an empty record set and render a misleading "(no villagers
    ///     yet)" — depending on the village/villagers being instantiated to look right.
    ///     <para>
    ///     Instead the tabs call <see cref="VillageReady" /> first and, while false,
    ///     render a single "Village loading…" placeholder. They flip to real content
    ///     automatically once the deferred tasks finish (the host re-queries on its
    ///     OnUpdate cadence) — no villager instantiation required to open the UI.
    ///     </para>
    /// </summary>
    public static class RegistryTabLoading
    {
        /// <summary>
        ///     True once the village is hydrated, valid, and its region graph is built —
        ///     i.e. the deferred setup tasks have completed for this registry's village.
        /// </summary>
        public static bool VillageReady(RegistryContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.VillageId)) return false;
            var village = Villages.Entity.VillageRegistry.FindById(context.VillageId);
            return village != null && !village.IsInvalid && village.HasGraph;
        }

        /// <summary>The placeholder list shown while the village is still loading.</summary>
        public static List<TabListItemUI> ListItems()
        {
            return new List<TabListItemUI> { new() { TabName = "Village loading…" } };
        }

        /// <summary>The placeholder detail shown while the village is still loading.</summary>
        public static TabDetailDataUI Detail()
        {
            return new TabDetailDataUI
            {
                Title = "Village loading…",
                Description = "This village is still being set up.\n" +
                              "Its roster and actions will appear once loading completes.",
            };
        }
    }
}
