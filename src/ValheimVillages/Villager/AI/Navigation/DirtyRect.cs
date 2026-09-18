namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     The XZ rectangle a batch of structural changes could have affected, carried from
    ///     <c>PieceChangePatch</c> through the <c>hna_partition</c> task into the stages that
    ///     can scope their work by it.
    ///     <para>
    ///     Its ABSENCE is meaningful and is not a degraded mode: a partition with no rect is
    ///     a full rebuild that discards every cache and re-derives everything from the world.
    ///     That is what keeps a bare <c>vv_repartition</c> an honest correctness backstop for
    ///     the incremental path.
    ///     </para>
    ///     <para>
    ///     Consumers each grow it by their own reach margin — the distance at which a change
    ///     inside the rect can still flip a result outside it. See
    ///     <c>RegionBuilder.VerdictReachMargin</c> (triangle probes) and
    ///     <c>RubberBandPrune.FloodReachMargin</c> (flood oracles).
    ///     </para>
    /// </summary>
    internal readonly struct DirtyRect
    {
        public readonly float MinX, MinZ, MaxX, MaxZ;

        public DirtyRect(float minX, float minZ, float maxX, float maxZ)
        {
            if (minX > maxX || minZ > maxZ)
                throw new System.ArgumentException(
                    $"Inverted dirty rect x[{minX}..{maxX}] z[{minZ}..{maxZ}].");
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }
    }
}
