using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// The Tapestry's summary step (DESIGN §21.11): the only component that sends corpus text to a model.
/// </summary>
/// <remarks>
/// Every case drives the real <see cref="TapestrySummarizer"/> against a fake provider and a fake token
/// estimator, so the model fallback chain, the context-fit arithmetic, the untrusted-data framing and
/// fence, the headless-request flags and the failure mapping are each pinned by an assertion that a
/// mutation of that behaviour would break.
/// </remarks>
public sealed class TapestrySummarizerTests
{
    /// <summary><c>Tapestry:MaxSummaryTokens</c>'s code-owned default, which the reservations scale from.</summary>
    private const int MaxSummaryTokens = 512;

    private const int PromptTokens = 1000;

    private const int SafetyMarginTokens = 100;

    private static ArcanumSettings Settings(
        string? summaryModel = null,
        string? fastModel = null,
        string? defaultModel = null,
        int contextWindowLimit = 8192) =>
        new()
        {
            FastModel = fastModel ?? string.Empty,
            DefaultModel = defaultModel ?? string.Empty,
            Providers =
            [
                new ProviderSettings
                {
                    Name = "local",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost:1/v1",
                    Models = [new ModelEntry("summary"), new ModelEntry("fast"), new ModelEntry("default")],
                    ContextWindowLimit = contextWindowLimit,
                },
            ],
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Tapestry = new TapestryIntegrationSettings { SummaryModel = summaryModel },
                },
            },
        };

    private static TapestrySummarizer CreateSummarizer(
        ArcanumSettings settings,
        FakeIntelligenceProvider? provider = null,
        CapturingLogger? logger = null) =>
        new(
            provider ?? new FakeIntelligenceProvider(),
            new FixedTokenEstimator(PromptTokens, SafetyMarginTokens),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            logger ?? new CapturingLogger());

    private static TapestrySummaryRequest Request(params string[] texts) =>
        new(TapestryScopeKind.Workspace, "repo", 1, texts);

    [Theory]
    [InlineData("summary", "fast", "default", "summary")]
    [InlineData("  summary  ", "fast", "default", "summary")]
    [InlineData(null, "fast", "default", "fast")]
    [InlineData("", "  fast ", "default", "fast")]
    [InlineData(null, null, "default", "default")]
    [InlineData("   ", "", " default ", "default")]
    [InlineData(null, null, null, null)]
    public void ResolveSummaryModel_prefers_SummaryModel_then_FastModel_then_DefaultModel(
        string? summaryModel,
        string? fastModel,
        string? defaultModel,
        string? expected)
    {
        TapestrySummarizer summarizer = CreateSummarizer(Settings(summaryModel, fastModel, defaultModel));

        Assert.Equal(expected, summarizer.ResolveSummaryModel());
    }

    /// <summary>
    /// The fit check reserves the summary and the reasoning that counts against the same limit, so a
    /// cluster that only fits because the response was ignored is repartitioned rather than sent.
    /// </summary>
    [Fact]
    public void FitsOneRequest_reserves_twice_MaxSummaryTokens()
    {
        long required = PromptTokens + SafetyMarginTokens + (2L * MaxSummaryTokens);

        TapestrySummarizer exactlyFits = CreateSummarizer(
            Settings(summaryModel: "summary", contextWindowLimit: (int)required));

        TapestrySummarizer oneTokenShort = CreateSummarizer(
            Settings(summaryModel: "summary", contextWindowLimit: (int)required - 1));

        Assert.True(exactlyFits.FitsOneRequest(Request("alpha")));

        // One reservation of MaxSummaryTokens instead of two would still fit here.
        Assert.False(oneTokenShort.FitsOneRequest(Request("alpha")));
    }

    [Fact]
    public void FitsOneRequest_is_false_when_no_model_resolves_to_a_provider()
    {
        Assert.False(CreateSummarizer(Settings()).FitsOneRequest(Request("alpha")));

        ArcanumSettings unknownProvider = Settings(summaryModel: "summary");

        unknownProvider.Providers = [];

        Assert.False(CreateSummarizer(unknownProvider).FitsOneRequest(Request("alpha")));
    }

    [Fact]
    public void BuildUserPrompt_uses_a_fence_longer_than_any_backtick_run_in_the_excerpt()
    {
        string prompt = TapestrySummarizer.BuildUserPrompt(
            Request(
                "plain text",
                "a code sample ``` that closes a three-tick fence",
                "five ````` ticks and then two `` ticks",
                "```"));

        string[] fences = ExcerptFences(prompt);

        // Each excerpt is wrapped by a fence that is the longest backtick run it carries plus one, and
        // never shorter than three, so no excerpt can close the fence that frames it as data.
        Assert.Equal([3, 4, 6, 4], fences.Select(static fence => fence.Length));

        Assert.All(fences, static fence => Assert.True(fence.All(static character => character == '`')));

        Assert.Contains("UNTRUSTED DATA", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarizeAsync_sends_a_headless_request_for_the_resolved_model()
    {
        FakeIntelligenceProvider provider = new();

        TapestrySummarizer summarizer = CreateSummarizer(Settings(summaryModel: "summary"), provider);

        Result<string> result = await summarizer.SummarizeAsync(Request("alpha", "beta"), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("a summary", result.Value);

        PingRequest ping = Assert.Single(provider.Requests);

        Assert.Equal("summary", ping.Model);

        Assert.True(ping.UnattendedMode);

        Assert.True(ping.DisableMcpTools);

        Assert.True(ping.SkipSpellRouting);

        Assert.Same(ArcanumInvocationContext.None, Assert.Single(provider.Contexts));

        List<CoreChatMessage> messages = Assert.IsType<List<CoreChatMessage>>(ping.StatelessMessages);

        Assert.Equal(["system", "user"], messages.Select(static message => message.Role));

        Assert.Contains("UNTRUSTED DATA", messages[0].Content, StringComparison.Ordinal);

        Assert.Equal(TapestrySummarizer.BuildUserPrompt(Request("alpha", "beta")), messages[1].Content);
    }

    /// <summary>
    /// The excerpts are arbitrary workspace files, attachments and transcripts, so the model that reads
    /// them on an unattended timer must have nothing to call: a hostile file could otherwise ask it to
    /// send the corpus out through a web tool.
    /// </summary>
    [Fact]
    public async Task SummarizeAsync_requests_no_tools()
    {
        FakeIntelligenceProvider provider = new();

        TapestrySummarizer summarizer = CreateSummarizer(Settings(summaryModel: "summary"), provider);

        _ = await summarizer.SummarizeAsync(Request("alpha"), CancellationToken.None);

        PingRequest ping = Assert.Single(provider.Requests);

        Assert.True(ping.DisableAllTools);
    }

    /// <summary>
    /// The fit estimate reserves twice <c>MaxSummaryTokens</c> because reasoning counts against the
    /// output limit, so the request is capped at exactly that reservation rather than left unbounded.
    /// </summary>
    [Fact]
    public async Task SummarizeAsync_caps_output_at_the_reserved_budget()
    {
        FakeIntelligenceProvider provider = new();

        TapestrySummarizer summarizer = CreateSummarizer(Settings(summaryModel: "summary"), provider);

        _ = await summarizer.SummarizeAsync(Request("alpha"), CancellationToken.None);

        Assert.Equal(2 * MaxSummaryTokens, Assert.Single(provider.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task SummarizeAsync_treats_a_truncated_finish_as_a_summary_and_logs_it()
    {
        FakeIntelligenceProvider provider = new() { FinishReason = "length", Text = "a summary cut short" };

        CapturingLogger logger = new();

        TapestrySummarizer summarizer = CreateSummarizer(Settings(summaryModel: "summary"), provider, logger);

        Result<string> result = await summarizer.SummarizeAsync(Request("alpha"), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("a summary cut short", result.Value);

        Assert.Contains(logger.Entries, static entry => entry.Contains("output cap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SummarizeAsync_without_a_resolvable_model_reports_the_feature_as_unconfigured()
    {
        FakeIntelligenceProvider provider = new();

        Result<string> result = await CreateSummarizer(Settings(), provider)
            .SummarizeAsync(Request("alpha"), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.FeatureDisabled, result.Error.Code);

        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task SummarizeAsync_passes_a_provider_failure_through()
    {
        FakeIntelligenceProvider provider = new()
        {
            Failure = new Error(ErrorCodes.Embeddings.ProviderUnavailable, "budget refused"),
        };

        Result<string> result = await CreateSummarizer(Settings(summaryModel: "summary"), provider)
            .SummarizeAsync(Request("alpha"), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("budget refused", result.Error.Message);
    }

    [Fact]
    public async Task SummarizeAsync_maps_a_thrown_provider_exception_to_an_unavailable_failure()
    {
        FakeIntelligenceProvider provider = new() { Throw = new InvalidOperationException("transport blew up") };

        Result<string> result = await CreateSummarizer(Settings(summaryModel: "summary"), provider)
            .SummarizeAsync(Request("alpha"), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

        Assert.DoesNotContain("transport blew up", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarizeAsync_rejects_an_empty_summary()
    {
        FakeIntelligenceProvider provider = new() { Text = "   " };

        Result<string> result = await CreateSummarizer(Settings(summaryModel: "summary"), provider)
            .SummarizeAsync(Request("alpha"), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);
    }

    [Fact]
    public async Task SummarizeAsync_lets_a_cancellation_the_caller_requested_escape()
    {
        using CancellationTokenSource cancellation = new();

        FakeIntelligenceProvider provider = new() { OnExecute = cancellation.Cancel };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSummarizer(Settings(summaryModel: "summary"), provider)
                .SummarizeAsync(Request("alpha"), cancellation.Token));
    }

    /// <summary>The fence lines that open each excerpt, in order.</summary>
    private static string[] ExcerptFences(string prompt)
    {
        List<string> fences = [];

        string[] lines = prompt.ReplaceLineEndings("\n").Split('\n');

        for (int index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index].StartsWith("--- excerpt ", StringComparison.Ordinal))
            {
                fences.Add(lines[index + 1]);
            }
        }

        return [.. fences];
    }

    private sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider
    {
        public List<PingRequest> Requests { get; } = [];

        public List<ArcanumInvocationContext> Contexts { get; } = [];

        public string Text { get; set; } = "a summary";

        public string? FinishReason { get; set; }

        public Error? Failure { get; set; }

        public Exception? Throw { get; set; }

        public Action? OnExecute { get; set; }

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            Requests.Add(request);

            Contexts.Add(invocationContext);

            OnExecute?.Invoke();

            cancellationToken.ThrowIfCancellationRequested();

            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(
                Failure is { } failure
                    ? Result<PromptTurnResult>.Failure(failure)
                    : Result<PromptTurnResult>.Success(new PromptTurnResult(Text, null, null, FinishReason)));
        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();
    }

    /// <summary>A token estimator that charges every text the same fixed cost.</summary>
    private sealed class FixedTokenEstimator(int tokens, int safetyMargin) : IModelTokenEstimator
    {
        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string canonicalModel) =>
            throw new NotSupportedException();

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string canonicalModel) =>
            throw new NotSupportedException();

        public TokenEstimate EstimateText(ProviderSettings provider, string canonicalModel, string? text) =>
            new(tokens, TokenEstimateClassification.Estimated, "fixed", 1d, safetyMargin);

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<TapestrySummarizer>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
