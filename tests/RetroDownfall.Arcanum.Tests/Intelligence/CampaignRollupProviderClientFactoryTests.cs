using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class CampaignRollupProviderClientFactoryTests
{
    [Fact]
    public async Task Model_resolution_indexes_the_materialized_configuration_without_caller_collection_callbacks()
    {
        ArcanumSettings settings = Settings();

        CallbackModels models = new([new ModelEntry("model")]);

        settings.Providers[0].Models = models;

        RecordingFactory clients = new(new RecordingProvider(HttpStatusCode.OK));

        CampaignRollupProviderClientFactory factory = new(clients, new TestOptionsMonitor<ArcanumSettings>(settings),
            EnvironmentOnlyProviderApiKeyResolver.Instance);

        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, CancellationToken.None);

        Assert.True(resolved.IsSuccess);

        using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

        Assert.Equal("model", lease.ResolvedModel);

        Assert.Equal("provider", lease.Provider.Name);

        Assert.Equal(0, models.IndexerCalls);

        Assert.NotSame(models, lease.Provider.Models);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_lease_preserves_the_provider_selected_before_key_resolution(bool changeDuringKeyResolution)
    {
        ArcanumSettings settings = Settings();

        ProviderSettings configured = settings.Providers[0];

        void ChangeConfiguration()
        {
            configured.Endpoint = "https://changed-provider.test/v1";

            configured.Models[0].Name = "changed-model";

            configured.ContextWindowLimit = 2048;
        }

        RecordingFactory clients = new(new RecordingProvider(HttpStatusCode.OK));

        CallbackKeyResolver keys = new(changeDuringKeyResolution ? ChangeConfiguration : null);

        CampaignRollupProviderClientFactory factory = new(clients, new TestOptionsMonitor<ArcanumSettings>(settings), keys);

        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, CancellationToken.None);

        Assert.True(resolved.IsSuccess);

        using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

        if (!changeDuringKeyResolution)
        {
            ChangeConfiguration();
        }

        Assert.Equal("https://provider.test/v1", lease.Provider.Endpoint);

        Assert.Equal("model", lease.Provider.Models[0].Name);

        Assert.Equal("model", lease.ResolvedModel);

        Assert.Equal(8192, lease.Provider.ContextWindowLimit);

        Assert.NotSame(configured, lease.Provider);

        Assert.NotSame(configured.Models[0], lease.Provider.Models[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task One_executor_call_sends_one_physical_request_without_strict_rewrite(HttpStatusCode status)
    {
        RecordingProvider handler = new(status);

        RecordingFactory clients = new(handler);

        CampaignRollupProviderClientFactory factory = new(clients, new TestOptionsMonitor<ArcanumSettings>(Settings()),
            EnvironmentOnlyProviderApiKeyResolver.Instance);

        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, CancellationToken.None);

        Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Message : null);

        using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""");

        ChatOptions options = new()
        {
            MaxOutputTokens = 2048,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "campaign_summary", "A bounded continuity summary."),
        };

        _ = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Decisions from a completed contribution.")], options));

        Assert.Equal(CampaignRollupProviderClientFactory.HttpClientName, clients.RequestedName);

        Assert.Equal(1, handler.CallCount);

        using JsonDocument body = JsonDocument.Parse(Assert.IsType<string>(handler.LastBody));

        JsonElement responseFormat = body.RootElement.GetProperty("response_format");

        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());

        JsonElement wrapper = responseFormat.GetProperty("json_schema");

        Assert.Equal("campaign_summary", wrapper.GetProperty("name").GetString());

        Assert.Equal("A bounded continuity summary.", wrapper.GetProperty("description").GetString());

        Assert.False(wrapper.TryGetProperty("strict", out _));

        Assert.False(body.RootElement.TryGetProperty("tools", out _));

        Assert.Equal(JsonValueKind.False, wrapper.GetProperty("schema").GetProperty("additionalProperties").ValueKind);
    }

    [Fact]
    public async Task Opaque_familiar_transport_is_deferred_without_creating_a_client()
    {
        ArcanumSettings settings = Settings();

        settings.Providers[0].Type = AiProviderKind.CodexCli;

        RecordingFactory clients = new(new RecordingProvider(HttpStatusCode.OK));

        CampaignRollupProviderClientFactory factory = new(clients, new TestOptionsMonitor<ArcanumSettings>(settings),
            EnvironmentOnlyProviderApiKeyResolver.Instance);

        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, CancellationToken.None);

        Assert.True(resolved.IsSuccess);

        Assert.Null(resolved.Value);

        Assert.Null(clients.RequestedName);
    }

    private static ArcanumSettings Settings() => new()
    {
        DefaultModel = "model",
        Providers =
        [
            new ProviderSettings
            {
                Name = "provider",
                Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://provider.test/v1",
                Models = ["model"],
            },
        ],
    };

    private sealed class RecordingFactory(RecordingProvider handler) : IHttpClientFactory
    {
        public string? RequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestedName = name;

            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CallbackModels(ModelEntry[] values) : IReadOnlyList<ModelEntry>
    {
        public int IndexerCalls { get; private set; }

        public int Count => values.Length;

        public ModelEntry this[int index]
        {
            get
            {
                IndexerCalls++;

                return values[index];
            }
        }

        public IEnumerator<ModelEntry> GetEnumerator() => ((IEnumerable<ModelEntry>)values).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CallbackKeyResolver(Action? callback) : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken)
        {
            callback?.Invoke();

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class RecordingProvider(HttpStatusCode status) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error":{"message":"strict unsupported","type":"provider_error","code":"test"}}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
