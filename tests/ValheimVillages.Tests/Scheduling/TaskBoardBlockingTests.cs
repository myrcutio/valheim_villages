using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Scheduling;
using Xunit;
using Graph = ValheimVillages.Villager.AI.Navigation.RegionGraph;
using Link = ValheimVillages.Villager.AI.Navigation.RegionLink;

namespace ValheimVillages.Tests.Scheduling;

/// <summary>
///     Blocking of tasks a behavior reported UNREACHABLE.
///
///     <para>
///     The point of the block is that an unreachable task must stop consuming a dispatch
///     slot every tick, and must come back the moment — and only the moment — the village
///     could plausibly have changed. "Could have changed" is exactly
///     <see cref="Graph.Generation" /> advancing, which happens when a partition commits a
///     new graph, which is what a player placing a piece or reshaping terrain triggers.
///     </para>
///
///     <para>
///     <see cref="TaskBoard" /> is static, so every test here uses its own village id
///     rather than clearing shared state out from under a parallel test. That alone is
///     not enough: xUnit parallelizes test CLASSES, and SchedulerSelectionTests calls
///     <c>TaskBoard.Clear()</c>, which wipes the block registry wholesale regardless of
///     village id. Both classes therefore share one collection so they never interleave.
///     </para>
/// </summary>
[Collection(TaskBoardCollection.Name)]
public class TaskBoardBlockingTests
{
    private static CandidateTask Row(string sourceId)
        => new()
        {
            SourceId = sourceId,
            Kind = TaskKind.RepairPiece,
            Position = new Vector3(1f, 0f, 1f),
            Priority = 0.5f,
            ExpiresAt = 0f,
            RequiredCapability = "repair",
        };

    private static List<string> SourceIds(IEnumerable<CandidateTask> tasks)
    {
        var ids = new List<string>();
        foreach (var t in tasks) ids.Add(t.SourceId);
        ids.Sort();
        return ids;
    }

    [Fact]
    public void BlockedTask_IsExcludedFromSelection_ButStillVisibleInDiagnostics()
    {
        const string village = "v-block-excluded";
        TaskBoard.Upsert(village, Row("piece:reachable"));
        TaskBoard.Upsert(village, Row("piece:walled-off"));

        TaskBoard.MarkBlocked(village, "piece:walled-off", 7u);

        Assert.Equal(
            new List<string> { "piece:reachable" },
            SourceIds(TaskBoard.UnblockedTasks(village, 0f, 7u)));

        // The dump command must still show it — a row that vanished entirely would be
        // indistinguishable from one the producers never minted.
        Assert.Equal(
            new List<string> { "piece:reachable", "piece:walled-off" },
            SourceIds(TaskBoard.AllTasks(village, 0f)));
    }

    [Fact]
    public void BlockedTask_ReturnsToSelection_WhenGraphGenerationAdvances()
    {
        const string village = "v-block-repartition";
        TaskBoard.Upsert(village, Row("piece:walled-off"));
        TaskBoard.MarkBlocked(village, "piece:walled-off", 3u);

        Assert.Empty(TaskBoard.UnblockedTasks(village, 0f, 3u));

        // A repartition committed a new graph: the walls may have moved, so the verdict is
        // no longer trustworthy and the task has to be offered again.
        Assert.Single(TaskBoard.UnblockedTasks(village, 0f, 4u));
    }

    [Fact]
    public void StaleBlock_IsDroppedOnFirstCheck_SoItCannotResurrect()
    {
        const string village = "v-block-stale-evict";
        TaskBoard.Upsert(village, Row("piece:walled-off"));
        TaskBoard.MarkBlocked(village, "piece:walled-off", 1u);

        // Observed under a newer graph: the entry is stale and must be evicted, not merely
        // ignored — otherwise a later query that happened to quote generation 1 again
        // (counter wrap, a graph rebuilt from scratch) would silently re-block the task.
        Assert.False(TaskBoard.IsBlocked(village, "piece:walled-off", 2u));
        Assert.False(TaskBoard.IsBlocked(village, "piece:walled-off", 1u));
    }

    [Fact]
    public void RemovingTask_ClearsItsBlock_SoTheNextIncarnationStartsClean()
    {
        const string village = "v-block-remove";
        TaskBoard.Upsert(village, Row("piece:1"));
        TaskBoard.MarkBlocked(village, "piece:1", 5u);
        Assert.True(TaskBoard.IsBlocked(village, "piece:1", 5u));

        // Producer dropped the row (piece repaired or destroyed). The same SourceId coming
        // back later is a fresh situation and must not inherit the old verdict.
        TaskBoard.Remove(village, "piece:1");
        TaskBoard.Upsert(village, Row("piece:1"));

        Assert.False(TaskBoard.IsBlocked(village, "piece:1", 5u));
        Assert.Single(TaskBoard.UnblockedTasks(village, 0f, 5u));
    }

    [Fact]
    public void BlockedCount_CountsOnlyBlocksAgainstTheCurrentGraph()
    {
        const string village = "v-block-count";
        TaskBoard.Upsert(village, Row("piece:a"));
        TaskBoard.Upsert(village, Row("piece:b"));
        TaskBoard.MarkBlocked(village, "piece:a", 9u);
        TaskBoard.MarkBlocked(village, "piece:b", 9u);

        Assert.Equal(2, TaskBoard.BlockedCount(village, 9u));
        Assert.Equal(0, TaskBoard.BlockedCount(village, 10u));
    }

    [Fact]
    public void CommittingAGraph_AdvancesGeneration()
    {
        // This is the whole unblock trigger: the partition handler calls SetGraph when it
        // commits, so "a repartition happened" and "Generation moved" are the same event.
        var graph = new Graph();
        var before = graph.Generation;

        graph.SetGraph(
            new HashSet<string> { "r1" },
            new List<Link>(),
            new Dictionary<string, Vector3> { ["r1"] = new(0f, 0f, 0f) },
            new Dictionary<long, string>());

        Assert.Equal(before + 1u, graph.Generation);

        graph.SetGraph(
            new HashSet<string> { "r1" },
            new List<Link>(),
            new Dictionary<string, Vector3> { ["r1"] = new(0f, 0f, 0f) },
            new Dictionary<long, string>());

        Assert.Equal(before + 2u, graph.Generation);
    }
}
