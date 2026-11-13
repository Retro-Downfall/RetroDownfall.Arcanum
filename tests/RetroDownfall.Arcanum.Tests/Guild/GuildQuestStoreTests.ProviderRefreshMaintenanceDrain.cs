using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_maintenance_drain_and_host_lock_wait_for_real_refresh_after_terminal_or_expired_lease(bool expireLease)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        scenario.Settings.Providers[0].CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N");

        GuildSpendRefreshConfiguration configuration = new(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(scenario.Settings));

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

        using ProviderCredentialStore credentials = new(os, new EphemeralDataProtectionProvider());

        ProviderApiKeyResolver resolver = new(credentials);

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore inner = new(db, scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = new(inner);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-maintenance-" + Guid.NewGuid().ToString("N"));

        var locked = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(locked.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = locked.Value;

        BlockingRefreshStream stream = new();

        GuildSpendRefreshService service = new(scenario.Fixture.Store, configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new BlockingRefreshHandler(stream)),
            new RefreshTestReadiness(hostLock), scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(),
            TimeProvider.System, new GuildSpendRefreshTracker());

        Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(scenario.Fixture.Caller, scenario.QuestId,
            scenario.EffectId, Guid.NewGuid(), default);

        Task<Result>? drain = null;

        IGrimoireClosingOwner? closing = null;

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await operations.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Guid operationId = operations.HeartbeatOperationId;

            LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inner.GetAsync(operationId));

            if (expireLease)
            {
                await using SqliteCommand expire = connection.CreateCommand();

                expire.CommandText = "UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id=$operation";

                expire.Parameters.AddWithValue("$operation", operationId.ToString("N"));

                Assert.Equal(1, await expire.ExecuteNonQueryAsync());
            }
            else
            {
                Assert.True(await inner.TryTransitionAsync(operationId, operation.Revision, operation.LeaseOwner,
                    LongRunningOperationState.Completed, DateTimeOffset.UtcNow));
            }

            // The remaining owner is the service's physical pin, not this outer lock handle.
            hostLock.Dispose();

            CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                new CovenantDigest(new byte[32]));

            Result<IGrimoireClosingOwner> begun = scenario.Fixture.Gate.BeginOrResumeExclusive(owner);

            Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

            closing = begun.Value;

            drain = scenario.Fixture.Gate.DrainRequestAndWorkAsync(closing, default).AsTask();

            Assert.False(drain.IsCompleted);

            AssertMaintenanceHostLockHeld(lockPath);

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(drain.IsCompleted);

            AssertMaintenanceHostLockHeld(lockPath);

            TaskCompletionSource heartbeatStopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using CancellationTokenRegistration stop = operations.HeartbeatStop.Register(() => heartbeatStopping.TrySetResult());

            stream.ReleaseDispose.TrySetResult();

            await heartbeatStopping.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(drain.IsCompleted);

            Assert.False(operations.HeartbeatFinished.Task.IsCompleted);

            AssertMaintenanceHostLockHeld(lockPath);

            operations.ReleaseHeartbeat.TrySetResult();

            await running.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);

            var successor = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

            Assert.True(successor.IsSuccess, successor.IsFailure ? successor.Error.Message : null);

            successor.Value.Dispose();
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            operations.ReleaseHeartbeat.TrySetResult();

            await running;

            if (drain is not null) await drain;

            if (closing is not null) await closing.DisposeAsync();
        }
    }

    private static void AssertMaintenanceHostLockHeld(string path)
    {
        var contender = GuildWorkspaceLeaseHostLock.TryAcquire(path);

        using GuildWorkspaceLeaseHostLock? unexpected = contender.IsSuccess ? contender.Value : null;

        Assert.True(contender.IsFailure);
    }
}
