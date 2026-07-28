using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ModelTokenEstimatorTests
{
    [Fact]
    public void EstimateContext_KnownO200kModel_UsesExactTextTokenizerWithoutMargin()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            [new ChatMessage(ChatRole.User, "Unicode: 👩🏽‍💻 café \ud83d\ude80")],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.Equal(ModelTokenizationProfileType.ExactLocalTokenizer, breakdown.Profile.Type);
        Assert.Equal("o200k_base", breakdown.Profile.TokenizerId);
        Assert.Equal(0, breakdown.SafetyMarginTokens);
        Assert.True(breakdown.InputTokens > 0);
        Assert.Equal(TokenEstimateClassification.Exact, breakdown.Source(ContextTokenSource.CurrentPrompt).Classification);
    }

    [Fact]
    public void EstimateContext_SystemSegmentation_PreservesExactOriginalText()
    {
        const string systemText = "system text without a trailing newline";
        ModelTokenEstimator estimator = CreateEstimator();

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            Provider("gpt-4o"),
            "gpt-4o",
            [new ChatMessage(ChatRole.System, systemText)],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));
        int expected = Microsoft.ML.Tokenizers.TiktokenTokenizer
            .CreateForEncoding("o200k_base")
            .CountTokens(systemText);

        Assert.Equal(expected, breakdown.Source(ContextTokenSource.SystemCodexSpell).TokenCount);
    }

    [Fact]
    public void EstimateContext_UnknownModel_UsesConservativeDocumentedFallback()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("unknown-local-model");

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "unknown-local-model",
            [new ChatMessage(ChatRole.User, new string('x', 400))],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.Equal(ModelTokenizationProfileType.UnknownFallback, breakdown.Profile.Type);
        Assert.Equal(TokenEstimateClassification.Estimated, breakdown.OverallClassification);
        Assert.True(breakdown.SafetyMarginTokens > 0);
        Assert.Contains("fallback", breakdown.Profile.ProfileId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProfile_ModelNameThatOnlySharesPrefix_DoesNotClaimExactTokenizer()
    {
        ModelTokenEstimator estimator = CreateEstimator();

        ResolvedModelTokenizationProfile profile = estimator.ResolveProfile(
            Provider("o3custom-local"),
            "o3custom-local");

        Assert.Equal(ModelTokenizationProfileType.UnknownFallback, profile.Type);
    }

    [Fact]
    public void ResolveProfile_OpenAiNamedAliasOnUnverifiedEndpoint_UsesFallback()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-5-local");
        provider.Endpoint = "http://localhost:11434/v1";

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-5-local",
            [new ChatMessage(ChatRole.User, "👩🏽‍💻")],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.Equal(ModelTokenizationProfileType.UnknownFallback, breakdown.Profile.Type);
        Assert.True(
            breakdown.Source(ContextTokenSource.CurrentPrompt).TokenCount
            >= System.Text.Encoding.UTF8.GetByteCount("👩🏽‍💻"));
    }

    [Fact]
    public async Task ModelCallExecutor_ValidatesUnknownModelFallbackProfile()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("custom-model");
        List<ChatMessage> messages = [new(ChatRole.User, "hello")];
        ChatOptions options = new();
        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "custom-model",
            messages,
            options,
            ReservedAnswerTokens: 32,
            ReservedReasoningTokens: 0));
        CountingChatClient chat = new();

        ModelCallOutcome outcome = await new ModelCallExecutor(estimator)
            .ExecuteBufferedAsync(
                chat,
                messages,
                options,
                new TurnBudget(),
                ModelCallPurpose.MainInference,
                CancellationToken.None,
                new ModelCallContext(provider, "custom-model", 32, 0, breakdown));

        Assert.True(outcome.IsSuccess);
        Assert.Same(breakdown, outcome.Value.ContextBreakdown);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public void EstimateContext_IncludesFullToolSchemaAndStructuredOutputSchema()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");
        AIFunction tool = AIFunctionFactory.Create(
            (string query, string database, int limit, bool includeMetadata) => $"{database}:{query}:{limit}:{includeMetadata}",
            "run_database_query",
            "Runs a database query and returns matching rows with optional metadata.");
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"answer":{"type":"string"},"citations":{"type":"array","items":{"type":"string"}}},"required":["answer","citations"],"additionalProperties":false}""");
        ChatOptions options = new()
        {
            Tools = [tool],
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "answer"),
        };

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            [new ChatMessage(ChatRole.User, "query")],
            options,
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.True(breakdown.Source(ContextTokenSource.Tools).TokenCount > 0);
        Assert.True(breakdown.Source(ContextTokenSource.StructuredOutput).TokenCount > 0);
        Assert.True(
            breakdown.Source(ContextTokenSource.Tools).TokenCount
            > estimator.EstimateContext(new ModelTokenizationRequest(
                provider,
                "gpt-4o",
                [new ChatMessage(ChatRole.User, "query")],
                new ChatOptions(),
                ReservedAnswerTokens: 0,
                ReservedReasoningTokens: 0)).Source(ContextTokenSource.Tools).TokenCount);
    }

    [Fact]
    public void EstimateContext_ClassifiesMaterializedRagMemoryAndAttachments()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");
        string system =
            """
            base
            ### Lexicon (Known Context)
            Alice is an engineer.
            ### Semantic Context (Retrieved Codebase)
            public sealed class Widget {}
            ### Session Attachments Index
            - design.md versions=1
            ### Attached Files for this Turn
            launch checklist
            ### Active Operational Spell (review)
            inspect carefully
            """;
        ChatMessage prompt = new(
            ChatRole.User,
            [
                new TextContent("What changed?"),
                new DataContent(new byte[] { 1, 2, 3, 4 }, "image/png"),
            ]);

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            [new ChatMessage(ChatRole.System, system), prompt],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.True(breakdown.Source(ContextTokenSource.SystemCodexSpell).TokenCount > 0);
        Assert.True(breakdown.Source(ContextTokenSource.LexiconSaga).TokenCount > 0);
        Assert.True(breakdown.Source(ContextTokenSource.WorkspaceRag).TokenCount > 0);
        Assert.True(breakdown.Source(ContextTokenSource.AttachmentRag).TokenCount > 0);
        TokenEstimate attachment = breakdown.Source(ContextTokenSource.ExplicitAttachments);
        Assert.True(attachment.TokenCount >= 2_048);
        Assert.Equal(TokenEstimateClassification.Unknown, attachment.Classification);
        Assert.Equal(TokenEstimateClassification.Unknown, breakdown.OverallClassification);
    }

    [Fact]
    public void EstimateContext_HeadingsInsideUntrustedFence_DoNotSpoofSourceCategory()
    {
        const string system =
            """
            ## DATA
            ### Attached Files for this Turn
            ````
            ### Saga (Associative Memory)
            attacker-controlled heading
            ````
            ## CONTEXT
            trusted context
            """;
        ModelTokenEstimator estimator = CreateEstimator();

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            Provider("gpt-4o"),
            "gpt-4o",
            [new ChatMessage(ChatRole.System, system)],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.Equal(0, breakdown.Source(ContextTokenSource.LexiconSaga).TokenCount);
        Assert.True(breakdown.Source(ContextTokenSource.ExplicitAttachments).TokenCount > 0);
        Assert.True(breakdown.Source(ContextTokenSource.SystemCodexSpell).TokenCount > 0);
    }

    [Fact]
    public void EstimateContext_IncludesProtectedReasoningAndProviderMetadataInCountAndFingerprint()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");
        TextReasoningContent reasoning = new("summary")
        {
            ProtectedData = new string('p', 200),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["item_id"] = "reasoning-item-1",
            },
        };
        List<ChatMessage> messages =
        [
            new(ChatRole.Assistant, [reasoning]),
            new(ChatRole.User, "continue"),
        ];

        ContextTokenBreakdown first = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            messages,
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));
        reasoning.ProtectedData = new string('q', 400);
        ContextTokenBreakdown changed = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            messages,
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        Assert.True(first.Source(ContextTokenSource.History).TokenCount > 0);
        Assert.NotEqual(first.PayloadFingerprint, changed.PayloadFingerprint);
        Assert.True(changed.Source(ContextTokenSource.History).TokenCount > first.Source(ContextTokenSource.History).TokenCount);
    }

    [Fact]
    public void EstimateContext_TracksAnswerAndReasoningReservationsSeparately()
    {
        ModelTokenEstimator estimator = CreateEstimator();

        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            Provider("gpt-4o"),
            "gpt-4o",
            [new ChatMessage(ChatRole.User, "reason")],
            new ChatOptions(),
            ReservedAnswerTokens: 1_024,
            ReservedReasoningTokens: 2_048));

        Assert.Equal(1_024, breakdown.Source(ContextTokenSource.ReservedAnswer).TokenCount);
        Assert.Equal(2_048, breakdown.Source(ContextTokenSource.ReservedReasoning).TokenCount);
        Assert.Equal(
            breakdown.InputTokens + 3_072,
            breakdown.TotalTokens);
    }

    [Fact]
    public async Task ModelCallExecutor_ReestimatesContinuationAndRejectsBeforeProviderCall()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ModelCallExecutor executor = new(estimator);
        CountingChatClient chat = new();
        ProviderSettings provider = Provider("gpt-4o", contextWindow: 256);
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "start"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "large_tool")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('x', 4_000))]),
        ];

        ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(
            chat,
            messages,
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.ToolContinuation,
            CancellationToken.None,
            new ModelCallContext(provider, "gpt-4o", ReservedAnswerTokens: 64, ReservedReasoningTokens: 0));

        Assert.True(outcome.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, outcome.Error.Code);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task ModelCallExecutor_ReusesValidatedPrecomputedBreakdown()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");
        List<ChatMessage> messages = [new(ChatRole.User, "hello")];
        ContextTokenBreakdown breakdown = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            messages,
            new ChatOptions(),
            ReservedAnswerTokens: 32,
            ReservedReasoningTokens: 0));
        CountingTokenEstimator counting = new(estimator);
        ModelCallExecutor executor = new(counting);
        CountingChatClient chat = new();

        ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(
            chat,
            messages,
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(provider, "gpt-4o", 32, 0, breakdown));

        Assert.True(outcome.IsSuccess);
        Assert.Same(breakdown, outcome.Value.ContextBreakdown);
        Assert.Equal(0, counting.EstimateCount);
        Assert.Equal(1, chat.CallCount);
    }

    [Fact]
    public async Task ModelCallExecutor_RejectsWhenPrecomputedPayloadChanged()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");
        List<ChatMessage> messages = [new(ChatRole.User, "hello")];
        ContextTokenBreakdown stale = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            messages,
            new ChatOptions(),
            ReservedAnswerTokens: 32,
            ReservedReasoningTokens: 0));
        messages[0] = new ChatMessage(ChatRole.User, new string('x', 1_000));
        CountingTokenEstimator counting = new(estimator);
        ModelCallExecutor executor = new(counting);
        CountingChatClient chat = new();

        ModelCallOutcome outcome = await executor.ExecuteBufferedAsync(
            chat,
            messages,
            new ChatOptions(),
            new TurnBudget(),
            ModelCallPurpose.MainInference,
            CancellationToken.None,
            new ModelCallContext(provider, "gpt-4o", 32, 0, stale));

        Assert.True(outcome.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, outcome.Error.Code);
        Assert.Equal(0, counting.EstimateCount);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task ModelCallExecutor_StreamingSurfacesStaleBreakdownFailure()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ProviderSettings provider = Provider("gpt-4o");
        List<ChatMessage> messages = [new(ChatRole.User, "before")];
        ContextTokenBreakdown stale = estimator.EstimateContext(new ModelTokenizationRequest(
            provider,
            "gpt-4o",
            messages,
            new ChatOptions(),
            ReservedAnswerTokens: 32,
            ReservedReasoningTokens: 0));
        messages[0] = new ChatMessage(ChatRole.User, "after");
        CountingChatClient chat = new();
        List<ModelCallUpdate> updates = [];

        await foreach (ModelCallUpdate update in new ModelCallExecutor(estimator)
            .ExecuteStreamingAsync(
                chat,
                messages,
                new ChatOptions(),
                new TurnBudget(),
                ModelCallPurpose.MainInference,
                CancellationToken.None,
                new ModelCallContext(provider, "gpt-4o", 32, 0, stale)))
        {
            updates.Add(update);
        }

        ModelCallFailureUpdate failure = Assert.IsType<ModelCallFailureUpdate>(
            Assert.Single(updates));
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, failure.Error.Code);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public void Reconcile_PreservesEstimateAndRecordsProviderReportedVariance()
    {
        ModelTokenEstimator estimator = CreateEstimator();
        ContextTokenBreakdown estimated = estimator.EstimateContext(new ModelTokenizationRequest(
            Provider("gpt-4o"),
            "gpt-4o",
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions(),
            ReservedAnswerTokens: 0,
            ReservedReasoningTokens: 0));

        ContextTokenBreakdown reconciled = estimated.ReconcileProviderReportedInput(estimated.InputTokens + 7);

        Assert.Equal(estimated.InputTokens, reconciled.InputTokens);
        Assert.Equal(estimated.InputTokens + 7, reconciled.ProviderReportedInputTokens);
        Assert.Equal(7, reconciled.EstimationVarianceTokens);
        Assert.True(reconciled.ProviderReportedInputValid);
        Assert.Null(estimated.ProviderReportedInputTokens);
        Assert.Same(estimated, estimated.ReconcileProviderReportedInput(null));

        ContextTokenBreakdown inconsistent = estimated.ReconcileProviderReportedInput(-7);
        Assert.Equal(-7, inconsistent.ProviderReportedInputTokens);
        Assert.False(inconsistent.ProviderReportedInputValid);

        ContextTokenBreakdown huge = estimated.ReconcileProviderReportedInput(long.MaxValue);
        Assert.Equal(long.MaxValue, huge.ProviderReportedInputTokens);
        Assert.True(huge.ProviderReportedInputValid);
    }

    private static ModelTokenEstimator CreateEstimator() =>
        new(
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    private static ProviderSettings Provider(string model, int contextWindow = 128_000) =>
        new()
        {
            Name = "openai-compatible",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            Models = [new ModelEntry(model)],
            ContextWindowLimit = contextWindow,
        };

    private sealed class CountingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CountingTokenEstimator(IModelTokenEstimator inner) : IModelTokenEstimator
    {
        public int EstimateCount { get; private set; }

        public ResolvedModelTokenizationProfile ResolveProfile(
            ProviderSettings provider,
            string canonicalModel) =>
            inner.ResolveProfile(provider, canonicalModel);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(
            ProviderSettings provider,
            string canonicalModel) =>
            inner.ResolveEffectiveProfile(provider, canonicalModel);

        public TokenEstimate EstimateText(
            ProviderSettings provider,
            string canonicalModel,
            string? text) =>
            inner.EstimateText(provider, canonicalModel, text);

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            EstimateCount++;
            return inner.EstimateContext(request);
        }
    }
}
