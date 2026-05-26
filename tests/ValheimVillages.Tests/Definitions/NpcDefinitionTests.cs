using ValheimVillages.Schemas;
using ValheimVillages.Tags;
using Xunit;

namespace ValheimVillages.Tests.Definitions;

/// <summary>
///     Validate villager definition data integrity and tag references.
/// </summary>
public class NpcDefinitionTests
{
    [Fact]
    public void VillagerDef_TypeIsString()
    {
        var def = new VillagerDef { type = "Guard" };
        Assert.Equal("Guard", def.type);
    }

    [Fact]
    public void VillagerDef_CategoryIsString()
    {
        var def = new VillagerDef { category = "Specialist" };
        Assert.Equal("Specialist", def.category);
    }

    [Fact]
    public void VillagerDef_ScalingTypeIsString()
    {
        var def = new VillagerDef { scalingType = "Workbench" };
        Assert.Equal("Workbench", def.scalingType);
    }

    [Fact]
    public void VillagerDef_ScalingTypeDefaultsToComfort()
    {
        var def = new VillagerDef();
        Assert.Equal("Comfort", def.scalingType);
    }

    [Fact]
    public void VillagerDef_Tags_CanBeFilteredByNamespace()
    {
        var def = new VillagerDef
        {
            tags = new List<string>
            {
                "behavior:patrol",
                "behavior:craft",
                "listpanel:guardstatus",
                "tab:workorder",
            },
        };

        var behaviors = TagParser.GetValues(def.tags, "behavior");
        Assert.Equal(2, behaviors.Count);
        Assert.Contains("patrol", behaviors);
        Assert.Contains("craft", behaviors);

        Assert.True(TagParser.HasTag(def.tags, "tab", "workorder"));
        Assert.False(TagParser.HasTag(def.tags, "ability", "mountainstride"));
    }
}