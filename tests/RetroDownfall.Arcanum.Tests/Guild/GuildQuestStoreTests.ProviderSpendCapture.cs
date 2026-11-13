using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    private const string SpendModel = "provider/model";

    private const string SpendSecret = "private-provider-key-sentinel";

    [Fact]
    public async Task Opted_in_sdk_call_commits_binding_before_send_and_retains_completion_id()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        Assert.Equal(1, scenario.Transport.Calls);

        Assert.Equal("Guild.SpendGenerationMissing", scenario.PendingReason);

        Assert.Equal(SpendSecret, scenario.Transport.Credential);

        await using SqliteCommand command = scenario.Connection.CreateCommand();

        command.CommandText = "SELECT SourceKindCode,Provider,Model,GenerationId,length(CredentialFingerprint) FROM guild_spend_sources";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal(1, reader.GetInt64(0));

        Assert.Equal("router", reader.GetString(1));

        Assert.Equal(SpendModel, reader.GetString(2));

        Assert.Equal("gen-test-123", reader.GetString(3));

        Assert.Equal(32, reader.GetInt64(4));
    }

    [Fact]
    public async Task Source_verification_survives_reopened_connection_and_keeps_credential_private()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        GuildSpendSourceCredential credential = scenario.Credential();

        var verified = await scenario.VerifyAsync(credential);

        Assert.True(verified.IsSuccess);

        Assert.DoesNotContain(SpendSecret, credential.ToString());

        Assert.DoesNotContain(SpendSecret, verified.Value.ToString());
    }

    [Theory]
    [InlineData("credential", "Guild.SpendCredentialChanged")]
    [InlineData("missingCredential", "Guild.SpendCredentialChanged")]
    [InlineData("missingSource", "Guild.SpendSourceMissing")]
    [InlineData("model", "Guild.SpendConfigurationChanged")]
    [InlineData("type", "Guild.SpendConfigurationChanged")]
    [InlineData("disabled", "Guild.SpendConfigurationChanged")]
    [InlineData("endpoint", "Guild.SpendConfigurationChanged")]
    public async Task Source_verification_reports_ineligibility_without_sensitive_values(string change, string expected)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        ProviderSettings provider = scenario.Settings.Providers[0] with { };

        string model = SpendModel;

        string? secret = SpendSecret;

        Guid effect = scenario.EffectId;

        switch (change)
        {
            case "credential": secret = "different-key"; break;
            case "missingCredential": secret = null; break;
            case "missingSource": effect = Guid.NewGuid(); break;
            case "model": model = "other-model"; break;
            case "type": provider.Type = AiProviderKind.CodexCli; break;
            case "disabled": provider.GuildUsageReconciliation = GuildUsageReconciliationSource.None; break;
            case "endpoint": provider.Endpoint = "https://other.example/v1"; break;
            default: throw new InvalidOperationException("Unexpected fixture change.");
        }

        var result = await scenario.VerifyAsync(new GuildSpendSourceCredential(provider, model, secret), effect);

        Assert.True(result.IsFailure);

        Assert.Equal(expected, result.Error.Code);

        Assert.DoesNotContain("different-key", result.Error.Message);

        Assert.DoesNotContain(effect.ToString(), result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("rotated")]
    [InlineData("missing")]
    public async Task Source_verification_refuses_retired_or_missing_authority(string change)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection reopened = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = reopened.BeginTransaction();

        if (change is "rotated" or "missing")
        {
            await using SqliteCommand command = reopened.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = change == "rotated"
                ? "UPDATE guild_spend_authority_state SET AuthorityEpoch=$epoch WHERE StateKey=1"
                : "DELETE FROM guild_spend_authority_state WHERE StateKey=1";

            if (change == "rotated") command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D").ToUpperInvariant());

            await command.ExecuteNonQueryAsync();
        }

        Guid generation = change == "generation" ? Guid.NewGuid() : scenario.Fixture.Host.InstallationGeneration;

        var result = await scenario.Credential().VerifyWithinAsync(reopened, transaction, scenario.EffectId, generation, default);

        Assert.True(result.IsFailure);

        Assert.Equal("Guild.SpendAuthorityRetired", result.Error.Code);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Metadata_reader_sends_bound_bearer_to_fixed_origin_and_uses_native_facts()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        var source = await scenario.VerifiedAsync();

        using SpendMetadataHandler handler = new();

        var observation = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler).ReadAsync(source, default);

        Assert.True(observation.IsSuccess, observation.IsFailure ? observation.Error.Message : null);

        Assert.Equal(new GuildSpendAmountsRequest(100, 20, 40, 5, 0.25m), observation.Value.Amounts);

        Assert.Equal("https://openrouter.ai/api/v1/generation?id=gen-test-123", handler.Uri);

        Assert.Equal(SpendSecret, handler.Bearer);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Metadata_reader_refuses_error_status_without_response_or_credential_diagnostics(HttpStatusCode status)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        using SpendMetadataHandler handler = new() { Status = status, Body = "private-response-sentinel" };

        var result = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler).ReadAsync(await scenario.VerifiedAsync(), default);

        Assert.True(result.IsFailure);

        Assert.DoesNotContain(SpendSecret, result.Error.Message);

        Assert.DoesNotContain("private-response-sentinel", result.Error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_reader_refuses_oversized_body_with_or_without_content_length(bool unknownLength)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        using SpendMetadataHandler handler = new() { UnknownLength = unknownLength, Body = new string('x', 65537) };

        var result = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler).ReadAsync(await scenario.VerifiedAsync(), default);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Metadata_reader_blocks_private_dns_before_sending_credentials()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        using SpendMetadataHandler handler = new();

        var result = await new GuildOpenRouterSpendReader(new PrivateSpendDns(), () => handler).ReadAsync(await scenario.VerifiedAsync(), default);

        Assert.True(result.IsFailure);

        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task Metadata_reader_joins_a_handler_that_ignores_cancellation()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        using CancellationTokenSource cancel = new();

        using BlockingSpendHandler handler = new();

        Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendObservation>> pending =
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler).ReadAsync(await scenario.VerifiedAsync(), cancel.Token);

        await handler.Entered.Task;

        cancel.Cancel();

        try
        {
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Opted_out_guild_call_completes_without_spend_authority()
    {
        const string model = "provider/model";

        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteCommand removeAuthority = connection.CreateCommand())
        {
            removeAuthority.CommandText = "DELETE FROM guild_spend_authority_state";

            await removeAuthority.ExecuteNonQueryAsync();
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [model],
                GuildUsageReconciliation = GuildUsageReconciliationSource.None }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", model, [], 30);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

        using GuildWireCapture transport = new(model);

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new SpendCaptureKeys());

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal(1, transport.Calls);

            await using SqliteCommand source = connection.CreateCommand();

            source.CommandText = "SELECT SourceKindCode,CredentialFingerprint FROM guild_spend_sources";

            await using SqliteDataReader reader = await source.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.Equal(0, reader.GetInt64(0));

            Assert.True(reader.IsDBNull(1));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    [Fact]
    public async Task Opted_in_guild_call_refuses_missing_spend_authority_before_dispatch()
    {
        const string model = "provider/model";

        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteCommand removeAuthority = connection.CreateCommand())
        {
            removeAuthority.CommandText = "DELETE FROM guild_spend_authority_state";

            await removeAuthority.ExecuteNonQueryAsync();
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [model],
                GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", model, [], 30);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

        using GuildWireCapture transport = new(model);

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new SpendCaptureKeys());

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsFailure);

            Assert.Equal(0, transport.Calls);

            Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));

        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    [Fact]
    public async Task Opted_in_unregistered_client_is_refused_before_admission()
    {
        const string model = "provider/model";

        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [model],
                GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", model, [], 30);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

        UnregisteredSpendClient transport = new();

        IChatClientFactory clients = new UnregisteredSpendFactory(transport, settings.Providers[0], model);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsFailure);

            Assert.Equal(0, transport.Calls);

            Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));

        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    [Fact]
    public async Task Opted_in_context_cannot_use_a_client_without_source_capture()
    {
        const string model = "provider/model";

        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [model],
                GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", model, [], 30);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

        using GuildWireCapture transport = new(model);

        ChatClientFactory physical = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new SpendCaptureKeys());

        IChatClientFactory clients = new MismatchedSpendFactory(physical, settings.Providers[0], model);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsFailure);

            Assert.Equal(0, transport.Calls);

            Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));

        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad key")]
    [InlineData("\uD800")]
    public async Task Opted_in_invalid_credential_fails_before_dispatch(string? secret)
    {
        const string model = "provider/model";

        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [model],
                GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", model, [], 30);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

        using GuildWireCapture transport = new(model);

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new SpendCaptureKeys(secret));

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("private-provider-key-sentinel", result.Error.Message);

            Assert.Equal(0, transport.Calls);

            Assert.Equal(0L, await f.CountAsync("guild_spend_sources"));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    /// <summary>Real Guild admission, SDK transport, SQLCipher settlement, and a reopened source verifier.</summary>
    private sealed class SpendScenario : IAsyncDisposable
    {
        internal Fixture Fixture { get; private set; } = null!;

        internal SqliteConnection Connection { get; private set; } = null!;

        private ArcanumDbContext _db = null!;

        private WizardIntelligenceProviderTests _wizard = null!;

        internal GuildWireCapture Transport { get; private set; } = null!;

        internal ArcanumSettings Settings { get; private set; } = null!;

        internal Guid EffectId { get; private set; }

        internal string? PendingReason { get; private set; }

        internal static async Task<SpendScenario> CreateAsync(Fixture? fixture = null)
        {
            SpendScenario scenario = new();

            try
            {
                scenario.Fixture = fixture ?? await Fixture.CreateAsync();

                scenario.Connection = await scenario.Fixture.OpenAsync();

                scenario._db = GrimoireFixture.CreateContext(scenario.Connection);

                scenario.Settings = new()
                {
                    DefaultModel = SpendModel,
                    Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [SpendModel],
                        GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }],
                };

                BudgetReservationService daily = new(scenario._db, new TestOptionsMonitor<ArcanumSettings>(scenario.Settings));

                scenario.Fixture.UseBudgetParticipant(new GuildBudgetLedger(daily));

                GuildQuestStored quest = (await scenario.Fixture.Store.IssueAsync(scenario.Fixture.Caller, scenario.Fixture.Identity,
                    scenario.Fixture.Request, default)).Value;

                GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", SpendModel, [], 30);

                var bound = await scenario.Fixture.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
                    (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

                Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

                GuildQuestDriver driver = await scenario.Fixture.DriverAsync(quest);

                GuildQuestDispatchRecord dispatch = (await scenario.Fixture.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
                    ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

                scenario.Transport = new(SpendModel, async () =>
                {
                    await using SqliteConnection observed = await scenario.Fixture.OpenAsync();

                    await using SqliteCommand check = observed.CreateCommand();

                    check.CommandText = "SELECT COUNT(*) FROM guild_spend_sources WHERE SourceKindCode=1 AND GenerationId IS NULL";

                    Assert.Equal(1L, await check.ExecuteScalarAsync());

                    check.CommandText = "SELECT EffectId FROM guild_spend_sources WHERE SourceKindCode=1";

                    Guid pendingEffect = Guid.Parse((string)(await check.ExecuteScalarAsync())!);

                    await using SqliteTransaction transaction = observed.BeginTransaction();

                    var pending = await scenario.Credential().VerifyWithinAsync(observed, transaction, pendingEffect,
                        scenario.Fixture.Host.InstallationGeneration, default);

                    scenario.PendingReason = pending.Error.Code;

                    await transaction.RollbackAsync();
                }, "gen-test-123");

                ChatClientFactory clients = new(scenario.Transport, new TestOptionsMonitor<ArcanumSettings>(scenario.Settings), new SpendCaptureKeys());

                scenario._wizard = new();

                var runtime = await scenario._wizard.CreateGuildRuntimeAsync(daily, scenario.Settings, clients);

                var result = await runtime.Runtime.ExecuteAsync(new(scenario.Fixture.Store, driver, dispatch), input, default);

                Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

                await using SqliteCommand source = scenario.Connection.CreateCommand();

                source.CommandText = "SELECT EffectId FROM guild_spend_sources";

                scenario.EffectId = Guid.Parse((string)(await source.ExecuteScalarAsync())!);

                return scenario;
            }
            catch
            {
                await scenario.DisposeAsync();

                throw;
            }
        }

        internal GuildSpendSourceCredential Credential() => new(Settings.Providers[0], SpendModel, SpendSecret);

        internal async Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendSourceCredential.VerifiedSource>> VerifyAsync(
            GuildSpendSourceCredential credential, Guid? effect = null)
        {
            await using SqliteConnection reopened = await Fixture.OpenAsync();

            await using SqliteTransaction transaction = reopened.BeginTransaction();

            var result = await credential.VerifyWithinAsync(reopened, transaction, effect ?? EffectId, Fixture.Host.InstallationGeneration, default);

            await transaction.RollbackAsync();

            return result;
        }

        internal async Task<GuildSpendSourceCredential.VerifiedSource> VerifiedAsync()
        {
            var result = await VerifyAsync(Credential());

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            return result.Value;
        }

        public async ValueTask DisposeAsync()
        {
            if (_wizard is not null) await _wizard.DisposeAsync();

            Transport?.Dispose();

            if (_db is not null) await _db.DisposeAsync();

            if (Connection is not null) await Connection.DisposeAsync();

            if (Fixture is not null) await Fixture.DisposeAsync();
        }
    }

    private sealed class PublicSpendDns : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("1.1.1.1")]);
    }

    private sealed class SpendMetadataHandler : HttpMessageHandler
    {
        internal string? Uri { get; private set; }

        internal string? Bearer { get; private set; }

        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        internal string? Body { get; init; }

        internal bool UnknownLength { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();

            Bearer = request.Headers.Authorization?.Parameter;

            string body = Body ?? """
                {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
                "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
                "native_tokens_prompt":100,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
                "num_media_completion":0,"num_media_prompt":0,"total_cost":0.25,
                "tokens_prompt":888,"tokens_completion":999,"usage":14}}
                """;

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = UnknownLength ? new UnknownLengthSpendContent(body) : new StringContent(body),
            });
        }
    }

    private sealed class UnknownLengthSpendContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body)).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    private sealed class PrivateSpendDns : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
    }

    private sealed class BlockingSpendHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();

            await Release.Task;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class MismatchedSpendFactory(ChatClientFactory inner, ProviderSettings provider, string model) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            CreateAsync(provider, model, cancellationToken);

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings selected, string resolvedModel, CancellationToken cancellationToken) =>
            CreateAsync(selected, resolvedModel, cancellationToken);

        private async Task<ChatClientLease> CreateAsync(ProviderSettings selected, string resolvedModel, CancellationToken ct)
        {
            ProviderSettings unbound = selected with { GuildUsageReconciliation = GuildUsageReconciliationSource.None };

            ChatClientLease lease = await inner.ResolveClientAsync(unbound, resolvedModel, ct);

            return new ChatClientLease(lease.ChatClient, selected, resolvedModel, null);
        }
    }

    private sealed class UnregisteredSpendFactory(UnregisteredSpendClient client, ProviderSettings provider, string model) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(client, provider, model, null));

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings selected, string resolvedModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(client, selected, resolvedModel, null));
    }

    private sealed class UnregisteredSpendClient : IChatClient
    {
        internal int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unregistered")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class SpendCaptureKeys(string? secret = "private-provider-key-sentinel") : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(secret);
    }
}
