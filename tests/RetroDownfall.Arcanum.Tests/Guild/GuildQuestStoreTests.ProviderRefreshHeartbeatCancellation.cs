using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_refresh_recovery_cancellation_callback_fault_cannot_release_a_pending_heartbeat()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore inner = new(db, f.Scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = new ForwardingRefreshOperationStore(inner);

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid operationId = Guid.ParseExact(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()), "N");

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inner.GetAsync(operationId));

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess, acquired.IsFailure ? acquired.Error.Message : null);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        LongRunningOperationOwnership ownership = new();

        GuildSpendRefreshTracker tracker = new();

        BlockingRefreshStream stream = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new BlockingRefreshHandler(stream)),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, ownership,
            TimeProvider.System, tracker);

        const string callbackFailure = "heartbeat-stop-callback-failed";

        TaskCompletionSource callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationTokenRegistration throwingCallback = default;

        // Recovery owns an already acquired caller claim. Unlike RefreshAsync's public Result
        // mapping, this entry point propagates RunClaimedAsync's injected cancellation fault.
        Assert.True(ownership.TryClaim(operationId, out Guid reconcilerClaim));

        Task<LongRunningOperationRecoveryResult> running = service.RecoverClaimedAsync(
            f.Scenario.Fixture.Caller, operation, default);

        try
        {
            // Body I/O cannot finish until this test releases it. The real periodic monitor
            // must have entered this exact store's blocked renewal before we install the fault.
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await operations.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(operationId, operations.HeartbeatOperationId);

            Assert.True(operations.HeartbeatStop.CanBeCanceled);

            Assert.False(operations.HeartbeatStop.IsCancellationRequested);

            Assert.NotNull(operations.Renewal);

            Guid root = Assert.Single(tracker.ActiveRoots());

            throwingCallback = operations.HeartbeatStop.Register(() =>
            {
                callbackEntered.TrySetResult();

                throw new InvalidOperationException(callbackFailure);
            });

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Closing the owner makes the OS-lock contention assertion measure the service's
            // retained physical pin, rather than the still-open owner itself.
            hostLock.Dispose();

            stream.ReleaseDispose.TrySetResult();

            await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Entry alone precedes the throw. Registration disposal supplies the stronger
            // handshake: this throwing callback has physically returned, not merely started.
            await throwingCallback.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(operations.HeartbeatStop.IsCancellationRequested);

            // A bounded negative-completion assertion AFTER the positive body/callback gates.
            // Original code faults here instead of timing out; repaired code is waiting for
            // the deliberately unreleased renewal. This is not a sleep used to trigger work.
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.False(operations.ReleaseHeartbeat.Task.IsCompleted);

            Assert.True(ownership.IsClaimedBy(operationId, reconcilerClaim));

            Assert.Contains(root, tracker.ActiveRoots());

            var contender = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            using (GuildWorkspaceLeaseHostLock? unexpected = contender.IsSuccess ? contender.Value : null)
            {
                Assert.True(contender.IsFailure,
                    "The physical host pin was released before the blocked heartbeat completed.");
            }

            operations.ReleaseHeartbeat.TrySetResult();

            await operations.HeartbeatFinished.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // The renewal-finishing signal is not itself full monitor completion. Joining
            // the actual outer task proves that completion and preserves the original fault.
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(callbackFailure,
                Assert.IsType<InvalidOperationException>(Assert.Single(error.Flatten().InnerExceptions)).Message);

            // Recovery must leave its caller's exact claim alone. The service's own entry
            // and physical pin unwind only after joining, then this test releases the claim.
            Assert.True(ownership.IsClaimedBy(operationId, reconcilerClaim));

            Assert.Empty(tracker.ActiveRoots());

            var successor = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            using (GuildWorkspaceLeaseHostLock? successorLock = successor.IsSuccess ? successor.Value : null)
            {
                Assert.True(successor.IsSuccess, successor.IsFailure ? successor.Error.Message : null);
            }
        }
        finally
        {
            // Always unblock I/O/disposal and heartbeat renewal, including on RED assertions,
            // then join the exposed outer and store tasks. The broken RED outer does not
            // expose/join the private Monitor task; only GREEN outer join proves that part.
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            operations.ReleaseHeartbeat.TrySetResult();

            try
            {
                try
                {
                    await running;
                }
                catch (AggregateException error) when (
                    error.Flatten().InnerExceptions.Count == 1
                    && error.Flatten().InnerExceptions[0] is InvalidOperationException failure
                    && failure.Message == callbackFailure)
                {
                    // Already asserted on the success path; during RED preserve the primary
                    // assertion while still observing the exact deliberately injected fault.
                }
            }
            finally
            {
                try
                {
                    if (operations.Renewal is not null) await operations.Renewal;
                }
                finally
                {
                    try
                    {
                        await throwingCallback.DisposeAsync();
                    }
                    finally
                    {
                        ownership.Release(operationId, reconcilerClaim);
                    }
                }
            }
        }
    }
}
