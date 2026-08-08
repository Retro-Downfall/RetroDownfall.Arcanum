/// <summary>
/// Serializes tests that spawn real operating-system process trees.
///
/// These tests start actual child processes, kill process groups, and then assert that a
/// descendant was reaped within a wall-clock deadline. Those deadlines are liveness guards — they
/// exist so a genuine hang fails instead of blocking forever — but they are only meaningful while
/// the machine can schedule the spawn and the reap promptly. Running them alongside several hundred
/// other test classes makes process creation and signal delivery compete for the same CPU and OS
/// process table, so a deadline that is generous on an idle machine becomes marginal under a full
/// suite, and the test fails for a reason that has nothing to do with the code under test.
///
/// <c>ChildProcessFilesystemJailTests</c> and <c>WorkspaceCheckToolTests</c> already got this
/// guarantee incidentally by living in the <c>ProcessEnvironment</c> collection; this collection
/// extends it to the process tests that had no collection at all.
/// </summary>
[CollectionDefinition(ChildProcessCollectionName.Value, DisableParallelization = true)]
public sealed class ChildProcessCollection : ICollectionFixture<object>
{
}

internal static class ChildProcessCollectionName
{

    internal const string Value = "ChildProcess";

}
