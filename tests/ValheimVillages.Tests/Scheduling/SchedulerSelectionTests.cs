using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Scheduling;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;
using Graph = ValheimVillages.Villager.AI.Navigation.RegionGraph;

namespace ValheimVillages.Tests.Scheduling;

/// <summary>
///     Selection-level guarantees now that the scheduler is the ONLY thing that gives a
///     villager work. There is no self-discovery fallback any more, so "SelectBest returned
///     null" is not a degraded mode — it is a villager doing nothing at all. These pin the
///     cases where it must NOT return null, and the two filters that must still bite.
/// </summary>
// Calls TaskBoard.Clear(), which wipes shared static state other classes assert on.
[Collection(TaskBoardCollection.Name)]
public class SchedulerSelectionTests
{
    private static Graph PatchAtOrigin(string regionId = "r1")
    {
        var lookup = new Dictionary<long, string>();
        var hb = Graph.HeightBucket(0f);
        for (var gx = 0; gx < 4; gx++)
        for (var gz = 0; gz < 4; gz++)
            lookup[Graph.PackLookup(gx, gz, hb)] = regionId;

        var graph = new Graph();
        graph.SetGraph(
            new HashSet<string> { regionId },
            new List<RegionLink>(),
            new Dictionary<string, Vector3> { [regionId] = new(2f, 0f, 2f) },
            lookup);
        return graph;
    }

    private static CandidateTask CraftRow(string item, float priority, Vector3 pos, string owner = "farmer-1")
        => new()
        {
            SourceId = "craft:" + owner + ":" + item,
            Kind = TaskKind.CraftWork,
            Position = pos,
            Priority = priority,
            ExpiresAt = 0f,
            RequiredCapability = "craft",
            OwnerVillagerId = owner,
            TargetItemPrefab = item,
        };

    private static VillagerQuery QueryAt(Graph graph, Vector3 pos, string id = "farmer-1")
        => new()
        {
            VillagerId = id,
            Position = pos,
            Graph = graph,
            Triad = new[] { new Vector3(2f, 0f, 2f) },
            Capabilities = new HashSet<string> { "craft" },
            LastTaskKind = null,
        };

    [Fact]
    public void VillagerOffTheRegionGraph_StillGetsAPick()
    {
        var graph = PatchAtOrigin();
        var tasks = new List<CandidateTask>
        {
            CraftRow("CarrotSoup", 0.80f, new Vector3(1.5f, 0f, 1.5f)),
            CraftRow("MinceMeatSauce", 0.90f, new Vector3(2.5f, 0f, 2.5f)),
        };

        // The villager is standing somewhere the lookup grid never indexed — the exact
        // state that used to filter out every candidate and leave it idle forever.
        var query = QueryAt(graph, new Vector3(500f, 0f, 500f));

        var pick = TaskReranker.SelectBestExplained(in query, tasks, null, new RerankSettings(), now: 100f);

        Assert.NotNull(pick.Task);
    }

    [Fact]
    public void HighestDeficitWins_WhenBothAreEquallyFar()
    {
        var graph = PatchAtOrigin();
        var here = new Vector3(1.5f, 0f, 1.5f);
        var tasks = new List<CandidateTask>
        {
            CraftRow("CarrotSoup", 0.80f, here),
            CraftRow("MinceMeatSauce", 0.90f, here),
        };

        var query = QueryAt(graph, here);
        var pick = TaskReranker.SelectBestExplained(in query, tasks, null, new RerankSettings(), now: 100f);

        Assert.Equal("MinceMeatSauce", pick.Task?.TargetItemPrefab);
    }

    [Fact]
    public void MissingCapability_IsStillHardFiltered()
    {
        var graph = PatchAtOrigin();
        var here = new Vector3(1.5f, 0f, 1.5f);
        var query = QueryAt(graph, here);
        query.Capabilities = new HashSet<string> { "repair" }; // no "craft"

        var pick = TaskReranker.SelectBestExplained(
            in query, new List<CandidateTask> { CraftRow("CarrotSoup", 0.9f, here) },
            null, new RerankSettings(), now: 100f);

        Assert.Null(pick.Task);
    }

    [Fact]
    public void AnotherVillagersRow_IsStillHardFiltered()
    {
        var graph = PatchAtOrigin();
        var here = new Vector3(1.5f, 0f, 1.5f);

        var query = QueryAt(graph, here);
        var pick = TaskReranker.SelectBestExplained(
            in query,
            new List<CandidateTask> { CraftRow("CarrotSoup", 0.9f, here, owner: "someone-else") },
            null, new RerankSettings(), now: 100f);

        Assert.Null(pick.Task);
    }

    [Fact]
    public void DualEncoder_RetrievesAndReranks_ForAnOffGraphVillager()
    {
        TaskBoard.Clear(); // no stale claims from another test
        var graph = PatchAtOrigin();
        var tasks = new List<CandidateTask>
        {
            CraftRow("CarrotSoup", 0.80f, new Vector3(1.5f, 0f, 1.5f)),
            CraftRow("MinceMeatSauce", 0.90f, new Vector3(2.5f, 0f, 2.5f)),
        };

        var query = QueryAt(graph, new Vector3(500f, 0f, 500f));
        var pick = DualEncoderScheduler.SelectBestExplained(
            in query, tasks, null, new RerankSettings(), now: 100f);

        // Retrieval used to hand the rerank a set that the rerank then emptied; the whole
        // pipeline must survive an unresolved villager position end to end.
        Assert.NotNull(pick.Task);
    }
}
