using System.Buffers;
using System.ClientModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Intelligence.Guardrails;
using RetroDownfall.Arcanum.Infrastructure.Familiars;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

[ExcludeFromCodeCoverage] // Reason: intelligence hub coordinating external LLM providers, MCP tools, and Grimoire persistence; covered via WizardIntelligenceProvider scenario matrix and ApiHost integration tests.
public sealed partial class WizardIntelligenceProvider(
    IChatClientFactory chatClientFactory,
    IOptionsSnapshot<ArcanumSettings> settings,
    ILogger<WizardIntelligenceProvider> logger,
    IHttpClientFactory httpClientFactory,
    IGrimoireRepository grimoire,
    IMcpConnectionManager mcpConnectionManager,
    ICampaignRepository campaignRepository,
    ToolExecutionPipeline toolExecutionPipeline,
    GrimoireTurnWriter grimoireTurnWriter,
    InferenceContextBuilder inferenceContextBuilder,
    ISanctumGuard sanctumGuard,
    IProcessResourceLimiter processResourceLimiter,
    IWeaveService weaveService,
    IDivinationService divinationService,
    IWorkspaceIndexingService workspaceIndexingService,
    ISagaMemoryStore sagaMemoryStore,
    SagaExtractionService sagaExtractionService,
    SemanticSpellRouter semanticSpellRouter,
    ILexiconService lexiconService,
    ArcanumDbContext db,
    IInferenceAuditLogger inferenceAuditLogger,
    StructuredOutputValidator structuredOutputValidator,
    InferenceTokenizerResolver tokenizerResolver,
    BudgetMonitor budgetMonitor,
    ISessionAttachmentStore sessionAttachmentStore,
    IHumanPromptRegistry humanPromptRegistry,
    IProviderHealthTracker? healthTracker = null,
    GuardrailsPipeline? guardrailsPipeline = null,
    IModelCallExecutor? modelCallExecutor = null,
    ITurnRunWriter? turnRunWriter = null,
    IBudgetReservationService? budgetReservationService = null,
    Lazy<ITurnExecutionFacade>? turnCoordinator = null,
    IModelTokenEstimator? modelTokenEstimator = null,
    IWebResearchProviderCatalog? webResearchProviderCatalog = null,
    SessionContextPinMaterializer? sessionContextPinMaterializer = null,
    ISubagentRunner? subagentRunner = null,
    ISessionAttachmentRetrievalService? sessionAttachmentRetrieval = null,
    IAttachmentMemoryProvenanceStore? attachmentMemoryProvenanceStore = null,
    ITapestryStore? tapestryStore = null,
    CovenantDispatchGate? covenantDispatch = null,
    CovenantToolCapabilityRegistry? covenantToolCapabilities = null) : IArcanumIntelligenceProvider, IContextPreviewService, ITurnPipelineRunner
{
    /// <summary>
    /// The token allowance charged for one emitted Covenant section's headings, notice, and fences.
    /// </summary>
    /// <remarks>
    /// Deliberately generous. The exact framing lives inside the prompt builder, and a budget that
    /// guessed low would admit a section the context gate then had to evict, turning an admission
    /// receipt into a description of a prompt that never went out.
    /// </remarks>
    private const ulong CovenantSectionFramingTokenAllowance = 64;

    private readonly TokenAccountingDependencies _tokenAccounting =
        TokenAccountingDependencies.Create(
            modelTokenEstimator,
            modelCallExecutor,
            tokenizerResolver,
            logger);

    private IModelTokenEstimator ModelTokenEstimator => _tokenAccounting.Estimator;

    private IModelCallExecutor ModelCallExecutor => _tokenAccounting.Executor;

    private readonly Lazy<ITurnExecutionFacade>? _turnCoordinator = turnCoordinator;

    private const string PublicInferenceFailureMessage =
        PublicInferenceErrorMessages.NativeGenericFailure;

    private const string PublicModelResolutionFailureMessage =
        PublicInferenceErrorMessages.ModelNotConfigured;

    private const string AskHumanUnavailableMessage =
        "ask_human is only available during attended streaming turns with a live human-response channel.";

    private static readonly ArcanumLocalTimeTool _localTimeTool = new();

    private static readonly ArcanumSystemInfoTool _systemInfoTool = new();

    public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);

        return await ResolveCoordinator()
            .ExecuteBufferedAsync(
                request,
                invocationContext,
                TurnIdempotencyAmbient.Current,
                cancellationToken,
                auditContext)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);

        await foreach (IntelligenceEvent frame in ResolveCoordinator()
            .ExecuteIntelligenceStreamAsync(
                request,
                invocationContext,
                TurnIdempotencyAmbient.Current,
                cancellationToken,
                auditContext)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private ITurnExecutionFacade ResolveCoordinator()
    {
        if (_turnCoordinator is not null)
        {
            return _turnCoordinator.Value;
        }

        return new TurnExecutionCoordinator(new TurnEngine.TurnEngine(this));
    }

    /// <summary>
    /// Acquires this logical run's one Covenant scope, or the inert one.
    /// </summary>
    /// <remarks>
    /// Called once per logical turn and shared by every provider candidate, every retry, and every
    /// tool round. A second acquisition inside the run would open a second bounded read, and the two
    /// could straddle a mutation another turn published in between — which is exactly the drift the
    /// single-plan rule exists to prevent.
    ///
    /// <para>A host that composed no Covenant arm gets the inert scope rather than a null, so the
    /// dispatch path below reads one shape whether or not the gate exists.</para>
    /// </remarks>
    private ValueTask<CovenantTurnScope> BeginCovenantTurnAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken) =>
        covenantDispatch is null
            ? ValueTask.FromResult(CovenantTurnScope.NotEligible())
            : covenantDispatch.BeginTurnAsync(
                invocationContext,
                Guid.NewGuid(),
                request.SessionId,
                cancellationToken);

    async Task ITurnPipelineRunner.RunBufferedIntoEmitterAsync(
        TurnExecutionRequest request,
        TurnEventEmitter emitter,
        InferenceAuditContext? auditContext,
        CancellationToken cancellationToken)
    {
        await emitter
            .EmitAsync(new RunStarted(emitter.NextCorrelation()), cancellationToken)
            .ConfigureAwait(false);

        StreamingIntelligenceMapper mapper = new();

        async ValueTask EmitBufferedFrameAsync(
            IntelligenceEvent frame,
            CancellationToken frameCancellationToken)
        {
            if (frame.Type is IntelligenceEventType.Error
                or IntelligenceEventType.Result)
            {
                return;
            }

            foreach (TurnEvent mapped in mapper.Map(frame, emitter))
            {
                await emitter
                    .EmitAsync(mapped, frameCancellationToken)
                    .ConfigureAwait(false);
            }
        }

        Result<PromptTurnResult> result = await ExecutePromptCoreAsync(
                request.Request,
                request.InvocationContext,
                cancellationToken,
                auditContext,
                EmitBufferedFrameAsync)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            PromptTurnResult turn = result.Value;

            await emitter
                .EmitAsync(
                    new RunCompleted(
                        emitter.NextCorrelation(),
                        turn.Text,
                        turn.Usage,
                        turn.ToolCalls,
                        turn.FinishReason,
                        turn.Warnings,
                        request.Request.SessionId,
                        StructuredOutputWarning: turn.Warnings.Count > 0)
                    {
                        Reasoning = turn.Reasoning,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        await emitter
            .EmitAsync(
                new RunFailed(
                    emitter.NextCorrelation(),
                    result.Error,
                    TurnTerminationReason.ProviderFailure,
                    Usage: null,
                    Warnings: [],
                    Interrupted: false,
                    PartialText: null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    async Task ITurnPipelineRunner.RunStreamingIntoEmitterAsync(
        TurnExecutionRequest request,
        TurnEventEmitter emitter,
        InferenceAuditContext? auditContext,
        CancellationToken cancellationToken)
    {
        await emitter
            .EmitAsync(new RunStarted(emitter.NextCorrelation()), cancellationToken)
            .ConfigureAwait(false);

        bool terminalEmitted = false;

        StreamingIntelligenceMapper mapper = new();

        await foreach (IntelligenceEvent frame in StreamPromptCoreAsync(
                request.Request,
                request.InvocationContext,
                cancellationToken,
                auditContext)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (TurnEvent mapped in mapper.Map(frame, emitter))
            {
                await emitter.EmitAsync(mapped, cancellationToken).ConfigureAwait(false);

                if (mapped.IsTerminal)
                {
                    terminalEmitted = true;
                }
            }
        }

        if (!terminalEmitted && !emitter.TerminalEmitted)
        {
            await emitter
                .EmitAsync(
                    new RunAbandoned(
                        emitter.NextCorrelation(),
                        new Error(ErrorCodes.Hub.Error, "Streaming turn ended without a terminal frame."),
                        TurnTerminationReason.Cancelled,
                        Usage: null,
                        Warnings: [],
                        Interrupted: true,
                        PartialText: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class StreamingIntelligenceMapper
    {

        private IntelligenceEvent? _pendingToolError;

        private readonly StringBuilder _answer = new();

        private readonly List<ReasoningContentRun> _reasoning = [];

        public IEnumerable<TurnEvent> Map(IntelligenceEvent frame, TurnEventEmitter emitter)
        {
            TurnEventCorrelation correlation = emitter.NextCorrelation();

            switch (frame.Type)
            {
                case IntelligenceEventType.Status:
                    yield return new TurnStatusChanged(correlation, frame.Message);

                    yield break;

                case IntelligenceEventType.SessionBound:
                    {
                        string? idText = frame.Data ?? frame.Message;

                        if (Guid.TryParse(idText, out Guid sessionId))
                        {
                            yield return new SessionBound(correlation, sessionId);
                        }

                        yield break;
                    }

                case IntelligenceEventType.ConversationBound:
                    yield break;

                case IntelligenceEventType.Context when frame.ContextBreakdown is { } breakdown:
                    yield return new ContextAccounted(correlation, breakdown);
                    yield break;

                case IntelligenceEventType.Context:
                    yield break;

                case IntelligenceEventType.Token:
                    string text = frame.Data ?? frame.Message;
                    _ = _answer.Append(text);
                    yield return new TextDelta(correlation, text);

                    yield break;

                case IntelligenceEventType.Reasoning when frame.Reasoning is { Text.Length: > 0 } reasoning:
                    AppendReasoning(reasoning);

                    yield return new ReasoningDelta(correlation, reasoning);

                    yield break;

                case IntelligenceEventType.Reasoning:
                    yield break;

                case IntelligenceEventType.ToolCall:
                    yield return new ToolCallProposed(
                        correlation,
                        frame.ToolCall?.CallId ?? Guid.NewGuid().ToString("N"),
                        frame.ToolCall?.Name ?? frame.Message,
                        frame.ToolCall?.ArgumentsJson ?? frame.Data ?? string.Empty,
                        ToolCallDisposition.ServerExecution,
                        frame.ToolCall?.PreserveProviderCallId ?? false);

                    yield break;

                case IntelligenceEventType.Warded:
                    yield return new ApprovalRequested(
                        correlation,
                        frame.WardId ?? string.Empty,
                        frame.WardToolName ?? frame.Message,
                        frame.WardArguments?.GetRawText() ?? string.Empty,
                        frame.WardOrigin);

                    yield break;

                case IntelligenceEventType.WardResolved:
                    yield return new ApprovalResolved(
                        correlation,
                        frame.WardId ?? string.Empty,
                        frame.WardToolName ?? frame.Message,
                        frame.WardAllowed == true,
                        frame.WardReason,
                        frame.WardOrigin);

                    yield break;

                case IntelligenceEventType.ToolError:
                    _pendingToolError = frame;

                    yield break;

                case IntelligenceEventType.ToolResult:
                    bool failed = _pendingToolError is not null;

                    // The failure reason rides on Data; Message carries the tool name, so reading it
                    // first would report "read_file" as the error text. Message stays as the
                    // fallback for producers that only populate it.
                    string? publicError = _pendingToolError?.Data ?? _pendingToolError?.Message;

                    _pendingToolError = null;

                    yield return new ToolInvocationCompleted(
                        correlation,
                        frame.ToolCall?.CallId ?? string.Empty,
                        frame.ToolCall?.Name ?? frame.Message,
                        frame.ToolCall?.ArgumentsJson ?? string.Empty,
                        frame.Data ?? frame.Message,
                        Failed: failed,
                        Denied: frame.ToolDenied,
                        ToleratedFailure: failed,
                        PublicErrorText: publicError,
                        Duration: TimeSpan.Zero,
                        AttachmentPostProcessed: false);

                    yield break;

                case IntelligenceEventType.AttachmentRefreshed when frame.AttachmentRefresh is { } refresh:
                    yield return new AttachmentRefreshed(correlation, refresh);
                    yield break;

                case IntelligenceEventType.AttachmentRefreshed:
                    yield break;

                case IntelligenceEventType.Result:
                    yield return new RunCompleted(
                        correlation,
                        _answer.ToString(),
                        frame.Usage,
                        ToolCalls: null,
                        frame.FinishReason,
                        frame.Warnings,
                        SessionId: null,
                        StructuredOutputWarning: frame.Warnings.Count > 0)
                    {
                        Reasoning = _reasoning
                            .Select(static run => run.Materialize())
                            .ToArray(),
                    };

                    yield break;

                case IntelligenceEventType.Error:
                    yield return new RunFailed(
                        correlation,
                        new Error(frame.Data ?? ErrorCodes.Hub.Error, frame.Message),
                        TurnTerminationReason.ProviderFailure,
                        frame.Usage,
                        frame.Warnings,
                        Interrupted: false,
                        PartialText: null);

                    yield break;

                default:
                    yield break;
            }
        }

        private void AppendReasoning(ReasoningContentSegment reasoning)
        {
            if (_reasoning.Count > 0 && _reasoning[^1].Output == reasoning.Output)
            {
                _reasoning[^1].Append(reasoning.Text);
                return;
            }

            _reasoning.Add(new ReasoningContentRun(reasoning));
        }

    }

    private sealed class ReasoningContentRun
    {
        private readonly StringBuilder _text;

        public ReasoningContentRun(ReasoningContentSegment initial)
        {
            Output = initial.Output;
            _text = new StringBuilder(initial.Text);
        }

        public ReasoningOutputMode Output { get; }

        public void Append(string text) => _ = _text.Append(text);

        public ReasoningContentSegment Materialize() => new(_text.ToString(), Output);
    }

    private sealed class BufferedOutputFrameAccumulator
    {
        private readonly List<BufferedOutputRun> _runs = [];

        public bool HasToken { get; private set; }

        public void Clear()
        {
            _runs.Clear();
            HasToken = false;
        }

        public void AppendToken(string modelCallId, string text)
        {
            HasToken = true;
            Append(modelCallId, IntelligenceEventType.Token, output: null, text);
        }

        public void AppendReasoning(
            string modelCallId,
            ReasoningContentSegment reasoning) =>
            Append(
                modelCallId,
                IntelligenceEventType.Reasoning,
                reasoning.Output,
                reasoning.Text);

        public IEnumerable<IntelligenceEvent> Materialize()
        {
            foreach (BufferedOutputRun run in _runs)
            {
                string text = run.Text.ToString();
                yield return run.Type == IntelligenceEventType.Reasoning
                    ? new IntelligenceEvent(
                        IntelligenceEventType.Reasoning,
                        text,
                        Reasoning: new ReasoningContentSegment(text, run.Output!.Value))
                    : new IntelligenceEvent(
                        IntelligenceEventType.Token,
                        string.Empty,
                        text);
            }
        }

        private void Append(
            string modelCallId,
            IntelligenceEventType type,
            ReasoningOutputMode? output,
            string text)
        {
            if (_runs.Count > 0
                && _runs[^1].ModelCallId == modelCallId
                && _runs[^1].Type == type
                && _runs[^1].Output == output)
            {
                _ = _runs[^1].Text.Append(text);
                return;
            }

            _runs.Add(new BufferedOutputRun(
                modelCallId,
                type,
                output,
                new StringBuilder(text)));
        }

        private sealed record BufferedOutputRun(
            string ModelCallId,
            IntelligenceEventType Type,
            ReasoningOutputMode? Output,
            StringBuilder Text);
    }

    private async Task<Result<PromptTurnResult>> ExecutePromptCoreAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null,
        Func<IntelligenceEvent, CancellationToken, ValueTask>?
            eventSink = null)
    {
        string prompt = request.Prompt;

        Result guardrailsInput = await FilterGuardrailsInputAsync(request, cancellationToken).ConfigureAwait(false);

        if (guardrailsInput.IsFailure)
        {
            return Result<PromptTurnResult>.Failure(guardrailsInput.Error);
        }

        Result preflight = PingRequestPreflightValidator.Validate(
            request,
            settings.Value);

        if (preflight.IsFailure)
        {

            return Result<PromptTurnResult>.Failure(preflight.Error);

        }

        if (!InferenceContextBuilder.HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Validation.InvalidPrompt, "Prompt is required."));
        }

        Result budgetGate = await budgetMonitor.CheckAsync(cancellationToken).ConfigureAwait(false);

        if (budgetGate.IsFailure)
        {
            return Result<PromptTurnResult>.Failure(budgetGate.Error);
        }

        CancellationToken callerToken = cancellationToken;

        CancellationToken inferenceToken = callerToken;

        bool resilienceEnabled = healthTracker is not null;

        CovenantTurnScope covenantScope = await BeginCovenantTurnAsync(request, invocationContext, cancellationToken)
            .ConfigureAwait(false);

        try
        {

            if (!resilienceEnabled)
            {
                ChatClientLease singleLease;

                try
                {
                    singleLease = await chatClientFactory.ResolveClientAsync(request.Model, inferenceToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(
                        "Hub model resolution failed; exception type {ExceptionType}.",
                        ex.GetType().FullName);

                    return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));
                }

                using (singleLease)
                {
                    Result reasoningValidation = ValidateReasoningForCandidate(
                        request,
                        singleLease.Provider,
                        singleLease.ResolvedModel,
                        settings.Value.Features.Reasoning);

                    if (reasoningValidation.IsFailure)
                    {
                        return Result<PromptTurnResult>.Failure(reasoningValidation.Error);
                    }

                    InferenceAttemptResult single = await DrainBufferedInferenceAttemptAsync(
                            singleLease,
                            request,
                            invocationContext,
                            inferenceToken,
                            callerToken,
                            auditContext,
                            eventSink,
                            covenantScope: covenantScope)
                        .ConfigureAwait(false);

                    return single.Result;
                }
            }

            return await ExecutePromptWithFallbackAsync(
                    request,
                    invocationContext,
                    inferenceToken,
                    callerToken,
                    auditContext,
                    eventSink,
                    covenantScope)
                .ConfigureAwait(false);

        }
        finally
        {

            await covenantScope.DisposeAsync().ConfigureAwait(false);

        }
    }

    private async Task<Result<PromptTurnResult>> ExecutePromptWithFallbackAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken inferenceToken,
        CancellationToken callerToken,
        InferenceAuditContext? auditContext,
        Func<IntelligenceEvent, CancellationToken, ValueTask>?
            eventSink,
        CovenantTurnScope? covenantScope = null)
    {

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings.Value, request.Model, healthTracker);

        if (candidates.Count == 0)
        {
            logger.LogWarning("Hub model resolution failed for requested model {RequestedModel}.", request.Model);

            return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));
        }

        Result<PromptTurnResult> lastFailure = Result<PromptTurnResult>.Failure(
            new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));

        // Built once for the whole logical run and shared by every candidate (DESIGN §10.7.2).
        TurnContextSeed seed = new();

        try
        {
            for (int attemptIndex = 0; attemptIndex < candidates.Count; attemptIndex++)
            {
                (ProviderSettings provider, string resolvedModel) = candidates[attemptIndex];

                bool isLastAttempt = attemptIndex == candidates.Count - 1;

                Result reasoningValidation = ValidateReasoningForCandidate(
                    request,
                    provider,
                    resolvedModel,
                    settings.Value.Features.Reasoning);

                if (reasoningValidation.IsFailure)
                {
                    return Result<PromptTurnResult>.Failure(reasoningValidation.Error);
                }

                ChatClientLease lease;

                try
                {
                    lease = await chatClientFactory.ResolveClientAsync(provider, resolvedModel, inferenceToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Only mark the provider unhealthy for a genuine connectivity failure — matching
                    // the inference-failure handling below (attempt.IsConnectivityFailure). A lease
                    // construction error can also be a local misconfiguration or transient overload,
                    // neither of which means the provider itself is down; marking it failed regardless
                    // would incorrectly drain its health status and could take it out of rotation for
                    // unrelated reasons.
                    bool isConnectivityFailure = IsConnectivityFailure(ex, callerToken);

                    if (isConnectivityFailure)
                    {
                        healthTracker!.MarkFailed(provider.Name);
                    }

                    logger.LogWarning(
                        "Provider {ProviderName} unavailable while resolving client (fallback candidate {Attempt}/{CandidateCount}); exception type {ExceptionType}.",
                        provider.Name,
                        attemptIndex + 1,
                        candidates.Count,
                        ex.GetType().FullName);

                    lastFailure = Result<PromptTurnResult>.Failure(new Error(
                        ErrorCodes.Hub.Error,
                        BuildInferenceFailureMessage(provider, ex)));

                    if (!isConnectivityFailure || isLastAttempt)
                    {
                        return lastFailure;
                    }

                    continue;
                }

                using (lease)
                {
                    InferenceAttemptResult attempt = await DrainBufferedInferenceAttemptAsync(
                            lease,
                            request,
                            invocationContext,
                            inferenceToken,
                            callerToken,
                            auditContext,
                            eventSink,
                            seed,
                            canFallBack: !isLastAttempt,
                            covenantScope)
                        .ConfigureAwait(false);

                    if (attempt.IsConnectivityFailure)
                    {
                        healthTracker!.MarkFailed(provider.Name);
                    }

                    if (attempt.Result.IsSuccess)
                    {
                        if (!attempt.IsConnectivityFailure)
                        {
                            healthTracker!.MarkHealthy(provider.Name);
                        }

                        return attempt.Result;
                    }

                    lastFailure = attempt.Result;

                    bool eligibleForFallback = attempt.IsConnectivityFailure
                        && !attempt.ProviderCommitted
                        && !isLastAttempt;
                    if (!eligibleForFallback)
                    {
                        return lastFailure;
                    }

                    logger.LogWarning(
                        "Provider {ProviderName} inference failed with a connectivity error (fallback candidate {Attempt}/{CandidateCount}); trying next candidate.",
                        provider.Name,
                        attemptIndex + 1,
                        candidates.Count);

                }

            }
        }
        finally
        {
            await ResolveDeferredTurnAsync(seed).ConfigureAwait(false);
        }

        return lastFailure;

    }

    /// <summary>
    /// Applies model-capability validation to the actual resolved lease candidate before provider
    /// I/O. Both direct and resilience-enabled paths use this gate so unsupported explicit controls
    /// are rejected consistently with provider/model-specific diagnostics.
    /// </summary>
    private static Result ValidateReasoningForCandidate(
        PingRequest request,
        ProviderSettings provider,
        string resolvedModel,
        bool featuresReasoningEnabled)
    {
        ModelEntry? modelEntry =
            ProviderResolver.TryResolveModelEntry(provider, resolvedModel, out ModelEntry? entry)
                ? entry
                : null;

        return ReasoningRequestValidator.ValidateForModel(
            request.Reasoning,
            modelEntry,
            featuresReasoningEnabled,
            resolvedModel,
            provider.Name);
    }

    private async Task<InferenceAttemptResult> DrainBufferedInferenceAttemptAsync(
        ChatClientLease lease,
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken inferenceToken,
        CancellationToken callerToken,
        InferenceAuditContext? auditContext,
        Func<IntelligenceEvent, CancellationToken, ValueTask>?
            eventSink,
        TurnContextSeed? seed = null,
        bool canFallBack = false,
        CovenantTurnScope? covenantScope = null)
    {

        StreamFailureClassification classification = new();

        await foreach (IntelligenceEvent frame in RunInferenceAttemptAsync(
                lease,
                request,
                invocationContext,
                request.Prompt,
                TurnResponseMode.Buffered,
                classification,
                inferenceToken,
                callerToken,
                auditContext,
                seed,
                canFallBack,
                covenantScope)
            .ConfigureAwait(false))
        {
            if (eventSink is not null)
            {
                await eventSink(frame, callerToken).ConfigureAwait(false);
            }
        }

        if (classification.BufferedTerminal is { } terminal)
        {
            return new InferenceAttemptResult(
                terminal,
                classification.IsConnectivityFailure,
                classification.ProviderCommitted);
        }

        return new InferenceAttemptResult(
            Result<PromptTurnResult>.Failure(
                new Error(ErrorCodes.Hub.Error, PublicInferenceFailureMessage)),
            classification.IsConnectivityFailure,
            classification.ProviderCommitted);

    }

    private async IAsyncEnumerable<IntelligenceEvent> StreamPromptCoreAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null)
    {
        string prompt = request.Prompt;

        Result guardrailsInput = await FilterGuardrailsInputAsync(request, cancellationToken).ConfigureAwait(false);

        if (guardrailsInput.IsFailure)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, guardrailsInput.Error.Message);

            yield break;
        }

        Result streamPreflight = PingRequestPreflightValidator.Validate(
            request,
            settings.Value);

        if (streamPreflight.IsFailure)
        {

            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                streamPreflight.Error.Message);

            yield break;

        }

        if (!InferenceContextBuilder.HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, "Prompt is required.");

            yield break;
        }

        Result streamBudgetGate = await budgetMonitor.CheckAsync(cancellationToken).ConfigureAwait(false);

        if (streamBudgetGate.IsFailure)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, streamBudgetGate.Error.Message);

            yield break;
        }

        CancellationToken callerToken = cancellationToken;

        CancellationToken inferenceToken = callerToken;

        bool resilienceEnabled = healthTracker is not null;

        // One scope for the whole streamed run, disposed with the enumerator so that an abandoned
        // stream releases the turn lease as reliably as a completed one.
        await using CovenantTurnScope streamCovenantScope =
            await BeginCovenantTurnAsync(request, invocationContext, cancellationToken).ConfigureAwait(false);

        if (!resilienceEnabled)
        {
            InvalidOperationException? resolveFailure = null;

            ChatClientLease? leaseOrNull = null;

            try
            {
                leaseOrNull = await chatClientFactory.ResolveClientAsync(request.Model, inferenceToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                resolveFailure = ex;
            }

            if (resolveFailure is not null)
            {
                logger.LogWarning(
                    "Hub model resolution failed; exception type {ExceptionType}.",
                    resolveFailure.GetType().FullName);

                yield return new IntelligenceEvent(IntelligenceEventType.Error, PublicModelResolutionFailureMessage);

                yield break;
            }

            ChatClientLease singleLease = leaseOrNull!;

            Result reasoningValidation = ValidateReasoningForCandidate(
                request,
                singleLease.Provider,
                singleLease.ResolvedModel,
                settings.Value.Features.Reasoning);

            if (reasoningValidation.IsFailure)
            {
                singleLease.Dispose();

                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    reasoningValidation.Error.Message,
                    reasoningValidation.Error.Code);

                yield break;
            }

            StreamFailureClassification singleClassification = new();

            IAsyncEnumerator<IntelligenceEvent> singleEnumerator = RunInferenceAttemptAsync(
                singleLease,
                request,
                invocationContext,
                prompt,
                TurnResponseMode.Streaming,
                singleClassification,
                inferenceToken,
                callerToken,
                auditContext,
                covenantScope: streamCovenantScope).GetAsyncEnumerator(inferenceToken);

            Exception? singleMoveFailure = null;

            try
            {
                while (true)
                {
                    bool moved;

                    try
                    {
                        moved = await singleEnumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        singleMoveFailure = ex;

                        break;
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    yield return singleEnumerator.Current;
                }
            }
            finally
            {
                await singleEnumerator.DisposeAsync().ConfigureAwait(false);

                singleLease.Dispose();
            }

            if (singleMoveFailure is not null)
            {
                logger.LogError(
                    "Streaming inference threw after start; exception type {ExceptionType}.",
                    singleMoveFailure.GetType().FullName);

                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    PublicInferenceFailureMessage);
            }

            yield break;
        }

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> streamCandidates =
            ProviderResolver.ResolveCandidates(settings.Value, request.Model, healthTracker);

        if (streamCandidates.Count == 0)
        {
            logger.LogWarning("Hub model resolution failed for requested model {RequestedModel}.", request.Model);

            yield return new IntelligenceEvent(IntelligenceEventType.Error, PublicModelResolutionFailureMessage);

            yield break;
        }

        // Built once for the whole logical run and shared by every candidate (DESIGN §10.7.2).
        TurnContextSeed streamSeed = new();

        try
        {
            for (int attemptIndex = 0; attemptIndex < streamCandidates.Count; attemptIndex++)
            {
                (ProviderSettings candidateProvider, string candidateModel) = streamCandidates[attemptIndex];

                bool isLastAttempt = attemptIndex == streamCandidates.Count - 1;

                Result reasoningValidation = ValidateReasoningForCandidate(
                    request,
                    candidateProvider,
                    candidateModel,
                    settings.Value.Features.Reasoning);

                if (reasoningValidation.IsFailure)
                {
                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        reasoningValidation.Error.Message,
                        reasoningValidation.Error.Code);

                    yield break;
                }

                ChatClientLease? candidateLease = null;

                Exception? leaseBuildFailure = null;

                try
                {
                    candidateLease = await chatClientFactory.ResolveClientAsync(candidateProvider, candidateModel, inferenceToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    leaseBuildFailure = ex;
                }

                if (leaseBuildFailure is not null)
                {
                    // Only mark the provider unhealthy for a genuine connectivity failure — a local
                    // misconfiguration or transient overload does not mean the provider itself is down.
                    bool buildFailureIsConnectivity = IsConnectivityFailure(leaseBuildFailure, callerToken);

                    if (buildFailureIsConnectivity)
                    {
                        healthTracker!.MarkFailed(candidateProvider.Name);
                    }

                    bool retryableBuildFailure = buildFailureIsConnectivity && !isLastAttempt;

                    logger.LogWarning(
                        "Provider {ProviderName} unavailable while resolving streaming client (fallback candidate {Attempt}/{CandidateCount}); exception type {ExceptionType}.",
                        candidateProvider.Name,
                        attemptIndex + 1,
                        streamCandidates.Count,
                        leaseBuildFailure.GetType().FullName);

                    if (retryableBuildFailure)
                    {
                        continue;
                    }

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        BuildInferenceFailureMessage(candidateProvider, leaseBuildFailure));

                    yield break;
                }

                ChatClientLease lease = candidateLease!;

                StreamFailureClassification classification = new();

                IAsyncEnumerator<IntelligenceEvent> enumerator = RunInferenceAttemptAsync(
                    lease,
                    request,
                    invocationContext,
                    prompt,
                    TurnResponseMode.Streaming,
                    classification,
                    inferenceToken,
                    callerToken,
                    auditContext,
                    streamSeed,
                    canFallBack: !isLastAttempt,
                    streamCovenantScope).GetAsyncEnumerator();

                Exception? moveNextFailure = null;

                bool hasFirst = false;

                try
                {
                    hasFirst = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);

                    lease.Dispose();

                    throw;
                }
                catch (Exception ex)
                {
                    moveNextFailure = ex;
                }

                if (moveNextFailure is not null)
                {
                    // Only mark the provider unhealthy for a genuine connectivity failure — matches
                    // the lease-build-failure handling above and the buffered inference path.
                    bool moveFailureIsConnectivity = IsConnectivityFailure(moveNextFailure, callerToken);

                    if (moveFailureIsConnectivity)
                    {
                        healthTracker!.MarkFailed(candidateProvider.Name);
                    }

                    bool retryableMoveFailure = moveFailureIsConnectivity && !isLastAttempt;

                    logger.LogWarning(
                        "Provider {ProviderName} failed to start streaming (fallback candidate {Attempt}/{CandidateCount}); exception type {ExceptionType}.",
                        candidateProvider.Name,
                        attemptIndex + 1,
                        streamCandidates.Count,
                        moveNextFailure.GetType().FullName);

                    await enumerator.DisposeAsync().ConfigureAwait(false);

                    lease.Dispose();

                    if (retryableMoveFailure)
                    {
                        continue;
                    }

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        BuildInferenceFailureMessage(candidateProvider, moveNextFailure));

                    yield break;
                }

                if (!hasFirst)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);

                    lease.Dispose();

                    yield break;
                }

                IntelligenceEvent firstEvent = enumerator.Current;

                // Pre-commit events (Status / SessionBound / ConversationBound / Context) must not close
                // the fallback window (ADR 0004: commit from ModelCallUpdate / tool proposal / empty
                // success — tracked on classification.ProviderCommitted).
                List<IntelligenceEvent> preCommitBuffer = [];

                IntelligenceEvent? commitGateEvent = firstEvent;

                while (commitGateEvent is not null
                       && !classification.ProviderCommitted
                       && IsPreCommitStreamingEvent(commitGateEvent))
                {
                    preCommitBuffer.Add(commitGateEvent);

                    bool movedPre;

                    try
                    {
                        movedPre = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);

                        lease.Dispose();

                        throw;
                    }
                    catch (Exception ex)
                    {
                        moveNextFailure = ex;

                        movedPre = false;
                    }

                    if (moveNextFailure is not null)
                    {
                        break;
                    }

                    commitGateEvent = movedPre ? enumerator.Current : null;
                }

                if (moveNextFailure is not null)
                {
                    bool moveFailureIsConnectivity = IsConnectivityFailure(moveNextFailure, callerToken);

                    if (moveFailureIsConnectivity)
                    {
                        healthTracker!.MarkFailed(candidateProvider.Name);
                    }

                    bool retryableMoveFailure = moveFailureIsConnectivity && !isLastAttempt && !classification.ProviderCommitted;

                    logger.LogWarning(
                        "Provider {ProviderName} failed during pre-commit streaming (fallback candidate {Attempt}/{CandidateCount}); exception type {ExceptionType}.",
                        candidateProvider.Name,
                        attemptIndex + 1,
                        streamCandidates.Count,
                        moveNextFailure.GetType().FullName);

                    await enumerator.DisposeAsync().ConfigureAwait(false);

                    lease.Dispose();

                    if (retryableMoveFailure)
                    {
                        continue;
                    }

                    foreach (IntelligenceEvent buffered in preCommitBuffer)
                    {
                        yield return buffered;
                    }

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        BuildInferenceFailureMessage(candidateProvider, moveNextFailure));

                    yield break;
                }

                IntelligenceEvent? gateEvent = commitGateEvent;

                bool gateIsConnectivityError = gateEvent is { Type: IntelligenceEventType.Error }
                    && classification.IsConnectivityFailure;
                bool providerMarkedFailed = false;
                bool gateIsRetryableConnectivityError = gateIsConnectivityError
                    && !classification.ProviderCommitted
                    && !isLastAttempt;

                if (gateIsConnectivityError)
                {
                    healthTracker!.MarkFailed(candidateProvider.Name);
                    providerMarkedFailed = true;
                }

                if (gateIsRetryableConnectivityError)
                {
                    logger.LogWarning(
                        "Provider {ProviderName} streaming connection failed (fallback candidate {Attempt}/{CandidateCount}); trying next candidate.",
                        candidateProvider.Name,
                        attemptIndex + 1,
                        streamCandidates.Count);

                    await enumerator.DisposeAsync().ConfigureAwait(false);

                    lease.Dispose();

                    continue;
                }

                Exception? midStreamFailure = null;

                try
                {
                    foreach (IntelligenceEvent buffered in preCommitBuffer)
                    {
                        yield return buffered;
                    }

                    if (gateEvent is not null)
                    {
                        yield return gateEvent;
                    }

                    while (true)
                    {
                        bool moved;

                        try
                        {
                            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            midStreamFailure = ex;
                            break;
                        }

                        if (!moved)
                        {
                            break;
                        }

                        yield return enumerator.Current;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);

                    lease.Dispose();
                }

                if (midStreamFailure is not null)
                {
                    bool midStreamConnectivityFailure = IsConnectivityFailure(midStreamFailure, callerToken);
                    if (midStreamConnectivityFailure && !providerMarkedFailed)
                    {
                        healthTracker!.MarkFailed(candidateProvider.Name);
                        providerMarkedFailed = true;
                    }

                    logger.LogError(
                        "Streaming inference threw mid-stream for provider {ProviderName}; exception type {ExceptionType}.",
                        candidateProvider.Name,
                        midStreamFailure.GetType().FullName);

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        PublicInferenceFailureMessage);
                }

                if (classification.IsConnectivityFailure && !providerMarkedFailed)
                {
                    healthTracker!.MarkFailed(candidateProvider.Name);
                    providerMarkedFailed = true;
                }

                if (!providerMarkedFailed)
                {
                    healthTracker!.MarkHealthy(candidateProvider.Name);
                }

                yield break;
            }
        }
        finally
        {
            await ResolveDeferredTurnAsync(streamSeed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Backstop owner for the run's shared Grimoire handle, run from the <c>finally</c> around both
    /// fallback loops. <see cref="ShouldDeferTurnToNextCandidate"/> hands an uncommitted turn to the
    /// next candidate instead of discarding it, which assumes a later candidate reaches inference —
    /// but the per-candidate reasoning gate and a non-connectivity lease-build failure both leave the
    /// loop before that happens, and a deferred handle then keeps a Session and an empty assistant
    /// <c>Entry</c> in flight with nobody left to resolve them. A handle a candidate already resolved
    /// is untouched: the writer no-ops on <c>IsFinalized</c> and on a null assistant entry, and it
    /// swallows its own failures so this can never mask the run's terminal error.
    /// </summary>
    private async Task ResolveDeferredTurnAsync(TurnContextSeed seed)
    {
        if (seed.Turn is not { } deferred)
        {
            return;
        }

        await grimoireTurnWriter
            .TryResolveInterruptedOnStreamExitAsync(deferred, null)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// True when this candidate must hand the Grimoire turn to the next provider untouched: it died
    /// on a connectivity error before committing any output, and another candidate is still to be
    /// tried. Discarding the empty assistant row here would force the next candidate to seed the
    /// turn again — a second user <c>Entry</c>, and a second Session for a session-less request
    /// (DESIGN §10.7.2). <see cref="ResolveDeferredTurnAsync"/> owns whatever the last candidate
    /// leaves behind, so a run that exits the loop before any later candidate runs still resolves it.
    /// </summary>
    private static bool ShouldDeferTurnToNextCandidate(
        TurnContextSeed? seed,
        bool canFallBack,
        StreamFailureClassification classification,
        int streamedLength) =>
        seed is not null
        && canFallBack
        && classification.IsConnectivityFailure
        && !classification.ProviderCommitted
        && streamedLength == 0;

    /// <summary>
    /// Frames a candidate can emit before it has committed anything. <c>Context</c> belongs here:
    /// <c>ModelCallExecutor</c> yields the token-accounting frame before the provider socket is
    /// dialled at all, so every streaming turn emits one ahead of a connectivity error. Leaving it
    /// out closed the fallback window on that frame, the gate never reached the terminal
    /// <c>error</c>, and streaming fallback never advanced to the next candidate.
    /// </summary>
    private static bool IsPreCommitStreamingEvent(IntelligenceEvent evt) =>
        evt.Type is IntelligenceEventType.Status
            or IntelligenceEventType.SessionBound
            or IntelligenceEventType.ConversationBound
            or IntelligenceEventType.Context;

    // CS8425: intentionally no [EnumeratorCancellation] parameter — every caller (StreamPromptAsync)
    // passes inferenceToken/callerToken as ordinary arguments and drives the enumerator manually via
    // GetAsyncEnumerator()/MoveNextAsync() rather than `await foreach ... .WithCancellation(...)`.
    // Attributing a parameter here would let an incidental token passed to GetAsyncEnumerator silently
    // override the explicit per-candidate inferenceToken, which is not what the fallback loop wants.
#pragma warning disable CS8425
    private async IAsyncEnumerable<IntelligenceEvent> RunInferenceAttemptAsync(
        ChatClientLease lease,
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        string prompt,
        TurnResponseMode mode,
        StreamFailureClassification classification,
        CancellationToken inferenceToken,
        CancellationToken callerToken,
        InferenceAuditContext? auditContext,
        TurnContextSeed? seed = null,
        bool canFallBack = false,
        CovenantTurnScope? covenantScope = null)
#pragma warning restore CS8425
    {

        bool streaming = mode == TurnResponseMode.Streaming;

        GrimoireTurnWriter.TurnHandle grimoireTurn = new();

        StringBuilder streamAccumulator = new(1024);

        ModelCallReasoningAccumulator ephemeralReasoningSegments = new();

        Stopwatch inferenceStopwatch = Stopwatch.StartNew();

        Channel<IntelligenceEvent>? liveHumanPromptChannel = null;
        IHumanPromptLiveEmitter? previousHumanPromptEmitter = HumanPromptLiveEmitterAmbient.Current;

        TurnAccountingHandle? streamAccounting = null;

        InferenceRunStatus streamAccountingStatus = InferenceRunStatus.Abandoned;

        bool publishedStreamAmbient = false;

        TurnAccountingHandle? previousAmbient = TurnAccountingAmbient.Current;

        ITurnRunWriter? previousAmbientWriter = TurnAccountingAmbient.Writer;

        List<PromptToolCall>? observedToolCalls = null;

        Dictionary<string, ContextTokenBreakdown> contextBreakdownsByCall = new(StringComparer.Ordinal);

        ChatResponse? bufferedFinalResponse = null;

        // Published once a dispatch has earned an admission receipt, because a staging capability is
        // minted from that receipt: a tool call with no admission behind it has nothing to prove it
        // belongs to this turn's plan.
        IDisposable? streamCovenantStaging = null;

        try
        {
            string targetModel = lease.ResolvedModel;

            bool reasoningEnabled = ResolveReasoningEnabled(lease);
            ReasoningOutputMode clientReasoningOutput = ResolveClientReasoningOutput(
                request.Reasoning?.Output,
                reasoningEnabled,
                settings.Value.Features.ReasoningSummaries,
                streaming);

            IChatClient chatClient = lease.ChatClient;

            if (streaming)
            {
                yield return new IntelligenceEvent(IntelligenceEventType.Status, "Mage is generating response...");
            }

        Session? thread = await inferenceContextBuilder
            .LoadThreadAsync(request, inferenceToken)
            .ConfigureAwait(false);

        bool attachmentsEnabled = settings.Value.ResolveAttachments().Enabled;

        string? streamPendingTurnId = null;

        if (attachmentsEnabled
            && request.SessionId is null
            && HasSessionAttachmentPayload(request))
        {
            streamPendingTurnId = Guid.NewGuid().ToString("N");
        }

        bool streamTurnBegunEarly = false;

        // A retried candidate inherits the turn the run already opened (DESIGN §10.7.2). Beginning
        // it again would insert a second user Entry — and, for a session-less request, a second
        // orphaned Session — because interrupted-turn cleanup only discards the empty assistant row.
        if (seed?.Turn is { } seededTurn)
        {
            grimoireTurn = seededTurn;

            streamTurnBegunEarly = true;
        }
        else if (!streaming)
        {
            Result<GrimoireTurnWriter.TurnHandle> begun = await grimoireTurnWriter
                .BeginBufferedAssistantReplyAsync(
                    request,
                    invocationContext,
                    prompt,
                    targetModel,
                    inferenceToken)
                .ConfigureAwait(false);

            if (begun.IsFailure)
            {
                // Fail before prompt construction, tool advertisement, or provider dispatch. A turn
                // whose Session or Campaign binding could not be honoured has no answer to give, and
                // continuing would produce one nothing durable is attached to (§10.12).
                yield return new IntelligenceEvent(IntelligenceEventType.Error, begun.Error.Message);

                yield break;
            }

            grimoireTurn = begun.Value;

            streamTurnBegunEarly = true;

            if (seed is not null)
            {
                seed.Turn = grimoireTurn;
            }
        }
        else if (attachmentsEnabled && !InferenceContextBuilder.HasStatelessMessages(request))
        {
            Result<GrimoireTurnWriter.TurnHandle> begun = await grimoireTurnWriter
                .BeginStreamedAssistantReplyAsync(
                    request,
                    invocationContext,
                    prompt,
                    targetModel,
                    inferenceToken)
                .ConfigureAwait(false);

            if (begun.IsFailure)
            {
                yield return new IntelligenceEvent(IntelligenceEventType.Error, begun.Error.Message);

                yield break;
            }

            grimoireTurn = begun.Value;

            streamTurnBegunEarly = true;

            if (seed is not null)
            {
                seed.Turn = grimoireTurn;
            }
        }

        SessionAttachmentTurnPreparation streamAttachmentPrep = await SessionAttachmentTurnService
            .PrepareAsync(
                request,
                sessionAttachmentStore,
                settings.Value,
                grimoireTurn.SessionId,
                grimoireTurn.AssistantEntryId,
                streamPendingTurnId,
                inferenceToken,
                logger)
            .ConfigureAwait(false);

        SessionContextPinMaterialization streamPinMaterialization =
            sessionContextPinMaterializer is not null
            && (grimoireTurn.SessionId ?? request.SessionId) is { } contextPinSessionId
                ? await sessionContextPinMaterializer.MaterializeAsync(
                    contextPinSessionId, request.WorkingDirectory, inferenceToken).ConfigureAwait(false)
                : new SessionContextPinMaterialization([], 0, 0);

        if (streamAttachmentPrep.ErrorMessage is not null)
        {
            if (!streaming)
            {
                classification.BufferedTerminal = Result<PromptTurnResult>.Failure(
                    new Error(ErrorCodes.Validation.AttachedFiles, streamAttachmentPrep.ErrorMessage));
            }

            if (streaming)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    streamAttachmentPrep.ErrorMessage);
            }

            await grimoireTurnWriter
                .ResolveInterruptedAndMarkFinalizedAsync(grimoireTurn, null, CancellationToken.None)
                .ConfigureAwait(false);

            yield break;
        }

        SessionAttachmentTurnBudget.BeginTurn();

        AttachmentsSettings streamAttachmentSettings = settings.Value.ResolveAttachments();

        EmbeddingSettings streamEmbeddingSettings = settings.Value.ResolveEmbeddings();

        AttachmentEmbeddingSettings streamRetrievalSettings = streamEmbeddingSettings.Attachments;

        ContextMaterializationLedger streamMaterializationLedger = new(
            grimoireTurn.SessionId ?? request.SessionId,
            new ContextMaterializationLimits(
                ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedChunks(
                    streamRetrievalSettings.MaxRetrievedChunks),
                ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedAttachments(
                    streamRetrievalSettings.MaxRetrievedAttachments),
                ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedBytes(
                    streamRetrievalSettings.MaxRetrievedBytes),
                ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedTokens(
                    streamRetrievalSettings.MaxRetrievedTokens)));

        ContextMaterializationLedgerAmbient.Begin(streamMaterializationLedger);

        List<AIContent> acceptedAppendedContext = [];

        HashSet<int> acceptedAttachedFileIndices = [];

        HashSet<int> acceptedScryingFocusIndices = [];

        List<(SessionAttachmentExplicitMaterialization Materialization,
            ContextMaterializationIdentity Identity)> deferredAttachmentMaterializations = [];

        if (!attachmentsEnabled && request.AttachedFiles is { Count: > 0 } directAttachedFiles)
        {

            for (int index = 0; index < directAttachedFiles.Count; index++)
            {

                AttachedFileDto file = directAttachedFiles[index];

                byte[] bytes = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);

                string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                ContextMaterializationEntry accepted = streamMaterializationLedger.Accept(
                    new ContextMaterializationCandidate(
                        grimoireTurn.SessionId ?? request.SessionId,
                        ContextMaterializationSourceKind.CurrentTurnAttachment,
                        $"current:{index.ToString(CultureInfo.InvariantCulture)}",
                        hash,
                        VersionOrdinal: null,
                        ContextMaterializationRange.Whole,
                        ContextMaterializationOrigin.Explicit,
                        file.RelativePath,
                        hash,
                        EstimateMaterializedTokens(bytes.Length),
                        bytes.Length,
                ContextMaterializationTrust.UntrustedData),
                materialized: true);

                if (accepted.Accepted)
                {

                    _ = streamMaterializationLedger.TryMarkInjected(
                        accepted.Identity,
                        providerRound: 0);

                    acceptedAttachedFileIndices.Add(index);

                }

            }

        }

        if (!attachmentsEnabled && request.ScryingFoci is { Count: > 0 } directScryingFoci)
        {

            for (int index = 0; index < directScryingFoci.Count; index++)
            {

                byte[] bytes = Convert.FromBase64String(directScryingFoci[index].Data);

                string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                ContextMaterializationEntry accepted = streamMaterializationLedger.Accept(
                    new ContextMaterializationCandidate(
                        grimoireTurn.SessionId ?? request.SessionId,
                        ContextMaterializationSourceKind.CurrentTurnAttachment,
                        $"current-image:{index.ToString(CultureInfo.InvariantCulture)}",
                        hash,
                        VersionOrdinal: null,
                        ContextMaterializationRange.Whole,
                        ContextMaterializationOrigin.Explicit,
                        $"image-{index.ToString(CultureInfo.InvariantCulture)}",
                        hash,
                        EstimateMaterializedTokens(bytes.Length),
                        bytes.Length,
                        ContextMaterializationTrust.UntrustedData),
                    materialized: true);

                if (accepted.Accepted)
                {

                    _ = streamMaterializationLedger.TryMarkInjected(
                        accepted.Identity,
                        providerRound: 0);

                    acceptedScryingFocusIndices.Add(index);

                }

            }

        }

        foreach (SessionAttachmentExplicitMaterialization materialization
                 in streamAttachmentPrep.ExplicitMaterializations ?? [])
        {

            SessionAttachmentRecord record = materialization.Record;

            ContextMaterializationEntry accepted = streamMaterializationLedger.Accept(
                new ContextMaterializationCandidate(
                    record.SessionId ?? grimoireTurn.SessionId ?? request.SessionId,
                    materialization.SourceKind,
                    record.Id.ToString("N"),
                    record.Version.ToString(CultureInfo.InvariantCulture),
                    record.Version,
                    ContextMaterializationRange.Whole,
                    ContextMaterializationOrigin.Explicit,
                    record.OriginalFileName,
                    record.ContentSha256,
                    EstimateMaterializedTokens(record.ByteLength),
                    int.CreateSaturating(record.ByteLength),
                    ContextMaterializationTrust.UntrustedData)
                {
                    AttachmentProvenance = BuildAttachmentMemoryProvenance(record),
                },
                materialized: true);

            if (!accepted.Accepted)
            {

                continue;

            }

            if (record.Source is
                {

                    Kind: AttachmentSourceKind.WorkspaceFile,
                    WorkspaceRelativePath: { Length: > 0 } workspaceRelativePath,

                })
            {

                streamMaterializationLedger.RegisterExplicitWorkspaceSource(
                    workspaceRelativePath);

            }

            if (materialization.RequiresMaterialization)
            {

                deferredAttachmentMaterializations.Add((materialization, accepted.Identity));

                continue;

            }

            _ = streamMaterializationLedger.TryMarkInjected(accepted.Identity, providerRound: 0);

            acceptedAppendedContext.AddRange(materialization.Contents);

            if (materialization.AttachedFileIndex is { } attachedFileIndex)
            {

                acceptedAttachedFileIndices.Add(attachedFileIndex);

            }

            if (materialization.ScryingFocusIndex is { } scryingFocusIndex)
            {

                acceptedScryingFocusIndices.Add(scryingFocusIndex);

            }

        }

        int deferredAttachmentInsertionIndex = acceptedAppendedContext.Count;

        foreach (ContextPinMaterializedItem pin in streamPinMaterialization.Items ?? [])
        {

            ContextMaterializationEntry accepted = streamMaterializationLedger.Accept(
                new ContextMaterializationCandidate(
                    grimoireTurn.SessionId ?? request.SessionId,
                    ContextMaterializationSourceKind.ExplicitContextPin,
                    Guid.TryParse(pin.SourceId, out Guid pinSourceId)
                        ? pinSourceId.ToString("N")
                        : pin.SourceId,
                    pin.VersionOrdinal?.ToString(CultureInfo.InvariantCulture)
                        ?? pin.ContentHash,
                    pin.VersionOrdinal,
                    ContextMaterializationRange.Whole,
                    ContextMaterializationOrigin.Explicit,
                    pin.SourceLabel,
                    pin.ContentHash,
                    EstimateMaterializedTokens(pin.MaterializedBytes),
                    pin.MaterializedBytes,
                    ContextMaterializationTrust.UntrustedData),
                materialized: true);

            if (accepted.Accepted)
            {

                _ = streamMaterializationLedger.TryMarkInjected(accepted.Identity, providerRound: 0);

                acceptedAppendedContext.Add(pin.Content);

                if (pin.Kind == SessionContextPinKind.File)
                {

                    streamMaterializationLedger.RegisterExplicitWorkspaceSource(pin.SourceId);

                }

            }

        }

        HashSet<AIContent> trackedPinContents = new(ReferenceEqualityComparer.Instance);

        trackedPinContents.UnionWith(
            (streamPinMaterialization.Items ?? []).Select(static item => item.Content));

        acceptedAppendedContext.AddRange(
            streamPinMaterialization.Contents.Where(content => !trackedPinContents.Contains(content)));

        IReadOnlyList<AIContent> streamAppendedContext = acceptedAppendedContext;

        List<AttachedFileDto>? streamAcceptedAttachedFiles = request.AttachedFiles is null
            ? null
            : request.AttachedFiles
                .Where((_, index) => acceptedAttachedFileIndices.Contains(index))
                .ToList();

        PingRequest streamContextRequest = request with
        {

            ScryingFoci = request.ScryingFoci is null
                ? null
                : request.ScryingFoci
                    .Where((_, index) => acceptedScryingFocusIndices.Contains(index))
                    .ToList(),

        };

        Result<TurnAccountingHandle> streamAccountingBegin;

        if (TurnAccountingAmbient.Current is { } streamAmbientAccounting)
        {
            streamAccountingBegin = Result<TurnAccountingHandle>.Success(streamAmbientAccounting);
        }
        else
        {
            streamAccountingBegin = await TurnAccountingHandle.BeginAsync(
                    turnRunWriter,
                    budgetReservationService,
                    settings.Value.ResolvePricing(),
                    targetModel,
                    grimoireTurn.SessionId ?? request.SessionId,
                    surface: streaming ? "stream" : "buffered",
                    purpose: "chat",
                    requestId: Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
                    cancellationToken: inferenceToken,
                    maxOutputTokens: request.MaxOutputTokens,
                    reasoningBudgetTokens: request.Reasoning?.BudgetTokens)
                .ConfigureAwait(false);
        }

        if (streamAccountingBegin.IsFailure)
        {
            SessionAttachmentTurnBudget.EndTurn();

            ContextMaterializationLedgerAmbient.End();

            if (!streaming)
            {
                classification.BufferedTerminal = Result<PromptTurnResult>.Failure(streamAccountingBegin.Error);
            }

            if (streaming)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    streamAccountingBegin.Error.Message);
            }

            await grimoireTurnWriter
                .ResolveInterruptedAndMarkFinalizedAsync(grimoireTurn, null, CancellationToken.None)
                .ConfigureAwait(false);

            yield break;
        }

        TurnAccountingHandle streamAccountingLocal = streamAccountingBegin.Value;

        streamAccounting = streamAccountingLocal;

        if (previousAmbient is null)
        {
            TurnAccountingAmbient.Publish(streamAccountingLocal, turnRunWriter);
            publishedStreamAmbient = true;
        }

        List<MeAiChatMessage> chatMessages = InferenceContextBuilder.BuildInitialMeAiChatMessages(
            streamContextRequest,
            thread,
            prompt);

        string? streamCodexContent = await CodexReader
            .ReadCodexAsync(
                request.WorkingDirectory,
                ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value),
                inferenceToken)
            .ConfigureAwait(false);

        ResolvedSpell? streamResolvedSpell;

        if (request.SkipSpellRouting)
        {
            streamResolvedSpell = null;
        }
        else
        {
            string? streamSpellWorkspaceRoot = RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy.TryNormalizeWorkspace(
                request.WorkingDirectory,
                out string? streamSpellRoot,
                out _)
                ? streamSpellRoot
                : null;

            Result<ResolvedSpell?> streamRoutedSpell = await ResolveRoutedSpellAsync(
                request,
                chatClient,
                lease.Provider,
                targetModel,
                streamSpellWorkspaceRoot,
                inferenceToken).ConfigureAwait(false);

            if (streamRoutedSpell.IsFailure)
            {
                if (!streaming)
                {
                    await grimoireTurnWriter
                        .ResolveInterruptedAndMarkFinalizedAsync(
                            grimoireTurn,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    classification.BufferedTerminal = Result<PromptTurnResult>.Failure(streamRoutedSpell.Error);
                }

                if (streaming)
                {
                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        streamRoutedSpell.Error.Message);
                }

                yield break;
            }

            streamResolvedSpell = streamRoutedSpell.Value;
        }

        ParsedSpell? streamActiveSpell = streamResolvedSpell?.Primary;

        IReadOnlyList<ParsedSpell>? streamResonants = streamResolvedSpell?.Resonants;

        Embedding<float>? streamQueryEmbedding;

        // Provider-independent and billed through TryRecordAuxiliaryUsageAsync, so the run resolves
        // it once and every fallback candidate reuses it (DESIGN §10.7.2).
        if (seed is { QueryEmbeddingResolved: true })
        {
            streamQueryEmbedding = seed.QueryEmbedding;
        }
        else
        {
            streamQueryEmbedding = await ResolveRagQueryEmbeddingAsync(request, inferenceToken).ConfigureAwait(false);

            if (seed is not null)
            {
                seed.QueryEmbedding = streamQueryEmbedding;

                seed.QueryEmbeddingResolved = true;
            }
        }

        SemanticContextChunk[]? streamSemanticContext = await RetrieveSemanticContextAsync(request, streamQueryEmbedding, inferenceToken).ConfigureAwait(false);

        SagaMemory[]? streamSagaMemories = await RetrieveSagaMemoriesAsync(streamQueryEmbedding, invocationContext, inferenceToken).ConfigureAwait(false);

        SessionAttachmentRetrievedChunk[]? streamAttachmentContext = await RetrieveSessionAttachmentContextAsync(
            request,
            streamQueryEmbedding,
            inferenceToken).ConfigureAwait(false);

        streamAttachmentContext = AcceptAttachmentRagMaterializations(
            streamMaterializationLedger,
            streamAttachmentContext,
            lease.Provider,
            lease.ResolvedModel);

        streamSemanticContext = AcceptWorkspaceRagMaterializations(
            streamMaterializationLedger,
            streamSemanticContext,
            lease.Provider,
            lease.ResolvedModel,
            grimoireTurn.SessionId ?? request.SessionId);

        streamSagaMemories = AcceptSagaMaterializations(
            streamMaterializationLedger,
            streamSagaMemories,
            lease.Provider,
            lease.ResolvedModel,
            grimoireTurn.SessionId ?? request.SessionId);

        // Admitted last of the semantic sources so an exact raw leaf already in the ledger wins the
        // duplicate-content check against a Tapestry node covering the same text (DESIGN §21.11).
        TapestryContextNode[]? streamTapestryContext = AcceptTapestryMaterializations(
            streamMaterializationLedger,
            await RetrieveTapestryContextAsync(request, streamQueryEmbedding, inferenceToken)
                .ConfigureAwait(false),
            lease.Provider,
            lease.ResolvedModel,
            grimoireTurn.SessionId ?? request.SessionId);

        IReadOnlyList<LexiconEntryDto>? streamLexiconEntries = await RetrieveLexiconEntriesAsync(
            request,
            streamResolvedSpell?.Entities ?? Array.Empty<string>(),
            chatClient,
            lease.Provider,
            targetModel,
            invocationContext,
            inferenceToken).ConfigureAwait(false);

        int streamMaxIndexItems = ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(
            streamAttachmentSettings.MaxIndexItemsInPrompt);

        int streamMaxIndexBytes = ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt(
            streamAttachmentSettings.MaxIndexBytesInPrompt);

        // Declared here so that every prompt build in this attempt reads the same admitted content,
        // and resolved once below, at the first point where the attempt's own tool surface and options
        // exist: measuring head-room without them would overstate what fits.
        CovenantDispatchPlan streamCovenantDispatch = CovenantDispatchPlan.Empty;

        CovenantPromptContent? streamCovenantContent = null;

        SystemPromptDocument baseSystemPromptDocument = SystemPromptBuilder.BuildDocument(
            request,
            streamCodexContent,
            streamActiveSpell,
            streamAcceptedAttachedFiles,
            dependencySpells: streamResonants,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(
                ArcanumRuntimeDefaults.Spells.MaxResonantBytes),
            semanticContext: streamSemanticContext,
            sagaMemories: streamSagaMemories,
            lexiconEntries: streamLexiconEntries,
            maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(
                settings.Value.ResolveIntelligence().LexiconMaxInjectedBytes),
            sessionAttachmentsIndex: streamAttachmentPrep.IndexItems,
            maxIndexItems: streamMaxIndexItems,
            maxIndexBytes: streamMaxIndexBytes,
            sessionAttachmentContext: streamAttachmentContext,
            tapestryContext: streamTapestryContext,
            covenant: streamCovenantContent);
        SystemPromptDocument streamSystemPromptDocument = baseSystemPromptDocument;
        string streamBuiltSystemPrompt = streamSystemPromptDocument.Render();

        bool streamContextUsesCompressedSummary = false;

        InferenceContextBuilder.PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

        void RebuildMaterializedSystemPrompt()
        {

            streamSystemPromptDocument = SystemPromptBuilder.BuildDocument(
                request,
                streamCodexContent,
                streamActiveSpell,
                streamAcceptedAttachedFiles,
                campaignSummary: streamContextUsesCompressedSummary ? thread?.Summary : null,
                dependencySpells: streamResonants,
                maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(
                    ArcanumRuntimeDefaults.Spells.MaxResonantBytes),
                semanticContext: streamSemanticContext,
                sagaMemories: streamSagaMemories,
                lexiconEntries: streamLexiconEntries,
                maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(
                    settings.Value.ResolveIntelligence().LexiconMaxInjectedBytes),
                sessionAttachmentsIndex: streamAttachmentPrep.IndexItems,
                maxIndexItems: streamMaxIndexItems,
                maxIndexBytes: streamMaxIndexBytes,
                sessionAttachmentContext: streamAttachmentContext,
                tapestryContext: streamTapestryContext,
                covenant: streamCovenantContent);

            streamBuiltSystemPrompt = streamSystemPromptDocument.Render();

            if (!streamContextUsesCompressedSummary)
            {

                baseSystemPromptDocument = streamSystemPromptDocument;

            }

            if (chatMessages.Count > 0 && chatMessages[0].Role == ChatRole.System)
            {

                chatMessages[0] = new MeAiChatMessage(ChatRole.System, streamBuiltSystemPrompt);

            }

        }

        bool RemoveSemanticMaterialization(ContextMaterializationEntry removed)
        {

            bool changed = removed.SourceKind switch
            {

                ContextMaterializationSourceKind.AttachmentRag =>
                    RemoveAttachmentRagChunk(ref streamAttachmentContext, removed),

                ContextMaterializationSourceKind.WorkspaceRag =>
                    RemoveWorkspaceRagChunk(ref streamSemanticContext, removed),

                ContextMaterializationSourceKind.SagaMemory =>
                    RemoveSagaMemory(ref streamSagaMemories, removed),

                ContextMaterializationSourceKind.TapestryMemory =>
                    RemoveTapestryNode(ref streamTapestryContext, removed),

                _ => false,

            };

            if (!changed)
            {

                return false;

            }

            RebuildMaterializedSystemPrompt();

            return true;

        }

        void ReconcileSuppressedSemanticContext()
        {

            bool changed = PruneSuppressedAttachmentRag(
                ref streamAttachmentContext,
                streamMaterializationLedger);

            changed |= PruneSuppressedWorkspaceRag(
                ref streamSemanticContext,
                streamMaterializationLedger);

            if (!changed)
            {

                return;

            }

            RebuildMaterializedSystemPrompt();

        }

        if (streaming && !streamTurnBegunEarly && !InferenceContextBuilder.HasStatelessMessages(request))
        {
            Result<GrimoireTurnWriter.TurnHandle> lateBegun = await grimoireTurnWriter
                .BeginStreamedAssistantReplyAsync(
                    request,
                    invocationContext,
                    prompt,
                    targetModel,
                    inferenceToken)
                .ConfigureAwait(false);

            if (lateBegun.IsFailure)
            {
                yield return new IntelligenceEvent(IntelligenceEventType.Error, lateBegun.Error.Message);

                yield break;
            }

            grimoireTurn = lateBegun.Value;

            if (seed is not null)
            {
                seed.Turn = grimoireTurn;
            }
        }

        if (grimoireTurn.SessionId is { } bcid)
        {
            if (streaming)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.SessionBound,
                    "Session started",
                    bcid.ToString());

                yield return new IntelligenceEvent(
                    IntelligenceEventType.ConversationBound,
                    "Conversation started",
                    bcid.ToString());
            }

            if (streamAttachmentPrep.PendingTurnId is not null)
            {
                string? promoteError = await SessionAttachmentTurnService
                    .PromotePendingAsync(
                        sessionAttachmentStore,
                        streamAttachmentPrep.PendingTurnId,
                        bcid,
                        grimoireTurn.AssistantEntryId,
                        inferenceToken,
                        logger)
                    .ConfigureAwait(false);

                if (promoteError is not null)
                {
                    if (!streaming)
                    {
                        classification.BufferedTerminal = Result<PromptTurnResult>.Failure(
                            new Error(ErrorCodes.Validation.AttachedFiles, promoteError));
                    }

                    if (streaming)
                    {
                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            promoteError);
                    }

                    await grimoireTurnWriter
                        .ResolveInterruptedAndMarkFinalizedAsync(
                            grimoireTurn,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    yield break;
                }
            }
        }

        // Per-turn live HITL emitter — created before tool-set assembly so HumanInteractionAvailable
        // can strip ask_human when no live response channel exists (buffered path never creates this).
        // Also published via AsyncLocal so the singleton MCP ElicitationHandler can emit ask_human-shaped
        // frames on this same channel during nested tool calls.
        Func<IntelligenceEvent, CancellationToken, Task>? liveHumanPromptEmit = null;

        // Buffered turns never have a live HITL channel — ask_human would deadlock until timeout.
        if (streaming && !request.UnattendedMode)
        {
            liveHumanPromptChannel = Channel.CreateUnbounded<IntelligenceEvent>();

            liveHumanPromptEmit = async (evt, ct) =>
            {
                await liveHumanPromptChannel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            };

            HumanPromptLiveEmitterAmbient.Current = new ChannelHumanPromptLiveEmitter(liveHumanPromptChannel);
        }

        bool humanInteractionAvailable = streaming && liveHumanPromptEmit is not null;

        List<AITool> streamToolSet = request.DisableAllTools
            ? []
            : request.ForwardClientTools
                ? BuildClientForwardedToolSet(request)
                : await BuildToolSetWithMcpAsync(
                    request,
                    streamResolvedSpell,
                    grimoireTurn.SessionId ?? request.SessionId,
                    grimoireTurn.AssistantEntryId,
                    inferenceToken).ConfigureAwait(false);

        ToolExecutionPipeline.TurnContext streamTurnContext = await BuildTurnContextAsync(
            request,
            streamToolSet,
            humanInteractionAvailable,
            grimoireTurn,
            targetModel,
            streamAttachmentPrep.VisibleAttachmentIds,
            invocationContext,
            inferenceToken).ConfigureAwait(false);

        bool streamUsesTools = !request.DisableAllTools && streamTurnContext.InferenceTools.Count > 0;

        if (deferredAttachmentMaterializations.Count > 0)
        {

            ChatOptions attachmentAdmissionOptions = CreateInferenceChatOptions(
                streamUsesTools,
                streamTurnContext.InferenceTools,
                request,
                lease);

            foreach ((SessionAttachmentExplicitMaterialization materialization,
                     ContextMaterializationIdentity identity)
                     in deferredAttachmentMaterializations)
            {

                SessionAttachmentReferenceMaterialization reference =
                    await SessionAttachmentTurnService.MaterializeReferenceAsync(
                        materialization.Record,
                        sessionAttachmentStore,
                        settings.Value,
                        lease.ResolvedModel,
                        inferenceToken).ConfigureAwait(false);

                if (reference.ErrorMessage is not null)
                {

                    Error referenceError = new(
                        ErrorCodes.Validation.AttachedFiles,
                        reference.ErrorMessage);

                    if (!streaming)
                    {

                        classification.BufferedTerminal = Result<PromptTurnResult>.Failure(referenceError);

                    }

                    if (streaming)
                    {

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            referenceError.Message,
                            referenceError.Code);

                    }

                    await grimoireTurnWriter
                        .ResolveInterruptedAndMarkFinalizedAsync(
                            grimoireTurn,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    yield break;

                }

                List<AIContent> candidateAppendedContext = [.. acceptedAppendedContext];

                candidateAppendedContext.InsertRange(
                    deferredAttachmentInsertionIndex,
                    reference.Contents);

                List<MeAiChatMessage> admissionMessages = chatMessages
                    .Select(static message => new MeAiChatMessage(
                        message.Role,
                        [.. message.Contents])
                    {

                        AuthorName = message.AuthorName,

                    })
                    .ToList();

                InferenceContextBuilder.AppendContentsToLastMessage(
                    admissionMessages,
                    candidateAppendedContext);

                ModelCallContext attachmentAdmissionContext = BuildModelCallContext(lease, request);

                (bool admissionCompressed, List<MeAiChatMessage> preparedAdmissionMessages) =
                    inferenceContextBuilder.TryApplyContextCompressionIfNeeded(
                        new ContextCompressionRequest
                        {

                            Request = request,

                            Messages = admissionMessages,

                            Thread = thread,

                            NewUserPrompt = prompt,

                            Lease = lease,

                            ChatOptions = attachmentAdmissionOptions,

                            CodexContent = streamCodexContent,

                            ActiveSpell = streamActiveSpell,

                            DependencySpells = streamResonants,

                            ReservedAnswerTokens = attachmentAdmissionContext.ReservedAnswerTokens,

                            ReservedReasoningTokens = attachmentAdmissionContext.ReservedReasoningTokens,

                            AppendedContents = candidateAppendedContext,

                            SemanticContext = streamSemanticContext,

                            SagaMemories = streamSagaMemories,

                            SessionAttachmentContext = streamAttachmentContext,

                            TapestryContext = streamTapestryContext,

                            LexiconEntries = streamLexiconEntries,

                            SessionAttachmentsIndex = streamAttachmentPrep.IndexItems,

                            MaxIndexItems = streamMaxIndexItems,

                            MaxIndexBytes = streamMaxIndexBytes,

                            ScryingFoci = streamContextRequest.ScryingFoci,

                        });

                bool RemoveSemanticForAttachmentAdmission(ContextMaterializationEntry removed)
                {

                    bool changed = RemoveSemanticMaterialization(removed);

                    if (!changed)
                    {

                        return false;

                    }

                    SystemPromptDocument admissionSystemPrompt = SystemPromptBuilder.BuildDocument(
                        request,
                        streamCodexContent,
                        streamActiveSpell,
                        streamAcceptedAttachedFiles,
                        campaignSummary: admissionCompressed ? thread?.Summary : null,
                        dependencySpells: streamResonants,
                        maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(
                            ArcanumRuntimeDefaults.Spells.MaxResonantBytes),
                        semanticContext: streamSemanticContext,
                        sagaMemories: streamSagaMemories,
                        lexiconEntries: streamLexiconEntries,
                        maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(
                            settings.Value.ResolveIntelligence().LexiconMaxInjectedBytes),
                        sessionAttachmentsIndex: streamAttachmentPrep.IndexItems,
                        maxIndexItems: streamMaxIndexItems,
                        maxIndexBytes: streamMaxIndexBytes,
                        sessionAttachmentContext: streamAttachmentContext,
                        tapestryContext: streamTapestryContext,
                covenant: streamCovenantContent);

                    if (preparedAdmissionMessages.Count > 0
                        && preparedAdmissionMessages[0].Role == ChatRole.System)
                    {

                        preparedAdmissionMessages[0] = new MeAiChatMessage(
                            ChatRole.System,
                            admissionSystemPrompt.Render());

                    }

                    return true;

                }

                Result attachmentAdmission = EnsureContextBudgetWithMaterializations(
                    preparedAdmissionMessages,
                    attachmentAdmissionOptions,
                    lease,
                    request,
                    streamMaterializationLedger,
                    RemoveSemanticForAttachmentAdmission,
                    out _);

                if (attachmentAdmission.IsFailure)
                {

                    string attachmentLabel = SystemPromptBuilder.HardenAttachmentIndexName(
                        materialization.Record.OriginalFileName);

                    if (attachmentLabel.Length == 0)
                    {

                        attachmentLabel = materialization.Record.Id.ToString();

                    }

                    Error boundaryError = new(
                        attachmentAdmission.Error.Code,
                        $"Attachment '{attachmentLabel}' cannot fit in the resolved provider context. "
                        + attachmentAdmission.Error.Message
                        + " Use a larger-context model or reference fewer attachments in this turn.");

                    if (!streaming)
                    {

                        classification.BufferedTerminal = Result<PromptTurnResult>.Failure(boundaryError);

                    }

                    if (streaming)
                    {

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            boundaryError.Message,
                            boundaryError.Code);

                    }

                    await grimoireTurnWriter
                        .ResolveInterruptedAndMarkFinalizedAsync(
                            grimoireTurn,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    yield break;

                }

                acceptedAppendedContext.InsertRange(
                    deferredAttachmentInsertionIndex,
                    reference.Contents);

                deferredAttachmentInsertionIndex += reference.Contents.Count;

                _ = streamMaterializationLedger.TryMarkInjected(identity, providerRound: 0);

            }

        }

        string? inferenceError;

        Error? inferenceTypedError = null;

        string? streamFinishReason = null;

        ChatCompletionUsage? streamAccumulatedUsage = null;

        StructuredOutputSettings structuredOutput = ResolveStructuredOutputPolicy(request);
        GuardrailsSettings guardrails = settings.Value.ResolveGuardrails();
        GuardrailsStreamingMode guardrailsStreamingMode = guardrails.StreamingMode;

        bool guardrailsOutputActive = guardrails.Enabled
            && (guardrails.BlockToxicity
                || guardrails.BlockedTopics is { Length: > 0 });

        if (streaming && guardrailsOutputActive && guardrailsStreamingMode == GuardrailsStreamingMode.Passthrough)
        {
            logger.LogWarning(
                "Internal guardrail streaming mode resolved to Passthrough; blocked assistant output may reach the client before the post-hoc filter runs. Public configuration fixes this policy to Buffered.");
        }

        bool bufferTokens = streaming
            && ((guardrailsStreamingMode == GuardrailsStreamingMode.Buffered && guardrailsOutputActive)
                || (request.ResponseFormat is "json_schema"
                    && structuredOutput.Enabled
                    && structuredOutput.StrictMode));

        BufferedOutputFrameAccumulator bufferedOutputFrames = new();

        bool replacementOutputPreservesContentOrder = false;

        bool suppressInvocationFailures =
            streaming || settings.Value.ResolveIntelligence().TolerateToolFailures;

        ChatOptions? lastInferenceChatOptions = null;

        bool streamingContextPrepared = false;

        bool streamToolCompatibilityRetry = false;

        ToolLoopProgressDetector toolLoopProgressDetector = new();

        while (true)
        {
            bool streamOuterRestart = false;

            // The outer loop repeats only for the pre-commit no-tools compatibility restart.
            streamAccumulator.Clear();

            ephemeralReasoningSegments.Clear();

            bufferedOutputFrames.Clear();

            replacementOutputPreservesContentOrder = false;

            streamAccumulatedUsage = null;

            bufferedFinalResponse = null;

            if (!streaming)
            {
                // Buffered no-tools restart rebuilds the message list each outer attempt.
                chatMessages = InferenceContextBuilder.BuildInitialMeAiChatMessages(
                    streamContextRequest,
                    thread,
                    prompt);

                InferenceContextBuilder.PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

                streamSystemPromptDocument = baseSystemPromptDocument;
            }

            ChatOptions streamChatOptions = CreateInferenceChatOptions(streamUsesTools, streamTurnContext.InferenceTools, request, lease);

            if (streamCovenantContent is not null)
            {

                // Head-room is measured against the transcript as it stands, and only a Covenant-free
                // transcript makes that measurement honest. A restart inheriting the previous attempt's
                // Covenant bytes counts them as somebody else's context and admits less than fits, and
                // if the shrunken head-room refuses outright, the rebuild below is skipped and this
                // attempt would dispatch the very content its own planner declined.
                streamCovenantContent = null;

                RebuildMaterializedSystemPrompt();

            }

            streamCovenantDispatch = ResolveCovenantAdmission(
                covenantScope,
                lease,
                request,
                chatMessages,
                streamChatOptions);

            if (streamCovenantDispatch.HasAdmittedContent)
            {

                streamCovenantContent = streamCovenantDispatch.Content;

                RebuildMaterializedSystemPrompt();

            }

            if (streamCovenantDispatch.Admission is { ConfirmedAdmitted: true } streamCovenantAdmission)
            {

                // Recorded whether or not anything was dropped: zero is a measurement here, and an
                // operator asking why a preference was not honored needs to see that the answer is
                // "nothing was pressured" rather than "nobody looked".
                streamMaterializationLedger.RecordCovenantPressure(
                    streamCovenantAdmission.ProposedRemovals,
                    (int)Math.Min(int.MaxValue, streamCovenantAdmission.PressuredProposedTokens));

            }
            else if (streamCovenantDispatch.Admission is not null)
            {

                // A refusal marks every candidate Pressured and counts every Proposed entry as a
                // removal, but nothing was trimmed: Confirmed is all-or-fail, so the entire section
                // was withheld. Reporting those counts as Proposed pressure would tell an operator
                // their standing agreement was honored minus a few preferences, when none of it was
                // sent at all -- the one outcome that must never be silent.
                streamMaterializationLedger.RecordCovenantConfirmedNoFit();

            }

            lastInferenceChatOptions = streamChatOptions;

            if (!streaming || !streamingContextPrepared)
            {
                ModelCallContext preparationContext = BuildModelCallContext(lease, request);
                InferenceContextBuilder.AppendContentsToLastMessage(
                    chatMessages,
                    streamAppendedContext);
                (bool compressed, List<MeAiChatMessage> preparedMessages) =
                    inferenceContextBuilder.TryApplyContextCompressionIfNeeded(
                        new ContextCompressionRequest
                        {
                            Request = request,
                            Messages = chatMessages,
                            Thread = thread,
                            NewUserPrompt = prompt,
                            Lease = lease,
                            ChatOptions = streamChatOptions,
                            CodexContent = streamCodexContent,
                            ActiveSpell = streamActiveSpell,
                            DependencySpells = streamResonants,
                            ReservedAnswerTokens = preparationContext.ReservedAnswerTokens,
                            ReservedReasoningTokens = preparationContext.ReservedReasoningTokens,
                            AppendedContents = streamAppendedContext,
                            SemanticContext = streamSemanticContext,
                            SagaMemories = streamSagaMemories,
                            SessionAttachmentContext = streamAttachmentContext,
                            TapestryContext = streamTapestryContext,
                            LexiconEntries = streamLexiconEntries,
                            SessionAttachmentsIndex = streamAttachmentPrep.IndexItems,
                            MaxIndexItems = streamMaxIndexItems,
                            MaxIndexBytes = streamMaxIndexBytes,
                            ScryingFoci = streamContextRequest.ScryingFoci,
                            Covenant = streamCovenantContent,
                        });
                chatMessages = preparedMessages;
                streamingContextPrepared = true;

                if (compressed)
                {
                    streamContextUsesCompressedSummary = true;

                    // The one rebuild path, rather than a second copy of the same call. Compression
                    // replaced the whole transcript, so the document and chatMessages[0] have to be
                    // rewritten together: the dispatch gate compares them and fails the turn closed on
                    // any divergence, so a rebuild that refreshed only the document would refuse every
                    // compressed turn that carried Covenant content.
                    RebuildMaterializedSystemPrompt();

                    if (streaming)
                    {
                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Status,
                            IntelligenceStatusMessages.MemoryCompressionNotice);
                    }
                    else
                    {
                        logger.LogInformation(IntelligenceStatusMessages.MemoryCompressionNotice);
                    }
                }
            }

            int streamToolRoundCount = 0;

            inferenceError = null;

            inferenceTypedError = null;

            Exception? streamingMoveNextFailure = null;

                while (true)
                {
                    List<ChatResponseUpdate> roundUpdates = [];

                    ChatResponse? bufferedRoundResponse = null;

                    ContextMaterializationLedgerAmbient.SetProviderRound(streamToolRoundCount);

                    ReconcileSuppressedSemanticContext();

                    Result streamContextGate = EnsureContextBudgetWithMaterializations(
                        chatMessages,
                        streamChatOptions,
                        lease,
                        request,
                        streamMaterializationLedger,
                        RemoveSemanticMaterialization,
                        out ContextTokenBreakdown callBreakdown);

                    if (streamContextGate.IsSuccess)
                    {
                        streamContextGate = await streamAccountingLocal
                            .EnsureReservationForContextAsync(
                                budgetReservationService,
                                settings.Value.ResolvePricing(),
                                targetModel,
                                callBreakdown,
                                inferenceToken)
                            .ConfigureAwait(false);
                    }

                    if (streamContextGate.IsFailure)
                    {
                        inferenceError = streamContextGate.Error.Message;
                        inferenceTypedError = streamContextGate.Error;

                        streamAccountingStatus = InferenceRunStatus.Failed;

                        if (!streaming)
                        {
                            await grimoireTurnWriter
                                .ResolveInterruptedAndMarkFinalizedAsync(
                                    grimoireTurn,
                                    null,
                                    CancellationToken.None)
                                .ConfigureAwait(false);

                            classification.BufferedTerminal = Result<PromptTurnResult>.Failure(streamContextGate.Error);
                        }

                        break;
                    }

                    // Disclosure precedes egress. Every physical dispatch that carries admitted
                    // Covenant content or tainted history commits its own durable receipt here, before
                    // the executor is reached; a receipt written afterwards could not record the one
                    // case it exists for, a call that left the process and then crashed. A turn that
                    // may stage also passes through, discloses nothing, and earns the admission its
                    // proposal tool runs under — the two obligations are separate, and treating them
                    // as one is what made a first proposal impossible to author.
                    Result<CovenantDispatchAdmission?> streamCovenantGate = await AcknowledgeCovenantDispatchAsync(
                            covenantScope,
                            streamCovenantDispatch,
                            lease,
                            targetModel,
                            streaming,
                            streamSystemPromptDocument,
                            chatMessages,
                            streamChatOptions,
                            callBreakdown,
                            inferenceToken)
                        .ConfigureAwait(false);

                    if (streamCovenantGate.IsFailure)
                    {
                        inferenceError = streamCovenantGate.Error.Message;
                        inferenceTypedError = streamCovenantGate.Error;

                        streamAccountingStatus = InferenceRunStatus.Failed;

                        if (!streaming)
                        {
                            await grimoireTurnWriter
                                .ResolveInterruptedAndMarkFinalizedAsync(
                                    grimoireTurn,
                                    null,
                                    CancellationToken.None)
                                .ConfigureAwait(false);

                            classification.BufferedTerminal = Result<PromptTurnResult>.Failure(streamCovenantGate.Error);
                        }

                        break;
                    }

                    if (streamCovenantGate.Value is { Receipt: not null } streamAdmitted
                        && covenantScope is { Collector: not null, HeadProbe: not null } stagingScope
                        && invocationContext.Campaign is { } stagingCampaign
                        && covenantToolCapabilities is not null)
                    {

                        streamCovenantStaging?.Dispose();

                        streamCovenantStaging = CovenantToolStagingAmbient.Push(new CovenantToolStagingContext(
                            stagingScope.Collector,
                            stagingCampaign,
                            streamAdmitted.Receipt,
                            streamAdmitted.Receipt.Materialization,
                            stagingScope.HeadProbe,
                            invocationContext.CanStageCovenantMutation,
                            covenantToolCapabilities,
                            inferenceToken));

                    }

                    ModelCallPurpose streamPurpose = streamToolRoundCount == 0
                        ? streamToolCompatibilityRetry
                            ? ModelCallPurpose.ToolCompatibilityRetry
                            : ModelCallPurpose.MainInference
                        : ModelCallPurpose.ToolContinuation;
                    PromptCachePlan streamPromptCachePlan = BuildPromptCachePlan(
                        lease,
                        streamSystemPromptDocument,
                        streamChatOptions,
                        PromptCacheSemanticNamespace.Main);

                    if (!streaming)
                    {
                        ModelCallOutcome modelCall;

                        {
                            modelCall = await ModelCallExecutor
                                .ExecuteBufferedAsync(
                                    chatClient,
                                    chatMessages,
                                    streamChatOptions,
                                    streamAccountingLocal.Budget,
                                    streamPurpose,
                                    inferenceToken,
                                    BuildModelCallContext(
                                        lease,
                                        request,
                                        callBreakdown,
                                        streamPromptCachePlan))
                                .ConfigureAwait(false);
                        }

                        if (modelCall.IsFailure)
                        {
                            // Do not break the tool-round loop yet — the inferenceError handler
                            // below owns no-tools compatibility restart.
                            streamingMoveNextFailure = modelCall.Failure.Cause
                                ?? new InvalidOperationException(modelCall.Error.Message);

                            inferenceError = BuildInferenceFailureMessage(lease, streamingMoveNextFailure);

                            classification.IsConnectivityFailure = IsConnectivityFailure(streamingMoveNextFailure, callerToken);
                        }
                        else
                        {
                            ModelCallResult bufferedCall = modelCall.Value;

                            if (bufferedCall.ContextBreakdown is { } bufferedBreakdown)
                            {
                                contextBreakdownsByCall[bufferedCall.ModelCallId] = bufferedBreakdown;
                            }

                            bufferedRoundResponse = bufferedCall.Response;

                            bufferedFinalResponse = bufferedRoundResponse;

                            ChatCompletionUsage? bufferedUsage = MapUsageDetails(bufferedRoundResponse.Usage);

                            await RecordProviderCallUsageAsync(
                                    streamAccountingLocal,
                                    bufferedUsage,
                                    lease.Provider.Name,
                                    targetModel,
                                    streamPurpose,
                                    CancellationToken.None)
                                .ConfigureAwait(false);

                            SubagentExecutionAmbient.Tracker?.ThrowIfExhausted();

                            string bufferedText = bufferedRoundResponse.Text ?? string.Empty;

                            if (bufferedText.Length > 0)
                            {
                                _ = streamAccumulator.Append(bufferedText);

                                if (ProviderAttemptCommitTracker.CommitsProviderAttempt(
                                        new ModelCallTextDelta(streamPurpose, modelCall.Value.ModelCallId, bufferedText)))
                                {
                                    classification.ProviderCommitted = true;
                                }
                            }

                            if (ProviderAttemptCommitTracker.CommitsProviderAttempt(bufferedCall.Reasoning))
                            {
                                // Buffered responses do not yield ModelCallUpdate instances, so commit
                                // from the executor's normalized metadata before validation/projection.
                                classification.ProviderCommitted = true;
                            }

                            if (bufferedCall.Reasoning.Segments.Count > 0)
                            {
                                foreach (ModelCallReasoningSegment segment in bufferedCall.Reasoning.Segments)
                                {
                                    ephemeralReasoningSegments.Append(segment);
                                }
                            }

                            streamAccumulatedUsage = AccumulateUsage(streamAccumulatedUsage, bufferedUsage);
                        }
                    }
                    else
                    {
                    IAsyncEnumerator<ModelCallUpdate> streamEnumerator = ModelCallExecutor
                            .ExecuteStreamingAsync(
                                chatClient,
                                chatMessages,
                                streamChatOptions,
                                streamAccountingLocal.Budget,
                                streamPurpose,
                                inferenceToken,
                                BuildModelCallContext(
                                    lease,
                                    request,
                                    callBreakdown,
                                    streamPromptCachePlan))
                        .GetAsyncEnumerator(inferenceToken);

                try
                {
                    while (true)
                    {
                        bool hasNext;

                        try
                        {
                            hasNext = await streamEnumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {

                            throw;

                        }
                        catch (Exception ex)
                        {
                            streamingMoveNextFailure = ex;

                            logger.LogError(
                                "Streaming read failed; exception type {ExceptionType}.",
                                ex.GetType().FullName);

                            inferenceError = BuildInferenceFailureMessage(lease, ex);

                            // Only a genuine connectivity failure should trigger provider fallback.
                            classification.IsConnectivityFailure = IsConnectivityFailure(ex, callerToken);

                            break;
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        ModelCallUpdate modelUpdate = streamEnumerator.Current;

                        if (modelUpdate is ModelCallContextUpdate contextUpdate)
                        {
                            contextBreakdownsByCall[contextUpdate.ModelCallId] = contextUpdate.Breakdown;
                            if (streaming)
                            {
                                yield return new IntelligenceEvent(
                                    IntelligenceEventType.Context,
                                    "Context token accounting",
                                    ContextBreakdown: contextUpdate.Breakdown);
                            }

                            continue;
                        }

                        if (modelUpdate is ModelCallFailureUpdate failureUpdate)
                        {
                            inferenceTypedError = failureUpdate.Error;
                            inferenceError = failureUpdate.Error.Message;
                            streamAccountingStatus = InferenceRunStatus.Failed;
                            break;
                        }

                        if (ProviderAttemptCommitTracker.CommitsProviderAttempt(modelUpdate))
                        {
                            // Semantic updates precede their raw response update, so commitment always
                            // happens before any client-visible Token (including buffered guardrails).
                            classification.ProviderCommitted = true;
                        }

                        if (modelUpdate is ModelCallReasoningUpdate reasoningUpdate
                            && (reasoningUpdate.VisibleText.Length > 0 || reasoningUpdate.HasProtectedData))
                        {
                            ephemeralReasoningSegments.Append(reasoningUpdate);

                            if (streaming
                                && TryProjectClientReasoning(
                                    reasoningUpdate.VisibleText,
                                    clientReasoningOutput,
                                    out ReasoningContentSegment? projectedReasoning))
                            {
                                if (bufferTokens)
                                {
                                    bufferedOutputFrames.AppendReasoning(
                                        reasoningUpdate.ModelCallId,
                                        projectedReasoning);
                                }
                                else
                                {
                                    yield return new IntelligenceEvent(
                                        IntelligenceEventType.Reasoning,
                                        projectedReasoning.Text,
                                        Reasoning: projectedReasoning);
                                }
                            }
                        }

                        if (modelUpdate is ModelCallTextDelta textDelta)
                        {
                            _ = streamAccumulator.Append(textDelta.Text);

                            if (bufferTokens)
                            {
                                bufferedOutputFrames.AppendToken(
                                    textDelta.ModelCallId,
                                    textDelta.Text);
                            }
                            else
                            {
                                yield return new IntelligenceEvent(
                                    IntelligenceEventType.Token,
                                    string.Empty,
                                    textDelta.Text);
                            }

                            continue;
                        }

                        if (modelUpdate is not ModelCallResponseUpdate responseUpdate)
                        {
                            continue;
                        }

                        ChatResponseUpdate update = responseUpdate.Update;

                        roundUpdates.Add(update);
                    }
                }
                finally
                {
                    await streamEnumerator.DisposeAsync().ConfigureAwait(false);
                }
                    }

                if (inferenceError is not null)
                {
                    bool allowNoToolsRestart = streamUsesTools
                        && streamingMoveNextFailure is { Message: var moveMsg }
                        && LooksLikeModelDoesNotSupportTools(moveMsg)
                        && !classification.ProviderCommitted
                        && (streaming ? streamAccumulator.Length == 0 : true);

                    if (allowNoToolsRestart)
                    {
                        logger.LogInformation(
                            "Model does not support tools; retrying without local tools (exception type {ExceptionType}).",
                            streamingMoveNextFailure!.GetType().FullName);

                        if (streaming)
                        {
                            yield return new IntelligenceEvent(
                                IntelligenceEventType.Status,
                                "This model does not support tools; continuing without local tools.");
                        }

                        streamUsesTools = false;

                        streamToolCompatibilityRetry = true;

                        inferenceError = null;

                        streamingMoveNextFailure = null;

                        classification.IsConnectivityFailure = false;

                        streamOuterRestart = true;
                    }
                    else if (!streaming && streamingMoveNextFailure is not null)
                    {
                        logger.LogError(
                            "Hub inference failed; exception type {ExceptionType}.",
                            streamingMoveNextFailure.GetType().FullName);

                        if (!ShouldDeferTurnToNextCandidate(
                                seed,
                                canFallBack,
                                classification,
                                streamAccumulator.Length))
                        {
                            await grimoireTurnWriter
                                .ResolveInterruptedAndMarkFinalizedAsync(
                                    grimoireTurn,
                                    null,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }

                        classification.BufferedTerminal = Result<PromptTurnResult>.Failure(
                            new Error(
                                ErrorCodes.Hub.Error,
                                BuildInferenceFailureMessage(lease, streamingMoveNextFailure)));
                    }

                    break;
                }

                ChatResponse combinedRound = bufferedRoundResponse ?? roundUpdates.ToChatResponse();

                if (streaming)
                {
                    ChatCompletionUsage? streamingUsage = MapUsageDetails(combinedRound.Usage);

                    await RecordProviderCallUsageAsync(
                            streamAccountingLocal,
                            streamingUsage,
                            lease.Provider.Name,
                            targetModel,
                            streamPurpose,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    SubagentExecutionAmbient.Tracker?.ThrowIfExhausted();

                    streamAccumulatedUsage = AccumulateUsage(streamAccumulatedUsage, streamingUsage);
                }

                List<FunctionCallContent> toolCalls = ToolExecutionPipeline.CollectActionableFunctionCalls(combinedRound);
                IReadOnlyList<TextReasoningContent> rawRoundReasoning =
                    CollectRawReasoningContent(combinedRound);

                bool roundHasText = streamAccumulator.Length > 0 || !string.IsNullOrEmpty(combinedRound.Text);

                if (ProviderAttemptCommitTracker.CommitsOnCompleteToolProposal(toolCalls.Count > 0))
                {
                    // Commit before ToolCall yield / authorize.
                    classification.ProviderCommitted = true;
                }
                else if (ProviderAttemptCommitTracker.CommitsOnEmptySuccessfulRound(roundHasText, hasToolCalls: false))
                {
                    classification.ProviderCommitted = true;
                }

                if (toolCalls.Count == 0)
                {
                    streamFinishReason = MapChatFinishReasonToOpenAi(combinedRound.FinishReason);

                    break;
                }

                if (request.ForwardClientTools)
                {
                    int forwardToolCallIndex = 0;

                    foreach (FunctionCallContent fcc in toolCalls)
                    {
                        string argsSnapshot = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(fcc);

                        string toolCallData = ToolExecutionPipeline.FormatToolCallEventData(fcc, argsSnapshot);

                        string callId = toolExecutionPipeline.ResolveCallId(fcc);

                        (observedToolCalls ??= []).Add(new PromptToolCall(callId, fcc.Name ?? string.Empty, argsSnapshot));

                        if (streaming)
                        {
                            yield return new IntelligenceEvent(
                                IntelligenceEventType.ToolCall,
                                fcc.Name ?? string.Empty,
                                toolCallData,
                                null,
                                new IntelligenceToolCallEvent(callId, fcc.Name ?? string.Empty, argsSnapshot, forwardToolCallIndex, PreserveProviderCallId: true));
                        }

                        forwardToolCallIndex++;
                    }

                    streamFinishReason = "tool_calls";

                    break;
                }

                List<ToolLoopProgressEntry> toolLoopProgressEntries = new(
                    toolCalls.Count);

                streamToolRoundCount++;

                ContextMaterializationLedgerAmbient.SetProviderRound(streamToolRoundCount);

                int toolCallIndex = 0;

                Guid? streamAmbientSessionId = grimoireTurn.SessionId ?? request.SessionId;

                SessionAttachmentToolAmbient.CurrentSessionId = streamAmbientSessionId;

                try
                {
                    List<AIContent>? streamPendingAttachmentExtras = null;

                    foreach (FunctionCallContent fcc in toolCalls)
                    {
                        FunctionCallContent invokeFcc = fcc;

                        IHumanPromptReservation? askHumanReservation = null;

                        try
                        {
                            if (IsAskHumanTool(fcc))
                            {
                                if (!humanInteractionAvailable)
                                {
                                    ToolExecutionPipeline.ProcessedToolCall denied = CreateSyntheticAskHumanDenial(
                                        fcc,
                                        AskHumanUnavailableMessage);

                                    toolLoopProgressEntries.Add(
                                        new ToolLoopProgressEntry(
                                            denied.ToolName,
                                            denied.ArgsSnapshot,
                                            denied.ResultText));

                                    if (!streaming)
                                    {
                                        (observedToolCalls ??= []).Add(new PromptToolCall(denied.CallId, denied.ToolName, denied.ArgsSnapshot));

                                        auditContext?.ToolNames.Add(denied.ToolName);

                                        auditContext?.ToolArgumentsJson.Add(denied.ArgsSnapshot);

                                        ToolExecutionPipeline.AppendToolExchangeToMessages(
                                            chatMessages,
                                            fcc,
                                            denied.CallId,
                                            denied.ResultText,
                                            toolCallIndex == 0 ? rawRoundReasoning : null);

                                        await grimoireTurnWriter.TryAppendToolInteractionAsync(
                                            grimoireTurn.SessionId,
                                            denied.ToolName,
                                            denied.ArgsSnapshot,
                                            denied.ResultText,
                                            targetModel,
                                            inferenceToken)
                                            .ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await foreach (IntelligenceEvent denialEvent in EmitSyntheticAskHumanDenialAsync(
                                                           fcc,
                                                           AskHumanUnavailableMessage,
                                                           toolCallIndex,
                                                           chatMessages,
                                                           grimoireTurn,
                                                           targetModel,
                                                           auditContext,
                                                           toolCallIndex == 0 ? rawRoundReasoning : null,
                                                           inferenceToken)
                                                           .ConfigureAwait(false))
                                        {
                                            yield return denialEvent;
                                        }
                                    }

                                    toolCallIndex++;

                                    continue;
                                }

                                askHumanReservation = await humanPromptRegistry
                                    .CreateReservationAsync(inferenceToken)
                                    .ConfigureAwait(false);

                                invokeFcc = PrepareAskHumanFunctionCall(fcc, askHumanReservation.PromptId);
                            }

                            string argsSnapshot = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(invokeFcc);

                            string toolCallData = ToolExecutionPipeline.FormatToolCallEventData(invokeFcc, argsSnapshot);

                            string callId = toolExecutionPipeline.ResolveCallId(invokeFcc);

                            yield return new IntelligenceEvent(
                                IntelligenceEventType.ToolCall,
                                invokeFcc.Name ?? string.Empty,
                                toolCallData,
                                null,
                                new IntelligenceToolCallEvent(callId, invokeFcc.Name ?? string.Empty, argsSnapshot, toolCallIndex));

                            Channel<IntelligenceEvent> liveWards = Channel.CreateUnbounded<IntelligenceEvent>();

                            async Task<ToolExecutionPipeline.ProcessedToolCall> ProcessWithLiveWardsAsync()
                            {
                                try
                                {
                                    return await toolExecutionPipeline
                                        .ProcessSingleToolCallAsync(
                                            invokeFcc,
                                            request,
                                            streamChatOptions,
                                            streamActiveSpell,
                                            grimoireTurn.SessionId?.ToString(),
                                            streamTurnContext,
                                            suppressInvocationFailures: suppressInvocationFailures,
                                            inferenceToken,
                                            liveWardEmit: streaming
                                                ? async (wardEvent, ct) =>
                                                {
                                                    await liveWards.Writer.WriteAsync(wardEvent, ct).ConfigureAwait(false);
                                                }
                                                : null,
                                            observer: streaming
                                                ? async toolEvent =>
                                                {
                                                    // Request-before-wait: surface human-input prompts on the
                                                    // live channel while ask_human is still blocked (ADR 0004).
                                                    if (toolEvent is ToolHumanInputRequestedEvent human)
                                                    {
                                                        await liveWards.Writer
                                                            .WriteAsync(
                                                                new IntelligenceEvent(
                                                                    IntelligenceEventType.Status,
                                                                    human.Prompt,
                                                                    human.CallId),
                                                                inferenceToken)
                                                            .ConfigureAwait(false);
                                                    }
                                                }
                                                : null,
                                            argumentsSnapshot: argsSnapshot,
                                            toolRoundOrdinal: streamToolRoundCount - 1,
                                            callOrdinal: toolCallIndex)
                                        .ConfigureAwait(false);
                                }
                                finally
                                {
                                    liveWards.Writer.TryComplete();
                                }
                            }

                            Task<ToolExecutionPipeline.ProcessedToolCall> processTask = ProcessWithLiveWardsAsync();

                            // Pump wards and elicitation/HITL frames concurrently while the tool runs.
                            // Elicitation emits ask_human-shaped ToolCall frames on the per-turn channel
                            // and blocks until the operator answers — those frames must reach the client
                            // before AwaitReservedAsync can complete.
                            if (streaming)
                            {
                                await foreach (IntelligenceEvent liveEvent in PumpWardAndHumanEventsAsync(
                                                   liveWards.Reader,
                                                   liveHumanPromptChannel?.Reader,
                                                   processTask,
                                                   inferenceToken)
                                                   .ConfigureAwait(false))
                                {
                                    yield return liveEvent;
                                }
                            }

                            ToolExecutionPipeline.ProcessedToolCall processed = await processTask.ConfigureAwait(false);

                            toolLoopProgressEntries.Add(
                                new ToolLoopProgressEntry(
                                    processed.ToolName,
                                    processed.ArgsSnapshot,
                                    processed.ResultText));

                            (observedToolCalls ??= []).Add(new PromptToolCall(processed.CallId, processed.ToolName, processed.ArgsSnapshot));

                            auditContext?.ToolNames.Add(processed.ToolName);

                            auditContext?.ToolArgumentsJson.Add(processed.ArgsSnapshot);

                            ToolExecutionPipeline.AppendToolExchangeToMessages(
                                chatMessages,
                                invokeFcc,
                                processed.CallId,
                                processed.ResultText,
                                toolCallIndex == 0 ? rawRoundReasoning : null);

                            SubagentParentContextInjector.AppendBudgetFailureIfRequired(
                                chatMessages,
                                processed.ToolName,
                                processed.ResultText);

                            if (processed.AdditionalContextContents is { Count: > 0 } extras)
                            {
                                // Defer User extras until every tool result in this round is appended
                                // so providers never see User interleaved mid-tool-transcript.
                                streamPendingAttachmentExtras ??= [];

                                streamPendingAttachmentExtras.AddRange(extras);
                            }

                            if (!processed.ReceiptHandled)
                            {
                                await grimoireTurnWriter.TryAppendToolInteractionAsync(
                                    grimoireTurn.SessionId,
                                    processed.ToolName,
                                    processed.ArgsSnapshot,
                                    processed.ResultText,
                                    targetModel,
                                    inferenceToken)
                                    .ConfigureAwait(false);
                            }

                            foreach (IntelligenceEvent wardEvent in processed.WardEvents)
                            {
                                yield return wardEvent;
                            }

                            if (processed.Failed)
                            {
                                yield return new IntelligenceEvent(
                                    IntelligenceEventType.ToolError,
                                    processed.ToolName,
                                    "Tool invocation failed and was tolerated; a synthetic error result was returned to the model.",
                                    null,
                                    new IntelligenceToolCallEvent(processed.CallId, processed.ToolName, processed.ArgsSnapshot, toolCallIndex));
                            }

                            yield return new IntelligenceEvent(
                                IntelligenceEventType.ToolResult,
                                processed.ToolName,
                                processed.ResultText,
                                null,
                                new IntelligenceToolCallEvent(processed.CallId, processed.ToolName, processed.ArgsSnapshot, toolCallIndex),
                                ToolDenied: processed.Denied);

                            if (processed.AttachmentRefresh is { } refreshDetail)
                            {
                                yield return new IntelligenceEvent(
                                    IntelligenceEventType.AttachmentRefreshed,
                                    "Session attachment source refreshed",
                                    AttachmentRefresh: refreshDetail);
                            }

                            toolCallIndex++;
                        }
                        finally
                        {
                            if (askHumanReservation is not null)
                            {
                                await askHumanReservation.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    }

                    if (streamPendingAttachmentExtras is { Count: > 0 })
                    {
                        // Prefer a User message so vision providers receive DataContent on the next round
                        // (Tool-role messages are a poor carrier for multimodal payload).
                        chatMessages.Add(new MeAiChatMessage(ChatRole.User, streamPendingAttachmentExtras));
                    }
                }
                finally
                {
                    SessionAttachmentToolAmbient.CurrentSessionId = null;
                }

                if (toolLoopProgressDetector.ObserveCompletedRound(
                        toolLoopProgressEntries))
                {

                    Error noProgressError = new(
                        ErrorCodes.Hub.NoProgressDetected,
                        "Tool execution stopped because the latest call/result round repeated a recent round and produced no new evidence.");

                    logger.LogWarning(
                        "Tool loop stopped for no progress; recurring round signature {ProgressSignature}.",
                        toolLoopProgressDetector.RepeatedSignature);

                    inferenceTypedError = noProgressError;

                    inferenceError = noProgressError.Message;

                    streamAccountingStatus = InferenceRunStatus.Failed;

                    break;

                }
            }

            if (streamOuterRestart)
            {
                continue;
            }

            break;
        }

            if (inferenceError is not null)
            {
                if (!streaming)
                {
                    if (classification.BufferedTerminal is null)
                    {
                        classification.BufferedTerminal = Result<PromptTurnResult>.Failure(
                            inferenceTypedError
                            ?? new Error(ErrorCodes.Hub.Error, inferenceError));
                    }
                }

                // W3.5: cleanup must use CancellationToken.None, not the (often already-cancelled)
                // inferenceToken — otherwise ResolveInterruptedAssistantEntryAsync rethrows OCE here
                // and the terminal Error event below is never emitted to the client.
                if (!ShouldDeferTurnToNextCandidate(
                        seed,
                        canFallBack,
                        classification,
                        streamAccumulator.Length))
                {
                    await grimoireTurnWriter.ResolveInterruptedAndMarkFinalizedAsync(
                        grimoireTurn,
                        streamAccumulator.Length > 0 ? streamAccumulator.ToString() : null,
                        CancellationToken.None,
                        covenantScope?.DerivedSensitivity,
                        covenantScope?.StagedCommit()).ConfigureAwait(false);
                }

                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    inferenceTypedError?.Message ?? inferenceError,
                    inferenceTypedError?.Code);

                yield break;
            }

        string finalText = streamAccumulator.ToString();

        IReadOnlyList<string> streamWarnings = [];

        if (request.ResponseFormat is "json_schema"
            && request.ResponseFormatJsonSchema is { } streamJsonSchemaWrapper
            && structuredOutput.Enabled)
        {
            JsonElement streamSchemaElement = streamJsonSchemaWrapper;

            if (streamJsonSchemaWrapper.ValueKind == JsonValueKind.Object
                && streamJsonSchemaWrapper.TryGetProperty("schema", out JsonElement streamNestedSchema))
            {
                streamSchemaElement = streamNestedSchema;
            }

            using JsonDocument streamSchema = JsonDocument.Parse(streamSchemaElement.GetRawText());
            bool useRetryValidator = !streaming
                || structuredOutput.StrictMode;

            if (useRetryValidator)
            {
                if (!streaming && bufferedFinalResponse is null)
                {
                    classification.BufferedTerminal = Result<PromptTurnResult>.Failure(
                        new Error(ErrorCodes.Hub.Error, PublicInferenceFailureMessage));

                    yield break;
                }

                int schemaMaxDepth = ArcanumSettingClamps.JsonSchemaMaxDepth(
                    structuredOutput.SchemaMaxDepth);

                int contextWindowLimit = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);

                Func<string, int> estimateTokenCount = text =>
                    ModelTokenEstimator
                        .EstimateText(lease.Provider, lease.ResolvedModel, text)
                        .TokenCount;

                ChatResponse responseForValidation = bufferedFinalResponse
                    ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, finalText));

                Result<StructuredOutputResult> validationResult;
                try
                {
                    validationResult = await structuredOutputValidator
                        .ValidateAndRetryAsync(
                        responseForValidation,
                        streamSchema,
                        structuredOutput.StrictMode,
                        schemaMaxDepth,
                        contextWindowLimit,
                        estimateTokenCount,
                        async (errorMessage, ct) =>
                        {
                            chatMessages.Add(new ChatMessage(ChatRole.Assistant, responseForValidation.Text));

                            chatMessages.Add(new ChatMessage(ChatRole.System, errorMessage));

                            ChatOptions retryOptions = lastInferenceChatOptions
                                ?? CreateInferenceChatOptions(
                                    streamUsesTools,
                                    streamTurnContext.InferenceTools,
                                    request,
                                    lease);
                            Result retryContextGate = EnsureContextBudgetWithMaterializations(
                                chatMessages,
                                retryOptions,
                                lease,
                                request,
                                streamMaterializationLedger,
                                RemoveSemanticMaterialization,
                                out ContextTokenBreakdown retryContextBreakdown);

                            if (retryContextGate.IsFailure)
                            {

                                throw new ModelCallAdmissionException(retryContextGate.Error);

                            }

                            Result retryReservation = await streamAccountingLocal
                                .EnsureReservationForContextAsync(
                                    budgetReservationService,
                                    settings.Value.ResolvePricing(),
                                    targetModel,
                                    retryContextBreakdown,
                                    ct)
                                .ConfigureAwait(false);
                            if (retryReservation.IsFailure)
                            {
                                throw new ModelCallAdmissionException(retryReservation.Error);
                            }

                            PromptCachePlan retryPromptCachePlan = BuildPromptCachePlan(
                                lease,
                                streamSystemPromptDocument,
                                retryOptions,
                                PromptCacheSemanticNamespace.Main);
                            ModelCallOutcome retryCall = await ModelCallExecutor
                                .ExecuteBufferedAsync(
                                    chatClient,
                                    chatMessages,
                                    retryOptions,
                                    streamAccountingLocal.Budget,
                                    ModelCallPurpose.StructuredOutputRetry,
                                    ct,
                                    BuildModelCallContext(
                                        lease,
                                        request,
                                        retryContextBreakdown,
                                        retryPromptCachePlan))
                                .ConfigureAwait(false);

                            if (retryCall.IsFailure)
                            {
                                throw retryCall.Failure.Cause
                                    ?? new ModelCallAdmissionException(retryCall.Error);
                            }

                            ModelCallResult retryResult = retryCall.Value;

                            if (retryResult.ContextBreakdown is { } retryBreakdown)
                            {
                                contextBreakdownsByCall[retryResult.ModelCallId] = retryBreakdown;
                            }

                            ChatResponse retryResponse = retryResult.Response;

                            ChatCompletionUsage? retryUsage = MapUsageDetails(retryResponse.Usage);

                            await RecordProviderCallUsageAsync(
                                    streamAccountingLocal,
                                    retryUsage,
                                    lease.Provider.Name,
                                    targetModel,
                                    ModelCallPurpose.StructuredOutputRetry,
                                    CancellationToken.None)
                                .ConfigureAwait(false);

                            SubagentExecutionAmbient.Tracker?.ThrowIfExhausted();

                            if (ProviderAttemptCommitTracker.CommitsProviderAttempt(retryResult.Reasoning))
                            {
                                classification.ProviderCommitted = true;
                            }

                            ephemeralReasoningSegments.Clear();

                            foreach (ModelCallReasoningSegment reasoning in retryResult.Reasoning.Segments)
                            {
                                ephemeralReasoningSegments.Append(reasoning);
                            }

                            responseForValidation = retryResponse;

                            bufferedFinalResponse = retryResponse;

                            if (streaming)
                            {
                                bufferedOutputFrames.Clear();
                                AppendReplacementFramesInContentOrder(
                                    bufferedOutputFrames,
                                    retryResult,
                                    clientReasoningOutput);
                                replacementOutputPreservesContentOrder = true;
                            }

                            streamAccumulatedUsage = AccumulateUsage(
                                streamAccumulatedUsage,
                                retryUsage);

                            return retryResponse;
                        },
                        inferenceToken)
                        .ConfigureAwait(false);
                }
                catch (ModelCallAdmissionException ex)
                {
                    validationResult = Result<StructuredOutputResult>.Failure(ex.Error);
                }

                if (validationResult.IsFailure)
                {
                    if (!streaming)
                    {
                        classification.BufferedTerminal = Result<PromptTurnResult>.Failure(validationResult.Error);
                    }
                    else
                    {
                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            validationResult.Error.Message,
                            validationResult.Error.Code);
                    }

                    yield break;
                }

                bufferedFinalResponse = validationResult.Value.Response;

                finalText = bufferedFinalResponse.Text ?? finalText;

                streamAccumulator.Clear();

                _ = streamAccumulator.Append(finalText);

                streamWarnings = validationResult.Value.Warnings;
            }
            else
            {
                Result<JsonSchemaDefinition> streamParseResult = JsonSchemaHelper.Parse(
                    streamSchema,
                    ArcanumSettingClamps.JsonSchemaMaxDepth(
                        structuredOutput.SchemaMaxDepth));

                if (streamParseResult.IsSuccess)
                {
                    ValidationResult streamValidation = JsonSchemaHelper.Validate(
                        finalText,
                        streamParseResult.Value,
                        ArcanumSettingClamps.JsonSchemaMaxDepth(
                            structuredOutput.SchemaMaxDepth));

                    if (!streamValidation.IsValid)
                    {
                        if (structuredOutput.StrictMode)
                        {
                            yield return new IntelligenceEvent(
                                IntelligenceEventType.Error,
                                "Streamed response failed JSON schema validation after generation: "
                                    + string.Join("; ", streamValidation.Errors),
                                ErrorCodes.StructuredOutput.ValidationFailed);

                            yield break;
                        }

                        streamWarnings = ["streamed response failed JSON schema validation: " + string.Join("; ", streamValidation.Errors)];
                    }
                }
                else
                {
                    if (structuredOutput.StrictMode)
                    {
                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            "Invalid JSON schema for streamed structured output: " + streamParseResult.Error.Message,
                            ErrorCodes.StructuredOutput.SchemaInvalid);

                        yield break;
                    }

                    streamWarnings = ["invalid JSON schema for streamed structured output: " + streamParseResult.Error.Message];
                }
            }
        }

        IReadOnlyList<ReasoningContentSegment> clientReasoningSegments =
            ProjectClientReasoningSegments(
                ephemeralReasoningSegments.Materialize(),
                clientReasoningOutput);

        string safetyInspectionText = replacementOutputPreservesContentOrder
            ? BuildSafetyInspectionText(bufferedOutputFrames.Materialize())
            : BuildSafetyInspectionText(finalText, clientReasoningSegments);

        Result guardrailsStreamOutput = await FilterGuardrailsOutputAsync(
            safetyInspectionText,
            grimoireTurn.SessionId,
            targetModel,
            inferenceToken).ConfigureAwait(false);

        if (guardrailsStreamOutput.IsFailure)
        {
            if (!streaming)
            {
                classification.BufferedTerminal = Result<PromptTurnResult>.Failure(guardrailsStreamOutput.Error);
            }

            await grimoireTurnWriter
                .ResolveInterruptedAndMarkFinalizedAsync(grimoireTurn, null, CancellationToken.None)
                .ConfigureAwait(false);

            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                guardrailsStreamOutput.Error.Message,
                guardrailsStreamOutput.Error.Code);

            yield break;
        }

        if (streaming && bufferTokens)
        {
            foreach (IntelligenceEvent bufferedOutputFrame in bufferedOutputFrames.Materialize())
            {
                yield return bufferedOutputFrame;
            }

            if (finalText.Length > 0
                && !bufferedOutputFrames.HasToken)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Token,
                    string.Empty,
                    finalText);
            }
        }

        // The one arm that publishes a staged Covenant batch. It is the only place the answer this
        // turn actually produced and the profile change staged against it can be written together,
        // which is what the batch's atomicity contract means in practice.
        CovenantTurnCommitBinding? covenantCommit = covenantScope?.StagedCommit();

        bool finalizeOk = streaming
            ? await grimoireTurnWriter
                .TryFinalizeStreamedAssistantEntryAsync(
                    grimoireTurn,
                    finalText,
                    targetModel,
                    inferenceToken,
                    covenantScope?.DerivedSensitivity,
                    covenantCommit)
                .ConfigureAwait(false)
            : await grimoireTurnWriter
                .TryFinalizeBufferedAssistantEntryAsync(
                    grimoireTurn,
                    finalText,
                    targetModel,
                    inferenceToken,
                    covenantScope?.DerivedSensitivity,
                    covenantCommit)
                .ConfigureAwait(false);

        if (!finalizeOk)
        {
            if (!streaming)
            {
                classification.BufferedTerminal = Result<PromptTurnResult>.Failure(
                    new Error(ErrorCodes.Hub.Error, GrimoireTurnWriter.PublicFinalizeFailureMessage));
            }

            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                GrimoireTurnWriter.PublicFinalizeFailureMessage);

            yield break;
        }

        await TryIncrementSessionTokensAsync(
                grimoireTurn.SessionId,
                streamAccumulatedUsage,
                targetModel,
                streamAccountingLocal.RunId.HasValue
                    ? streamAccountingLocal.AccumulatedCostUsd
                    : null,
                inferenceToken)
            .ConfigureAwait(false);

        IReadOnlyList<AttachmentMemoryProvenance> attachmentProvenance =
            AttachmentMemoryGateAmbient.Snapshot();

        await TryRecordAttachmentConsultationsAsync(
            grimoireTurn.AssistantEntryId,
            attachmentProvenance,
            inferenceToken).ConfigureAwait(false);

        TryEnqueueSagaExtraction(
            grimoireTurn.SessionId,
            attachmentProvenance,
            AttachmentMemoryGateAmbient.HasUnprovenancedAttachmentContent);

        string usageData = streamAccumulatedUsage?.TotalTokens.ToString(CultureInfo.InvariantCulture) ?? "0";

        RecordInferenceMetrics(lease.Provider, targetModel, inferenceStopwatch.Elapsed, streamAccumulatedUsage);

        if (auditContext is not null)
        {
            await TryLogInferenceAuditAsync(
                auditContext,
                grimoireTurn.SessionId,
                lease.Provider.Name,
                targetModel,
                streamAccumulatedUsage,
                streamFinishReason ?? "stop",
                streamActiveSpell?.Name,
                request.CampaignId,
                inferenceStopwatch.Elapsed,
                [.. contextBreakdownsByCall.Values],
                CancellationToken.None).ConfigureAwait(false);
        }

        streamAccountingStatus = InferenceRunStatus.Completed;

        if (!streaming)
        {
            classification.BufferedTerminal = Result<PromptTurnResult>.Success(
                new PromptTurnResult(
                    finalText,
                    streamAccumulatedUsage,
                    observedToolCalls?.ToList(),
                    streamFinishReason ?? "stop")
                {
                    Warnings = streamWarnings,
                    Reasoning = clientReasoningSegments,
                });

            yield break;
        }

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            "Complete",
            usageData,
            streamAccumulatedUsage,
            FinishReason: streamFinishReason ?? "stop")
        {
            Warnings = streamWarnings
        };
        }
        finally
        {
            streamCovenantStaging?.Dispose();

            HumanPromptLiveEmitterAmbient.Current = previousHumanPromptEmitter;

            liveHumanPromptChannel?.Writer.TryComplete();

            SessionAttachmentTurnBudget.EndTurn();

            ContextMaterializationLedgerAmbient.End();

            if (publishedStreamAmbient)
            {
                TurnAccountingAmbient.Clear();
            }

            if (streamAccounting is not null && streamAccounting.OwnsLifecycle)
            {
                try
                {
                    await streamAccounting.CompleteAsync(
                            turnRunWriter,
                            budgetReservationService,
                            streamAccountingStatus,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        "Failed to complete turn accounting for streamed inference; exception type {ExceptionType}, session {SessionId}.",
                        ex.GetType().FullName,
                        grimoireTurn.SessionId);
                }
            }

            if (!ShouldDeferTurnToNextCandidate(
                    seed,
                    canFallBack,
                    classification,
                    streamAccumulator.Length))
            {
                // Reached after a failed finalize as well as after a genuine interrupt, and the
                // handle is still unfinalized in both. Passing the staged batch is what stops this
                // cleanup from committing the answer a moment after the atomic arm refused to.
                await grimoireTurnWriter
                    .TryResolveInterruptedOnStreamExitAsync(
                        grimoireTurn,
                        streamAccumulator.Length > 0 ? streamAccumulator.ToString() : null,
                        covenantScope?.DerivedSensitivity,
                        covenantScope?.StagedCommit())
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<ToolExecutionPipeline.TurnContext> BuildTurnContextAsync(
        PingRequest request,
        IReadOnlyList<AITool> toolSet,
        bool humanInteractionAvailable,
        GrimoireTurnWriter.TurnHandle grimoireTurn,
        string targetModel,
        IReadOnlySet<Guid>? visibleAttachmentIds,
        ArcanumInvocationContext? invocation,
        CancellationToken cancellationToken)
    {
        Campaign? campaign = null;

        string? campaignId = null;

        string? workspaceRoot = null;

        bool sanctumEnabled = false;

        SanctumMode sanctumMode = SanctumMode.Strict;

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            campaign = await campaignRepository
                .GetByPathAsync(request.WorkingDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (campaign is not null)
            {
                campaignId = campaign.Id.ToString();

                workspaceRoot = campaign.Path;

                SanctumConfig sanctumConfig = CampaignRepository.GetSanctumConfig(campaign);

                sanctumEnabled = sanctumConfig.Enabled;

                sanctumMode = sanctumConfig.Mode;
            }
        }

        IReadOnlyList<AITool> inferenceTools = ApplyToolPolicyFilters(request, toolSet);

        inferenceTools = request.UnattendedMode
            ? FilterToolsForUnattended(inferenceTools)
            : inferenceTools;

        inferenceTools = FilterAskHumanUnlessAvailable(inferenceTools, humanInteractionAvailable);

        // W3.5: capture every spell-script root (active spell + resonant dependencies) so the Sanctum
        // preflight validates each candidate path the tool may resolve, not just the active spell's.
        IReadOnlyList<string> spellScriptRoots = toolSet
            .OfType<ArcanumSpellScriptTool>()
            .FirstOrDefault()?.ScriptRoots ?? [];

        return new ToolExecutionPipeline.TurnContext
        {
            Campaign = campaign,
            CampaignId = campaignId,
            WorkspaceRoot = workspaceRoot,
            PersistedSessionId = grimoireTurn.SessionId,
            AssistantEntryId = grimoireTurn.AssistantEntryId,
            InvocationId = grimoireTurn.AssistantEntryId?.ToString("D"),
            ModelUsed = targetModel,
            Invocation = invocation,
            SanctumEnabled = sanctumEnabled,
            SanctumMode = sanctumMode,
            InferenceTools = inferenceTools,
            VisibleAttachmentIds = visibleAttachmentIds,
            SpellScriptRoots = spellScriptRoots,
        };
    }

    private static IReadOnlyList<AITool> FilterAskHumanUnlessAvailable(
        IReadOnlyList<AITool> tools,
        bool humanInteractionAvailable)
    {
        if (humanInteractionAvailable)
        {
            return tools;
        }

        var filtered = new List<AITool>(tools.Count);

        foreach (AITool tool in tools)
        {
            if (tool is AIFunction function && string.Equals(function.Name, "ask_human", StringComparison.Ordinal))
            {
                continue;
            }

            filtered.Add(tool);
        }

        return filtered;
    }

    private static IReadOnlyList<AITool> FilterToolsForUnattended(IReadOnlyList<AITool> tools)
    {
        var filtered = new List<AITool>(tools.Count);

        foreach (AITool tool in tools)
        {
            if (tool is AIFunction function && string.Equals(function.Name, "ask_human", StringComparison.Ordinal))
            {
                continue;
            }

            filtered.Add(tool);
        }

        return filtered;
    }

    private static IReadOnlyList<TextReasoningContent> CollectRawReasoningContent(ChatResponse response)
    {
        List<TextReasoningContent> reasoningContents = [];

        foreach (ChatMessage message in response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    reasoningContents.Add(reasoning);
                }
            }
        }

        return reasoningContents;
    }

    private static bool IsAskHumanTool(FunctionCallContent fcc) =>
        string.Equals(fcc.Name, "ask_human", StringComparison.Ordinal);

    private sealed class ChannelHumanPromptLiveEmitter(Channel<IntelligenceEvent> channel) : IHumanPromptLiveEmitter
    {
        public ValueTask EmitAsync(IntelligenceEvent evt, CancellationToken cancellationToken) =>
            channel.Writer.WriteAsync(evt, cancellationToken);
    }

    /// <summary>
    /// Yields ward + human-prompt frames until <paramref name="processTask"/> completes and the
    /// ward channel is finished. The human channel is per-turn and is not completed here.
    /// </summary>
    private static async IAsyncEnumerable<IntelligenceEvent> PumpWardAndHumanEventsAsync(
        ChannelReader<IntelligenceEvent> wards,
        ChannelReader<IntelligenceEvent>? humans,
        Task processTask,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            while (wards.TryRead(out IntelligenceEvent? ward) && ward is not null)
            {
                yield return ward;
            }

            if (humans is not null)
            {
                while (humans.TryRead(out IntelligenceEvent? human) && human is not null)
                {
                    yield return human;
                }
            }

            if (processTask.IsCompleted && wards.Completion.IsCompleted)
            {
                while (wards.TryRead(out IntelligenceEvent? ward) && ward is not null)
                {
                    yield return ward;
                }

                if (humans is not null)
                {
                    while (humans.TryRead(out IntelligenceEvent? human) && human is not null)
                    {
                        yield return human;
                    }
                }

                yield break;
            }

            List<Task> waits = [processTask, wards.WaitToReadAsync(cancellationToken).AsTask()];
            if (humans is not null)
            {
                waits.Add(humans.WaitToReadAsync(cancellationToken).AsTask());
            }

            _ = await Task.WhenAny(waits).ConfigureAwait(false);

            if (processTask.IsCompleted)
            {
                // Surface tool failures later via await processTask; ignore here.
                try
                {
                    await processTask.ConfigureAwait(false);
                }
                catch
                {
                    // ignored — caller awaits processTask after the pump
                }
            }
        }
    }

    private static FunctionCallContent PrepareAskHumanFunctionCall(FunctionCallContent fcc, string hostPromptId)
    {
        string question = ExtractAskHumanQuestion(fcc);

        Dictionary<string, object?> preparedArgs = new(StringComparer.Ordinal)
        {
            ["question"] = question,
            ["promptId"] = hostPromptId,
        };

        // Preserve provider CallId; only promptId is host-owned.
        return new FunctionCallContent(fcc.CallId ?? string.Empty, fcc.Name, preparedArgs);
    }

    private static string ExtractAskHumanQuestion(FunctionCallContent fcc)
    {
        if (fcc.Arguments is null || fcc.Arguments.Count == 0)
        {
            return string.Empty;
        }

        if (!fcc.Arguments.TryGetValue("question", out object? raw)
            && !fcc.Arguments.TryGetValue("Question", out raw))
        {
            return string.Empty;
        }

        return raw switch
        {
            null => string.Empty,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? string.Empty,
            JsonElement je => je.ToString(),
            _ => raw.ToString() ?? string.Empty,
        };
    }

    private ToolExecutionPipeline.ProcessedToolCall CreateSyntheticAskHumanDenial(
        FunctionCallContent fcc,
        string message)
    {
        string callId = toolExecutionPipeline.ResolveCallId(fcc);

        string argsSnapshot = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(fcc);

        return new ToolExecutionPipeline.ProcessedToolCall(
            callId,
            fcc.Name ?? "ask_human",
            argsSnapshot,
            message,
            [],
            Failed: true);
    }

    private async IAsyncEnumerable<IntelligenceEvent> EmitSyntheticAskHumanDenialAsync(
        FunctionCallContent fcc,
        string message,
        int toolCallIndex,
        List<MeAiChatMessage> chatMessages,
        GrimoireTurnWriter.TurnHandle grimoireTurn,
        string targetModel,
        InferenceAuditContext? auditContext,
        IReadOnlyList<TextReasoningContent>? reasoningContents,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ToolExecutionPipeline.ProcessedToolCall denied = CreateSyntheticAskHumanDenial(fcc, message);

        auditContext?.ToolNames.Add(denied.ToolName);

        auditContext?.ToolArgumentsJson.Add(denied.ArgsSnapshot);

        ToolExecutionPipeline.AppendToolExchangeToMessages(
            chatMessages,
            fcc,
            denied.CallId,
            denied.ResultText,
            reasoningContents);

        await grimoireTurnWriter.TryAppendToolInteractionAsync(
            grimoireTurn.SessionId,
            denied.ToolName,
            denied.ArgsSnapshot,
            denied.ResultText,
            targetModel,
            cancellationToken)
            .ConfigureAwait(false);

        // Do not emit ToolCall — no waiter was registered, so clients must not try to answer.
        // toolCall.argumentsJson carries the serialized call arguments on every frame; the denial
        // text is the human-readable failure and rides on Data, exactly as a tolerated tool failure
        // does.
        yield return new IntelligenceEvent(
            IntelligenceEventType.ToolError,
            denied.ToolName,
            message,
            null,
            new IntelligenceToolCallEvent(denied.CallId, denied.ToolName, denied.ArgsSnapshot, toolCallIndex));

        yield return new IntelligenceEvent(
            IntelligenceEventType.ToolResult,
            denied.ToolName,
            denied.ResultText,
            null,
            new IntelligenceToolCallEvent(denied.CallId, denied.ToolName, denied.ArgsSnapshot, toolCallIndex));
    }

    private async Task<Result<ResolvedSpell?>> ResolveRoutedSpellAsync(
        PingRequest request,
        IChatClient chatClient,
        ProviderSettings mainProvider,
        string mainModel,
        string? spellWorkspaceRoot,
        CancellationToken cancellationToken,
        Action<string, int>? observeEmbedding = null)
    {
        long maxSpellFileSizeBytes = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings.Value);

        if (!string.IsNullOrWhiteSpace(request.OverrideSpellPath))
        {
            Result<string> validated = SpellPathPolicy.ValidateOverrideSpellPath(
                request.OverrideSpellPath,
                spellWorkspaceRoot);

            if (validated.IsFailure)
            {
                return Result<ResolvedSpell?>.Failure(validated.Error);
            }

            ParsedSpell? overrideSpell = await SpellScanner
                .LoadFullAsync(validated.Value, cancellationToken, maxSpellFileSizeBytes)
                .ConfigureAwait(false);

            if (overrideSpell is null)
            {
                return Result<ResolvedSpell?>.Success(null);
            }

            ResolvedSpell resolvedOverride = await SpellDependencyResolver
                .ResolveAsync(
                    overrideSpell,
                    spellWorkspaceRoot,
                    maxSpellFileSizeBytes,
                    cancellationToken,
                    logger)
                .ConfigureAwait(false);

            return Result<ResolvedSpell?>.Success(resolvedOverride);
        }

        IReadOnlyList<SpellMetadata> spellMetadata = await SpellScanner
            .ScanMetadataAsync(
                spellWorkspaceRoot,
                cancellationToken,
                maxSpellFileSizeBytes,
                ArcanumSettingClamps.MetadataScanCacheTtlSeconds(
                    ArcanumRuntimeDefaults.Spells.MetadataScanCacheTtlSeconds))
            .ConfigureAwait(false);

        SpellMetadata? matchedMetadata;

        IReadOnlyList<string> routerEntities = Array.Empty<string>();

        if (!string.IsNullOrWhiteSpace(request.OverrideSpellName))
        {
            if (!TryResolveOverrideSpellMetadata(request.OverrideSpellName, spellMetadata, out SpellMetadata? overridePick))
            {
                return Result<ResolvedSpell?>.Failure(
                    new Error(
                        ErrorCodes.Validation.SpellOverride,
                        $"No spell matches OverrideSpellName '{request.OverrideSpellName.Trim()}'. Expected a SPELL.md frontmatter name or parent folder name."));
            }

            matchedMetadata = overridePick;
        }
        else
        {
            string semanticProbe = GetSemanticRouterUserProbe(request);

            SpellRoutingDecision routingDecision = await semanticSpellRouter
                .ResolveAsync(spellMetadata, semanticProbe, cancellationToken, observeEmbedding)
                .ConfigureAwait(false);

            if (routingDecision.Mode == SpellRoutingDecisionMode.DirectResonance)
            {
                // RAG Phase 5 — pure embedding mode already resolved (or ruled out) an active spell by
                // vector similarity alone; no LLM call is made, so no router-extracted entities.
                matchedMetadata = routingDecision.ResolvedSpell;
            }
            else
            {
                TimeSpan spellPreflight = TimeSpan.FromSeconds(
                    ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds(
                        settings.Value.ResolveIntelligence().SemanticRouterPreflightTimeoutSeconds));

                int routerMaxTokens = ArcanumSettingClamps.SemanticRouterMaxTokens(
                    settings.Value.ResolveIntelligence().SemanticRouterMaxTokens);

                float routerTemperature = ArcanumSettingClamps.SemanticRouterTemperature(
                    settings.Value.ResolveIntelligence().SemanticRouterTemperature);

                IChatClient routerClient = chatClient;

                ChatClientLease? routerLease = null;

                // RAG Phase 5 — FilteredDivination carries a pre-filtered top-K candidate list (hybrid
                // mode); FullGrimoire (disabled, or any Phase 5 fallback) passes null, which is
                // SemanticRouter's unchanged "use the full catalog" behavior.
                IReadOnlyList<SpellMetadata>? candidates = routingDecision.Mode == SpellRoutingDecisionMode.FilteredDivination
                    ? routingDecision.Candidates
                    : null;

                try
                {
                    if (settings.Value.ResolveIntelligence().UseFastModelForSpellRouting
                        && !string.IsNullOrWhiteSpace(settings.Value.FastModel))
                    {
                        routerLease = await chatClientFactory
                            .ResolveClientAsync(settings.Value.FastModel.Trim(), cancellationToken)
                            .ConfigureAwait(false);

                        routerClient = routerLease.ChatClient;
                    }

                    SemanticSpellRoutingResult? routingResult = await SemanticRouter
                        .DetermineActiveSpellAsync(
                            routerClient,
                            semanticProbe,
                            spellMetadata,
                            spellPreflight,
                            routerMaxTokens,
                            routerTemperature,
                            cancellationToken,
                            logger,
                            candidates,
                            ModelCallExecutor,
                            new ModelCallContext(
                                routerLease?.Provider ?? mainProvider,
                                routerLease?.ResolvedModel ?? mainModel,
                                ReservedAnswerTokens: routerMaxTokens,
                                ReservedReasoningTokens: 0,
                                PromptCachePlan: PromptCachePlan.NonCacheable(
                                    (routerLease?.Provider ?? mainProvider).Name,
                                    routerLease?.ResolvedModel ?? mainModel,
                                    PromptCacheSemanticNamespace.SpellRouting,
                                    PromptCacheEligibility.NonCacheable,
                                    PromptCacheNonEligibilityReason.AuxiliaryPurpose)))
                        .ConfigureAwait(false);

                    matchedMetadata = routingResult?.Spell;

                    routerEntities = routingResult?.Entities ?? Array.Empty<string>();

                    (string routingProvider, string routingModel) = ResolveAuxiliaryAccountingIdentity(
                        routerLease?.Provider.Name,
                        routerLease?.ResolvedModel,
                        mainProvider.Name,
                        mainModel);

                    await TryRecordAuxiliaryUsageAsync(
                            routingResult?.Usage,
                            BillableOperationType.Routing,
                            purpose: "routing",
                            provider: routingProvider,
                            model: routingModel)
                        .ConfigureAwait(false);
                }
                finally
                {
                    routerLease?.Dispose();
                }
            }
        }

        if (matchedMetadata is null)
        {
            return Result<ResolvedSpell?>.Success(
                routerEntities.Count == 0
                    ? null
                    : ResolvedSpell.EntitiesOnly(routerEntities));
        }

        ParsedSpell? activeSpell = await SpellScanner
            .LoadFullAsync(matchedMetadata.FilePath, cancellationToken, maxSpellFileSizeBytes)
            .ConfigureAwait(false);

        if (activeSpell is null)
        {
            return Result<ResolvedSpell?>.Success(
                routerEntities.Count == 0
                    ? null
                    : ResolvedSpell.EntitiesOnly(routerEntities));
        }

        ResolvedSpell resolved = await SpellDependencyResolver
            .ResolveAsync(
                activeSpell,
                spellWorkspaceRoot,
                maxSpellFileSizeBytes,
                cancellationToken,
                logger,
                spellMetadata)
            .ConfigureAwait(false);

        return Result<ResolvedSpell?>.Success(resolved with { Entities = routerEntities });
    }

    /// <summary>
    /// Lexicon retrieval gate. Skips only true internal headless tasks (Campaign Logger
    /// summarization, Saga extraction) that set all three of <c>SkipSpellRouting</c>,
    /// <c>DisableMcpTools</c>, and <c>UnattendedMode</c>. User-facing turns — including
    /// <c>OverrideSpellName</c>, pure embedding spell routing, and no-spell paths — still retrieve
    /// Lexicon context via the fallback extractor when the router supplied no entities.
    /// </summary>
    private static bool ShouldUseLexiconForTurn(PingRequest request)
    {
        if (request.SkipSpellRouting && request.DisableMcpTools && request.UnattendedMode)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Best-effort Lexicon retrieval for the current inference turn. Uses router-extracted entities
    /// when present; otherwise runs <see cref="LexiconEntityExtractor"/> on the fast model (when
    /// configured) for non-router paths. Failures (DB or extractor) are logged and swallowed —
    /// Lexicon never fails the inference turn. Returns null when disabled, gated out, or no matches.
    /// </summary>
    private async Task<IReadOnlyList<LexiconEntryDto>?> RetrieveLexiconEntriesAsync(
        PingRequest request,
        IReadOnlyList<string> routerEntities,
        IChatClient chatClient,
        ProviderSettings mainProvider,
        string mainModel,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.ResolveIntelligence().EnableLexiconSystem)
        {
            return null;
        }

        if (!ShouldUseLexiconForTurn(request))
        {
            return null;
        }

        IReadOnlyList<string> entities = routerEntities;

        if (entities.Count == 0)
        {
            // Unseen Servant injects Previous State into the kickoff user prompt and bypasses the
            // SemanticRouter via OverrideSpellName — skip a redundant LexiconEntityExtractor LLM call.
            if (request.UnattendedMode && !string.IsNullOrWhiteSpace(request.OverrideSpellName))
            {
                return null;
            }

            IChatClient extractorClient = chatClient;

            ChatClientLease? extractorLease = null;

            try
            {
                if (settings.Value.ResolveIntelligence().UseFastModelForSpellRouting
                    && !string.IsNullOrWhiteSpace(settings.Value.FastModel))
                {
                    extractorLease = await chatClientFactory
                        .ResolveClientAsync(settings.Value.FastModel.Trim(), cancellationToken)
                        .ConfigureAwait(false);

                    extractorClient = extractorLease.ChatClient;
                }

                TimeSpan preflight = TimeSpan.FromSeconds(
                    ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds(
                        settings.Value.ResolveIntelligence().SemanticRouterPreflightTimeoutSeconds));

                (IReadOnlyList<string> extracted, UsageDetails? extractionUsage) = await LexiconEntityExtractor
                    .ExtractAsync(
                        extractorClient,
                        GetSemanticRouterUserProbe(request),
                        preflight,
                        cancellationToken,
                        logger,
                        ModelCallExecutor,
                        new ModelCallContext(
                            extractorLease?.Provider ?? mainProvider,
                            extractorLease?.ResolvedModel ?? mainModel,
                            ReservedAnswerTokens: LexiconEntityExtractor.MaxOutputTokens,
                            ReservedReasoningTokens: 0,
                            PromptCachePlan: PromptCachePlan.NonCacheable(
                                (extractorLease?.Provider ?? mainProvider).Name,
                                extractorLease?.ResolvedModel ?? mainModel,
                                PromptCacheSemanticNamespace.Main,
                                PromptCacheEligibility.NonCacheable,
                                PromptCacheNonEligibilityReason.AuxiliaryPurpose)))
                    .ConfigureAwait(false);

                entities = extracted;

                (string extractionProvider, string extractionModel) = ResolveAuxiliaryAccountingIdentity(
                    extractorLease?.Provider.Name,
                    extractorLease?.ResolvedModel,
                    mainProvider.Name,
                    mainModel);

                await TryRecordAuxiliaryUsageAsync(
                        extractionUsage,
                        BillableOperationType.Extraction,
                        purpose: "lexicon",
                        provider: extractionProvider,
                        model: extractionModel)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException
                && TurnAccountingAmbient.Current?.AccountingFailed != true)
            {
                logger.LogWarning(
                    "Lexicon entity extraction failed; continuing without Lexicon context (exception type {ExceptionType}).",
                    ex.GetType().FullName);

                return null;
            }
            finally
            {
                extractorLease?.Dispose();
            }
        }

        if (entities.Count == 0)
        {
            return null;
        }

        int limit = ArcanumSettingClamps.LexiconMaxMatchedEntries(
            settings.Value.ResolveIntelligence().LexiconMaxMatchedEntries);

        // The tier resolved first is the turn's own Campaign, then the global one. With the gate off
        // this is the global tier alone, which is every match this surface has ever made.
        LexiconScope lexiconScope = MemoryScope
            .Resolve(
                settings.Value.Features.CampaignScopedMemory,
                invocationContext.Campaign?.CampaignId)
            .ToLexiconScope();

        Result<IReadOnlyList<LexiconEntryDto>> match = await lexiconService
            .MatchEntitiesAsync(entities, limit, lexiconScope, cancellationToken)
            .ConfigureAwait(false);

        if (match.IsFailure)
        {
            logger.LogWarning("Lexicon retrieval failed ({ErrorCode}); continuing without Lexicon context.", match.Error.Code);

            return null;
        }

        return match.Value.Count == 0 ? null : match.Value;
    }

    private static string GetSemanticRouterUserProbe(PingRequest request)
    {
        if (!InferenceContextBuilder.HasStatelessMessages(request))
        {
            return request.Prompt;
        }

        IReadOnlyList<CoreChatMessage> msgs = request.StatelessMessages!;

        for (int i = msgs.Count - 1; i >= 0; i--)
        {
            CoreChatMessage m = msgs[i];

            if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(m.Content))
            {
                return m.Content;
            }
        }

        for (int i = msgs.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(msgs[i].Content))
            {
                return msgs[i].Content;
            }
        }

        return "\u200b";
    }

    /// <summary>
    /// RAG Phases 3-4 — embeds the current turn's probe prompt at most once, shared between
    /// <see cref="RetrieveSemanticContextAsync"/> (Phase 3) and <see cref="RetrieveSagaMemoriesAsync"/>
    /// (Phase 4) so a single turn never pays for the same embedding twice. Returns <c>null</c> when
    /// neither feature needs an embedding this turn (both disabled), or when embedding fails for any
    /// reason — callers treat <c>null</c> as "skip RAG retrieval for this turn", never as an error.
    /// </summary>
    private async Task<Embedding<float>?> ResolveRagQueryEmbeddingAsync(PingRequest request, CancellationToken cancellationToken)
    {
        if (SubagentExecutionAmbient.IsIsolated)
        {
            return null;
        }

        EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

        bool needsCodebaseEmbedding = embeddings.Enabled
            && embeddings.CodebaseRetrievalEnabled
            && !string.IsNullOrWhiteSpace(request.WorkingDirectory);

        bool needsSagaEmbedding = embeddings.Enabled && embeddings.SagaEnabled;

        bool needsAttachmentEmbedding = embeddings.Enabled
            && embeddings.AttachmentRetrievalEnabled
            && request.SessionId is not null;

        bool needsTapestryEmbedding = embeddings.Enabled
            && embeddings.TapestryEnabled
            && (!string.IsNullOrWhiteSpace(request.WorkingDirectory) || request.SessionId is not null);

        if (!needsCodebaseEmbedding
            && !needsSagaEmbedding
            && !needsAttachmentEmbedding
            && !needsTapestryEmbedding)
        {
            return null;
        }

        return await EmbedQueryAsync(GetSemanticRouterUserProbe(request), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Embeds <paramref name="prompt"/> via The Weave. Never throws for expected failure modes (provider
    /// unavailable, embedding call failure) — returns <c>null</c> instead, at Debug log level, so callers
    /// can gracefully skip RAG retrieval for the turn.
    /// </summary>
    private async Task<Embedding<float>?> EmbedQueryAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            if (!weaveService.IsAvailable)
            {
                return null;
            }

            EmbeddingSettings embeddingSettings = settings.Value.ResolveEmbeddings();

            PreviewAuxiliaryObserver.Value?.Invoke(

                new ContextPreviewAuxiliaryCall(

                    "retrieval-embedding",

                    true,

                    embeddingSettings.Provider,

                    embeddingSettings.Model,

                    null,

                    TokenEstimateClassification.Unknown,

                    "The shared retrieval embedding was invoked; this provider does not report token usage."));

            Result<Embedding<float>> embedResult = await weaveService.EmbedAsync(prompt, cancellationToken).ConfigureAwait(false);

            if (embedResult.IsFailure)
            {
                logger.LogDebug(
                    "RAG query embedding failed ({Code}); semantic retrieval for this turn will be skipped.",
                    embedResult.Error.Code);

                return null;
            }

            return embedResult.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "RAG query embedding threw; semantic retrieval for this turn will be skipped (exception type {ExceptionType}).",
                ex.GetType().FullName);

            return null;
        }
    }

    /// <summary>
    /// Retrieves semantically relevant workspace file chunks for the current turn's
    /// prompt, for injection into the system prompt (see <c>SystemPromptBuilder.Build</c>'s
    /// <c>semanticContext</c> parameter). Called from <see cref="RunInferenceAttemptAsync"/>
    /// before the system prompt is built.
    /// <paramref name="queryEmbedding"/> with <see cref="RetrieveSagaMemoriesAsync"/> (see
    /// <see cref="ResolveRagQueryEmbeddingAsync"/>) to avoid embedding the same prompt twice.
    ///
    /// Graceful degradation: returns <c>null</c> (never throws for expected failure modes) when the
    /// feature is disabled, <see cref="PingRequest.WorkingDirectory"/> is empty, the query embedding is
    /// unavailable, Divination fails, or no chunks are found above the similarity threshold — in every
    /// case the inference turn proceeds with an unchanged system prompt
    /// (<c>docs/Arcanum.DESIGN.md</c> §21.4).
    /// </summary>
    private async Task<SemanticContextChunk[]?> RetrieveSemanticContextAsync(
        PingRequest request,
        Embedding<float>? queryEmbedding,
        CancellationToken cancellationToken)
    {
        EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled || string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return null;
        }

        // Normalized once here (rather than relying solely on RegisterWorkspace's internal
        // normalization) so this exact string is also used for the WorkspacePath filter below —
        // WorkspaceIndexingService persists chunks keyed by its own Path.GetFullPath-normalized form,
        // and a mismatch (trailing slash, relative segments, casing) would silently return zero rows
        // even though the workspace was indexed successfully.
        string normalizedWorkingDirectory = request.WorkingDirectory;

        try
        {
            normalizedWorkingDirectory = Path.GetFullPath(request.WorkingDirectory.Trim());

            workspaceIndexingService.RegisterWorkspace(normalizedWorkingDirectory);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Failed to register workspace for background indexing; exception type {ExceptionType}.",
                ex.GetType().FullName);
        }

        if (queryEmbedding is not { } embedding)
        {
            return null;
        }

        try
        {
            CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

            int maxChunks = ArcanumSettingClamps.EmbeddingsCodebaseMaxRetrievedChunks(codebase.MaxRetrievedChunks);

            float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

            // Scoped to this workspace's chunks before ranking: an unscoped global KNN capped at
            // maxChunks could be dominated by another registered workspace's chunks, starving out
            // this workspace's genuinely-closest matches before the join below ever sees them.
            Result<DivinationResult[]> searchResult = await divinationService
                .SearchScopedAsync(
                    "workspace_file_embeddings_vec",
                    "ChunkId",
                    "Embedding",
                    "workspace_file_chunks",
                    "ChunkId",
                    "WorkspacePath",
                    normalizedWorkingDirectory,
                    embedding,
                    maxChunks,
                    similarityThreshold,
                    cancellationToken)
                .ConfigureAwait(false);

            if (searchResult.IsFailure || searchResult.Value.Length == 0)
            {
                return null;
            }

            SemanticContextChunk[] chunks = await JoinWorkspaceChunkMetadataAsync(
                db,
                searchResult.Value,
                normalizedWorkingDirectory,
                maxChunks,
                cancellationToken).ConfigureAwait(false);

            return chunks.Length == 0 ? null : chunks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Semantic context retrieval failed; continuing without it (exception type {ExceptionType}).",
                ex.GetType().FullName);

            return null;
        }
    }

    /// <summary>
    /// Retrieves Saga memories relevant to the current turn's prompt, for injection into
    /// the system prompt (see <c>SystemPromptBuilder.Build</c>'s <c>sagaMemories</c> parameter). Called
    /// from <see cref="RunInferenceAttemptAsync"/>
    /// alongside <see cref="RetrieveSemanticContextAsync"/>, sharing the same
    /// <paramref name="queryEmbedding"/> (see <see cref="ResolveRagQueryEmbeddingAsync"/>).
    ///
    /// Graceful degradation: returns <c>null</c> (never throws for expected failure modes) when the
    /// feature is disabled, the query embedding is unavailable, Divination fails, or no memories are
    /// found above the similarity threshold — in every case the inference turn proceeds with an
    /// unchanged system prompt (<c>docs/Arcanum.DESIGN.md</c> §21.4).
    /// </summary>
    private async Task<SagaMemory[]?> RetrieveSagaMemoriesAsync(
        Embedding<float>? queryEmbedding,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.SagaEnabled)
        {
            return null;
        }

        if (queryEmbedding is not { } embedding)
        {
            return null;
        }

        try
        {
            int maxResults = ArcanumSettingClamps.EmbeddingsMaxResults(embeddings.MaxResults);

            float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

            // The Campaign comes from the context the turn already resolved canonically, never from
            // anything the request carried: a caller that could name its own Campaign could read another
            // Campaign's memory by asking for it.
            Result<DivinationResult[]> searchResult = settings.Value.Features.CampaignScopedMemory
                ? await divinationService
                    .SearchCampaignScopedAsync(
                        SagaStorageKeys.VectorTable,
                        SagaStorageKeys.EmbeddingKeyColumn,
                        SagaStorageKeys.EmbeddingColumn,
                        SagaStorageKeys.CampaignScope(invocationContext.Campaign?.CampaignId),
                        embedding,
                        maxResults,
                        similarityThreshold,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await divinationService
                    .SearchAsync(
                        SagaStorageKeys.VectorTable,
                        SagaStorageKeys.EmbeddingKeyColumn,
                        SagaStorageKeys.EmbeddingColumn,
                        embedding,
                        maxResults,
                        similarityThreshold,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (searchResult.IsFailure || searchResult.Value.Length == 0)
            {
                return null;
            }

            IReadOnlyDictionary<string, SagaMemoryDto> byId = await sagaMemoryStore
                .GetByIdsAsync(
                    [.. searchResult.Value.Select(static hit => hit.Id)],
                    cancellationToken)
                .ConfigureAwait(false);

            List<SagaMemory> memories = new(searchResult.Value.Length);

            foreach (DivinationResult hit in searchResult.Value)
            {
                if (byId.TryGetValue(hit.Id, out SagaMemoryDto? memory))
                {
                    memories.Add(
                        new SagaMemory(
                            memory.Content,
                            hit.Similarity,
                            memory.CreatedAt,
                            memory.AttachmentProvenance));
                }
            }

            return memories.Count == 0 ? null : [.. memories];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Saga memory retrieval failed; continuing without it (exception type {ExceptionType}).",
                ex.GetType().FullName);

            return null;
        }
    }

    private SessionAttachmentRetrievedChunk[]? AcceptAttachmentRagMaterializations(
        ContextMaterializationLedger ledger,
        SessionAttachmentRetrievedChunk[]? chunks,
        ProviderSettings provider,
        string model)
    {

        if (chunks is not { Length: > 0 })
        {

            return null;

        }

        List<SessionAttachmentRetrievedChunk> accepted = [];

        foreach (SessionAttachmentRetrievedChunk chunk in chunks)
        {

            int bytes = Encoding.UTF8.GetByteCount(chunk.Content);

            ContextMaterializationEntry entry = ledger.Accept(
                new ContextMaterializationCandidate(
                    chunk.SessionId,
                    ContextMaterializationSourceKind.AttachmentRag,
                    chunk.AttachmentId.ToString("N"),
                    chunk.Version.ToString(CultureInfo.InvariantCulture),
                    chunk.Version,
                    new ContextMaterializationRange(chunk.CharacterStart, chunk.CharacterEnd),
                    ContextMaterializationOrigin.Semantic,
                    chunk.OriginalFileName,
                    chunk.ContentSha256,
                    ModelTokenEstimator.EstimateText(provider, model, chunk.Content).TokenCount,
                    bytes,
                    ContextMaterializationTrust.UntrustedData)
                {
                    AttachmentProvenance = new AttachmentMemoryProvenance(
                        chunk.SessionId,
                        chunk.AttachmentId,
                        chunk.LogicalKey,
                        chunk.Version,
                        chunk.ContentSha256,
                        DateTimeOffset.UtcNow,
                        "SessionAttachmentRag",
                        AttachmentSourceAvailability.Available),
                },
                materialized: true);

            if (entry.Accepted)
            {

                _ = ledger.TryMarkInjected(entry.Identity, providerRound: 0);

                accepted.Add(chunk);

            }

        }

        return accepted.Count == 0 ? null : [.. accepted];

    }

    private static AttachmentMemoryProvenance BuildAttachmentMemoryProvenance(
        SessionAttachmentRecord record) =>
        new(
            record.SessionId ?? Guid.Empty,
            record.Id,
            record.LogicalKey,
            record.Version,
            record.ContentSha256,
            DateTimeOffset.UtcNow,
            record.Source?.Kind.ToString() ?? AttachmentSourceKind.SnapshotOnly.ToString(),
            AttachmentSourceAvailability.Available);

    /// <summary>
    /// Retrieves hierarchical context from The Tapestry (DESIGN §21.11) for injection under
    /// <c>### Hierarchical Context (The Tapestry)</c>. Reuses the turn's single query embedding
    /// (see <see cref="ResolveRagQueryEmbeddingAsync"/>) and reads <b>only</b> each scope's current
    /// complete generation — nodes from an incomplete or superseded generation are never mixed in.
    ///
    /// Every scope the turn can see is queried: the working directory's workspace tree, and the
    /// session's attachment and history trees. Results are ranked together, redundant
    /// ancestor/descendant coverage is suppressed, and the remainder is bounded by the Tapestry's own
    /// node/byte/token limits.
    ///
    /// Graceful degradation: returns <c>null</c> (never throws for expected failure modes) when the
    /// feature is disabled, no generation is published, the query embedding is unavailable, or
    /// Divination fails — the turn proceeds with an unchanged system prompt (§21.4).
    /// </summary>
    private async Task<TapestryRetrievedNode[]?> RetrieveTapestryContextAsync(
        PingRequest request,
        Embedding<float>? queryEmbedding,
        CancellationToken cancellationToken)
    {
        EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

        if (!embeddings.Enabled
            || !embeddings.TapestryEnabled
            || tapestryStore is null
            || queryEmbedding is not { } embedding)
        {
            return null;
        }

        try
        {
            TapestryEmbeddingSettings tapestry = embeddings.Tapestry ?? new TapestryEmbeddingSettings();

            List<TapestryScope> scopes = [];

            if (tapestry.WorkspaceTreesEnabled && !string.IsNullOrWhiteSpace(request.WorkingDirectory))
            {
                scopes.Add(new TapestryScope(
                    TapestryScopeKind.Workspace,
                    Path.GetFullPath(request.WorkingDirectory.Trim())));
            }

            if (request.SessionId is { } sessionId)
            {
                if (tapestry.SessionAttachmentTreesEnabled)
                {
                    scopes.Add(new TapestryScope(TapestryScopeKind.SessionAttachment, sessionId.ToString()));
                }

                if (tapestry.SessionTreesEnabled)
                {
                    scopes.Add(new TapestryScope(TapestryScopeKind.Session, sessionId.ToString()));
                }
            }

            if (scopes.Count == 0)
            {
                return null;
            }

            int maxNodes = ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedNodes(tapestry.MaxRetrievedNodes);

            float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(
                embeddings.SimilarityThreshold);

            List<TapestryRetrievedNode> candidates = [];

            foreach (TapestryScope scope in scopes)
            {
                TapestryGeneration? generation = await tapestryStore
                    .GetCurrentGenerationAsync(scope, cancellationToken)
                    .ConfigureAwait(false);

                if (generation is null)
                {
                    continue;
                }

                IReadOnlyList<(string NodeId, float Similarity)> hits =
                    tapestry.RetrievalMode == TapestryRetrievalMode.TreeTraversal
                        ? await TraverseTapestryAsync(
                            generation,
                            embedding,
                            maxNodes,
                            similarityThreshold,
                            cancellationToken).ConfigureAwait(false)
                        : await SearchCollapsedTapestryAsync(
                            generation,
                            embedding,
                            maxNodes,
                            similarityThreshold,
                            cancellationToken).ConfigureAwait(false);

                if (hits.Count == 0)
                {
                    continue;
                }

                candidates.AddRange(
                    await tapestryStore.HydrateRetrievedNodesAsync(
                        generation,
                        hits,
                        tapestry.RetrievalMode,
                        cancellationToken).ConfigureAwait(false));
            }

            TapestryRetrievedNode[] selected = SelectTapestryNodes(candidates, tapestry, maxNodes);

            return selected.Length == 0 ? null : selected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Tapestry retrieval failed; continuing without it (exception type {ExceptionType}).",
                ex.GetType().FullName);

            return null;
        }
    }

    /// <summary>
    /// Collapsed-tree retrieval: leaf and summary nodes are one flat pool, scoped to the current
    /// generation so a superseded tree can never leak in.
    /// </summary>
    private async Task<IReadOnlyList<(string NodeId, float Similarity)>> SearchCollapsedTapestryAsync(
        TapestryGeneration generation,
        Embedding<float> embedding,
        int maxNodes,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        Result<DivinationResult[]> search = await divinationService
            .SearchScopedAsync(
                TapestryStorageKeys.VectorTable,
                TapestryStorageKeys.KeyColumn,
                TapestryStorageKeys.EmbeddingColumn,
                TapestryStorageKeys.NodeTable,
                TapestryStorageKeys.KeyColumn,
                TapestryStorageKeys.GenerationColumn,
                generation.GenerationId,
                embedding,
                maxNodes,
                similarityThreshold,
                cancellationToken)
            .ConfigureAwait(false);

        return search.IsFailure
            ? []
            : [.. search.Value.Select(static hit => (hit.Id, hit.Similarity))];
    }

    /// <summary>
    /// Tree-traversal retrieval: start at the generation's roots, then expand only the selected
    /// nodes' children, level by level. Each step is one scoped search on <c>ParentScopeKey</c>, and
    /// the walk stops when a level yields no children or the node budget is met.
    /// </summary>
    private async Task<IReadOnlyList<(string NodeId, float Similarity)>> TraverseTapestryAsync(
        TapestryGeneration generation,
        Embedding<float> embedding,
        int maxNodes,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        List<(string NodeId, float Similarity)> selected = [];

        List<string> frontier = [TapestryStorageKeys.RootParentMarker];

        int terminalLayer = await tapestryStore!
            .GetTerminalLayerAsync(generation.GenerationId, cancellationToken)
            .ConfigureAwait(false);

        for (int depth = 0; depth <= terminalLayer && frontier.Count > 0; depth++)
        {
            List<(string NodeId, float Similarity)> level = [];

            foreach (string parent in frontier)
            {
                Result<DivinationResult[]> search = await divinationService
                    .SearchScopedAsync(
                        TapestryStorageKeys.VectorTable,
                        TapestryStorageKeys.KeyColumn,
                        TapestryStorageKeys.EmbeddingColumn,
                        TapestryStorageKeys.NodeTable,
                        TapestryStorageKeys.KeyColumn,
                        TapestryStorageKeys.ParentScopeColumn,
                        TapestryStorageKeys.ParentScopeKey(generation.GenerationId, parent),
                        embedding,
                        maxNodes,
                        similarityThreshold,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (search.IsSuccess)
                {
                    level.AddRange(search.Value.Select(static hit => (hit.Id, hit.Similarity)));
                }
            }

            if (level.Count == 0)
            {
                break;
            }

            // Stable ordering: strongest similarity first, ties broken by node id, so a traversal is
            // reproducible for the same generation and query embedding.
            level.Sort(static (left, right) =>
                right.Similarity.CompareTo(left.Similarity) is var byScore && byScore != 0
                    ? byScore
                    : string.CompareOrdinal(left.NodeId, right.NodeId));

            List<(string NodeId, float Similarity)> keep = [.. level.Take(maxNodes)];

            selected.AddRange(keep);

            frontier = [.. keep.Select(static hit => hit.NodeId)];
        }

        return selected;
    }

    /// <summary>
    /// Lineage-aware redundancy control. A summary and one of its descendants carry different text
    /// and different hashes, so the shared ledger's exact content/range dedupe cannot see that they
    /// cover the same material — this does. Candidates are ranked by similarity, then by cheaper
    /// token cost, then by stable node id, and a candidate is suppressed when an already-selected
    /// node is its ancestor or its descendant. The Tapestry's own node/byte/token bounds are applied
    /// on the way through.
    /// </summary>
    internal static TapestryRetrievedNode[] SelectTapestryNodes(
        List<TapestryRetrievedNode> candidates,
        TapestryEmbeddingSettings tapestry,
        int maxNodes)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        int maxBytes = ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedBytes(tapestry.MaxRetrievedBytes);

        int maxTokens = ArcanumSettingClamps.EmbeddingsTapestryMaxRetrievedTokens(tapestry.MaxRetrievedTokens);

        candidates.Sort((left, right) =>
        {
            int byScore = right.Similarity.CompareTo(left.Similarity);

            if (byScore != 0)
            {
                return byScore;
            }

            int byCost = Encoding.UTF8.GetByteCount(left.Content)
                .CompareTo(Encoding.UTF8.GetByteCount(right.Content));

            return byCost != 0 ? byCost : string.CompareOrdinal(left.NodeId, right.NodeId);
        });

        List<TapestryRetrievedNode> accepted = [];

        HashSet<string> acceptedIds = new(StringComparer.Ordinal);

        long bytes = 0;

        long estimatedTokens = 0;

        foreach (TapestryRetrievedNode candidate in candidates)
        {
            if (accepted.Count >= maxNodes)
            {
                break;
            }

            if (!acceptedIds.Add(candidate.NodeId))
            {
                continue;
            }

            bool overlapsLineage = accepted.Any(
                existing =>
                    candidate.AncestorNodeIds.Contains(existing.NodeId, StringComparer.Ordinal)
                    || existing.AncestorNodeIds.Contains(candidate.NodeId, StringComparer.Ordinal));

            if (overlapsLineage)
            {
                continue;
            }

            int candidateBytes = Encoding.UTF8.GetByteCount(candidate.Content);

            int candidateTokens = Math.Max(1, candidate.Content.Length / 4);

            if (bytes + candidateBytes > maxBytes || estimatedTokens + candidateTokens > maxTokens)
            {
                continue;
            }

            bytes += candidateBytes;

            estimatedTokens += candidateTokens;

            accepted.Add(candidate);
        }

        return [.. accepted];
    }

    /// <summary>
    /// Admits retrieved Tapestry nodes through the per-turn ledger as a distinct source kind, so they
    /// share dedupe, inject-once, and the documented eviction priority with every other context
    /// source. Identity is generation-scoped node identity plus content hash, so the same node can
    /// never be injected twice and an exact-content match with a raw leaf already in the ledger is
    /// rejected as a duplicate.
    /// </summary>
    private TapestryContextNode[]? AcceptTapestryMaterializations(
        ContextMaterializationLedger ledger,
        TapestryRetrievedNode[]? nodes,
        ProviderSettings provider,
        string model,
        Guid? sessionId)
    {

        if (nodes is not { Length: > 0 })
        {

            return null;

        }

        List<TapestryContextNode> accepted = [];

        foreach (TapestryRetrievedNode node in nodes)
        {

            ContextMaterializationEntry entry = ledger.Accept(
                new ContextMaterializationCandidate(
                    // A workspace tree is installation-scoped rather than session-scoped, so only
                    // session-scoped trees carry a session id into the ledger's session gate.
                    node.ScopeKind == TapestryScopeKind.Workspace ? null : sessionId,
                    ContextMaterializationSourceKind.TapestryMemory,
                    $"{node.GenerationId}\u001f{node.NodeId}",
                    node.ContentHash,
                    VersionOrdinal: null,
                    ContextMaterializationRange.Whole,
                    ContextMaterializationOrigin.Semantic,
                    node.SourceLabel,
                    node.ContentHash,
                    ModelTokenEstimator.EstimateText(provider, model, node.Content).TokenCount,
                    Encoding.UTF8.GetByteCount(node.Content),
                    ContextMaterializationTrust.UntrustedData),
                materialized: true);

            if (entry.Accepted)
            {

                _ = ledger.TryMarkInjected(entry.Identity, providerRound: 0);

                accepted.Add(new TapestryContextNode(
                    DescribeTapestryScope(node),
                    node.SourceLabel,
                    node.Layer,
                    node.NodeKind == TapestryNodeKind.Summary,
                    node.DescendantLeafCount,
                    node.ContentHash,
                    node.Similarity,
                    node.Content));

            }

        }

        return accepted.Count == 0 ? null : [.. accepted];

    }

    /// <summary>
    /// A short, non-sensitive scope label for the prompt. A workspace tree reports only its leaf
    /// directory name, and session-scoped trees report their kind — neither leaks a host path or a
    /// session identifier into the model's context.
    /// </summary>
    private static string DescribeTapestryScope(TapestryRetrievedNode node) => node.ScopeKind switch
    {
        TapestryScopeKind.Workspace =>
            $"workspace {Path.GetFileName(node.ScopeId.TrimEnd(Path.DirectorySeparatorChar, '/'))}",

        TapestryScopeKind.SessionAttachment => "session attachments",

        _ => "session history",
    };

    /// <summary>
    /// Projects retrieved Tapestry nodes for the read-only context preview, which has no turn ledger
    /// to admit them through. Uses the same projection the ledger path produces so
    /// <c>mana</c> / <c>context inspect</c> shows the operator exactly what a real turn would inject.
    /// </summary>
    private static TapestryContextNode[]? ProjectTapestryPreview(TapestryRetrievedNode[]? nodes) =>
        nodes is not { Length: > 0 }
            ? null
            : [.. nodes.Select(static node => new TapestryContextNode(
                DescribeTapestryScope(node),
                node.SourceLabel,
                node.Layer,
                node.NodeKind == TapestryNodeKind.Summary,
                node.DescendantLeafCount,
                node.ContentHash,
                node.Similarity,
                node.Content))];

    private SemanticContextChunk[]? AcceptWorkspaceRagMaterializations(
        ContextMaterializationLedger ledger,
        SemanticContextChunk[]? chunks,
        ProviderSettings provider,
        string model,
        Guid? sessionId)
    {

        if (chunks is not { Length: > 0 })
        {

            return null;

        }

        List<SemanticContextChunk> accepted = [];

        foreach (SemanticContextChunk chunk in chunks)
        {

            string hash = ComputeContentHash(chunk.Content);

            ContextMaterializationEntry entry = ledger.Accept(
                new ContextMaterializationCandidate(
                    sessionId,
                    ContextMaterializationSourceKind.WorkspaceRag,
                    $"{chunk.RelativePath}\u001f{chunk.ChunkIndex.ToString(CultureInfo.InvariantCulture)}",
                    hash,
                    VersionOrdinal: null,
                    new ContextMaterializationRange(chunk.ChunkIndex, chunk.ChunkIndex),
                    ContextMaterializationOrigin.Semantic,
                    chunk.RelativePath,
                    hash,
                    ModelTokenEstimator.EstimateText(provider, model, chunk.Content).TokenCount,
                    Encoding.UTF8.GetByteCount(chunk.Content),
                    ContextMaterializationTrust.UntrustedData),
                materialized: true);

            if (entry.Accepted)
            {

                _ = ledger.TryMarkInjected(entry.Identity, providerRound: 0);

                accepted.Add(chunk);

            }

        }

        return accepted.Count == 0 ? null : [.. accepted];

    }

    private SagaMemory[]? AcceptSagaMaterializations(
        ContextMaterializationLedger ledger,
        SagaMemory[]? memories,
        ProviderSettings provider,
        string model,
        Guid? sessionId)
    {

        if (memories is not { Length: > 0 })
        {

            return null;

        }

        List<SagaMemory> accepted = [];

        foreach (SagaMemory memory in memories)
        {

            string hash = ComputeContentHash(memory.Content);

            ContextMaterializationEntry entry = ledger.Accept(
                new ContextMaterializationCandidate(
                    sessionId,
                    ContextMaterializationSourceKind.SagaMemory,
                    hash,
                    hash,
                    VersionOrdinal: null,
                    ContextMaterializationRange.Whole,
                    ContextMaterializationOrigin.Semantic,
                    "Saga memory",
                    hash,
                    ModelTokenEstimator.EstimateText(provider, model, memory.Content).TokenCount,
                    Encoding.UTF8.GetByteCount(memory.Content),
                    ContextMaterializationTrust.UntrustedData),
                materialized: true);

            if (entry.Accepted)
            {

                _ = ledger.TryMarkInjected(entry.Identity, providerRound: 0);

                accepted.Add(memory);

            }

        }

        return accepted.Count == 0 ? null : [.. accepted];

    }

    private static string ComputeContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool RemoveAttachmentRagChunk(
        ref SessionAttachmentRetrievedChunk[]? chunks,
        ContextMaterializationEntry removed)
    {

        if (chunks is not { Length: > 0 })
        {

            return false;

        }

        int originalCount = chunks.Length;

        chunks = chunks
            .Where(
                chunk => !string.Equals(
                        chunk.AttachmentId.ToString("N"),
                        removed.Identity.SourceId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        chunk.Version.ToString(CultureInfo.InvariantCulture),
                        removed.Identity.VersionOrContentHash,
                        StringComparison.Ordinal)
                    || new ContextMaterializationRange(
                        chunk.CharacterStart,
                        chunk.CharacterEnd) != removed.Identity.Range)
            .ToArray();

        if (chunks.Length == 0)
        {

            chunks = null;

        }

        return chunks?.Length != originalCount;

    }

    private static bool PruneSuppressedAttachmentRag(
        ref SessionAttachmentRetrievedChunk[]? chunks,
        ContextMaterializationLedger ledger)
    {

        if (chunks is not { Length: > 0 })
        {

            return false;

        }

        int originalCount = chunks.Length;

        chunks = chunks
            .Where(
                chunk => ledger.Contains(
                    new ContextMaterializationIdentity(
                        ContextMaterializationSourceKind.AttachmentRag,
                        chunk.AttachmentId.ToString("N"),
                        chunk.Version.ToString(CultureInfo.InvariantCulture),
                        new ContextMaterializationRange(
                            chunk.CharacterStart,
                            chunk.CharacterEnd))))
            .ToArray();

        if (chunks.Length == 0)
        {

            chunks = null;

        }

        return chunks?.Length != originalCount;

    }

    private static bool PruneSuppressedWorkspaceRag(
        ref SemanticContextChunk[]? chunks,
        ContextMaterializationLedger ledger)
    {

        if (chunks is not { Length: > 0 })
        {

            return false;

        }

        int originalCount = chunks.Length;

        chunks = chunks
            .Where(
                chunk =>
                {

                    string hash = ComputeContentHash(chunk.Content);

                    return ledger.Contains(
                        new ContextMaterializationIdentity(
                            ContextMaterializationSourceKind.WorkspaceRag,
                            $"{chunk.RelativePath}\u001f{chunk.ChunkIndex.ToString(CultureInfo.InvariantCulture)}",
                            hash,
                            new ContextMaterializationRange(
                                chunk.ChunkIndex,
                                chunk.ChunkIndex)));

                })
            .ToArray();

        if (chunks.Length == 0)
        {

            chunks = null;

        }

        return chunks?.Length != originalCount;

    }

    private static bool RemoveWorkspaceRagChunk(
        ref SemanticContextChunk[]? chunks,
        ContextMaterializationEntry removed)
    {

        if (chunks is not { Length: > 0 })
        {

            return false;

        }

        int originalCount = chunks.Length;

        chunks = chunks
            .Where(chunk => !string.Equals(
                ComputeContentHash(chunk.Content),
                removed.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (chunks.Length == 0)
        {

            chunks = null;

        }

        return chunks?.Length != originalCount;

    }

    /// <summary>
    /// Drops an evicted Tapestry node from the rendered prompt. Matching is by content hash, which is
    /// exactly the ledger identity's hash component, so an eviction can never leave the node visible
    /// in the prompt while the ledger believes it was removed.
    /// </summary>
    private static bool RemoveTapestryNode(
        ref TapestryContextNode[]? nodes,
        ContextMaterializationEntry removed)
    {

        if (nodes is not { Length: > 0 })
        {

            return false;

        }

        int originalCount = nodes.Length;

        nodes = [.. nodes.Where(node => !string.Equals(
            node.ContentHash,
            removed.ContentHash,
            StringComparison.OrdinalIgnoreCase))];

        if (nodes.Length == 0)
        {

            nodes = null;

        }

        return nodes?.Length != originalCount;

    }

    private static bool RemoveSagaMemory(
        ref SagaMemory[]? memories,
        ContextMaterializationEntry removed)
    {

        if (memories is not { Length: > 0 })
        {

            return false;

        }

        int originalCount = memories.Length;

        memories = memories
            .Where(memory => !string.Equals(
                ComputeContentHash(memory.Content),
                removed.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (memories.Length == 0)
        {

            memories = null;

        }

        return memories?.Length != originalCount;

    }

    private async Task<SessionAttachmentRetrievedChunk[]?> RetrieveSessionAttachmentContextAsync(
        PingRequest request,
        Embedding<float>? queryEmbedding,
        CancellationToken cancellationToken)
    {

        EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

        if (!embeddings.Enabled
            || !embeddings.AttachmentRetrievalEnabled
            || request.SessionId is not { } sessionId
            || queryEmbedding is null
            || sessionAttachmentRetrieval is null)
        {

            return null;

        }

        try
        {

            SessionAttachmentRetrievedChunk[] chunks = await sessionAttachmentRetrieval.SearchAsync(
                sessionId,
                queryEmbedding,
                includeHistorical: false,
                cancellationToken).ConfigureAwait(false);

            return chunks.Length == 0 ? null : chunks;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogDebug(
                ex,
                "Session attachment semantic retrieval failed; continuing without attachment context.");

            return null;

        }

    }

    /// <summary>
    /// Joins Divination hits (ChunkId + similarity) against <c>workspace_file_chunks</c>, scoped to
    /// <paramref name="workspacePath"/>, to populate the retrieved chunk's file path/index/content, and
    /// computes <see cref="SemanticContextChunk.TotalChunks"/> per file.
    /// </summary>
    private static async Task<SemanticContextChunk[]> JoinWorkspaceChunkMetadataAsync(
        ArcanumDbContext db,
        DivinationResult[] hits,
        string workspacePath,
        int maxChunks,
        CancellationToken cancellationToken)
    {
        Dictionary<string, float> similarityByChunkId = new(StringComparer.Ordinal);

        foreach (DivinationResult hit in hits)
        {
            similarityByChunkId[hit.Id] = hit.Similarity;
        }

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        List<(string ChunkId, string RelativePath, int ChunkIndex, string Content)> rows = [];

        await using (DbCommand cmd = connection.CreateCommand())
        {
            StringBuilder sql = new(
                """
                SELECT "ChunkId", "RelativePath", "ChunkIndex", "Content"
                FROM "workspace_file_chunks"
                WHERE "WorkspacePath" = @workspacePath AND "ChunkId" IN (
                """);

            AddParameter(cmd, "@workspacePath", workspacePath);

            for (int i = 0; i < hits.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                string paramName = $"@id{i.ToString(CultureInfo.InvariantCulture)}";

                sql.Append(paramName);

                AddParameter(cmd, paramName, hits[i].Id);
            }

            sql.Append(')');

            cmd.CommandText = sql.ToString();

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
            }
        }

        if (rows.Count == 0)
        {
            return [];
        }

        Dictionary<string, int> totalChunksByPath = await GetTotalChunksByPathAsync(
            connection,
            workspacePath,
            rows.Select(static r => r.RelativePath).Distinct(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => new SemanticContextChunk(
                r.RelativePath,
                r.ChunkIndex,
                totalChunksByPath.GetValueOrDefault(r.RelativePath, 1),
                similarityByChunkId.GetValueOrDefault(r.ChunkId, 0f),
                r.Content))
            .OrderByDescending(static c => c.Similarity)
            .Take(maxChunks)
            .ToArray();
    }

    private static async Task<Dictionary<string, int>> GetTotalChunksByPathAsync(
        DbConnection connection,
        string workspacePath,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        List<string> paths = [.. relativePaths];

        Dictionary<string, int> result = new(StringComparer.Ordinal);

        if (paths.Count == 0)
        {
            return result;
        }

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new(
            """
            SELECT "RelativePath", COUNT(*)
            FROM "workspace_file_chunks"
            WHERE "WorkspacePath" = @workspacePath AND "RelativePath" IN (
            """);

        AddParameter(cmd, "@workspacePath", workspacePath);

        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            string paramName = $"@path{i.ToString(CultureInfo.InvariantCulture)}";

            sql.Append(paramName);

            AddParameter(cmd, paramName, paths[i]);
        }

        sql.Append(") GROUP BY \"RelativePath\"");

        cmd.CommandText = sql.ToString();

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);
    }

    private static bool TryResolveOverrideSpellMetadata(
        string? overrideSpellName,
        IReadOnlyList<SpellMetadata> spells,
        out SpellMetadata? matched)
    {
        matched = null;

        if (string.IsNullOrWhiteSpace(overrideSpellName))
        {
            return false;
        }

        string needle = overrideSpellName.Trim();

        for (int i = 0; i < spells.Count; i++)
        {
            SpellMetadata spell = spells[i];

            if (string.Equals(spell.Name, needle, StringComparison.OrdinalIgnoreCase))
            {
                matched = spell;

                return true;
            }

            string? directoryPath = Path.GetDirectoryName(spell.FilePath);

            string leaf = GetSpellDirectoryLeafName(directoryPath ?? string.Empty);

            if (string.Equals(leaf, needle, StringComparison.OrdinalIgnoreCase))
            {
                matched = spell;

                return true;
            }
        }

        return false;
    }

    private static string GetSpellDirectoryLeafName(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return string.Empty;
        }

        try
        {
            string trimmed = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return Path.GetFileName(trimmed);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private async Task<List<AITool>> BuildToolSetWithMcpAsync(
        PingRequest request,
        ResolvedSpell? resolvedSpell,
        Guid? sessionIdForTurn,
        Guid? assistantEntryIdForTurn,
        CancellationToken cancellationToken,
        List<ContextPreviewTool>? previewExclusions = null)
    {
        if (request.DisableAllTools)
        {
            return [];
        }

        string workingDirectory = request.WorkingDirectory;

        ParsedSpell? activeSpell = resolvedSpell?.Primary;

        List<AITool> tools = [_localTimeTool, _systemInfoTool];

        if (subagentRunner is not null)
        {
            tools.Add(new ArcanumDelegateTaskTool(
                subagentRunner,
                request.Model));
        }

        List<string> scriptRoots = CollectScriptRoots(resolvedSpell);

        bool hostProcessToolsAllowed = HostProcessToolPolicy.AreAllowed(
            ArcanumEnvironment.ResolveEdition(settings.Value.Edition));

        if (scriptRoots.Count > 0 && hostProcessToolsAllowed)
        {
            long outputCap = ArcanumSettingClamps.ToolOutputCapBytes(
                settings.Value.ResolveIntelligence().ToolOutputCapBytes);

            // The SpellScript profile's ARCANUM_ prefix scrub only covers the derived default names.
            // Every credential variable an operator can rename — a provider key at MY_OPENAI_KEY, the
            // Comm Link webhook URL at TEAM_WEBHOOK_URL — has to travel with the tool by name, or the
            // spell script reads it straight out of its own environment. execute_command sources the
            // same list at its call site (ArcanumInternalToolServer.ExecuteCommand).
            IReadOnlyList<string> operatorDeclaredSecrets =
                FamiliarSecretEnvironmentNames.Collect(settings.Value);

            tools.Add(new ArcanumSpellScriptTool(
                scriptRoots,
                outputCap,
                logger,
                sanctumGuard,
                processResourceLimiter,
                workingDirectory,
                settings.Value.Security.AllowUnsandboxedToolChildren,
                settings.Value.Edition,
                operatorDeclaredSecrets));
        }

        IReadOnlyList<string>? declaredTools = activeSpell?.SkillMetadata?.DeclaredTools;

        bool unrestrictedWebTools = declaredTools is not { Count: > 0 };
        bool webSearchAttuned = unrestrictedWebTools
            || declaredTools!.Contains(
                ArcanumWebSearchTool.ToolName,
                StringComparer.OrdinalIgnoreCase);
        bool readUrlAttuned = unrestrictedWebTools
            || declaredTools!.Contains(
                ArcanumReadUrlTool.ToolName,
                StringComparer.OrdinalIgnoreCase)
            || declaredTools!.Contains(
                ArcanumBrowseWebTool.ToolName,
                StringComparer.OrdinalIgnoreCase);

        if (settings.Value.ResolveWebBrowsing().Enabled
            && webResearchProviderCatalog is not null)
        {
            if (webSearchAttuned)
            {
                tools.Add(
                    new ArcanumWebSearchTool(
                        webResearchProviderCatalog,
                        settings,
                        logger));
            }

            if (readUrlAttuned)
            {
                tools.Add(
                    new ArcanumReadUrlTool(
                        webResearchProviderCatalog,
                        settings,
                        logger));
            }
        }
        else if (settings.Value.ResolveWebBrowsing().Enabled && readUrlAttuned)
        {
            // Constructor compatibility for embedders that have not yet registered the
            // provider catalog. The production host always registers the native catalog.
            tools.Add(new ArcanumBrowseWebTool(httpClientFactory, settings, logger));
        }

        if (ShouldDisableMcpTools(request))
        {
            return FilterHostProcessTools(tools, hostProcessToolsAllowed);
        }

        IReadOnlyList<AITool> mcpTools = await mcpConnectionManager
            .GetAvailableToolsAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        AttunementResult attunement = ArtifactAttunement.ApplyAttunement(
            mcpTools,
            declaredTools);

        if (attunement.Excluded.Count > 0)
        {
            logger.LogDebug(
                "Artifact Attunement: spell {Spell} restricted MCP toolset to {Allowed}/{Total}; excluded {Count}: {Names}",
                activeSpell?.Name ?? "(none)",
                attunement.Allowed.Count,
                mcpTools.Count,
                attunement.Excluded.Count,
                string.Join(", ", attunement.Excluded));

            if (previewExclusions is not null)

            {

                previewExclusions.AddRange(

                    attunement.Excluded.Select(

                        static name => new ContextPreviewTool(

                            name,

                            "mcp",

                            false,

                            "Excluded by active Spell tool attunement.")));

            }
        }

        AttachmentsSettings attachments = settings.Value.ResolveAttachments();

        bool advertiseAttachTool = attachments.Enabled
            && attachments.EnableModelAttachTool
            && (request.SessionId.HasValue || sessionIdForTurn.HasValue);

        bool advertiseApplyPatch =
            sessionIdForTurn is { } patchSessionId
            && patchSessionId != Guid.Empty
            && assistantEntryIdForTurn is { } patchAssistantEntryId
            && patchAssistantEntryId != Guid.Empty;

        foreach (AITool t in attunement.Allowed)
        {
            if (!advertiseAttachTool
                && t.Name is "attach_session_file" or "refresh_session_file")
            {
                continue;
            }

            if (!advertiseApplyPatch
                && string.Equals(
                    t.Name,
                    ToolRiskClassifier.ApplyPatchToolName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!advertiseApplyPatch
                && IsLegacyMutatingFilesystemTool(t.Name))
            {
                continue;
            }

            if (!hostProcessToolsAllowed && HostProcessToolPolicy.IsHostProcessTool(t.Name))
            {
                continue;
            }

            tools.Add(t);
        }

        return tools;
    }

    private static bool IsLegacyMutatingFilesystemTool(
        string? toolName) =>
        string.Equals(
            toolName,
            "write_file",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            "replace_text_block",
            StringComparison.OrdinalIgnoreCase);

    private static List<AITool> FilterHostProcessTools(List<AITool> tools, bool hostProcessToolsAllowed)
    {
        if (hostProcessToolsAllowed)
        {
            return tools;
        }

        tools.RemoveAll(static t => HostProcessToolPolicy.IsHostProcessTool(t.Name));

        return tools;
    }

    private List<AITool> BuildClientForwardedToolSet(PingRequest request)
    {
        if (request.ClientTools is not { Length: > 0 } tools)
        {
            return [];
        }

        logger.LogWarning("Client-supplied tools detected. These bypass Arcanum's tool loop and security controls.");

        var forwarded = new List<AITool>(tools.Length);

        foreach (OpenAiToolDefinition tool in tools)
        {
            if (tool.Function is null)
            {
                continue;
            }

            forwarded.Add(new ClientForwardedFunction(tool.Function));
        }

        return forwarded;
    }

    private static List<string> CollectScriptRoots(ResolvedSpell? resolvedSpell)
    {
        if (resolvedSpell is null)
        {
            return [];
        }

        var roots = new List<string>();

        if (resolvedSpell.Primary is { AvailableScripts.Count: > 0, DirectoryPath: { Length: > 0 } directoryPath })
        {
            roots.Add(Path.Combine(directoryPath, "scripts"));
        }

        foreach (ParsedSpell dep in resolvedSpell.Resonants)
        {
            if (dep.AvailableScripts is { Count: > 0 }
                && !string.IsNullOrWhiteSpace(dep.DirectoryPath))
            {
                roots.Add(Path.Combine(dep.DirectoryPath, "scripts"));
            }
        }

        return roots;
    }

    /// <summary>
    /// <c>toolPolicy</c> and <c>disableMcpTools</c> are both narrowing instructions, so the answer to
    /// both is their intersection. The filtering policies no longer supersede the flag, which used to
    /// leave a caller that asked for no MCP tools with a filtered MCP toolset anyway.
    ///
    /// <para>The <c>_</c> arm treats an unrecognized policy as <c>noTools</c>. The wire converter
    /// refuses undefined values, so one can only arrive from in-process construction — and an unknown
    /// restriction must never be the arm that widens advertisement.</para>
    /// </summary>
    private static bool ShouldDisableMcpTools(PingRequest request) =>
        request.ToolPolicy switch
        {
            ToolPolicy.NoTools => true,
            null
                or ToolPolicy.AllTools
                or ToolPolicy.ReadOnlyTools
                or ToolPolicy.NoForbiddenArts => request.DisableMcpTools,
            _ => true,
        };

    /// <summary>
    /// The advertisement filter for a resolved policy. Its <c>_</c> arm advertises nothing, for the same
    /// reason <see cref="ShouldDisableMcpTools"/>'s does: an undefined policy is not a licence.
    /// </summary>
    private IReadOnlyList<AITool> ApplyToolPolicyFilters(PingRequest request, IReadOnlyList<AITool> tools) =>
        request.ToolPolicy switch
        {
            null or ToolPolicy.AllTools or ToolPolicy.NoTools => tools,
            ToolPolicy.ReadOnlyTools => FilterToolsToAllowlist(tools, ReadOnlyToolNames),
            ToolPolicy.NoForbiddenArts => FilterToolsExcludingNames(
                tools,
                ToolRiskClassifier.BuildForbiddenToolNames(
                    settings.Value.Security?.Ward?.ForbiddenArts ?? [])),
            _ => [],
        };

    private static readonly HashSet<string> ReadOnlyToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file_chunk",
        "list_directory",
        "read_lore",
        "search_archives",
        "attach_session_file",
        "refresh_session_file",
        ToolRiskClassifier.SearchWorkspaceToolName,
        ToolRiskClassifier.ReadCommandOutputToolName,
        "ask_human",
        "send_commlink_alert",
        "petition_dungeon_master",
        ArcanumLocalTimeTool.ToolName,
        ArcanumSystemInfoTool.ToolName,
        ArcanumBuiltInToolNames.DelegateTask,
    };

    private static IReadOnlyList<AITool> FilterToolsToAllowlist(IReadOnlyList<AITool> tools, HashSet<string> allowlist)
    {
        var filtered = new List<AITool>(tools.Count);

        foreach (AITool tool in tools)
        {
            if (tool is AIFunction function && allowlist.Contains(function.Name))
            {
                filtered.Add(tool);
            }
        }

        return filtered;
    }

    private static IReadOnlyList<AITool> FilterToolsExcludingNames(IReadOnlyList<AITool> tools, IEnumerable<string> excludedNames)
    {
        var excluded = new HashSet<string>(excludedNames, StringComparer.OrdinalIgnoreCase);

        var filtered = new List<AITool>(tools.Count);

        foreach (AITool tool in tools)
        {
            if (tool is AIFunction function && excluded.Contains(function.Name))
            {
                continue;
            }

            filtered.Add(tool);
        }

        return filtered;
    }

    private ChatOptions CreateInferenceChatOptions(bool includeTools, IReadOnlyList<AITool>? tools, PingRequest request, ChatClientLease lease)
    {
        var options = new ChatOptions();

        ApplyInferenceParameters(options, request);

        ReasoningChatOptionsAdapter.Apply(
            options,
            request.Reasoning,
            ResolveReasoningWireDialect(lease));

        ApplyClientToolMode(options, request);

        if (request.DisableAllTools)
        {
            options.ToolMode = ChatToolMode.None;

            return options;
        }

        if (!includeTools || tools is null)
        {
            return options;
        }

        options.Tools = tools.ToList();

        return options;
    }

    private static ReasoningWireDialect ResolveReasoningWireDialect(ChatClientLease lease) =>
        ProviderResolver.TryResolveModelEntry(lease.Provider, lease.ResolvedModel, out ModelEntry? modelEntry)
            ? modelEntry?.Reasoning?.WireDialect ?? ReasoningWireDialect.Standard
            : ReasoningWireDialect.Standard;

    private static bool ResolveReasoningEnabled(ChatClientLease lease) =>
        ProviderResolver.TryResolveModelEntry(
            lease.Provider,
            lease.ResolvedModel,
            out ModelEntry? modelEntry)
            ? modelEntry?.Reasoning?.WireDialect is not null
            : false;

    private static IReadOnlyList<ReasoningContentSegment> ProjectClientReasoningSegments(
        IReadOnlyList<ModelCallReasoningSegment> segments,
        ReasoningOutputMode clientReasoningOutput)
    {
        List<ReasoningContentRun> projected = [];

        foreach (ModelCallReasoningSegment segment in segments)
        {
            if (!TryProjectClientReasoning(
                    segment.VisibleText,
                    clientReasoningOutput,
                    out ReasoningContentSegment? clientSegment))
            {
                continue;
            }

            if (projected.Count > 0 && projected[^1].Output == clientSegment.Output)
            {
                projected[^1].Append(clientSegment.Text);
            }
            else
            {
                projected.Add(new ReasoningContentRun(clientSegment));
            }
        }

        return projected
            .Select(static run => run.Materialize())
            .ToArray();
    }

    private static void AppendReplacementFramesInContentOrder(
        BufferedOutputFrameAccumulator frames,
        ModelCallResult replacement,
        ReasoningOutputMode clientReasoningOutput)
    {
        foreach (ChatMessage message in replacement.Response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        frames.AppendToken(replacement.ModelCallId, text.Text);
                        break;

                    case TextReasoningContent reasoning
                        when TryProjectClientReasoning(
                            reasoning.Text ?? string.Empty,
                            clientReasoningOutput,
                            out ReasoningContentSegment? projected):
                        frames.AppendReasoning(replacement.ModelCallId, projected);
                        break;
                }
            }
        }
    }

    private static bool TryProjectClientReasoning(
        string visibleText,
        ReasoningOutputMode clientReasoningOutput,
        [NotNullWhen(true)] out ReasoningContentSegment? projected)
    {
        projected = null;

        if (visibleText.Length == 0
            || clientReasoningOutput == ReasoningOutputMode.None)
        {
            return false;
        }

        projected = new ReasoningContentSegment(visibleText, clientReasoningOutput);
        return true;
    }

    private static ReasoningOutputMode ResolveClientReasoningOutput(
        ReasoningOutputMode? requestedOutput,
        bool reasoningCapable,
        bool reasoningSummariesEnabled,
        bool streaming)
    {
        // Projection is best-effort: the provider returns what it returns. The only hard gates are
        // reasoning capability (declared reasoning block) and the operator display preference
        // (features.reasoningSummaries).
        if (!reasoningCapable || !reasoningSummariesEnabled)
        {
            return ReasoningOutputMode.None;
        }

        return requestedOutput switch
        {
            ReasoningOutputMode.None => ReasoningOutputMode.None,
            ReasoningOutputMode.Summary => ReasoningOutputMode.Summary,
            ReasoningOutputMode.Full => ReasoningOutputMode.Full,
            _ => ReasoningOutputMode.Summary,
        };
    }

    private static string BuildSafetyInspectionText(
        string finalText,
        IReadOnlyList<ReasoningContentSegment> reasoning)
    {
        if (reasoning.Count == 0)
        {
            return finalText;
        }

        StringBuilder inspection = new(finalText);

        foreach (ReasoningContentSegment segment in reasoning)
        {
            if (inspection.Length > 0)
            {
                _ = inspection.Append('\n');
            }

            _ = inspection.Append(segment.Text);
        }

        return inspection.ToString();
    }

    private static string BuildSafetyInspectionText(
        IEnumerable<IntelligenceEvent> orderedFrames)
    {
        StringBuilder inspection = new();

        foreach (IntelligenceEvent frame in orderedFrames)
        {
            string? text = frame.Type switch
            {
                IntelligenceEventType.Token => frame.Data,
                IntelligenceEventType.Reasoning => frame.Reasoning?.Text,
                _ => null,
            };

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (inspection.Length > 0)
            {
                _ = inspection.Append('\n');
            }

            _ = inspection.Append(text);
        }

        return inspection.ToString();
    }

    private Result EnsureContextBudget(
        List<MeAiChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatClientLease lease,
        PingRequest request,
        out ContextTokenBreakdown breakdown) =>
        EnsureContextBudgetWithMaterializations(
            chatMessages,
            chatOptions,
            lease,
            request,
            ledger: null,
            onSemanticDropped: null,
            out breakdown);

    /// <summary>
    /// Evicts semantic materializations, lowest priority first, until the payload fits the model's
    /// context window or the ledger has no semantic entry left.
    /// </summary>
    /// <remarks>
    /// A ledger entry whose counterpart the rendered prompt does not hold frees nothing when it is
    /// surrendered. Two things follow. Its drop is rescinded, so the turn's reported <c>Dropped*</c>
    /// counts never claim relief the payload never got. And the cascade continues past it rather than
    /// abandoning admission — the ledger has already given the entry up, so the next iteration
    /// considers the next-lowest, which may well be the entry actually holding the payload over budget.
    /// <paramref name="isOverBudget"/> is a full tokenizer pass over the whole payload, so it is
    /// re-evaluated only after an eviction that changed something.
    /// </remarks>
    internal static void CascadeSemanticDrops(
        ContextMaterializationLedger ledger,
        Func<ContextMaterializationEntry, bool> onSemanticDropped,
        Func<bool> isOverBudget)
    {
        bool overBudget = isOverBudget();

        while (overBudget)
        {
            ContextMaterializationEntry? removed = ledger.DropLowestPrioritySemantic();

            if (removed is null)
            {
                break;
            }

            if (!onSemanticDropped(removed))
            {
                ledger.RescindContextPressureDrop(removed);

                continue;
            }

            overBudget = isOverBudget();
        }
    }

    /// <summary>
    /// Commits this dispatch's Covenant evidence, or refuses to let the bytes leave.
    /// </summary>
    /// <remarks>
    /// The one pre-egress obligation of the turn loop. A clean call on a clean Session that cannot
    /// stage anything returns success without touching the database, freezing an envelope, or
    /// allocating a receipt — that is what keeps the disabled path identical to the path that existed
    /// before the Covenant did.
    ///
    /// <para>A turn that may stage is not that turn, even when it admitted nothing. Its admission
    /// receipt is the sole authority a staging tool call runs under, so withholding one because no
    /// Covenant bytes were injected leaves the proposal tool advertised and permanently refused on
    /// every installation whose Covenant is still empty — which is every installation until some turn
    /// authors the first proposal, and none ever can.</para>
    ///
    /// <para>Every other call fails closed. A frozen envelope that cannot be built, an installation
    /// with no established authority, and a journal that cannot commit are all refusals, because each
    /// of them means the receipt an operator would later read would not describe what went out.</para>
    /// </remarks>
    private async ValueTask<Result<CovenantDispatchAdmission?>> AcknowledgeCovenantDispatchAsync(
        CovenantTurnScope? covenantScope,
        CovenantDispatchPlan dispatch,
        ChatClientLease lease,
        string targetModel,
        bool streaming,
        SystemPromptDocument systemPromptDocument,
        IReadOnlyList<MeAiChatMessage> messages,
        ChatOptions chatOptions,
        ContextTokenBreakdown breakdown,
        CancellationToken cancellationToken)
    {

        if (covenantDispatch is null || covenantScope is not { } scope)
        {

            return Result<CovenantDispatchAdmission?>.Success(null);

        }

        if (!dispatch.HasAdmittedContent && !scope.HistoryTainted && !scope.MayStage)
        {

            return Result<CovenantDispatchAdmission?>.Success(null);

        }

        SystemPromptBuildResult built = systemPromptDocument.BuildResult();

        // The envelope freezes the document this method was handed, while the transcript's own system
        // message is rewritten independently by compression and by the materialization rebuilds. If the
        // two ever diverge, the receipt would describe a prompt the provider never saw — so the whole
        // class of drift is turned into one fail-closed comparison rather than a set of invariants each
        // rebuild has to remember.
        if (messages.Count > 0
            && messages[0].Role == ChatRole.System
            && !string.Equals(messages[0].Text, built.Prompt, StringComparison.Ordinal))
        {

            return Result<CovenantDispatchAdmission?>.Failure(new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The frozen system prompt does not match the transcript this dispatch would send."));

        }

        ProviderCallSensitivity sensitivity = CovenantDispatchGate.ResolveSensitivity(scope, dispatch);

        Result<ProviderCallEnvelope> frozen = CovenantProviderCallFreezer.TryFreeze(
            new CovenantProviderCallDescriptor(
                lease.Provider.Name,
                targetModel,
                streaming ? CovenantProviderDispatchMode.Streaming : CovenantProviderDispatchMode.Buffered,
                breakdown.Profile.TokenizerId,
                (ulong)ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit),
                0,
                built.Prompt,
                built.Attribution,
                messages,
                chatOptions),
            sensitivity,
            new ProviderCallMaterializationSnapshot(false, []));

        if (frozen.IsFailure)
        {

            return Result<CovenantDispatchAdmission?>.Failure(frozen.Error);

        }

        Result<CovenantDispatchAdmission> admitted = await covenantDispatch
            .AcknowledgeDispatchAsync(scope, dispatch, frozen.Value, cancellationToken)
            .ConfigureAwait(false);

        return admitted.IsFailure
            ? Result<CovenantDispatchAdmission?>.Failure(admitted.Error)
            : Result<CovenantDispatchAdmission?>.Success(admitted.Value);

    }

    /// <summary>
    /// Decides what this attempt's Covenant plan may actually inject, against this attempt's budget.
    /// </summary>
    /// <remarks>
    /// Runs once per provider attempt, not once per tool round: the same plan and the same admitted
    /// prefix have to be what every round of one attempt saw, or the admission receipt would describe
    /// a prompt that changed underneath it.
    ///
    /// <para>The budget is the head-room the rest of the prompt leaves. It is measured against the
    /// transcript as it stands before any Covenant bytes exist, which is the only measurement that is
    /// not circular. Both delegates measure through the same estimator the context gate uses, so a
    /// section that the planner believes fits is a section the gate will also accept.</para>
    ///
    /// <para>A turn with no plan does none of this work and allocates nothing.</para>
    /// </remarks>
    private CovenantDispatchPlan ResolveCovenantAdmission(
        CovenantTurnScope? covenantScope,
        ChatClientLease lease,
        PingRequest request,
        IReadOnlyList<MeAiChatMessage> messages,
        ChatOptions chatOptions)
    {

        if (covenantScope is not { HasPlan: true } scope)
        {

            return CovenantDispatchPlan.Empty;

        }

        int contextWindowLimit = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);

        ModelCallContext context = BuildModelCallContext(lease, request);

        int baseline = ModelTokenEstimator.EstimateContext(
            new ModelTokenizationRequest(
                lease.Provider,
                lease.ResolvedModel,
                messages,
                chatOptions,
                context.ReservedAnswerTokens,
                context.ReservedReasoningTokens))
            .TotalTokens;

        ulong headroom = (ulong)Math.Max(0, contextWindowLimit - baseline);

        ulong Measure(string text) =>
            text.Length == 0
                ? 0UL
                : (ulong)ModelTokenEstimator.EstimateContext(
                    new ModelTokenizationRequest(
                        lease.Provider,
                        lease.ResolvedModel,
                        [new MeAiChatMessage(ChatRole.System, text)],
                        chatOptions,
                        0,
                        0))
                    .TotalTokens;

        // The builder owns the headings, the untrusted-data notice, and the fences, and none of them
        // are visible from here. A fixed allowance per emitted section covers them. Overstating is the
        // safe direction for a budget: it can only admit less than would have fitted, never more than
        // the window holds.
        ulong MeasureSections(CovenantPromptContent content) =>
            Measure(content.GlobalConfirmed)
            + Measure(content.CampaignConfirmed)
            + Measure(content.CampaignProposed)
            + (content.HasGlobalConfirmed ? CovenantSectionFramingTokenAllowance : 0UL)
            + (content.HasCampaignConfirmed ? CovenantSectionFramingTokenAllowance : 0UL)
            + (content.HasProposed ? CovenantSectionFramingTokenAllowance : 0UL);

        return CovenantDispatchGate.PlanDispatch(
            scope,
            headroom,
            MeasureSections,
            Measure);

    }

    private Result EnsureContextBudgetWithMaterializations(
        List<MeAiChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatClientLease lease,
        PingRequest request,
        ContextMaterializationLedger? ledger,
        Func<ContextMaterializationEntry, bool>? onSemanticDropped,
        out ContextTokenBreakdown breakdown)
    {
        int contextWindowLimit = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);
        ModelCallContext context = BuildModelCallContext(lease, request);

        int Count(IReadOnlyList<MeAiChatMessage> messages) =>
            ModelTokenEstimator.EstimateContext(
                new ModelTokenizationRequest(
                    lease.Provider,
                    lease.ResolvedModel,
                    messages,
                    chatOptions,
                    context.ReservedAnswerTokens,
                    context.ReservedReasoningTokens))
                .TotalTokens;

        if (ledger is not null && onSemanticDropped is not null)
        {
            CascadeSemanticDrops(
                ledger,
                onSemanticDropped,
                () => Count(chatMessages) > contextWindowLimit);
        }

        _ = TurnContextGuards.TryTrimOldestToolExchanges(
            chatMessages,
            Count,
            contextWindowLimit);

        breakdown = ModelTokenEstimator.EstimateContext(
            new ModelTokenizationRequest(
                lease.Provider,
                lease.ResolvedModel,
                chatMessages,
                chatOptions,
                context.ReservedAnswerTokens,
                context.ReservedReasoningTokens));

        if (ledger is not null)
        {
            breakdown = breakdown with
            {
                DroppedAttachmentRagChunks = ledger.DroppedAttachmentRagChunks,

                DroppedAttachmentRagTokens = ledger.DroppedAttachmentRagTokens,

                DroppedWorkspaceRagChunks = ledger.DroppedWorkspaceRagChunks,

                DroppedWorkspaceRagTokens = ledger.DroppedWorkspaceRagTokens,

                DroppedTapestryNodes = ledger.DroppedTapestryNodes,

                DroppedTapestryTokens = ledger.DroppedTapestryTokens,

                DroppedCovenantProposed = ledger.DroppedCovenantProposed,

                DroppedCovenantProposedTokens = ledger.DroppedCovenantProposedTokens,

                CovenantConfirmedNoFit = ledger.CovenantConfirmedNoFit,
            };
        }

        Result admission = TurnContextGuards.CheckContextBudget(
            breakdown,
            contextWindowLimit);
        if (admission.IsSuccess)
        {
            return admission;
        }

        ArcanumMetrics.ContextBudgetRejectionsTotal.Add(
            1,
            new KeyValuePair<string, object?>("provider", lease.Provider.Name),
            new KeyValuePair<string, object?>("model", lease.ResolvedModel));
        return admission;
    }

    private ModelCallContext BuildModelCallContext(
        ChatClientLease lease,
        PingRequest request,
        ContextTokenBreakdown? precomputedBreakdown = null,
        PromptCachePlan? promptCachePlan = null) =>
        new(
            lease.Provider,
            lease.ResolvedModel,
            ReservedAnswerTokens: Math.Max(
                0,
                request.MaxOutputTokens
                ?? ArcanumSettingClamps.ReservedOutputTokens(
                    settings.Value.ResolveIntelligence().ReservedOutputTokens)),
            ReservedReasoningTokens: Math.Max(0, request.Reasoning?.BudgetTokens ?? 0),
            PrecomputedBreakdown: precomputedBreakdown,
            PromptCachePlan: promptCachePlan);

    private PromptCachePlan BuildPromptCachePlan(
        ChatClientLease lease,
        SystemPromptDocument document,
        ChatOptions options,
        PromptCacheSemanticNamespace semanticNamespace)
    {
        PromptCachingProfile? profile = ProviderResolver.ResolvePromptCachingProfile(
            lease.Provider,
            lease.ResolvedModel);

        if (profile?.ControlMode != PromptCachingControlMode.Explicit)
        {
            return PromptCachePlanner.Create(
                lease.Provider,
                lease.ResolvedModel,
                document,
                options,
                semanticNamespace,
                eligiblePrefixTokenEstimate: 0);
        }

        StringBuilder prefix = new();
        int eligibleLength = 0;

        foreach (PromptSegment segment in document.OrderedSegments)
        {
            if (segment.Stability == PromptSegmentStability.Volatile)
            {
                break;
            }

            _ = prefix.Append(segment.Text);

            if (segment.CacheBoundaryEligible)
            {
                eligibleLength = prefix.Length;
            }
        }

        int eligiblePrefixTokens = eligibleLength == 0
            ? 0
            : ModelTokenEstimator
                .EstimateText(
                    lease.Provider,
                    lease.ResolvedModel,
                    prefix.ToString(0, eligibleLength))
                .TokenCount;

        return PromptCachePlanner.Create(
            lease.Provider,
            lease.ResolvedModel,
            document,
            options,
            semanticNamespace,
            eligiblePrefixTokens);
    }

    private static void ApplyClientToolMode(ChatOptions options, PingRequest request)
    {
        if (!request.ForwardClientTools || request.ClientToolChoice is null)
        {
            return;
        }

        JsonElement choice = request.ClientToolChoice.Value;

        if (choice.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (choice.ValueKind == JsonValueKind.String)
        {
            string? value = choice.GetString();

            options.ToolMode = value?.Trim().ToLowerInvariant() switch
            {
                "auto" => ChatToolMode.Auto,
                "none" => ChatToolMode.None,
                "required" => ChatToolMode.RequireAny,
                _ => options.ToolMode
            };

            return;
        }

        if (choice.ValueKind == JsonValueKind.Object
            && choice.TryGetProperty("type", out JsonElement typeElement)
            && string.Equals(typeElement.GetString(), "function", StringComparison.Ordinal)
            && choice.TryGetProperty("function", out JsonElement functionElement)
            && functionElement.TryGetProperty("name", out JsonElement nameElement)
            && nameElement.ValueKind == JsonValueKind.String)
        {
            string? name = nameElement.GetString();

            if (!string.IsNullOrWhiteSpace(name))
            {
                options.ToolMode = ChatToolMode.RequireSpecific(name);
            }
        }
    }

    private static void ApplyInferenceParameters(ChatOptions options, PingRequest request)
    {
        if (request.Temperature is { } temp)
        {
            options.Temperature = Math.Clamp(temp, 0f, 2f);
        }

        if (request.TopP is { } topP)
        {
            options.TopP = Math.Clamp(topP, 0f, 1f);
        }

        if (request.MaxOutputTokens is { } maxOutput && maxOutput > 0)
        {
            options.MaxOutputTokens = maxOutput;
        }

        if (request.PresencePenalty is { } presence)
        {
            options.PresencePenalty = Math.Clamp(presence, -2f, 2f);
        }

        if (request.FrequencyPenalty is { } frequency)
        {
            options.FrequencyPenalty = Math.Clamp(frequency, -2f, 2f);
        }

        if (request.Seed is { } seed)
        {
            options.Seed = seed;
        }

        if (request.Stop is { Count: > 0 } stops)
        {
            options.StopSequences = stops.ToList();
        }

        if (request.ResponseFormat is { } responseFormatType && !string.IsNullOrWhiteSpace(responseFormatType))
        {
            string normalizedFormat = responseFormatType.ToLowerInvariant();

            if (normalizedFormat == "json_schema"
                && request.ResponseFormatJsonSchema is { } jsonSchemaWrapper)
            {
                JsonElement schemaElement = jsonSchemaWrapper;

                string schemaName = "response";

                if (jsonSchemaWrapper.ValueKind == JsonValueKind.Object
                    && jsonSchemaWrapper.TryGetProperty("schema", out JsonElement nestedSchema))
                {
                    schemaElement = nestedSchema;

                    if (jsonSchemaWrapper.TryGetProperty("name", out JsonElement nameElement)
                        && nameElement.ValueKind == JsonValueKind.String)
                    {
                        string? parsedName = nameElement.GetString();

                        if (!string.IsNullOrWhiteSpace(parsedName))
                        {
                            schemaName = parsedName.Trim();
                        }
                    }
                }

                options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    schemaElement,
                    schemaName,
                    schemaDescription: string.Empty);
            }
            else
            {
                options.ResponseFormat = normalizedFormat switch
                {
                    "json_object" or "json_schema" => ChatResponseFormat.Json,
                    "text" => ChatResponseFormat.Text,
                    _ => options.ResponseFormat,
                };
            }
        }
    }

    private static ChatCompletionUsage? MapUsageDetails(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        if (usage.InputTokenCount is null
            && usage.OutputTokenCount is null
            && usage.TotalTokenCount is null
            && usage.CachedInputTokenCount is null
            && usage.ReasoningTokenCount is null)
        {
            return null;
        }

        long providerPrompt = usage.InputTokenCount ?? 0L;
        long providerCompletion = usage.OutputTokenCount ?? 0L;

        int prompt = ClampUsageToInt(providerPrompt);

        int completion = ClampUsageToInt(providerCompletion);

        int total = ClampUsageToInt(
            usage.TotalTokenCount
            ?? SaturatingAdd(prompt, completion));

        int cached = ReadCachedTokens(usage);

        int reasoning = ClampUsageToInt(usage.ReasoningTokenCount ?? 0L);

        return new ChatCompletionUsage(prompt, completion, total, cached, reasoning);
    }

    private static int ReadCachedTokens(UsageDetails usage)
    {

        // Microsoft.Extensions.AI.Abstractions (v10.6.0+) surfaces prompt-cache hits via the
        // dedicated CachedInputTokenCount member. Cached input tokens are already included in
        // InputTokenCount, so we record them separately here only for the cache-hit metric.
        return ClampUsageToInt(usage.CachedInputTokenCount ?? 0L);

    }

    private static int ClampUsageToInt(long value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)value;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }

        return left + right;
    }

    private static ChatCompletionUsage AccumulateUsage(ChatCompletionUsage? running, ChatCompletionUsage? round)
    {
        int p = ClampUsageToInt(
            (long)(running?.PromptTokens ?? 0)
            + (round?.PromptTokens ?? 0));

        int c = ClampUsageToInt(
            (long)(running?.CompletionTokens ?? 0)
            + (round?.CompletionTokens ?? 0));

        int total = ClampUsageToInt(
            (long)(running?.TotalTokens ?? 0)
            + (round?.TotalTokens ?? 0));

        int cached = ClampUsageToInt(
            (long)(running?.CachedTokens ?? 0)
            + (round?.CachedTokens ?? 0));

        int reasoning = ClampUsageToInt(
            (long)(running?.ReasoningTokens ?? 0)
            + (round?.ReasoningTokens ?? 0));

        return new ChatCompletionUsage(p, c, total, cached, reasoning);
    }

    /// <summary>
    /// Records <c>arcanum_inference_duration_seconds</c> and <c>arcanum_inference_tokens_total</c> for a
    /// completed turn (buffered or streamed). Only called on the success path — a failed/cancelled/retried
    /// attempt does not represent a completed inference turn for latency purposes.
    /// </summary>
    private static void RecordInferenceMetrics(ProviderSettings provider, string model, TimeSpan elapsed, ChatCompletionUsage? usage)
    {

        string providerName = provider.Name;

        ArcanumMetrics.InferenceDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("model", model));

        if (usage is null)
        {

            return;

        }

        if (usage.PromptTokens > 0)
        {

            ArcanumMetrics.InferenceTokensTotal.Add(
                usage.PromptTokens,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("direction", "prompt"));

        }

        if (usage.CompletionTokens > 0)
        {

            ArcanumMetrics.InferenceTokensTotal.Add(
                usage.CompletionTokens,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("direction", "completion"));

        }

        if (usage.ReasoningTokens > 0)
        {
            ArcanumMetrics.ReasoningTokensTotal.Add(
                usage.ReasoningTokens,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", model));
        }

    }

    /// <summary>
    /// Writes one <see cref="InferenceAuditRecord"/> to the persisted inference audit log (§8.26) for
    /// a successfully completed turn. Called from both the buffered and streaming success paths,
    /// mirroring <see cref="TryIncrementSessionTokensAsync"/>'s call sites and error-tolerance
    /// contract: never throws, and a failure here must never surface as a failure of the inference
    /// turn it is recording (the turn has already succeeded by the time this runs).
    /// </summary>
    private async Task TryLogInferenceAuditAsync(
        InferenceAuditContext auditContext,
        Guid? sessionId,
        string providerName,
        string model,
        ChatCompletionUsage? usage,
        string finishReason,
        string? spellName,
        Guid? campaignId,
        TimeSpan elapsed,
        List<ContextTokenBreakdown> contextBreakdowns,
        CancellationToken cancellationToken)
    {

        try
        {

            List<string>? toolArgumentsJson = settings.Value.Host.AuditLog.RedactToolArguments
                ? null
                : [.. auditContext.ToolArgumentsJson];

            InferenceAuditRecord record = new(
                Timestamp: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                SessionId: sessionId?.ToString(),
                RequestType: auditContext.RequestType,
                Model: model,
                Provider: providerName,
                PromptTokens: usage?.PromptTokens ?? 0,
                CompletionTokens: usage?.CompletionTokens ?? 0,
                TotalTokens: usage?.TotalTokens ?? 0,
                LatencyMs: (long)elapsed.TotalMilliseconds,
                ToolCalls: auditContext.ToolNames.Count,
                ToolNames: [.. auditContext.ToolNames],
                ToolArgumentsJson: toolArgumentsJson,
                FinishReason: finishReason,
                ClientIp: auditContext.ClientIp,
                SpellName: spellName,
                CampaignId: campaignId?.ToString(),
                ReasoningTokens: usage?.ReasoningTokens ?? 0,
                ContextBreakdowns: contextBreakdowns);

            await inferenceAuditLogger.LogAsync(record, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            // Defense-in-depth: IInferenceAuditLogger.LogAsync itself promises never to throw, but a
            // failure while building the record (should not happen) must still never fail the turn.
            logger.LogWarning(
                "Failed to write inference audit record; continuing without it (exception type {ExceptionType}).",
                ex.GetType().FullName);

        }

    }

    internal static (string Provider, string Model) ResolveAuxiliaryAccountingIdentity(
        string? auxiliaryProvider,
        string? auxiliaryModel,
        string mainProvider,
        string mainModel) =>
        (
            string.IsNullOrWhiteSpace(auxiliaryProvider) ? mainProvider : auxiliaryProvider,
            string.IsNullOrWhiteSpace(auxiliaryModel) ? mainModel : auxiliaryModel
        );

    private async Task RecordProviderCallUsageAsync(
        TurnAccountingHandle accounting,
        ChatCompletionUsage? usage,
        string provider,
        string model,
        ModelCallPurpose callPurpose,
        CancellationToken cancellationToken)
    {
        if (usage is null)
        {
            return;
        }

        ModelPricingEntry pricing = settings.Value.ResolvePricing().ResolveForModel(model);

        BillableOperationType operationType = callPurpose is
            ModelCallPurpose.StructuredOutputRetry or ModelCallPurpose.ToolCompatibilityRetry
                ? BillableOperationType.Retry
                : BillableOperationType.Chat;

        string purpose = callPurpose switch
        {
            ModelCallPurpose.ToolContinuation => "tool-continuation",
            ModelCallPurpose.StructuredOutputRetry => "structured-output-retry",
            ModelCallPurpose.ToolCompatibilityRetry => "tool-compatibility-retry",
            _ => "chat",
        };

        await accounting.RecordUsageAsync(
                turnRunWriter,
                operationType,
                provider,
                model,
                purpose,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.CachedTokens,
                usage.ReasoningTokens,
                pricing,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryRecordAuxiliaryUsageAsync(
        UsageDetails? usage,
        BillableOperationType operationType,
        string purpose,
        string? provider,
        string? model)
    {
        ChatCompletionUsage? mapped = MapUsageDetails(usage);

        PreviewAuxiliaryObserver.Value?.Invoke(

            new ContextPreviewAuxiliaryCall(

                purpose,

                true,

                provider,

                model,

                mapped?.TotalTokens,

                mapped is null

                    ? TokenEstimateClassification.Unknown

                    : TokenEstimateClassification.ProviderReported,

                mapped is null

                    ? "The auxiliary model call was invoked, but provider token usage was unavailable."

                    : "The auxiliary model call was invoked; token usage is provider-reported."));

        if (TurnAccountingAmbient.Current is not TurnAccountingHandle accounting)
        {
            return;
        }

        if (mapped is null || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        ModelPricingEntry pricing = settings.Value.ResolvePricing().ResolveForModel(model);

        await accounting.RecordUsageAsync(
                TurnAccountingAmbient.Writer ?? turnRunWriter,
                operationType,
                provider ?? "unknown",
                model,
                purpose,
                mapped.PromptTokens,
                mapped.CompletionTokens,
                mapped.CachedTokens,
                mapped.ReasoningTokens,
                pricing,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task TryIncrementSessionTokensAsync(
        Guid? sessionId,
        ChatCompletionUsage? usage,
        string? model,
        decimal? ledgeredCostUsd,
        CancellationToken cancellationToken)
    {
        if (usage is null && ledgeredCostUsd is null)
        {
            return;
        }

        decimal costUsd;

        if (ledgeredCostUsd is { } ledgered)
        {
            costUsd = ledgered;
        }
        else
        {
            ModelPricingEntry pricing = settings.Value.ResolvePricing().ResolveForModel(model);
            ChatCompletionUsage reportedUsage = usage!;

            costUsd = CostCalculator.CalculateCost(
                reportedUsage.PromptTokens,
                reportedUsage.CompletionTokens,
                reportedUsage.CachedTokens,
                reportedUsage.ReasoningTokens,
                pricing);
        }

        if (!sessionId.HasValue)
        {
            return;
        }

        try
        {
            await grimoire
                .IncrementSessionTokensAndCostAsync(
                    sessionId.Value,
                    usage?.TotalTokens ?? 0,
                    costUsd,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Grimoire could not increment token totals for session {SessionId}; exception type {ExceptionType}.",
                sessionId,
                ex.GetType().FullName);
        }
    }

    /// <summary>
    /// RAG Phase 4 — enqueues Saga memory extraction for <paramref name="sessionId"/> after a
    /// successful inference turn. Fire-and-forget: enqueue failures never affect the completed turn
    /// (see <see cref="SagaExtractionService.EnqueueExtraction"/>, which itself never throws — the
    /// try/catch here is defense in depth against unexpected failures resolving settings).
    /// </summary>
    private void TryEnqueueSagaExtraction(
        Guid? sessionId,
        IReadOnlyList<AttachmentMemoryProvenance> attachmentProvenance,
        bool hadUnprovenancedAttachmentContent)
    {
        if (!sessionId.HasValue)
        {
            return;
        }

        try
        {
            EmbeddingSettings embeddings = settings.Value.ResolveEmbeddings();

            if (!embeddings.Enabled || !embeddings.SagaEnabled || !embeddings.Saga.ExtractionEnabled)
            {
                return;
            }

            sagaExtractionService.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId.Value,
                    attachmentProvenance,
                    hadUnprovenancedAttachmentContent));
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Failed to enqueue Saga extraction for session {SessionId}; exception type {ExceptionType}.",
                sessionId,
                ex.GetType().FullName);
        }
    }

    private async Task TryRecordAttachmentConsultationsAsync(
        Guid? sourceEntryId,
        IReadOnlyList<AttachmentMemoryProvenance> provenance,
        CancellationToken cancellationToken)
    {

        if (attachmentMemoryProvenanceStore is null
            || sourceEntryId is null
            || provenance.Count == 0)
        {

            return;

        }

        try
        {

            await attachmentMemoryProvenanceStore
                .RecordConsultationsAsync(
                    sourceEntryId.Value,
                    provenance,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(
                ex,
                "Attachment consultation provenance could not be persisted; durable attachment-derived promotion will remain fail-closed for this turn.");

        }

    }

    private static bool LooksLikeModelDoesNotSupportTools(string? message)
    {
        return !string.IsNullOrEmpty(message)

            && message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase);
    }

    internal static string MapChatFinishReasonToOpenAi(ChatFinishReason? finishReason)
    {

        if (finishReason is null)
        {

            return "stop";

        }

        if (finishReason == ChatFinishReason.Stop)
        {

            return "stop";

        }

        if (finishReason == ChatFinishReason.Length)
        {

            return "length";

        }

        if (finishReason == ChatFinishReason.ToolCalls)
        {

            return "tool_calls";

        }

        if (finishReason == ChatFinishReason.ContentFilter)
        {

            return "content_filter";

        }

        return finishReason.Value.Value;

    }

    private static bool HasSessionAttachmentPayload(PingRequest request) =>
        request.AttachedFiles is { Count: > 0 }
        || request.ScryingFoci is { Count: > 0 }
        || request.AttachmentReferences is { Count: > 0 };

    private static int EstimateMaterializedTokens(long materializedBytes) =>
        int.CreateSaturating((Math.Max(0L, materializedBytes) + 3L) / 4L);

    /// <summary>
    /// Runs the guardrails input filter (Tier 3 Phase 4) when a <see cref="GuardrailsPipeline"/> is
    /// injected. Stateless turns scan <see cref="PingRequest.StatelessMessages"/>; stateful turns
    /// synthesize a single user message from <see cref="PingRequest.Prompt"/>. A <see langword="null"/>
    /// pipeline (tests that don't opt in) is a no-op success — matching every other disabled-feature
    /// convention in Arcanum.
    /// </summary>
    private async Task<Result> FilterGuardrailsInputAsync(PingRequest request, CancellationToken cancellationToken)
    {

        if (guardrailsPipeline is null)
        {
            return Result.Success();
        }

        IReadOnlyList<CoreChatMessage> messages = request.StatelessMessages is { Count: > 0 } stateless
            ? stateless
            : string.IsNullOrEmpty(request.Prompt)
                ? []
                : [new CoreChatMessage("user", request.Prompt)];

        if (messages.Count == 0)
        {
            return Result.Success();
        }

        Result<GuardrailsResult> outcome = await guardrailsPipeline
            .FilterInputAsync(messages, cancellationToken, new GuardrailAuditContext(null, request.Model))
            .ConfigureAwait(false);

        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);

    }

    /// <summary>
    /// Runs the guardrails output filter on the model's completed text. A blocked output is not
    /// finalized as the assistant's reply (the caller resolves the grimoire turn as interrupted), and
    /// the violation is recorded in the guardrails audit log by the pipeline itself.
    /// </summary>
    private async Task<Result> FilterGuardrailsOutputAsync(
        string text,
        Guid? sessionId,
        string model,
        CancellationToken cancellationToken)
    {

        if (guardrailsPipeline is null)
        {
            return Result.Success();
        }

        Result<GuardrailsResult> outcome = await guardrailsPipeline
            .FilterOutputAsync(text, cancellationToken, new GuardrailAuditContext(sessionId?.ToString(), model))
            .ConfigureAwait(false);

        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);

    }

    private static string BuildInferenceFailureMessage(ChatClientLease lease, Exception? ex = null) =>
        BuildInferenceFailureMessage(lease.Provider, ex);

    private static string BuildInferenceFailureMessage(ProviderSettings provider, Exception? ex = null) =>
        InferenceProviderFailureMessage.Build(provider.Name, ex);

    /// <summary>
    /// Resolves structured-output behavior from the code-owned policy plus the standard
    /// <c>json_schema.strict</c> request contract. The removed configuration root is not consulted.
    /// </summary>
    private static StructuredOutputSettings ResolveStructuredOutputPolicy(PingRequest request)
    {
        StructuredOutputSettings policy = ArcanumRuntimeDefaults.StructuredOutput;
        bool requestRequiresStrictValidation =
            request.ResponseFormatJsonSchema is { ValueKind: JsonValueKind.Object } wrapper
            && wrapper.TryGetProperty("schema", out _)
            && wrapper.TryGetProperty("strict", out JsonElement strict)
            && strict.ValueKind == JsonValueKind.True;

        return policy with
        {
            StrictMode = policy.StrictMode || requestRequiresStrictValidation,
        };
    }

    /// <summary>
    /// Classifies an exception observed during lease construction or inference as a connectivity
    /// failure eligible for resilience fallback (retry on the next healthy candidate provider).
    /// Caller-initiated cancellation (<paramref name="callerToken"/> already cancelled) is never a
    /// connectivity failure — it propagates immediately and is never retried. Model-not-found, token
    /// limit, content filter, and tool-loop-limit failures also do not count.
    /// </summary>
    /// <summary>
    /// Classifies an inference-call exception as a connectivity failure (triggers provider
    /// fallback/health-tracking) purely by exception type/status — never by inspecting
    /// <see cref="Exception.Message"/>. A substring match on the message text is both too broad
    /// (any error whose text happens to contain "connection" — including some 4xx/5xx model errors —
    /// would wrongly fall back) and too narrow (a real connectivity failure phrased differently would
    /// be missed), and message text is not a stable contract across SDK/runtime versions.
    /// </summary>
    private static bool IsConnectivityFailure(Exception ex, CancellationToken callerToken)
    {

        if (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            return true;
        }

        if (ex is TaskCanceledException { InnerException: TimeoutException })
        {
            return true;
        }

        // A Familiar has no socket, so its transport failures arrive as a typed transport exception
        // rather than an HTTP one. The same distinction applies: an unreachable transport (binary
        // missing, spawn refused, deadline passed) is a connectivity-class failure and may fall back
        // to the next candidate, while the CLI answering and refusing — a rejected model name, a
        // rate limit — is a provider verdict that must stand.
        if (ex is Familiars.FamiliarTransportException familiar)
        {
            return familiar.Failure
                is FamiliarProcessFailure.NotInstalled
                or FamiliarProcessFailure.StartFailed
                or FamiliarProcessFailure.TimedOut;
        }

        if (ex is OperationCanceledException)
        {
            return !callerToken.IsCancellationRequested;
        }

        if (ex is ClientResultException clientResultEx)
        {
            // OpenAI-SDK-shaped providers (OpenAI, DeepSeek, most self-hosted OpenAI-compatible
            // servers — see WeaveService's identical handling for the embedding path) surface HTTP
            // error responses as ClientResultException, not HttpRequestException. A genuine
            // connectivity failure (DNS, connection refused, TLS handshake failure) either never
            // reaches an HTTP response (Status <= 0) or wraps the underlying transport exception; an
            // actual HTTP response (400/401/404/429/5xx, ...) is a model/auth/rate-limit failure, not
            // a connectivity one, and must not trigger provider fallback.
            return clientResultEx.Status <= 0
                || (clientResultEx.InnerException is { } clientInner && IsConnectivityFailure(clientInner, callerToken));
        }

        return ex.InnerException is { } inner && IsConnectivityFailure(inner, callerToken);

    }

    private readonly record struct InferenceAttemptResult(
        Result<PromptTurnResult> Result,
        bool IsConnectivityFailure,
        bool ProviderCommitted);

    /// <summary>
    /// Per-logical-run turn seed (DESIGN §10.7.2: "<c>TurnContextSeed</c> is built once per logical
    /// run, while each provider receives an isolated <c>ProviderAttemptContext</c>"). The fallback
    /// orchestrator creates one and hands it to every candidate attempt, so a run that falls back
    /// from a dead provider reuses the Grimoire turn it already opened — one Session, one user
    /// <c>Entry</c>, one assistant row — and pays for the RAG query embedding exactly once, instead
    /// of re-seeding (and permanently duplicating) the turn per candidate.
    /// </summary>
    private sealed class TurnContextSeed
    {

        /// <summary>
        /// The Grimoire turn opened by the first candidate that got far enough to begin one.
        /// <see langword="null"/> until then; a stateless request seeds an empty handle.
        /// </summary>
        public GrimoireTurnWriter.TurnHandle? Turn { get; set; }

        public bool QueryEmbeddingResolved { get; set; }

        public Embedding<float>? QueryEmbedding { get; set; }

    }

    private sealed class StreamFailureClassification
    {

        public bool IsConnectivityFailure { get; set; }

        /// <summary>
        /// True once this provider attempt has committed (answer or reasoning content, actionable
        /// tool proposal, or empty successful round). Status/SessionBound alone do not commit.
        /// </summary>
        public bool ProviderCommitted { get; set; }

        /// <summary>
        /// Buffered-mode terminal outcome (preserves <see cref="PromptTurnResult"/> / <see cref="Error"/> codes).
        /// </summary>
        public Result<PromptTurnResult>? BufferedTerminal { get; set; }

    }

    private sealed record TokenAccountingDependencies(
        IModelTokenEstimator Estimator,
        IModelCallExecutor Executor)
    {
        public static TokenAccountingDependencies Create(
            IModelTokenEstimator? estimator,
            IModelCallExecutor? executor,
            InferenceTokenizerResolver tokenizerResolver,
            ILogger logger)
        {
            IModelTokenEstimator resolvedEstimator =
                estimator ?? new ModelTokenEstimator(tokenizerResolver);
            return new TokenAccountingDependencies(
                resolvedEstimator,
                executor ?? new ModelCallExecutor(
                    resolvedEstimator,
                    settings: null,
                    logger: logger));
        }
    }

    private sealed class ModelCallAdmissionException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }

}
