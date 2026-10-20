using System.Net;
using System.ClientModel.Primitives;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class OutputTokenLimitWireTests
{
    [Theory]
    [InlineData(true, "maxTokens", "max_tokens")]
    [InlineData(false, "maxTokens", "max_tokens")]
    [InlineData(true, "maxCompletionTokens", "max_completion_tokens")]
    [InlineData(false, "maxCompletionTokens", "max_completion_tokens")]
    [InlineData(false, null, "max_completion_tokens")]
    public async Task Actual_factory_executor_SDK_uses_declared_field_without_mutating_options(bool streaming, string? dialect, string field)
    {
        ProviderSettings provider = Read(dialect);

        var settings = new ArcanumSettings { DefaultModel = "public-model", Providers = [provider] };

        using Capture capture = new(streaming);

        var factory = new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials());

        using ChatClientLease lease = await factory.ResolveClientAsync(null, CancellationToken.None);

        ChatOptions options = new() { MaxOutputTokens = 32, Temperature = 0, Seed = 103, ToolMode = ChatToolMode.None };

        List<ChatMessage> messages = [new(ChatRole.System, "Public preamble."), new(ChatRole.User, "Public question.")];

        ModelCallExecutor executor = new();

        if (streaming)
        {
            await foreach (var item in executor.ExecuteStreamingAsync(lease.ChatClient, messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None)) { }
        }
        else
        {
            Assert.False((await executor.ExecuteBufferedAsync(lease.ChatClient, messages, options, UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, CancellationToken.None)).IsFailure);
        }

        using JsonDocument wire = JsonDocument.Parse(Assert.Single(capture.Bodies));

        JsonElement root = wire.RootElement;

        Assert.Equal(32, root.GetProperty(field).GetInt32());

        Assert.False(root.TryGetProperty(field == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _));

        Assert.Equal("public-model", root.GetProperty("model").GetString());

        Assert.Equal("Public preamble.", root.GetProperty("messages")[0].GetProperty("content").GetString());

        Assert.Equal("Public question.", root.GetProperty("messages")[1].GetProperty("content").GetString());

        Assert.Equal(0, root.GetProperty("temperature").GetInt32());

        Assert.Equal(103, root.GetProperty("seed").GetInt32());

        Assert.False(root.TryGetProperty("tool_choice", out _));

        Assert.Equal(32, options.MaxOutputTokens);

        Assert.Null(options.RawRepresentationFactory);

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void Source_generated_configuration_roundtrips_explicit_dialect_and_rejects_unknown_names()
    {
        string json = JsonSerializer.Serialize(Read("maxTokens"), ConfigurationJsonContext.Default.ProviderSettings);

        Assert.Contains("\"outputTokenLimitWireDialect\": \"maxTokens\"", json, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => Read("unknown"));
    }

    [Fact]
    public void Configuration_binder_retains_explicit_dialect()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Providers:0:Name"] = "public", ["Providers:0:OutputTokenLimitWireDialect"] = "MaxTokens" }).Build();

        ArcanumSettings settings = new();

        config.Bind(settings);

        string json = JsonSerializer.Serialize(settings.Providers[0], ConfigurationJsonContext.Default.ProviderSettings);

        Assert.Contains("\"outputTokenLimitWireDialect\": \"maxTokens\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_captures_dialect_before_async_credentials_and_does_not_follow_mutable_provider()
    {
        ProviderSettings provider = Read("maxTokens");

        var settings = new ArcanumSettings { DefaultModel = "public-model", Providers = [provider] };

        var credentials = new DelayedCredentials();

        using Capture capture = new(false);

        var factory = new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), credentials);

        Task<ChatClientLease> pending = factory.ResolveClientAsync(null, CancellationToken.None);

        provider.OutputTokenLimitWireDialect = OutputTokenLimitWireDialect.MaxCompletionTokens;

        credentials.Release.SetResult(null);

        using var lease = await pending;

        await lease.ChatClient.GetResponseAsync([new(Microsoft.Extensions.AI.ChatRole.User, "Public question.")], new() { MaxOutputTokens = 32 });

        using var wire = JsonDocument.Parse(Assert.Single(capture.Bodies));

        Assert.Equal(32, wire.RootElement.GetProperty("max_tokens").GetInt32());

        Assert.Equal(OutputTokenLimitWireDialect.MaxTokens, lease.Provider.OutputTokenLimitWireDialect);
    }

    [Fact]
    public async Task Unknown_numeric_dialect_fails_validation_and_factory_before_any_IO()
    {
        var provider = Read(null) with { OutputTokenLimitWireDialect = (OutputTokenLimitWireDialect)99 };

        var settings = new ArcanumSettings { DefaultModel = "public-model", Providers = [provider] };

        var validation = new ConfigurationValidator().Validate(settings);

        Assert.True(validation.IsFailure);

        Assert.Contains(validation.Error.Details!, error => error.Pointer == "providers[0].outputTokenLimitWireDialect");

        using Capture capture = new(false);

        var factory = new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials());

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.ResolveClientAsync(null, CancellationToken.None));

        Assert.Empty(capture.Bodies);
    }

    [Theory]
    [InlineData("maxTokens")]
    [InlineData("maxCompletionTokens")]
    public async Task Conflicting_actual_SDK_fields_fail_closed_without_transport(string dialect)
    {
        var provider = Read(dialect);

        var settings = new ArcanumSettings { DefaultModel = "public-model", Providers = [provider] };

        using Capture capture = new(false);

        using var lease = await new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials()).ResolveClientAsync(null, CancellationToken.None);

        ChatOptions options = new() { MaxOutputTokens = 32, RawRepresentationFactory = _ => ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromString("{\"max_tokens\":99}")) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ChatClient.GetResponseAsync([new(Microsoft.Extensions.AI.ChatRole.User, "Public question.")], options));

        Assert.Empty(capture.Bodies);

        Assert.Equal(32, options.MaxOutputTokens);
    }

    [Fact]
    public async Task Cancelled_legacy_call_never_reaches_transport()
    {
        var provider = Read("maxTokens");

        var settings = new ArcanumSettings { DefaultModel = "public-model", Providers = [provider] };

        using Capture capture = new(false);

        using var lease = await new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials()).ResolveClientAsync(null, CancellationToken.None);

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ChatClient.GetResponseAsync([new(Microsoft.Extensions.AI.ChatRole.User, "Public question.")], new() { MaxOutputTokens = 32 }, cancelled.Token));

        Assert.Empty(capture.Bodies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_the_owned_limit_field_differs_across_actual_SDK_payloads(bool streaming)
    {
        List<JsonObject> wires = [];

        ChatOptions options = new()
        {
            MaxOutputTokens = 32,
            Temperature = 0,
            Seed = 103,
            RawRepresentationFactory = _ => ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromString("{\"public_extension\":{\"nested\":[true,null,0.125,\"public-value\"]}}")),
        };

        var originalFactory = options.RawRepresentationFactory;

        foreach (string dialect in new[] { "maxCompletionTokens", "maxTokens" })
        {
            var provider = Read(dialect);

            var settings = new ArcanumSettings { DefaultModel = "public-model", Providers = [provider] };

            using Capture capture = new(streaming);

            using var lease = await new ChatClientFactory(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials()).ResolveClientAsync(null, CancellationToken.None);

            if (streaming)
            {
                await foreach (var item in lease.ChatClient.GetStreamingResponseAsync([new(ChatRole.System, "Public preamble."), new(ChatRole.User, "Public question.")], options)) { }
            }
            else
            {
                await lease.ChatClient.GetResponseAsync([new(ChatRole.System, "Public preamble."), new(ChatRole.User, "Public question.")], options);
            }

            var wire = JsonNode.Parse(Assert.Single(capture.Bodies))!.AsObject();

            Assert.Equal("public-value", wire["public_extension"]!["nested"]![3]!.GetValue<string>());

            Assert.True(wire.Remove(dialect == "maxTokens" ? "max_tokens" : "max_completion_tokens"));

            wires.Add(wire);
        }

        Assert.True(JsonNode.DeepEquals(wires[0], wires[1]));

        Assert.Same(originalFactory, options.RawRepresentationFactory);

        Assert.Equal(32, options.MaxOutputTokens);
    }

    private sealed class DelayedCredentials : IProviderApiKeyResolver
    {
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Release.Task;
    }

    private static ProviderSettings Read(string? dialect) => JsonSerializer.Deserialize("{\"name\":\"public\",\"endpoint\":\"http://example.test/v1\",\"models\":[{\"name\":\"public-model\"}]" + (dialect is null ? "}" : ",\"outputTokenLimitWireDialect\":\"" + dialect + "\"}"), ConfigurationJsonContext.Default.ProviderSettings)!;

    private sealed class MissingCredentials : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class Capture(bool streaming) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            string body = streaming ? "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"public-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n" : "{\"id\":\"fixture\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"public-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}";

            return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
