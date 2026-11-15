using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Trait("Suite", "Dci")]
public sealed class ModelTokenEstimatorWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_provider_actual_wire_keeps_one_catalog_and_reported_usage_does_not_lower_admission(bool streaming)
    {
        ProviderSettings provider = new()
        {
            Name = "public-fixture",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "http://example.invalid/v1",
            Models = [new ModelEntry("public-model")],
            ContextWindowLimit = 131_072,
        };

        ArcanumSettings settings = new() { DefaultModel = "public-model", Providers = [provider] };

        using Capture capture = new(streaming);

        ChatClientFactory factory = new(capture, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials());

        using ChatClientLease lease = await factory.ResolveClientAsync(null, CancellationToken.None);

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        PublicLookupFunction tool = new();

        string prompt = new('x', 400);

        List<ChatMessage> messages = [new(ChatRole.User, prompt)];

        ChatOptions options = new() { Tools = [tool], MaxOutputTokens = 32 };

        ModelTokenizationRequest request = new(provider, "public-model", messages, options, 32, 0);

        ContextTokenBreakdown before = estimator.EstimateContext(request);

        Assert.Equal(ModelTokenizationProfileType.UnknownFallback, before.Profile.Type);

        Assert.Equal(Encoding.UTF8.GetByteCount(prompt), before.Source(ContextTokenSource.CurrentPrompt).TokenCount);

        int catalogBytes = Encoding.UTF8.GetByteCount(tool.Name)
            + Encoding.UTF8.GetByteCount(tool.Description)
            + Encoding.UTF8.GetByteCount(tool.JsonSchema.GetRawText());

        Assert.Equal(catalogBytes + before.Profile.PerToolOverheadTokens, before.Source(ContextTokenSource.Tools).TokenCount);

        Assert.True(before.SafetyMarginTokens > 0);

        ModelCallExecutor executor = new(estimator);

        ModelCallContext context = new(provider, "public-model", 32, 0, before);

        ContextTokenBreakdown after;

        if (streaming)
        {
            List<ModelCallUpdate> updates = [];

            await foreach (ModelCallUpdate update in executor.ExecuteStreamingAsync(
                lease.ChatClient, messages, options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context))
            {
                updates.Add(update);
            }

            Assert.Empty(updates.OfType<ModelCallFailureUpdate>());

            after = updates.OfType<ModelCallContextUpdate>().Last().Breakdown;
        }
        else
        {
            ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(
                lease.ChatClient, messages, options, UnrestrictedTurnBudget.Instance,
                ModelCallPurpose.MainInference, CancellationToken.None, context);

            Assert.True(outcome.IsSuccess);

            after = Assert.IsType<ContextTokenBreakdown>(outcome.Value.ContextBreakdown);
        }

        using JsonDocument wire = JsonDocument.Parse(Assert.Single(capture.Bodies));

        JsonElement root = wire.RootElement;

        Assert.Equal(prompt, Assert.Single(root.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());

        JsonElement function = Assert.Single(root.GetProperty("tools").EnumerateArray()).GetProperty("function");

        Assert.Equal(tool.Name, function.GetProperty("name").GetString());

        Assert.Equal(tool.Description, function.GetProperty("description").GetString());

        Assert.True(JsonElement.DeepEquals(tool.JsonSchema, function.GetProperty("parameters")));

        // This is an intentionally smaller fixture usage report, not a live tokenizer measurement.
        Assert.Equal(17, after.ProviderReportedInputTokens);

        Assert.True(after.ProviderReportedInputValid);

        Assert.Equal(before.InputTokens, after.InputTokens);

        Assert.Equal(17L - before.InputTokens, after.EstimationVarianceTokens);

        ContextTokenBreakdown next = estimator.EstimateContext(request);

        Assert.Equal(before.InputTokens, next.InputTokens);

        Assert.Equal(before.SafetyMarginTokens, next.SafetyMarginTokens);

        Assert.Null(next.ProviderReportedInputTokens);
    }

    private sealed class PublicLookupFunction : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{"type":"object","properties":{"query":{"type":"string","description":"A public query."}},"required":["query"],"additionalProperties":false}""")
            .RootElement.Clone();

        public override string Name => "public_lookup";

        public override string Description => "Looks up public information.";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The provider-wire fixture must never invoke a tool.");
    }

    private sealed class MissingCredentials : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class Capture(bool streaming) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            const string completion = """{"id":"fixture","object":"chat.completion","created":0,"model":"public-model","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":17,"completion_tokens":1,"total_tokens":18}}""";

            const string stream = "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"public-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"public-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":1,\"total_tokens\":18}}\n\n"
                + "data: [DONE]\n\n";

            return new(HttpStatusCode.OK)
            {
                Content = ProviderSdkTestTransport.Content(
                    streaming ? stream : completion,
                    streaming ? "text/event-stream" : "application/json"),
            };
        }
    }
}
