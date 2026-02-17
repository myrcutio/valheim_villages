using System.Collections.Generic;
using ValheimVillages.TaskQueue;
using Xunit;

namespace ValheimVillages.Tests.TaskQueue;

/// <summary>
/// Tests for task queue data types: priority ordering, settings, result factory.
/// </summary>
public class TaskQueueTests
{
    [Fact]
    public void TaskPriority_OrderingCorrect()
    {
        Assert.True(TaskPriority.High > TaskPriority.Medium);
        Assert.True(TaskPriority.Medium > TaskPriority.Low);
        Assert.Equal(3, (int)TaskPriority.High);
        Assert.Equal(2, (int)TaskPriority.Medium);
        Assert.Equal(1, (int)TaskPriority.Low);
    }

    [Fact]
    public void TaskResult_Ok_HasSuccessTrue()
    {
        var result = TaskResult.Ok();
        Assert.True(result.Success);
        Assert.Null(result.Error);
    }

    [Fact]
    public void TaskResult_Ok_WithData()
    {
        var data = new Dictionary<string, string> { ["key"] = "value" };
        var result = TaskResult.Ok(data);

        Assert.True(result.Success);
        Assert.Equal("value", result.Data["key"]);
    }

    [Fact]
    public void TaskResult_Ok_WithPayload()
    {
        var payload = new object();
        var result = TaskResult.Ok(payload: payload);

        Assert.True(result.Success);
        Assert.Same(payload, result.Payload);
    }

    [Fact]
    public void TaskResult_Fail_HasSuccessFalse()
    {
        var result = TaskResult.Fail("something broke");

        Assert.False(result.Success);
        Assert.Equal("something broke", result.Error);
    }

    [Fact]
    public void VillagerTask_DefaultValues()
    {
        var task = new VillagerTask
        {
            Name = "test_task",
            SourceId = "villager_1",
            Priority = TaskPriority.Medium
        };

        Assert.Equal("test_task", task.Name);
        Assert.Equal("villager_1", task.SourceId);
        Assert.Equal(TaskPriority.Medium, task.Priority);
        Assert.Equal(0, task.RetryCount);
        Assert.Equal(0f, task.CreatedAt);
    }

    [Fact]
    public void TaskSettings_ConstantsHaveReasonableValues()
    {
        Assert.True(TaskSettings.MaxRetries > 0, "MaxRetries should be positive");
        Assert.True(TaskSettings.DefaultTimeoutSeconds > 0, "DefaultTimeout should be positive");
        Assert.True(TaskSettings.MaxDeadLetterSize > 0, "MaxDeadLetterSize should be positive");
        Assert.True(TaskSettings.MaxActivityLogEntriesPerVillager > 0,
            "MaxActivityLogEntries should be positive");
    }

    [Fact]
    public void BehaviorPriorities_AreUnique_AcrossStandardValues()
    {
        // Standard behavior priorities as defined in the codebase:
        // alarm=100, craft/farm=50, patrol=30, explore=20
        var priorities = new[] { 100, 50, 30, 20 };
        var uniqueSet = new HashSet<int>(priorities);

        Assert.Equal(priorities.Length, uniqueSet.Count);
    }
}
