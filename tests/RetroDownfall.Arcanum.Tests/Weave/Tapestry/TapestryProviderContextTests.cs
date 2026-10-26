using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed class TapestryProviderContextTests
{
    [Fact]
    public void NestedBuilds_RestoreThePreviousObservationAndClearAtExit()
    {
        Assert.Null(TapestryBuildContext.Current);

        TapestryBuildObservation outer = Observation("outer");

        using (TapestryBuildContext.Enter(outer))
        {
            Assert.Same(outer, TapestryBuildContext.Current);

            TapestryBuildObservation inner = Observation("inner");

            using (TapestryBuildContext.Enter(inner))
            {
                Assert.Same(inner, TapestryBuildContext.Current);
            }

            Assert.Same(outer, TapestryBuildContext.Current);
        }

        Assert.Null(TapestryBuildContext.Current);
    }

    [Fact]
    public async Task CancellationAndParallelBuilds_DoNotLeakOrBorrowObservations()
    {
        using var cancellation = new CancellationTokenSource();

        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = Task.Run(async () =>
        {
            using (TapestryBuildContext.Enter(Observation("first")))
            {
                arrived.SetResult();

                await release.Task;

                Assert.Equal("first", Assert.IsType<TapestryBuildObservation>(TapestryBuildContext.Current).Snapshot.Scope.Id);

                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(() => cancellation.Token.ThrowIfCancellationRequested());
            }

            Assert.Null(TapestryBuildContext.Current);
        });

        await arrived.Task;

        Assert.Null(TapestryBuildContext.Current);

        using (TapestryBuildContext.Enter(Observation("second")))
        {
            release.SetResult();

            await first;

            Assert.Equal("second", Assert.IsType<TapestryBuildObservation>(TapestryBuildContext.Current).Snapshot.Scope.Id);
        }

        Assert.Null(TapestryBuildContext.Current);
    }

    private static TapestryBuildObservation Observation(string id) => new(
        new(new(TapestryScopeKind.Workspace, id), 7, 2, new(12, "fingerprint", false)),
        "summary-provider", "summary-model", "embedding-provider", "embedding-model");
}
