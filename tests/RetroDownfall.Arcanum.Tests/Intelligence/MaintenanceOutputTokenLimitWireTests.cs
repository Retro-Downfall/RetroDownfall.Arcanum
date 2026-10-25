using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class MaintenanceOutputTokenLimitWireTests
{
    [Theory]
    [InlineData("long-rest", OutputTokenLimitWireDialect.MaxTokens, "max_tokens", "max_completion_tokens")]
    [InlineData("rollup", OutputTokenLimitWireDialect.MaxTokens, "max_tokens", "max_completion_tokens")]
    [InlineData("evaluation", OutputTokenLimitWireDialect.MaxTokens, "max_tokens", "max_completion_tokens")]
    [InlineData("long-rest", OutputTokenLimitWireDialect.MaxCompletionTokens, "max_completion_tokens", "max_tokens")]
    [InlineData("rollup", OutputTokenLimitWireDialect.MaxCompletionTokens, "max_completion_tokens", "max_tokens")]
    [InlineData("evaluation", OutputTokenLimitWireDialect.MaxCompletionTokens, "max_completion_tokens", "max_tokens")]
    public async Task Chat_request_uses_the_declared_output_bound_on_the_actual_wire(
        string lane, OutputTokenLimitWireDialect dialect, string accepted, string rejected)
    {
        // Removing a lane's PerCall wire policy loses the legacy endpoint's output bound.
        ProviderSettings provider = Provider(dialect);

        RecordingKeys keys = new();

        Capture handler = new();

        using IDisposable factory = Factory(lane, provider, keys, handler);

        using ChatClientLease lease = await ResolveAsync(factory, provider);

        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""");

        ChatOptions options = new()
        {
            MaxOutputTokens = 2048,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "summary"),
        };

        ChatResponse response = await lease.ChatClient.GetResponseAsync(
            [new(ChatRole.User, "Bounded maintenance prompt.")], options);

        Assert.Equal("OK", response.Text);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));

        Assert.True(body.RootElement.TryGetProperty(accepted, out JsonElement bound),
            $"{lane}: missing accepted output bound {accepted}; body: {body.RootElement}");

        Assert.Equal(2048, bound.GetInt32());

        Assert.False(body.RootElement.TryGetProperty(rejected, out _));

        Assert.Equal(1, body.RootElement.EnumerateObject().Count(property => property.NameEquals(accepted)));

        Assert.Equal("Bounded maintenance prompt.", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        Assert.Equal("summary", body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString());

        Assert.Equal(2048, options.MaxOutputTokens);
    }

    [Theory]
    [InlineData("long-rest")]
    [InlineData("rollup")]
    [InlineData("evaluation")]
    public async Task Undefined_dialect_is_refused_before_credentials_or_sending(string lane)
    {
        // Removing enum validation would resolve credentials for an undefined wire contract.
        ProviderSettings provider = Provider((OutputTokenLimitWireDialect)99);

        RecordingKeys keys = new();

        Capture handler = new();

        using IDisposable factory = Factory(lane, provider, keys, handler);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveAsync(factory, provider));

        Assert.Equal("Provider output-token limit wire dialect is not defined.", failure.Message);

        Assert.Equal(0, keys.Calls);

        Assert.Empty(handler.Bodies);
    }

    private static ProviderSettings Provider(OutputTokenLimitWireDialect dialect) => new()
    {
        Name = "provider",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://provider.test/v1",
        Models = ["model"],
        OutputTokenLimitWireDialect = dialect,
    };

    private static IDisposable Factory(string lane, ProviderSettings provider, RecordingKeys keys, Capture handler) => lane switch
    {
        "long-rest" => new LongRestProviderClientFactory(keys, handler),
        "evaluation" => new MemoryEvaluationProviderClientFactory(keys, handler),
        "rollup" => new CampaignRollupProviderClientFactory(
            new TestOptionsMonitor<ArcanumSettings>(new() { DefaultModel = "model", Providers = [provider] }), keys, handler),
        _ => throw new ArgumentOutOfRangeException(nameof(lane)),
    };

    private static async Task<ChatClientLease> ResolveAsync(IDisposable factory, ProviderSettings provider) => factory switch
    {
        LongRestProviderClientFactory longRest => (await longRest.ResolveChatAsync(
            new(provider, "model", 0, new()), CancellationToken.None)).Value,
        MemoryEvaluationProviderClientFactory evaluation => (await evaluation.ResolveChatAsync(provider, "model", CancellationToken.None)).Value,
        CampaignRollupProviderClientFactory rollup => Assert.IsType<ChatClientLease>((await rollup.ResolveClientAsync(null, CancellationToken.None)).Value),
        _ => throw new ArgumentOutOfRangeException(nameof(factory)),
    };

    private sealed class RecordingKeys : IProviderApiKeyResolver
    {
        public int Calls { get; private set; }

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class Capture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"fixture","object":"chat.completion","created":0,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
