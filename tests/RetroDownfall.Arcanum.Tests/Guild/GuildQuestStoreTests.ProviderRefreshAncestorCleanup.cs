using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
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
    public async Task Ancestor_erasure_joins_descendant_refresh_and_fresh_runtime_refuses_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new();

        ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [SpendModel],
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
            CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N") };

        settings.Providers = [provider];

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestStored child = await f.ChildAsync(root, await f.DriverAsync(root));

        Assert.NotEqual(root.Quest.SessionId, child.Quest.SessionId);

        GuildQuestDriver driver = await f.DriverAsync(child);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("ancestor-refresh"), child.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch.Value);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), child.Quest.Id, dispatch.Value.DispatchId, child.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(child.Quest.RunId, BillableOperationType.Chat, "router", SpendModel, "guild",
            now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

        var captured = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
            spendSource: new(provider, SpendModel, SpendSecret), spendPricing: "{}");

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

        using GuildSpendRefreshConfiguration configuration = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

        using ProviderCredentialStore credentialStore = new(os, new EphemeralDataProtectionProvider());

        ProviderApiKeyResolver resolver = new(credentialStore);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-ancestor-cleanup-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        byte[] payload = Encoding.UTF8.GetBytes("""
            {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
            "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
            "native_tokens_prompt":110,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
            "num_media_completion":0,"num_media_prompt":0,"total_cost":0.30}}
            """);

        OwnerRefreshStream stream = new(payload);

        GuildSpendRefreshTracker tracker = new();

        using LongRunningOperationStore operations = new(db, f.Connections);

        int gets = 0;

        GuildSpendRefreshService service = new(f.Store, configuration, resolver, new GuildOpenRouterSpendReader(new PublicSpendDns(), () =>
        {
            gets++;

            return new OwnerRefreshHandler(stream);
        }), new RefreshTestReadiness(hostLock), f.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, tracker);

        // A separately admitted, live guard delivers an already observed result directly to
        // the application writer after deletion. The real stream below tests cancellation.
        var currentness = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, resolver, "router", SpendModel, default);

        Assert.True(currentness.IsSuccess, currentness.IsFailure ? currentness.Error.Message : null);

        Guid writerRefresh = Guid.NewGuid();

        var admitted = await f.Store.AdmitProviderSpendRefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, writerRefresh, currentness.Value, default);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Guid writerOperation = admitted.Value.OperationId!.Value;

        var lease = await operations.TryAcquireLeaseAsync(writerOperation, "ancestor-writer", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(lease.Acquired);

        LongRunningOperationOwnership writerOwnership = new();

        Assert.True(writerOwnership.TryClaim(writerOperation, out Guid claim));

        Assert.True(tracker.TryEnter(root.Quest.Id, out var writerEntry));

        Assert.NotNull(writerEntry);

        Assert.True(f.Gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out var work));

        Assert.NotNull(work);

        Assert.True(work.TryBeginExternalEffectGroup(out var group));

        Assert.NotNull(group);

        var pin = hostLock.Pin();

        Assert.True(pin.IsSuccess);

        using IDisposable physical = pin.Value;

        using GuildSpendRefreshAttemptGuard guard = new(f.Gate, work.Generation, physical, writerEntry, writerOwnership, writerOperation);

        var verified = await f.Store.VerifyProviderSpendRefreshSourceAsync(f.Caller, child.Quest.Id, effect.EffectId,
            writerRefresh, "ancestor-writer", currentness.Value, guard, default);

        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Message : null);

        using SpendMetadataHandler observationHandler = new() { Body = Encoding.UTF8.GetString(payload) };

        var observed = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => observationHandler).ReadAsync(verified.Value, default);

        Assert.True(observed.IsSuccess, observed.IsFailure ? observed.Error.Message : null);

        Guid refreshId = Guid.NewGuid();

        Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, refreshId, default);

        Task<Result<GuildCleanupOutcome>>? cleanup = null;

        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await using SqliteCommand query = connection.CreateCommand();

            query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh AND EffectId=$effect";

            query.Parameters.AddWithValue("$refresh", refreshId.ToString("D").ToUpperInvariant());

            query.Parameters.AddWithValue("$effect", effect.EffectId.ToString("D").ToUpperInvariant());

            Guid operationId = Guid.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync()));

            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));

            long evidenceBefore = await f.CountAsync("guild_budget_evidence");

            query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

            long effectRevision = Assert.IsType<long>(await query.ExecuteScalarAsync());

            using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
            {
                query.CommandText = "DELETE FROM Sessions WHERE Id=$session";

                query.Parameters.AddWithValue("$session", root.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());

                Assert.Equal(1, await query.ExecuteNonQueryAsync());
            }

            var writerResult = await f.Store.ApplyProviderSpendRefreshAsync(f.Caller, child.Quest.Id, effect.EffectId,
                writerRefresh, "ancestor-writer", currentness.Value, observed.Value, guard, default);

            Assert.True(writerResult.IsFailure);

            Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, writerResult.Error.Code);

            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));

            Assert.Equal(evidenceBefore, await f.CountAsync("guild_budget_evidence"));

            query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

            Assert.Equal(effectRevision, await query.ExecuteScalarAsync());

            query.CommandText = "SELECT AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$writer";

            query.Parameters.AddWithValue("$writer", writerRefresh.ToString("D").ToUpperInvariant());

            Assert.Equal(DBNull.Value, await query.ExecuteScalarAsync());

            guard.Dispose();

            writerEntry.Dispose();

            Assert.True(writerOwnership.Release(writerOperation, claim));

            await group.DisposeAsync();

            await work.DisposeAsync();

            OwnerCleanupSealWitness witness = new();

            GuildOwnerCleanupCoordinator coordinator = new(f.Connections, f.Gate, new OwnerCleanupPurger(),
                FixtureLabeledArtifactGuard.For(db), [new GuildSignetCleanupParticipant(f.Gate), witness], TimeProvider.System,
                new GuildSpendRefreshCleanupParticipant(tracker));

            cleanup = coordinator.RunBatchAsync(8, 8, default);

            await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(cleanup.IsCompleted);

            Assert.False(witness.SealEntered.Task.IsCompleted);

            stream.ReleaseRead.TrySetResult();

            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(cleanup.IsCompleted);

            Assert.False(witness.SealEntered.Task.IsCompleted);

            stream.ReleaseDispose.TrySetResult();

            Result<GuildSpendRefreshResult> result = await running.WaitAsync(TimeSpan.FromSeconds(15));

            Result<GuildCleanupOutcome> cleaned = await cleanup.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(cleaned.IsSuccess, cleaned.IsFailure ? cleaned.Error.Message : null);

            Assert.True(witness.SealEntered.Task.IsCompleted);

            Assert.True(result.IsFailure || result.Value.Receipt is null);

            Assert.Equal(1L, await f.CountAsync("guild_spend_receipts"));

            Assert.Equal(evidenceBefore, await f.CountAsync("guild_budget_evidence"));

            query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

            Assert.Equal(effectRevision, await query.ExecuteScalarAsync());

            query.CommandText = "SELECT AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$refresh AND EffectId=$effect";

            Assert.Equal(DBNull.Value, await query.ExecuteScalarAsync());

            query.CommandText = "SELECT ActualCostUsd FROM BillableOperations";

            Assert.Equal(0m, ExactUsdText.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync())));

            // Reconstruct the service, operation store, ownership and tracker over persisted data.
            await using SqliteConnection freshConnection = await f.OpenAsync();

            await using ArcanumDbContext freshDb = GrimoireFixture.CreateContext(freshConnection);

            using LongRunningOperationStore freshOperations = new(freshDb, f.Connections);

            LongRunningOperationOwnership freshOwnership = new();

            GuildSpendRefreshService freshService = new(f.Store, configuration, resolver,
                new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
                new RefreshTestReadiness(hostLock), f.Gate, freshOperations, freshOwnership, TimeProvider.System, new GuildSpendRefreshTracker());

            var selected = await f.Store.SelectProviderSpendRefreshSourceAsync(f.Caller, child.Quest.Id, effect.EffectId, default);

            Assert.True(selected.IsFailure);

            Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, selected.Error.Code);

            var denied = await freshService.RefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, Guid.NewGuid(), default);

            Assert.True(denied.IsFailure);

            Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, denied.Error.Code);

            Assert.Equal(1, gets);

            LongRunningOperation? persisted = await freshOperations.GetAsync(operationId);

            Assert.NotNull(persisted);

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, persisted.State);

            var retry = await freshService.RetryAsync(f.Caller, persisted, default);

            Assert.True(retry.IsFailure);

            Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, retry.Error.Code);

            Assert.Equal(1, gets);
        }
        finally
        {
            stream.ReleaseRead.TrySetResult();

            stream.ReleaseDispose.TrySetResult();

            await running;

            if (cleanup is not null) await cleanup;
        }
    }
}
