using Xunit;

namespace ValheimVillages.Tests.Scheduling;

/// <summary>
///     Groups every test class that touches <c>TaskBoard</c>'s static state.
///
///     <para>
///     xUnit's unit of parallelism is the collection, and by default each class is its
///     own. <c>SchedulerSelectionTests</c> calls <c>TaskBoard.Clear()</c> to shed stale
///     claims, which also wipes the block registry — for every village, not just its own
///     — so it could erase a block that <c>TaskBoardBlockingTests</c> had just recorded
///     and was about to assert on. Unique village ids don't help against a wholesale
///     Clear. Sharing one collection makes those classes run sequentially.
///     </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class TaskBoardCollection
{
    public const string Name = "TaskBoard static state";
}
