using System.Collections;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class CampaignMaintenanceModelCallTests
{
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("unknown-local-model")]
    public void Closed_estimate_preserves_plaintext_schema_framing_margin_and_reservation(string model)
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider(model);
        CampaignMaintenancePayload payload = new("Summarize continuity.", "Unicode 👩🏽‍💻 café\nSession facts.");

        using System.Text.Json.JsonDocument schema = System.Text.Json.JsonDocument.Parse(CampaignMaintenancePayload.SchemaJson);
        ContextTokenBreakdown existing = estimator.EstimateContext(new(
            provider,
            model,
            [new(ChatRole.System, payload.SystemPrompt), new(ChatRole.User, payload.UserPrompt)],
            new ChatOptions
            {
                MaxOutputTokens = CampaignMaintenancePayload.MaxOutputTokens,
                Tools = [],
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(),
                    CampaignMaintenancePayload.SchemaName, CampaignMaintenancePayload.SchemaDescription),
            },
            CampaignMaintenancePayload.MaxOutputTokens,
            0));

        ContextTokenBreakdown closed = estimator.EstimateCampaignMaintenance(provider, model, payload);

        Assert.Equal(existing.Profile, closed.Profile);
        Assert.Equal(existing.Components, closed.Components);
        Assert.Equal(existing.MessageTokenCounts, closed.MessageTokenCounts);
        Assert.Equal(existing.InputTokens, closed.InputTokens);
        Assert.Equal(existing.SafetyMarginTokens, closed.SafetyMarginTokens);
        Assert.Equal(existing.TotalTokens, closed.TotalTokens);
        Assert.Equal(2048, closed.ReservedAnswerTokens);
        Assert.Equal(0, closed.ReservedReasoningTokens);
        Assert.NotEmpty(closed.PayloadFingerprint);
    }

    [Fact]
    public async Task Closed_call_sends_exact_owned_payload_after_fence_and_reconciles_paid_usage()
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider("gpt-4o");
        CampaignMaintenancePayload payload = new("Summarize continuity.", "facts");
        ContextTokenBreakdown estimate = estimator.EstimateCampaignMaintenance(provider, "gpt-4o", payload);
        bool fenced = false;
        int fences = 0;
        UsageDetails usage = new() { InputTokenCount = 77, OutputTokenCount = 9, TotalTokenCount = 86 };
        RecordingChatClient chat = new(() => Assert.True(fenced), new(new ChatMessage(ChatRole.Assistant, "{\"summary\":\"continuity\"}"))
        {
            Usage = usage,
            FinishReason = ChatFinishReason.Stop,
        });

        Result<CampaignMaintenanceCallResult> outcome = await new ModelCallExecutor(estimator).ExecuteCampaignMaintenanceAsync(
            chat, payload, UnrestrictedTurnBudget.Instance,
            _ => { fenced = true; fences++; return Task.CompletedTask; },
            CancellationToken.None, new(provider, "gpt-4o", 2048, 0, estimate));

        Assert.True(outcome.IsSuccess, outcome.Error.Message);
        Assert.Equal(1, fences);
        Assert.Equal(1, chat.CallCount);
        Assert.Equal([payload.SystemPrompt, payload.UserPrompt], chat.Messages!.Select(static message => message.Text));
        Assert.Equal([ChatRole.System, ChatRole.User], chat.Messages!.Select(static message => message.Role));
        Assert.Equal(2048, chat.Options!.MaxOutputTokens);
        Assert.Empty(chat.Options.Tools ?? []);
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(chat.Options.ResponseFormat);
        Assert.Equal(CampaignMaintenancePayload.SchemaName, format.SchemaName);
        Assert.Equal(CampaignMaintenancePayload.SchemaDescription, format.SchemaDescription);
        Assert.Equal(CampaignMaintenancePayload.SchemaJson, format.Schema!.Value.GetRawText());
        Assert.Equal("{\"summary\":\"continuity\"}", outcome.Value.Text);
        Assert.Same(usage, outcome.Value.Usage);
        Assert.Equal(estimate.InputTokens, outcome.Value.ContextBreakdown.InputTokens);
        Assert.Equal(77, outcome.Value.ContextBreakdown.ProviderReportedInputTokens);
        Assert.Equal("stop", outcome.Value.FinishReason);
    }

    [Fact]
    public async Task Changed_payload_is_refused_before_fence_or_provider_io()
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider("gpt-4o");
        CampaignMaintenancePayload original = new("Summarize continuity.", "original facts");
        ContextTokenBreakdown estimate = estimator.EstimateCampaignMaintenance(provider, "gpt-4o", original);
        RecordingChatClient chat = new();
        int fences = 0;

        Result<CampaignMaintenanceCallResult> outcome = await new ModelCallExecutor(estimator).ExecuteCampaignMaintenanceAsync(
            chat, new(original.SystemPrompt, "changed facts"), UnrestrictedTurnBudget.Instance,
            _ => { fences++; return Task.CompletedTask; },
            CancellationToken.None, new(provider, "gpt-4o", 2048, 0, estimate));

        Assert.True(outcome.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, outcome.Error.Code);
        Assert.Equal(0, fences);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Context_overflow_is_refused_before_fence_or_provider_io()
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider("gpt-4o");
        provider.ContextWindowLimit = 2048;
        CampaignMaintenancePayload payload = new("Summarize continuity.", "facts");
        ContextTokenBreakdown estimate = estimator.EstimateCampaignMaintenance(provider, "gpt-4o", payload);
        RecordingChatClient chat = new();
        int fences = 0;

        Result<CampaignMaintenanceCallResult> outcome = await new ModelCallExecutor(estimator).ExecuteCampaignMaintenanceAsync(
            chat, payload, UnrestrictedTurnBudget.Instance,
            _ => { fences++; return Task.CompletedTask; },
            CancellationToken.None, new(provider, "gpt-4o", 2048, 0, estimate));

        Assert.True(outcome.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, outcome.Error.Code);
        Assert.Equal(0, fences);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Authority_fence_failure_prevents_provider_io()
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider("gpt-4o");
        CampaignMaintenancePayload payload = new("Summarize continuity.", "facts");
        ContextTokenBreakdown estimate = estimator.EstimateCampaignMaintenance(provider, "gpt-4o", payload);
        RecordingChatClient chat = new();

        Result<CampaignMaintenanceCallResult> outcome = await new ModelCallExecutor(estimator).ExecuteCampaignMaintenanceAsync(
            chat, payload, UnrestrictedTurnBudget.Instance,
            _ => throw new InvalidOperationException("Authority revoked."),
            CancellationToken.None, new(provider, "gpt-4o", 2048, 0, estimate));

        Assert.True(outcome.IsFailure);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Cancellation_at_the_authority_fence_propagates_without_provider_io()
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider("gpt-4o");
        CampaignMaintenancePayload payload = new("Summarize continuity.", "facts");
        ContextTokenBreakdown estimate = estimator.EstimateCampaignMaintenance(provider, "gpt-4o", payload);
        RecordingChatClient chat = new();
        using CancellationTokenSource cancellation = new();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelCallExecutor(estimator).ExecuteCampaignMaintenanceAsync(
                chat, payload, UnrestrictedTurnBudget.Instance,
                token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                cancellation.Token, new(provider, "gpt-4o", 2048, 0, estimate)));

        Assert.Equal(0, chat.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opaque_response_collection_is_not_executed_and_paid_usage_is_retained(bool innerCollection)
    {
        ModelTokenEstimator estimator = Estimator();
        ProviderSettings provider = Provider("gpt-4o");
        CampaignMaintenancePayload payload = new("Summarize continuity.", "facts");
        ContextTokenBreakdown estimate = estimator.EstimateCampaignMaintenance(provider, "gpt-4o", payload);
        UsageDetails usage = new() { InputTokenCount = 42, OutputTokenCount = 5 };
        OpaqueList<ChatMessage> opaqueMessages = new();
        OpaqueList<AIContent> opaqueContents = new();
        ChatResponse response = innerCollection
            ? new(new ChatMessage(ChatRole.Assistant, opaqueContents))
            : new(opaqueMessages);

        response.Usage = usage;

        RecordingChatClient chat = new(response: response);

        Result<CampaignMaintenanceCallResult> outcome = await new ModelCallExecutor(estimator).ExecuteCampaignMaintenanceAsync(
            chat, payload, UnrestrictedTurnBudget.Instance, _ => Task.CompletedTask,
            CancellationToken.None, new(provider, "gpt-4o", 2048, 0, estimate));

        Assert.True(outcome.IsSuccess, outcome.Error.Message);
        Assert.Empty(outcome.Value.Text);
        Assert.Same(usage, outcome.Value.Usage);
        Assert.Equal(42, outcome.Value.ContextBreakdown.ProviderReportedInputTokens);
        Assert.Equal(0, opaqueMessages.AccessCount);
        Assert.Equal(0, opaqueContents.AccessCount);
    }

    private static ModelTokenEstimator Estimator() =>
        new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

    private static ProviderSettings Provider(string model) => new()
    {
        Name = "openai-compatible",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://api.openai.com/v1",
        Models = [new ModelEntry(model)],
        ContextWindowLimit = 128_000,
    };

    private sealed class RecordingChatClient(Action? onSend = null, ChatResponse? response = null) : IChatClient
    {
        public int CallCount { get; private set; }

        public List<ChatMessage>? Messages { get; private set; }

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            onSend?.Invoke();
            Messages = [.. messages];
            Options = options;
            return Task.FromResult(response ?? new(new ChatMessage(ChatRole.Assistant, "{\"summary\":\"continuity\"}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class OpaqueList<T> : IList<T>
    {
        public int AccessCount { get; private set; }

        public T this[int index] { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }

        public int Count
        {
            get
            {
                AccessCount++;

                throw new InvalidOperationException();
            }
        }

        public bool IsReadOnly => throw new InvalidOperationException();

        public IEnumerator<T> GetEnumerator()
        {
            AccessCount++;

            throw new InvalidOperationException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            AccessCount++;

            throw new InvalidOperationException();
        }

        public void Add(T item) => throw new InvalidOperationException();

        public void Clear() => throw new InvalidOperationException();

        public bool Contains(T item) => throw new InvalidOperationException();

        public void CopyTo(T[] array, int arrayIndex) => throw new InvalidOperationException();

        public bool Remove(T item) => throw new InvalidOperationException();

        public int IndexOf(T item) => throw new InvalidOperationException();

        public void Insert(int index, T item) => throw new InvalidOperationException();

        public void RemoveAt(int index) => throw new InvalidOperationException();
    }
}
