using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Owner_cleanup_joins_its_real_provider_attempt_while_another_root_finishes()
    {
        await using SpendScenario first = await SpendScenario.CreateAsync();

        Fixture secondFixture = Fixture.ForExistingDatabase(first.Connection.ConnectionString);

        await using SpendScenario second = await SpendScenario.CreateAsync(fixture: secondFixture);

        Assert.NotEqual(first.QuestId, second.QuestId);

        foreach (SpendScenario scenario in new[] { first, second })
            scenario.Settings.Providers[0].CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N");

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

        using ProviderCredentialStore credentials = new(os, new EphemeralDataProtectionProvider());

        ProviderApiKeyResolver resolver = new(credentials);

        await using SqliteConnection firstConnection = await first.Fixture.OpenAsync();

        await using SqliteConnection secondConnection = await second.Fixture.OpenAsync();

        await using ArcanumDbContext firstDb = GrimoireFixture.CreateContext(firstConnection);

        await using ArcanumDbContext secondDb = GrimoireFixture.CreateContext(secondConnection);

        using LongRunningOperationStore firstOperations = new(firstDb, first.Fixture.Connections);

        using LongRunningOperationStore secondOperations = new(secondDb, second.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-owner-roots-" + Guid.NewGuid().ToString("N"));

        var locked = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(locked.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = locked.Value;

        GuildSpendRefreshTracker tracker = new();

        OwnerRefreshStream firstStream = new();

        OwnerRefreshStream secondStream = new();

        GuildSpendRefreshService firstService = new(first.Fixture.Store,
            new GuildSpendRefreshConfiguration(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(first.Settings)), resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new OwnerRefreshHandler(firstStream)),
            new RefreshTestReadiness(hostLock), first.Fixture.Gate, firstOperations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        GuildSpendRefreshService secondService = new(second.Fixture.Store,
            new GuildSpendRefreshConfiguration(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(second.Settings)), resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new OwnerRefreshHandler(secondStream)),
            new RefreshTestReadiness(hostLock), second.Fixture.Gate, secondOperations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        Task<Result<GuildSpendRefreshResult>> firstRun = firstService.RefreshAsync(first.Fixture.Caller, first.QuestId,
            first.EffectId, Guid.NewGuid(), default);

        Task<Result<GuildSpendRefreshResult>> secondRun = secondService.RefreshAsync(second.Fixture.Caller, second.QuestId,
            second.EffectId, Guid.NewGuid(), default);

        Task<Result<GuildCleanupOutcome>>? cleanup = null;

        try
        {
            await Task.WhenAll(firstStream.Entered.Task, secondStream.Entered.Task).WaitAsync(TimeSpan.FromSeconds(15));

            await using SqliteCommand command = firstConnection.CreateCommand();

            command.CommandText = "SELECT SessionId,RootQuestId FROM guild_quests WHERE Id=$id";

            command.Parameters.AddWithValue("$id", first.QuestId.ToString("D").ToUpperInvariant());

            string session;

            string firstRoot;

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());

                session = reader.GetString(0);

                firstRoot = reader.GetString(1);
            }

            command.CommandText = "SELECT RootQuestId FROM guild_quests WHERE Id=$other";

            command.Parameters.AddWithValue("$other", second.QuestId.ToString("D").ToUpperInvariant());

            Assert.NotEqual(firstRoot, Assert.IsType<string>(await command.ExecuteScalarAsync()));

            using (CovenantSqliteConnectionInitializer.Instance.Authorize(firstConnection, CovenantSqliteAuthorizationKind.SessionRetention))
            {
                command.CommandText = "DELETE FROM Sessions WHERE Id=$session";

                command.Parameters.AddWithValue("$session", session);

                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            OwnerCleanupSealWitness witness = new();

            GuildOwnerCleanupCoordinator coordinator = new(first.Fixture.Connections, first.Fixture.Gate,
                new OwnerCleanupPurger(), FixtureLabeledArtifactGuard.For(firstDb),
                [new GuildSignetCleanupParticipant(first.Fixture.Gate), witness], TimeProvider.System,
                new GuildSpendRefreshCleanupParticipant(tracker));

            cleanup = coordinator.RunBatchAsync(8, 8, default);

            await firstStream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(secondStream.Cancelled.Task.IsCompleted);

            Assert.False(cleanup.IsCompleted);

            secondStream.ReleaseRead.TrySetResult();

            await secondStream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            secondStream.ReleaseDispose.TrySetResult();

            await secondRun.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(cleanup.IsCompleted);

            Assert.False(witness.SealEntered.Task.IsCompleted);

            firstStream.ReleaseRead.TrySetResult();

            await firstStream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            firstStream.ReleaseDispose.TrySetResult();

            Result<GuildCleanupOutcome> result = await cleanup.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            await firstRun.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            firstStream.ReleaseRead.TrySetResult();

            firstStream.ReleaseDispose.TrySetResult();

            secondStream.ReleaseRead.TrySetResult();

            secondStream.ReleaseDispose.TrySetResult();

            await Task.WhenAll(firstRun, secondRun);

            if (cleanup is not null) await cleanup;
        }
    }
}
