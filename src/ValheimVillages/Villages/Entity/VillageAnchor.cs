using UnityEngine;

namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     A named, durable point of interest belonging to a <see cref="Village" />.
    ///     Anchors are stored as a serialized blob on the village ZDO (the single source
    ///     of truth) and resolved on demand; the live object that may sit at the anchor
    ///     (a registry station, a player at mint) is a disposable projection.
    ///     <para>Full XYZ is preserved — never truncate the Y axis.</para>
    /// </summary>
    public readonly struct VillageAnchor
    {
        /// <summary>The registry station position — THE village anchor.</summary>
        public const string Registry = "registry";

        /// <summary>The placing player's position captured ONCE at mint.</summary>
        public const string Founder = "founder";

        /// <summary>
        ///     The self-healing anchor triad: 3 walkable, slot-31-on-mesh points near the
        ///     registry, mutually connected and each connected to the founder. Created/
        ///     repaired by <see cref="VillageRegistry.EnsureAnchorTriad" /> at partition.
        /// </summary>
        public static readonly string[] Triad = { "triad0", "triad1", "triad2" };

        public readonly string Name;
        public readonly Vector3 Position;

        public VillageAnchor(string name, Vector3 position)
        {
            Name = name;
            Position = position;
        }
    }
}
