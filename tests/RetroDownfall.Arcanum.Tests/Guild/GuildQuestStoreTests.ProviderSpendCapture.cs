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
    [Fact]
    public async Task Opted_in_actual_sdk_completion_binds_source_before_dispatch_and_saves_returned_id()
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

        using GuildWireCapture transport = new(model, async () =>
        {
            await using SqliteConnection observed = await f.OpenAsync();

            await using SqliteCommand check = observed.CreateCommand();

            check.CommandText = "SELECT COUNT(*) FROM guild_spend_sources WHERE SourceKindCode=1 AND GenerationId IS NULL";

            Assert.Equal(1L, await check.ExecuteScalarAsync());
        }, "gen-test-123");

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new SpendCaptureKeys());

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal(1, transport.Calls);

            Assert.Equal("private-provider-key-sentinel", transport.Credential);

            await using SqliteCommand source = connection.CreateCommand();

            source.CommandText = "SELECT SourceKindCode,Provider,Model,GenerationId,length(CredentialFingerprint) FROM guild_spend_sources";

            await using SqliteDataReader reader = await source.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.Equal(1, reader.GetInt64(0));

            Assert.Equal("router", reader.GetString(1));

            Assert.Equal(model, reader.GetString(2));

            Assert.Equal("gen-test-123", reader.GetString(3));

            Assert.Equal(32, reader.GetInt64(4));

            await reader.DisposeAsync();

            source.CommandText = "SELECT EffectId FROM guild_spend_sources";

            Guid effect = Guid.Parse((string)(await source.ExecuteScalarAsync())!);

            await using SqliteTransaction transaction = connection.BeginTransaction();

            GuildSpendSourceCredential same = new(settings.Providers[0], model, "private-provider-key-sentinel");

            var verified = await same.VerifyWithinAsync(connection, transaction, effect, f.Host.InstallationGeneration, default);

            Assert.True(verified.IsSuccess);

            GuildSpendSourceCredential changed = new(settings.Providers[0], model, "different-key");

            Assert.True((await changed.VerifyWithinAsync(connection, transaction, effect, f.Host.InstallationGeneration, default)).IsFailure);

            GuildSpendSourceCredential changedModel = new(settings.Providers[0], "other-model", "private-provider-key-sentinel");

            Assert.True((await changedModel.VerifyWithinAsync(connection, transaction, effect, f.Host.InstallationGeneration, default)).IsFailure);

            ProviderSettings familiar = settings.Providers[0] with { Type = AiProviderKind.CodexCli };

            Assert.True((await new GuildSpendSourceCredential(familiar, model, "private-provider-key-sentinel")
                .VerifyWithinAsync(connection, transaction, effect, f.Host.InstallationGeneration, default)).IsFailure);

            ProviderSettings disabled = settings.Providers[0] with { GuildUsageReconciliation = GuildUsageReconciliationSource.None };

            Assert.True((await new GuildSpendSourceCredential(disabled, model, "private-provider-key-sentinel")
                .VerifyWithinAsync(connection, transaction, effect, f.Host.InstallationGeneration, default)).IsFailure);

            await transaction.RollbackAsync();

            Assert.DoesNotContain("private-provider-key-sentinel", same.ToString());

            Assert.DoesNotContain("private-provider-key-sentinel", verified.Value.ToString());

            await using (SqliteConnection reopened = await f.OpenAsync())
            {
                await using SqliteTransaction restarted = reopened.BeginTransaction();

                Assert.True((await new GuildSpendSourceCredential(settings.Providers[0], model, "private-provider-key-sentinel")
                    .VerifyWithinAsync(reopened, restarted, effect, f.Host.InstallationGeneration, default)).IsSuccess);

                ProviderSettings changedEndpoint = settings.Providers[0] with { Endpoint = "https://other.example/v1" };

                Assert.True((await new GuildSpendSourceCredential(changedEndpoint, model, "private-provider-key-sentinel")
                    .VerifyWithinAsync(reopened, restarted, effect, f.Host.InstallationGeneration, default)).IsFailure);

                Assert.True((await same.VerifyWithinAsync(reopened, restarted, effect, Guid.NewGuid(), default)).IsFailure);

                await using SqliteCommand retire = reopened.CreateCommand();

                retire.Transaction = restarted;

                retire.CommandText = "UPDATE guild_spend_authority_state SET AuthorityEpoch=$epoch WHERE StateKey=1";

                retire.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D").ToUpperInvariant());

                await retire.ExecuteNonQueryAsync();

                Assert.True((await same.VerifyWithinAsync(reopened, restarted, effect, f.Host.InstallationGeneration, default)).IsFailure);

                await restarted.RollbackAsync();
            }

            using SpendMetadataHandler metadata = new();

            GuildOpenRouterSpendReader lookup = new(new PublicSpendDns(), () => metadata);

            var observation = await lookup.ReadAsync(verified.Value, default);

            Assert.True(observation.IsSuccess, observation.IsFailure ? observation.Error.Message : null);

            Assert.Equal(new GuildSpendAmountsRequest(100, 20, 40, 5, 0.25m), observation.Value.Amounts);

            Assert.Equal("https://openrouter.ai/api/v1/generation?id=gen-test-123", metadata.Uri);

            Assert.Equal("private-provider-key-sentinel", metadata.Bearer);

            foreach (HttpStatusCode code in new[] { HttpStatusCode.Redirect, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized,
                         HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            {
                using SpendMetadataHandler rejected = new() { Status = code, Body = "private-response-sentinel" };

                var failure = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => rejected).ReadAsync(verified.Value, default);

                Assert.True(failure.IsFailure);

                Assert.DoesNotContain("private-provider-key-sentinel", failure.Error.Message);

                Assert.DoesNotContain("private-response-sentinel", failure.Error.Message);
            }

            using SpendMetadataHandler oversized = new() { Body = new string('x', 65537) };

            Assert.True((await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => oversized).ReadAsync(verified.Value, default)).IsFailure);

            using SpendMetadataHandler streamedOversized = new() { UnknownLength = true, Body = new string('x', 65537) };

            Assert.True((await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => streamedOversized).ReadAsync(verified.Value, default)).IsFailure);

            using SpendMetadataHandler blocked = new();

            Assert.True((await new GuildOpenRouterSpendReader(new PrivateSpendDns(), () => blocked).ReadAsync(verified.Value, default)).IsFailure);

            Assert.Null(blocked.Uri);

            using CancellationTokenSource cancel = new();

            using BlockingSpendHandler blockedTransport = new();

            Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendObservation>> pending =
                new GuildOpenRouterSpendReader(new PublicSpendDns(), () => blockedTransport).ReadAsync(verified.Value, cancel.Token);

            await blockedTransport.Entered.Task;

            cancel.Cancel();

            Assert.False(pending.IsCompleted);

            blockedTransport.Release.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            await wizard.DisposeAsync();
        }
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
