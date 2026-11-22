using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Guild;
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

        await using SpendScenario second = await SpendScenario.CreateAsync(fixture: secondFixture,
            beforeEffectSelection: async connection =>
            {
                await using SqliteCommand selection = connection.CreateCommand();

                selection.CommandText = "SELECT EffectId FROM guild_spend_sources";

                string firstId = first.EffectId.ToString("D").ToUpperInvariant();

                if (!Equals(firstId, await selection.ExecuteScalarAsync()))
                {
                    selection.CommandText = "PRAGMA reverse_unordered_selects=ON";

                    await selection.ExecuteNonQueryAsync();
                }

                selection.CommandText = "SELECT EffectId FROM guild_spend_sources";

                Assert.Equal(firstId, await selection.ExecuteScalarAsync());
            });

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

        OwnerRefreshStream secondStream = new(Encoding.UTF8.GetBytes("""
            {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
            "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
            "native_tokens_prompt":110,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
            "num_media_completion":0,"num_media_prompt":0,"total_cost":0.30}}
            """));

        GuildSpendRefreshService firstService = new(first.Fixture.Store,
            new GuildSpendRefreshConfiguration(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(first.Settings)), resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new OwnerRefreshHandler(firstStream)),
            new RefreshTestReadiness(hostLock), first.Fixture.Gate, firstOperations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        GuildSpendRefreshService secondService = new(second.Fixture.Store,
            new GuildSpendRefreshConfiguration(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(second.Settings)), resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new OwnerRefreshHandler(secondStream)),
            new RefreshTestReadiness(hostLock), second.Fixture.Gate, secondOperations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        (Guid firstEffect, Guid firstSource) = await IssuedProviderEffectAsync(firstConnection, first);

        (Guid secondEffect, Guid secondSource) = await IssuedProviderEffectAsync(secondConnection, second);

        Assert.NotEqual(firstEffect, secondEffect);

        Assert.NotEqual(firstSource, secondSource);

        Task<Result<GuildSpendRefreshResult>> firstRun = firstService.RefreshAsync(first.Fixture.Caller, first.QuestId,
            firstEffect, Guid.NewGuid(), default);

        Task<Result<GuildSpendRefreshResult>>? secondRun = null;

        Task<Result<GuildCleanupOutcome>>? cleanup = null;

        try
        {
            // Establish each admission before starting the next credential observation. Both live
            // provider bodies are held together when owner cleanup starts.
            await WaitForRefreshBodyAsync(firstStream, firstRun);

            secondRun = secondService.RefreshAsync(second.Fixture.Caller, second.QuestId, secondEffect, Guid.NewGuid(), default);

            await WaitForRefreshBodyAsync(secondStream, secondRun);

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

            Result<GuildSpendRefreshResult> secondResult = await secondRun.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(secondResult.IsSuccess, secondResult.IsFailure ? secondResult.Error.Message : null);

            Assert.NotNull(secondResult.Value.Receipt);

            Assert.False(secondResult.Value.RequiresAttention);

            Assert.Equal(second.QuestId, secondResult.Value.Receipt.QuestId);

            Assert.Equal(secondEffect, secondResult.Value.Receipt.EffectId);

            Assert.Equal(GuildSpendOrigin.ProviderReported, secondResult.Value.Receipt.Origin);

            await using (SqliteCommand receipt = secondConnection.CreateCommand())
            {
                receipt.CommandText = """
                    SELECT COUNT(*) FROM guild_spend_receipts r JOIN guild_spend_refreshes f ON f.AcceptedReceiptId=r.ReceiptId
                    WHERE r.ReceiptId=$receipt AND r.EffectId=$effect AND f.OperationId=$operation
                    """;

                receipt.Parameters.AddWithValue("$receipt", secondResult.Value.Receipt.ReceiptId.ToString("D").ToUpperInvariant());

                receipt.Parameters.AddWithValue("$effect", secondEffect.ToString("D").ToUpperInvariant());

                receipt.Parameters.AddWithValue("$operation", secondResult.Value.OperationId.ToString("N"));

                Assert.Equal(1L, await receipt.ExecuteScalarAsync());
            }

            Assert.Equal(firstEffect, first.EffectId);

            Assert.Equal(secondEffect, second.EffectId);

            Assert.Equal("Guild.SpendGenerationMissing", second.PendingReason);

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

            await Task.WhenAll(firstRun, secondRun ?? Task.CompletedTask, cleanup ?? Task.CompletedTask);
        }
    }

    private static async Task WaitForRefreshBodyAsync(OwnerRefreshStream stream, Task<Result<GuildSpendRefreshResult>> run)
    {
        Task reached = await Task.WhenAny(stream.Entered.Task, run).WaitAsync(TimeSpan.FromSeconds(15));

        if (reached == stream.Entered.Task) return;

        Result<GuildSpendRefreshResult> early = await run;

        Assert.Fail(early.IsFailure ? $"Refresh ended before the body read with {early.Error.Code}."
            : $"Refresh ended before the body read. InFlight={early.Value.InFlight}, RequiresAttention={early.Value.RequiresAttention}, ErrorCode={early.Value.ErrorCode}.");
    }

    private static async Task<(Guid Effect, Guid Source)> IssuedProviderEffectAsync(SqliteConnection connection, SpendScenario scenario)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT s.EffectId,s.SourceId,e.QuestId,e.DispatchId FROM guild_spend_sources s
            JOIN guild_budget_effects e ON e.EffectId=s.EffectId
            WHERE e.QuestId=$quest AND e.DispatchId=$dispatch AND e.KindCode=0 AND s.SourceKindCode=1
            """;

        command.Parameters.AddWithValue("$quest", scenario.QuestId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$dispatch", scenario.DispatchId.ToString("D").ToUpperInvariant());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Guid effect = Guid.Parse(reader.GetString(0));

        Guid source = Guid.Parse(reader.GetString(1));

        Assert.NotEqual(Guid.Empty, source);

        Assert.Equal(scenario.QuestId, Guid.Parse(reader.GetString(2)));

        Assert.Equal(scenario.DispatchId, Guid.Parse(reader.GetString(3)));

        Assert.False(await reader.ReadAsync());

        return (effect, source);
    }

}
