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

        var prepared = GuildProviderWirePolicy.Prepare(lease.ChatClient, "qualified", model, messages, [function], 30);

        Assert.NotNull(prepared);

        Assert.Equal(30, prepared.MaxOutputTokens);

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

        Assert.False(body.RootElement.TryGetProperty("service_tier", out _));

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

        var prepared = GuildProviderWirePolicy.Prepare(lease.ChatClient, "qualified", model, "accepted", 30);

        Assert.NotNull(prepared);

        // Mutating the caller's config after client creation cannot change the sealed endpoint or catalogue.
        provider.Endpoint = "https://example.test/v1";

        Assert.Equal(30, prepared.MaxOutputTokens);

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
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", 30)]
    [InlineData("gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1", null)]
    [InlineData("local", "https://example.test/v1", null)]
    public async Task Stored_finite_grants_do_not_require_pricing_or_change_output_options(
        string model, string endpoint, int? maxOutput)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "qualified", Type = AiProviderKind.OpenAICompatible, Endpoint = endpoint, Models = [model], ContextWindowLimit = 400000 }],
        };

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 0), new(GuildBudgetMode.Finite, 0)) };

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

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(1, transport.Calls);

            Assert.Equal(1L, await f.CountAsync("BillableOperations"));

            using JsonDocument body = JsonDocument.Parse(transport.Body!);

            Assert.False(body.RootElement.TryGetProperty("service_tier", out _));

            Assert.Equal(1, body.RootElement.GetProperty("n").GetInt32());

            if (maxOutput is { } selected)
                Assert.Equal(selected, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            else
                Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));

            Assert.Equal(model, body.RootElement.GetProperty("model").GetString());

            Assert.False(body.RootElement.TryGetProperty("tools", out _));

            await using SqliteCommand effect = connection.CreateCommand();

            effect.CommandText = "SELECT UsdBound,TokenBound,KnownUsd,UnknownUsd,UnknownTokens,ReservationId FROM guild_budget_effects";

            await using SqliteDataReader reader = await effect.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.True(reader.IsDBNull(0));

            Assert.True(reader.IsDBNull(1));

            Assert.Equal(0m, reader.GetDecimal(2));

            Assert.Equal(1, reader.GetInt64(3));

            Assert.Equal(0, reader.GetInt64(4));

            Assert.True(reader.IsDBNull(5));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed class GuildWireCapture(string model, Func<Task>? beforeSend = null, string responseId = "chatcmpl-qualified") : HttpMessageHandler, IHttpClientFactory
    {
        internal int Calls { get; private set; }

        internal string? Body { get; private set; }

        internal string? Credential { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            Credential = request.Headers.Authorization?.Parameter;

            if (beforeSend is not null) await beforeSend();

            Body = await request.Content!.ReadAsStringAsync(ct);

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"RESPONSE_ID","object":"chat.completion","created":1700000000,"model":"MODEL",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"qualified answer"},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10},"service_tier":"default"}
                    """.Replace("MODEL", model, StringComparison.Ordinal).Replace("RESPONSE_ID", responseId, StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
