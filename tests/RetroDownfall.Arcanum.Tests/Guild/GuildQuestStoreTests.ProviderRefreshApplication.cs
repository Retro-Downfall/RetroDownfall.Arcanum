using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_refresh_commits_correction_and_receipt_pointer_together()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        var applied = await f.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        Assert.Equal(applied.Value.Receipt.ReceiptId.ToString("D").ToUpperInvariant(), await f.ReadPointerAsync());

        Assert.Equal(0.30m, applied.Value.Receipt.Facts.KnownUsd);

        Assert.Equal(2L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));
    }

    [Fact]
    public async Task Provider_refresh_refuses_pointer_to_non_provider_receipt()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Query.CommandText = "UPDATE guild_spend_refreshes SET AcceptedReceiptId=(SELECT ReceiptId FROM guild_spend_receipts WHERE EffectId=$effect AND OriginCode=0) WHERE RefreshId=$refresh";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var forged = await f.Scenario.Fixture.Store.ApplyProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, null, null, null, default);

        Assert.True(forged.IsFailure);
    }

    [Fact]
    public async Task Provider_refresh_pointer_failure_rolls_back_correction_and_can_retry()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        long initialEvents = await f.Scenario.Fixture.CountAsync("guild_events");

        f.Query.CommandText = "SELECT Revision FROM guild_quests WHERE Id=$quest";

        long questRevision = Assert.IsType<long>(await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        long effectRevision = Assert.IsType<long>(await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "CREATE TRIGGER fixture_abort_refresh_pointer BEFORE UPDATE OF AcceptedReceiptId ON guild_spend_refreshes BEGIN SELECT RAISE(ABORT,'injected'); END";

        await f.Query.ExecuteNonQueryAsync();

        var failed = await f.ApplyAsync();

        Assert.True(failed.IsFailure);

        Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

        Assert.Equal(initialEvents, await f.Scenario.Fixture.CountAsync("guild_events"));

        Assert.Equal(0L, await f.Scenario.Fixture.CountAsync("BudgetReservations"));

        f.Query.CommandText = "SELECT Revision FROM guild_quests WHERE Id=$quest";

        Assert.Equal(questRevision, await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        Assert.Equal(effectRevision, await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT ActualCostUsd FROM BillableOperations";

        Assert.Equal(0m, ExactUsdText.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync())));

        Assert.Equal(DBNull.Value, await f.ReadPointerAsync());

        f.Query.CommandText = "DROP TRIGGER fixture_abort_refresh_pointer";

        await f.Query.ExecuteNonQueryAsync();

        Assert.True((await f.ApplyAsync()).IsSuccess);
    }

    [Theory]
    [InlineData("cancelling")]
    [InlineData("expired")]
    [InlineData("owner")]
    [InlineData("checkpoint")]
    [InlineData("policy")]
    public async Task Provider_refresh_refuses_invalid_durable_lease_without_correction(string change)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Query.CommandText = change switch
        {
            "cancelling" => "UPDATE LongRunningOperations SET State=3 WHERE Id=$operation",
            "expired" => "UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id=$operation",
            "owner" => "UPDATE LongRunningOperations SET LeaseOwner='different-owner' WHERE Id=$operation",
            "checkpoint" => "UPDATE LongRunningOperations SET CheckpointPayload=x'7B7D' WHERE Id=$operation",
            "policy" => "UPDATE LongRunningOperations SET RecoveryPolicy=0 WHERE Id=$operation",
            _ => throw new InvalidOperationException("Unknown lease fixture change."),
        };

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var result = await f.ApplyAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(DBNull.Value, await f.ReadPointerAsync());
    }

    [Fact]
    public async Task Provider_refresh_lease_expiry_during_bill_write_rolls_back_correction()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        FakeTimeProvider clock = new();

        RefreshClockConnections connections = new(f.Scenario.Fixture.Connections, clock);

        using SignetAuthority authority = new(new(connections), f.Scenario.Fixture.Gate, f.Scenario.Fixture.Host, clock);

        GuildQuestStore store = new(new(connections, f.Scenario.Fixture.Gate, clock), authority,
            f.Scenario.Fixture.Host, new GuildBudgetLedger(), clock);

        f.Query.CommandText = "CREATE TRIGGER fixture_expire_refresh BEFORE UPDATE ON BillableOperations BEGIN SELECT fixture_expire_refresh(); END";

        await f.Query.ExecuteNonQueryAsync();

        var expired = await f.ApplyAsync(store);

        Assert.True(expired.IsFailure);

        Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(DBNull.Value, await f.ReadPointerAsync());
    }

    [Fact]
    public async Task Provider_refresh_replays_accepted_receipt_after_bill_pruning_without_source_inputs()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        var applied = await f.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        f.Query.CommandText = "DELETE FROM BillableOperations";

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        var replay = await f.Scenario.Fixture.Store.ApplyProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, f.RefreshId, null, null, null, default);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(applied.Value.Receipt, replay.Value.Receipt);

        Assert.Equal(2L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));
    }

    internal static async Task AssertCredentialMutationRefusesRefreshAsync(IProviderApiKeyResolver resolver,
        Func<ProviderSettings, ResolvedProviderCredential, Task<Func<Task>>> beginMutation, bool expectRefusal)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        ProviderSettings provider = f.Scenario.Settings.Providers[0] with { CredentialEnvironmentVariable = "ARCANUM_TEST_PENDING_GUILD_REFRESH" };

        ResolvedProviderCredential captured = await resolver.PeekWithIdentityAsync(provider);

        Assert.Equal(SpendSecret, captured.Secret);

        Assert.NotNull(captured.Stamp);

        f.Scenario.Settings.Providers[0].CredentialEnvironmentVariable = provider.CredentialEnvironmentVariable;

        var proof = await GuildSpendRefreshCurrentness.CaptureAsync(f.Configuration, resolver, provider.Name, SpendModel, default);

        Assert.True(proof.IsSuccess, proof.IsFailure ? proof.Error.Message : null);

        Func<Task> finish = await beginMutation(provider, captured);

        try
        {
            var result = await f.Scenario.Fixture.Store.ApplyProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
                f.Scenario.EffectId, f.RefreshId, "test-refresh-owner", proof.Value, f.Observation, default);

            Assert.Equal(expectRefusal, result.IsFailure);

            if (expectRefusal)
            {
                Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

                Assert.Equal(DBNull.Value, await f.ReadPointerAsync());
            }
            else
            {
                Assert.Equal(2L, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

                Assert.NotEqual(DBNull.Value, await f.ReadPointerAsync());
            }
        }
        finally
        {
            await finish();
        }
    }

    /// <summary>Seeds a durable operation only for writer tests; it does not establish real refresh admission.</summary>
    private sealed class RefreshApplicationScenario : IAsyncDisposable
    {
        internal SpendScenario Scenario { get; private set; } = null!;

        internal SqliteConnection Connection { get; private set; } = null!;

        internal SqliteCommand Query { get; private set; } = null!;

        internal GuildSpendObservation Observation { get; private set; } = null!;

        internal GuildSpendRefreshConfiguration Configuration { get; private set; } = null!;

        private ProviderCredentialStore _credentialStore = null!;

        private GuildSpendRefreshCurrentness _currentness = null!;

        internal Guid QuestId { get; private set; }

        internal Guid RefreshId { get; private set; }

        internal static async Task<RefreshApplicationScenario> CreateAsync()
        {
            RefreshApplicationScenario f = new();

            try
            {
                f.Scenario = await SpendScenario.CreateAsync();

                GuildSpendSourceCredential.VerifiedSource source = await f.Scenario.VerifiedAsync();

                using SpendMetadataHandler handler = new() { Body = """
                    {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
                    "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
                    "native_tokens_prompt":110,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
                    "num_media_completion":0,"num_media_prompt":0,"total_cost":0.30}}
                    """ };

                var observed = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler).ReadAsync(source, default);

                Assert.True(observed.IsSuccess, observed.IsFailure ? observed.Error.Message : null);

                f.Observation = observed.Value;

                f.Scenario.Settings.Providers[0].CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N");

                f.Configuration = new(new TestOptionsMonitor<ArcanumSettings>(f.Scenario.Settings));

                InMemoryOsCredentialStore os = new();

                os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

                f._credentialStore = new(os, new EphemeralDataProtectionProvider());

                ProviderApiKeyResolver resolver = new(f._credentialStore);

                var proof = await GuildSpendRefreshCurrentness.CaptureAsync(f.Configuration, resolver, "router", SpendModel, default);

                Assert.True(proof.IsSuccess, proof.IsFailure ? proof.Error.Message : null);

                f._currentness = proof.Value;

                f.Connection = await f.Scenario.Fixture.OpenAsync();

                f.Query = f.Connection.CreateCommand();

                f.Query.CommandText = """
                    SELECT e.QuestId,e.SourceId,q.RootQuestId,g.AuthorityEpoch,s.AuthorityEpoch,s.AuthorityKeyId
                    FROM guild_budget_effects e JOIN guild_quests q ON q.Id=e.QuestId
                    JOIN guild_signets g ON g.SignetId=q.SignetId JOIN guild_spend_sources s ON s.EffectId=e.EffectId
                    WHERE e.EffectId=$effect
                    """;

                f.Query.Parameters.AddWithValue("$effect", f.Scenario.EffectId.ToString("D").ToUpperInvariant());

                Guid sourceId;

                Guid rootId;

                Guid signetEpoch;

                Guid spendEpoch;

                Guid spendKeyId;

                await using (SqliteDataReader row = await f.Query.ExecuteReaderAsync())
                {
                    Assert.True(await row.ReadAsync());

                    f.QuestId = Guid.Parse(row.GetString(0));

                    sourceId = Guid.Parse(row.GetString(1));

                    rootId = Guid.Parse(row.GetString(2));

                    signetEpoch = Guid.Parse(row.GetString(3));

                    spendEpoch = Guid.Parse(row.GetString(4));

                    spendKeyId = Guid.Parse(row.GetString(5));
                }

                f.RefreshId = Guid.NewGuid();

                Guid operationId = Guid.NewGuid();

                string principal = f.Scenario.Fixture.Caller.PrincipalId!.Value.ToString("D").ToUpperInvariant();

                string generation = f.Scenario.Fixture.Host.InstallationGeneration.ToString("D").ToUpperInvariant();

                string quest = f.QuestId.ToString("D").ToUpperInvariant();

                string effect = f.Scenario.EffectId.ToString("D").ToUpperInvariant();

                string refresh = f.RefreshId.ToString("D").ToUpperInvariant();

                byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", "Arcanum.Guild.RefreshRequest.v1",
                    generation, principal, quest, effect, refresh)));

                f.Query.CommandText = """
                    INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,LeaseOwner,LeaseExpiresAt,
                        AttemptCount,CheckpointVersion,CheckpointPayload,PublicSummary,Revision)
                    VALUES($operation,'guild-spend-refresh',1,2,$created,'test-refresh-owner',$expires,1,1,$checkpoint,'Provider refresh',0);
                    INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest)
                    VALUES($refresh,$effect,$operation,$digest);
                    """;

                f.Query.Parameters.AddWithValue("$operation", operationId.ToString("N"));

                f.Query.Parameters.AddWithValue("$created", UtcInstantText.Format(DateTimeOffset.UtcNow));

                f.Query.Parameters.AddWithValue("$expires", UtcInstantText.Format(DateTimeOffset.UtcNow.AddMinutes(2)));

                string checkpoint = $$"""
                    {"version":1,"refreshId":"{{f.RefreshId:D}}","questId":"{{f.QuestId:D}}","rootQuestId":"{{rootId:D}}","effectId":"{{f.Scenario.EffectId:D}}","sourceId":"{{sourceId:D}}","operationId":"{{operationId:D}}","installationGeneration":"{{f.Scenario.Fixture.Host.InstallationGeneration:D}}","principalId":"{{f.Scenario.Fixture.Caller.PrincipalId!.Value:D}}","signetEpoch":"{{signetEpoch:D}}","spendEpoch":"{{spendEpoch:D}}","spendKeyId":"{{spendKeyId:D}}","phase":"admitted"}
                    """;

                f.Query.Parameters.AddWithValue("$checkpoint", Encoding.UTF8.GetBytes(checkpoint));

                f.Query.Parameters.AddWithValue("$refresh", refresh);

                f.Query.Parameters.AddWithValue("$quest", quest);

                f.Query.Parameters.AddWithValue("$digest", digest);

                await f.Query.ExecuteNonQueryAsync();

                return f;
            }
            catch
            {
                await f.DisposeAsync();

                throw;
            }
        }

        internal Task<Result<GuildSpendCorrectionResultDto>> ApplyAsync(GuildQuestStore? store = null) =>
            (store ?? Scenario.Fixture.Store).ApplyProviderSpendRefreshAsync(Scenario.Fixture.Caller, QuestId,
                Scenario.EffectId, RefreshId, "test-refresh-owner", _currentness, Observation, default);

        internal async Task<object?> ReadPointerAsync()
        {
            Query.CommandText = "SELECT AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

            return await Query.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Query is not null) await Query.DisposeAsync();

            if (Connection is not null) await Connection.DisposeAsync();

            Configuration?.Dispose();

            _credentialStore?.Dispose();

            if (Scenario is not null) await Scenario.DisposeAsync();
        }
    }

    private sealed class RefreshClockConnections(IGrimoireOrdinaryConnectionFactory inner, FakeTimeProvider clock)
        : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) =>
            inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            var acquired = await inner.OpenFreshAsync(kind, cancellationToken);

            if (acquired.IsFailure) return acquired.Error;

            try
            {
                acquired.Value.Connection.CreateFunction("fixture_expire_refresh", () =>
                {
                    clock.Advance(TimeSpan.FromMinutes(3));

                    return 0;
                });

                return acquired;
            }
            catch
            {
                await acquired.Value.DisposeAsync();

                throw;
            }
        }
    }
}
