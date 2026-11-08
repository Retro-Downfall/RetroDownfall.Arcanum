using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Qualified_provider_wire_covers_closed_function_schema_call_and_result()
    {
        const string model = "gpt-5.4-mini-2026-03-17";

        ProviderSettings provider = new() { Name = "qualified", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1", Models = [model] };

        using GuildWireCapture transport = new(model);

        ChatClientFactory factory = new(transport, new TestOptionsMonitor<ArcanumSettings>(new()));

        using ChatClientLease lease = await factory.ResolveClientAsync(provider, model, default);

        AIFunction function = new GuildWireFunction();

        List<ChatMessage> messages = [new(ChatRole.User, "accepted"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "delegate_task", new Dictionary<string, object?> { ["prompt"] = "child" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "child result")])];

        var prepared = GuildProviderWirePolicy.Prepare(lease.ChatClient, "qualified", model, messages, [function], 30, new());

        Assert.NotNull(prepared);

        Assert.Equal(400030, prepared.TokenBound);

        using IDisposable scope = prepared.Enter();

        ChatOptions options = new()
        {
            ModelId = model, MaxOutputTokens = 30, Tools = [function], ToolMode = ChatToolMode.Auto, AllowMultipleToolCalls = false,
        };

        try
        {
            _ = await lease.ChatClient.GetResponseAsync(messages, options);
        }
        catch (InvalidOperationException)
        {
            scope.Dispose();

            _ = await lease.ChatClient.GetResponseAsync(messages, options);

            Assert.Fail($"The fixed test SDK payload differed from the seal: {transport.Body}");
        }

        Assert.Equal(1, transport.Calls);

        using JsonDocument body = JsonDocument.Parse(transport.Body!);

        Assert.Equal("default", body.RootElement.GetProperty("service_tier").GetString());

        Assert.False(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());

        Assert.Equal("delegate_task", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());

        Assert.Equal("call-1", body.RootElement.GetProperty("messages")[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());

        Assert.Equal("child result", body.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
    }

    private sealed class GuildWireFunction : AIFunction
    {
        public override string Name => "delegate_task";

        public override string Description => "Closed child tool fixture.";

        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{"prompt":{"type":"string"}},"required":["prompt"],"additionalProperties":false}""").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Wire description must not invoke its function.");
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("model")]
    [InlineData("cap")]
    [InlineData("extra")]
    [InlineData("replay")]
    [InlineData("expired")]
    public async Task Qualified_provider_wire_seal_refuses_changed_or_repeated_payload(string mutation)
    {
        const string model = "gpt-5.4-mini-2026-03-17";

        await using var authority = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        var execution = await authority.RootAsync();

        using var invocation = GuildMemoryInvocationAmbient.Begin(new(authority.Authority, execution));

        ProviderSettings provider = new() { Name = "qualified", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1", Models = [model] };

        using GuildWireCapture transport = new(model);

        ChatClientFactory factory = new(transport, new TestOptionsMonitor<ArcanumSettings>(new()));

        using ChatClientLease lease = await factory.ResolveClientAsync(provider, model, default);

        var prepared = GuildProviderWirePolicy.Prepare(lease.ChatClient, "qualified", model, "accepted", 30, new());

        Assert.NotNull(prepared);

        // Mutating the caller's config after client creation cannot change the sealed endpoint or catalogue.
        provider.Endpoint = "https://example.test/v1";

        Assert.Equal(400030, prepared.TokenBound);

        using IDisposable scope = prepared.Enter();

        ChatOptions options = new() { ModelId = model, MaxOutputTokens = 30 };

        if (mutation == "replay")
            _ = await lease.ChatClient.GetResponseAsync([new(ChatRole.User, "accepted")], options);

        if (mutation == "expired") scope.Dispose();

        if (mutation == "model") options.ModelId = "gpt-5.4-mini";

        if (mutation == "cap") options.MaxOutputTokens = 31;

        if (mutation == "extra") options.Temperature = 0.5f;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await lease.ChatClient.GetResponseAsync([new(ChatRole.User, mutation == "prompt" ? "changed" : "accepted")], options));

        Assert.Equal(mutation == "replay" ? 1 : 0, transport.Calls);
    }

    [Theory]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", 1, 400030, 30, true)]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", 0.1, 400030, 30, false)]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", 1, 400029, 30, false)]
    [InlineData("gpt-5.4-mini", "https://api.openai.com/v1", 1, 400030, 30, false)]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://example.test/v1", 1, 400030, 30, false)]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", 1, 528000, null, true)]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", 1, 528000, 600000, true)]
    public async Task Qualified_finite_provider_binds_actual_wire_and_catalogue_price_floor(
        string model, string endpoint, double usd, long tokens, int? maxOutput, bool allowed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "qualified", Type = AiProviderKind.OpenAICompatible, Endpoint = endpoint, Models = [model], ContextWindowLimit = 400000 }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, (decimal)usd), new(GuildBudgetMode.Finite, tokens)) };

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "qualified", model, [], maxOutput);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.Error.Message);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("finite-wire"), quest.Quest.RunId, false, default)).Value;

        using GuildWireCapture transport = new(model);

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            if (allowed) Assert.True(result.IsSuccess, result.Error.Message);

            else Assert.True(result.IsFailure);

            Assert.Equal(allowed ? 1 : 0, transport.Calls);

            Assert.Equal(allowed ? 1L : 0L, await f.CountAsync("BillableOperations"));

            if (!allowed) return;

            using JsonDocument body = JsonDocument.Parse(transport.Body!);

            Assert.Equal("default", body.RootElement.GetProperty("service_tier").GetString());

            Assert.Equal(1, body.RootElement.GetProperty("n").GetInt32());

            int hardOutput = Math.Min(maxOutput ?? settings.ResolveIntelligence().ReservedOutputTokens, 128000);

            Assert.Equal(hardOutput, body.RootElement.GetProperty("max_completion_tokens").GetInt32());

            Assert.Equal(model, body.RootElement.GetProperty("model").GetString());

            Assert.False(body.RootElement.TryGetProperty("tools", out _));

            await using SqliteCommand effect = connection.CreateCommand();

            effect.CommandText = "SELECT UsdBound,TokenBound,KnownUsd,UnknownUsd,UnknownTokens FROM guild_budget_effects";

            await using SqliteDataReader reader = await effect.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.Equal(0.3m + hardOutput * 4.5m / 1000000m, reader.GetDecimal(0));

            Assert.Equal(400000 + hardOutput, reader.GetInt64(1));

            Assert.Equal(0.00001875m, reader.GetDecimal(2));

            Assert.Equal(0, reader.GetInt64(3));

            Assert.Equal(0, reader.GetInt64(4));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed class GuildWireCapture(string model) : HttpMessageHandler, IHttpClientFactory
    {
        internal int Calls { get; private set; }

        internal string? Body { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            Body = await request.Content!.ReadAsStringAsync(ct);

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"chatcmpl-qualified","object":"chat.completion","created":1700000000,"model":"MODEL",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"qualified answer"},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10},"service_tier":"default"}
                    """.Replace("MODEL", model, StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
