using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.UI.Panels
{
    /// <summary>What a village map cell is made of, for colouring the floor.</summary>
    public enum MapSurface
    {
        Grass,
        Dirt,
        Cultivated,
        Paved,
        WoodFloor,
        StoneFloor,
        Other,
    }

    /// <summary>
    ///     Classifies each walkable cell of a village as the surface a player would recognise —
    ///     grass, dirt, a cultivated patch, paving, a wooden floor, a stone one — so the map
    ///     reads as a place rather than one flat blob of "walkable".
    ///
    ///     <para>Terrain cells are read from the Heightmap's own paint mask, which is the same
    ///     data the ground is drawn from, so a field the player tilled shows as tilled. Piece
    ///     cells are resolved by a downward raycast to whatever floor is actually under them,
    ///     because the region graph records only Terrain-vs-Piece and cannot tell oak from
    ///     granite.</para>
    ///
    ///     <para>The whole thing is cached against the graph's <see cref="RegionGraph.Generation" />:
    ///     it costs a raycast per piece cell and a texture read per terrain cell, which is fine
    ///     once per partition and ruinous on the description refresh timer the map rebuilds on.
    ///     A repartition bumps the generation, which is exactly when the answer can change.</para>
    /// </summary>
    internal static class VillageSurfaceMap
    {
        private static readonly int s_pieceMask =
            LayerMask.GetMask("Default", "static_solid", "piece");

        /// <summary>
        ///     Floor colours, picked to sit on the parchment background rather than against it —
        ///     this is a map, so the surfaces are tinted paper, not screenshots of the material.
        /// </summary>
        private static readonly Dictionary<MapSurface, Color> s_palette = new()
        {
            [MapSurface.Grass] = new Color(0.60f, 0.63f, 0.43f),
            [MapSurface.Dirt] = new Color(0.60f, 0.48f, 0.34f),
            [MapSurface.Cultivated] = new Color(0.47f, 0.38f, 0.25f),
            [MapSurface.Paved] = new Color(0.62f, 0.60f, 0.55f),
            [MapSurface.WoodFloor] = new Color(0.68f, 0.49f, 0.29f),
            [MapSurface.StoneFloor] = new Color(0.56f, 0.56f, 0.58f),
            [MapSurface.Other] = new Color(0.64f, 0.55f, 0.40f),
        };

        /// <summary>
        ///     Classify every cell of a freshly committed graph and store the result ON the
        ///     graph. Called from the partition and from the ZDO load — the two places a graph
        ///     is committed — so the cost lands once, with the rest of region building, instead
        ///     of on whoever opens a map first.
        ///
        ///     <para>It is genuinely expensive: a downward raycast per piece cell and a
        ///     heightmap texture read per terrain cell, over every walkable cell in the village.
        ///     That is acceptable alongside a bake; it is not acceptable on the description
        ///     refresh timer the map redraws on.</para>
        /// </summary>
        public static void Build(RegionGraph graph)
        {
            if (graph == null || !graph.IsAvailable) return;

            var cells = graph.GetLookupCells();
            if (cells.Count == 0) return;

            var surfaces = new Dictionary<long, byte>(cells.Count);
            foreach (var (key, center) in cells)
                surfaces[key] = (byte)Classify(graph, center);

            graph.SetCellSurfaces(surfaces);
            Plugin.Log?.LogDebug(
                $"[SurfaceMap] Classified {surfaces.Count} cell(s) for graph gen {graph.Generation}");
        }

        /// <summary>
        ///     A colour per cell, parallel to <paramref name="cells" />. Pure lookup — returns
        ///     null when the graph has not been classified yet, which leaves the renderer on its
        ///     flat land colour rather than quietly doing the expensive work here.
        /// </summary>
        public static List<Color> ColorsFor(RegionGraph graph, List<Vector3> cells)
        {
            if (graph == null || cells == null || cells.Count == 0) return null;
            if (!graph.HasCellSurfaces) return null;

            var colors = new List<Color>(cells.Count);
            foreach (var cell in cells)
                colors.Add(graph.TryGetCellSurface(cell, out var s)
                    ? s_palette[(MapSurface)s]
                    : s_palette[MapSurface.Other]);

            return colors;
        }

        private static MapSurface Classify(RegionGraph graph, Vector3 cell)
        {
            var regionId = graph.PointToRegionId(cell);
            if (regionId != null && graph.GetRegionKind(regionId) == SurfaceKind.Piece)
                return PieceSurface(cell);

            return TerrainSurface(cell);
        }

        /// <summary>
        ///     Terrain paint, straight off the Heightmap. Valheim encodes the painted layers one
        ///     per channel (r=dirt, g=cultivated, b=paved) and blends between them, so the
        ///     strongest channel is the surface — an unpainted cell is all-zero and is grass.
        /// </summary>
        private static MapSurface TerrainSurface(Vector3 cell)
        {
            var heightmap = Heightmap.FindHeightmap(cell);
            if (heightmap == null) return MapSurface.Grass;

            var paint = heightmap.GetPaintMask(cell);

            // All channels high is deep snow, a biome effect rather than a painted surface;
            // treating it as the unpainted base keeps a mountain village from reading as paved.
            if (paint.r > 0.5f && paint.g > 0.5f && paint.b > 0.5f) return MapSurface.Grass;

            var strongest = Mathf.Max(paint.r, Mathf.Max(paint.g, paint.b));
            if (strongest < 0.5f) return MapSurface.Grass;
            if (Mathf.Approximately(strongest, paint.b)) return MapSurface.Paved;
            if (Mathf.Approximately(strongest, paint.g)) return MapSurface.Cultivated;
            return MapSurface.Dirt;
        }

        /// <summary>
        ///     The floor piece under a cell, by name. Raycast rather than looked up, because the
        ///     region graph knows a cell is "piece" but not which piece — the bake throws that
        ///     away once it has the geometry.
        /// </summary>
        private static MapSurface PieceSurface(Vector3 cell)
        {
            // Start above the cell and look down: the cell centre sits ON the floor, so a ray
            // cast from exactly there can start inside the collider and miss it.
            var from = cell + Vector3.up * 1f;
            if (!Physics.Raycast(from, Vector3.down, out var hit, 3f, s_pieceMask))
                return MapSurface.Other;

            var piece = hit.collider.GetComponentInParent<Piece>();
            if (piece == null) return MapSurface.Other;

            var name = piece.gameObject.name.ToLowerInvariant();
            if (name.Contains("stone") || name.Contains("paved") || name.Contains("marble"))
                return MapSurface.StoneFloor;
            if (name.Contains("wood") || name.Contains("plank") || name.Contains("straw"))
                return MapSurface.WoodFloor;

            return MapSurface.Other;
        }

    }
}
