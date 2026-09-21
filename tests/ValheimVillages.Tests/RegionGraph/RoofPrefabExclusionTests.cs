using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Roof pieces are excluded from the villager NavMesh bake by NAME, because nothing else
///     about them says "roof": a Valheim roof is a pitched MESH wrapped in an axis-aligned BOX
///     collider, and the box's top face is dead level. Every slope test in the pipeline —
///     including RegionBuilder's 27 degree cutoff — therefore passes a 45 degree roof as clean
///     floor. Measured at (-47,-409): wood_roof x7 + wood_roof_45 x3 produced a 37.79m2
///     walkable region at y=37.4 that no villager could path to from anywhere on the ground.
///
///     <para>The whole exclusion is two IndexOf calls, and the interesting half is the
///     EXCEPTION: "wall_roof" pieces are gable-end walls, not roofing. Dropping those from the
///     bake would open a hole a villager walks straight through. The names below are the real
///     prefab list from <c>search_id roof</c>, not remembered ones.</para>
/// </summary>
public class RoofPrefabExclusionTests
{
    [Theory]
    // Wood.
    [InlineData("wood_roof")]
    [InlineData("wood_roof_45")]
    [InlineData("wood_roof_67")]
    [InlineData("wood_roof_icorner")]
    [InlineData("wood_roof_icorner_45")]
    [InlineData("wood_roof_ocorner")]
    [InlineData("wood_roof_top")]
    [InlineData("wood_roof_top_67")]
    // Darkwood.
    [InlineData("darkwood_roof")]
    [InlineData("darkwood_roof_45")]
    [InlineData("darkwood_roof_icorner_67")]
    [InlineData("darkwood_roof_top_45")]
    // Everything else that roofs a building.
    [InlineData("turf_roof")]
    [InlineData("turf_roof_top")]
    [InlineData("goblin_roof_45d")]
    [InlineData("goblin_roof_cap")]
    [InlineData("piece_grausten_roof_45")]
    [InlineData("piece_grausten_roof_45_arch_corner2")]
    [InlineData("Ashlands_ArchRoof")]
    [InlineData("OLD_wood_roof_ocorner")]
    // Case is not meaningful in prefab names; the rule must not depend on it.
    [InlineData("WOOD_ROOF")]
    [InlineData("Wood_Roof_45")]
    public void RoofPieces_AreExcludedFromTheBake(string prefabName)
    {
        Assert.True(NavMeshBakeManager.IsRoofPrefabName(prefabName),
            $"{prefabName} is roofing and must not bake as walkable floor");
    }

    [Theory]
    // "wall_roof" is the gable end — a vertical WALL shaped to sit under a roof. Excluding it
    // would delete a wall from the bake and let villagers walk through the side of a house.
    [InlineData("wood_wall_roof")]
    [InlineData("wood_wall_roof_45")]
    [InlineData("wood_wall_roof_45_upsidedown")]
    [InlineData("wood_wall_roof_top_67")]
    [InlineData("ashwood_wall_roof_26")]
    [InlineData("scale_wall_roof_67_flipped")]
    [InlineData("OLD_wood_wall_roof")]
    // Reads as roofing by name and is a wall by function — the one that makes the "wall"
    // exception load-bearing rather than cosmetic.
    [InlineData("turf_roof_wall")]
    public void GableEndWalls_StayInTheBake(string prefabName)
    {
        Assert.False(NavMeshBakeManager.IsRoofPrefabName(prefabName),
            $"{prefabName} is a wall; dropping it would open a hole in the building");
    }

    [Theory]
    [InlineData("wood_floor")]
    [InlineData("wood_floor_1x1")]
    [InlineData("stone_stair")]
    [InlineData("wood_stair")]
    [InlineData("piece_workbench")]
    [InlineData("stake_wall")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElse_IsUntouched(string? prefabName)
    {
        Assert.False(NavMeshBakeManager.IsRoofPrefabName(prefabName!));
    }
}
