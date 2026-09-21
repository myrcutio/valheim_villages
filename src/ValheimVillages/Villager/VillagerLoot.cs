using UnityEngine;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     What a villager leaves behind when it dies: nothing, from the drop table.
    ///
    ///     <para>Villagers are built on a Dvergr prefab and arrive with the Dvergr's own
    ///     <see cref="CharacterDrop" /> — black marble, coins, the rest of a Mistlands table.
    ///     <c>NativeNpcStripper</c> deliberately preserves unrelated components, so it came
    ///     along for the ride: a villager dying in a Meadows starter village handed the player
    ///     black marble, from a biome they have no way to reach yet and a creature they never
    ///     fought.</para>
    ///
    ///     <para><b>The Lode Core is NOT dropped from here.</b> A villager's death already
    ///     returns its Core through <c>VillagerDeathPatch</c>, which calls
    ///     <c>LodeCore.DropAt</c> directly — that is the one place the recruit → die → revive
    ///     loop is closed, and it spawns exactly one, immune to the drop table's level and
    ///     resource-rate multipliers by construction. Putting a Core in the table as well
    ///     would simply pay out twice.</para>
    /// </summary>
    internal static class VillagerLoot
    {
        /// <summary>
        ///     Strip this NPC's inherited drop table. Called from every path that materialises
        ///     a villager — a fresh spawn and a restore on load — so villagers recruited before
        ///     this existed are corrected too, not just new ones.
        /// </summary>
        public static void Apply(GameObject npc)
        {
            if (npc == null) return;

            var drops = npc.GetComponent<CharacterDrop>();
            if (drops == null) return; // nothing inherited, nothing to strip

            if (drops.m_drops.Count == 0) return;
            drops.m_drops.Clear();
        }
    }
}
