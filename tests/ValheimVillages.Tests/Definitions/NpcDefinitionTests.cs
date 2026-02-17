using System.Collections.Generic;
using ValheimVillages.NPCs;
using ValheimVillages.Tags;
using Xunit;

namespace ValheimVillages.Tests.Definitions;

/// <summary>
/// Validate NPC type definition data integrity and tag references.
/// </summary>
public class NpcDefinitionTests
{
    [Fact]
    public void NpcTypeDefinition_GetNpcType_ParsesValidTypes()
    {
        var def = new NpcTypeDefinition { type = "Guard" };
        Assert.Equal(NpcType.Guard, def.GetNpcType());
    }

    [Fact]
    public void NpcTypeDefinition_GetNpcType_DefaultsToFarmerForInvalid()
    {
        var def = new NpcTypeDefinition { type = "InvalidType" };
        Assert.Equal(NpcType.Farmer, def.GetNpcType());
    }

    [Fact]
    public void NpcTypeDefinition_GetCategory_ParsesSpecialist()
    {
        var def = new NpcTypeDefinition { category = "Specialist" };
        Assert.Equal(NpcCategory.Specialist, def.GetCategory());
    }

    [Fact]
    public void NpcTypeDefinition_GetCategory_DefaultsToVillager()
    {
        var def = new NpcTypeDefinition { category = "Unknown" };
        Assert.Equal(NpcCategory.Villager, def.GetCategory());
    }

    [Fact]
    public void NpcTypeDefinition_GetScalingType_ParsesValid()
    {
        var def = new NpcTypeDefinition { scalingType = "Workbench" };
        Assert.Equal(BenefitScaling.Workbench, def.GetScalingType());
    }

    [Fact]
    public void NpcTypeDefinition_GetScalingType_DefaultsToComfort()
    {
        var def = new NpcTypeDefinition { scalingType = "Unknown" };
        Assert.Equal(BenefitScaling.Comfort, def.GetScalingType());
    }

    [Fact]
    public void NpcTypeDefinition_Tags_CanBeFilteredByNamespace()
    {
        var def = new NpcTypeDefinition
        {
            tags = new List<string>
            {
                "behavior:patrol",
                "behavior:craft",
                "listpanel:guardstatus",
                "contextmenu:workorder"
            }
        };

        var behaviors = TagParser.GetValues(def.tags, "behavior");
        Assert.Equal(2, behaviors.Count);
        Assert.Contains("patrol", behaviors);
        Assert.Contains("craft", behaviors);

        Assert.True(TagParser.HasTag(def.tags, "contextmenu", "workorder"));
        Assert.False(TagParser.HasTag(def.tags, "ability", "mountainstride"));
    }
}
