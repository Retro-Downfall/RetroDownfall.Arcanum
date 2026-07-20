using System.Buffers;
using System.ClientModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
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
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Intelligence.Guardrails;
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
public sealed class WizardIntelligenceProvider(
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
    IProviderHealthTracker? healthTracker = null,
    GuardrailsPipeline? guardrailsPipeline = null) : IArcanumIntelligenceProvider
{
    private const string PublicInferenceFailureMessage =
        "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.";

    private const string PublicModelResolutionFailureMessage =
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.";

    private const string PublicInferenceTimeoutMessage =
        "Inference timed out. Increase Arcanum:Intelligence:InferenceTimeoutSeconds or retry with a shorter prompt.";

    private static readonly ArcanumLocalTimeTool _localTimeTool = new();

    private static readonly ArcanumSystemInfoTool _systemInfoTool = new();

    public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
        PingRequest request,
        CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null)
    {
        string prompt = request.Prompt;

        Result guardrailsInput = await FilterGuardrailsInputAsync(request, cancellationToken).ConfigureAwait(false);

        if (guardrailsInput.IsFailure)
        {
            return Result<PromptTurnResult>.Failure(guardrailsInput.Error);
        }

        if (!TryValidateAttachedFiles(request, out Error attachedFilesError))
        {
            return Result<PromptTurnResult>.Failure(attachedFilesError);
        }

        Result bounds = PingRequestBoundsValidator.Validate(request, settings.Value);

        if (bounds.IsFailure)
        {
            return Result<PromptTurnResult>.Failure(bounds.Error);
        }

        Result scryingGate = ValidateScryingGate(request);

        if (scryingGate.IsFailure)
        {
            return Result<PromptTurnResult>.Failure(scryingGate.Error);
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

        using CancellationTokenSource inferenceTimeoutCts = CreateInferenceTimeoutSource(callerToken);

        CancellationToken inferenceToken = inferenceTimeoutCts.Token;

        bool resilienceEnabled = settings.Value.Resilience?.Enabled == true && healthTracker is not null;

        if (!resilienceEnabled)
        {
            ChatClientLease singleLease;

            try
            {
                singleLease = await chatClientFactory.ResolveClientAsync(request.Model, inferenceToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Hub model resolution failed for requested model {RequestedModel}.", request.Model);

                return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));
            }

            using (singleLease)
            {
                InferenceAttemptResult single = await AttemptBufferedInferenceAsync(singleLease, request, inferenceToken, callerToken, auditContext).ConfigureAwait(false);

                return single.Result;
            }
        }

        return await ExecutePromptWithFallbackAsync(request, inferenceToken, callerToken, auditContext).ConfigureAwait(false);
    }

    private async Task<Result<PromptTurnResult>> ExecutePromptWithFallbackAsync(
        PingRequest request,
        CancellationToken inferenceToken,
        CancellationToken callerToken,
        InferenceAuditContext? auditContext)
    {

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings.Value, request.Model, healthTracker);

        if (candidates.Count == 0)
        {
            logger.LogWarning("Hub model resolution failed for requested model {RequestedModel}.", request.Model);

            return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));
        }

        int maxAttempts = Math.Min(
            candidates.Count,
            ArcanumSettingClamps.MaxFallbackAttempts(
                settings.Value.Resilience?.MaxFallbackAttempts ?? new ResilienceSettings().MaxFallbackAttempts));

        Result<PromptTurnResult> lastFailure = Result<PromptTurnResult>.Failure(
            new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));

        for (int attemptIndex = 0; attemptIndex < maxAttempts; attemptIndex++)
        {
            (ProviderSettings provider, string resolvedModel) = candidates[attemptIndex];

            bool isLastAttempt = attemptIndex == maxAttempts - 1;

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
                    ex,
                    "Provider {ProviderName} unavailable while resolving client (fallback attempt {Attempt}/{MaxAttempts}).",
                    provider.Name,
                    attemptIndex + 1,
                    maxAttempts);

                lastFailure = Result<PromptTurnResult>.Failure(new Error(
                    ErrorCodes.Hub.Error,
                    BuildInferenceFailureMessage(provider)));

                if (!isConnectivityFailure || isLastAttempt)
                {
                    return lastFailure;
                }

                continue;
            }

            using (lease)
            {
                InferenceAttemptResult attempt = await AttemptBufferedInferenceAsync(lease, request, inferenceToken, callerToken, auditContext).ConfigureAwait(false);

                if (attempt.Result.IsSuccess)
                {
                    healthTracker!.MarkHealthy(provider.Name);

                    return attempt.Result;
                }

                lastFailure = attempt.Result;

                if (!attempt.IsConnectivityFailure || isLastAttempt)
                {
                    return lastFailure;
                }

                healthTracker!.MarkFailed(provider.Name);

                logger.LogWarning(
                    "Provider {ProviderName} inference failed with a connectivity error (fallback attempt {Attempt}/{MaxAttempts}); trying next candidate.",
                    provider.Name,
                    attemptIndex + 1,
                    maxAttempts);

            }

        }

        return lastFailure;

    }

    private async Task<InferenceAttemptResult> AttemptBufferedInferenceAsync(
        ChatClientLease lease,
        PingRequest request,
        CancellationToken inferenceToken,
        CancellationToken callerToken,
        InferenceAuditContext? auditContext)
    {

        string prompt = request.Prompt;

        Stopwatch inferenceStopwatch = Stopwatch.StartNew();

        {
            string targetModel = lease.ResolvedModel;

            IChatClient chatClient = lease.ChatClient;

            Session? thread = await inferenceContextBuilder
            .LoadThreadAsync(request, inferenceToken)
            .ConfigureAwait(false);

        GrimoireTurnWriter.TurnHandle grimoireTurn = await grimoireTurnWriter
            .TryBeginBufferedAssistantReplyAsync(request, prompt, targetModel, inferenceToken)
            .ConfigureAwait(false);

        SessionAttachmentTurnPreparation attachmentPrep = await SessionAttachmentTurnService
            .PrepareAsync(
                request,
                sessionAttachmentStore,
                settings.Value,
                grimoireTurn.SessionId,
                grimoireTurn.AssistantEntryId,
                pendingTurnId: null,
                inferenceToken)
            .ConfigureAwait(false);

        if (attachmentPrep.ErrorMessage is not null)
        {
            if (!grimoireTurn.IsFinalized)
            {
                await grimoireTurnWriter
                    .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return new InferenceAttemptResult(
                Result<PromptTurnResult>.Failure(
                    new Error(ErrorCodes.Validation.AttachedFiles, attachmentPrep.ErrorMessage)),
                IsConnectivityFailure: false);
        }

        AttachmentsSettings attachmentSettings = settings.Value.Attachments ?? new AttachmentsSettings();

        int maxRefsPerTurn = ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn(
            attachmentSettings.MaxReferencesPerTurn);

        int userRefCount = request.AttachmentReferences?.Count ?? 0;

        SessionAttachmentTurnBudget.BeginTurn(maxRefsPerTurn, userRefCount);

        try
        {
        if (attachmentPrep.PendingTurnId is not null
            && grimoireTurn.SessionId is { } promoteSessionId)
        {
            try
            {
                await sessionAttachmentStore
                    .PromotePendingAsync(
                        attachmentPrep.PendingTurnId,
                        promoteSessionId,
                        grimoireTurn.AssistantEntryId,
                        inferenceToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to promote pending session attachments for session {SessionId}.", promoteSessionId);

                if (!grimoireTurn.IsFinalized)
                {
                    await grimoireTurnWriter
                        .ResolveInterruptedAsync(grimoireTurn, null, inferenceToken)
                        .ConfigureAwait(false);
                }

                return new InferenceAttemptResult(
                    Result<PromptTurnResult>.Failure(
                        new Error(
                            ErrorCodes.Validation.AttachedFiles,
                            string.IsNullOrWhiteSpace(ex.Message)
                                ? "Session attachment promotion failed."
                                : ex.Message)),
                    IsConnectivityFailure: false);
            }
        }

        string? codexContent = await CodexReader
            .ReadCodexAsync(
                request.WorkingDirectory,
                ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value),
                inferenceToken)
            .ConfigureAwait(false);

        ResolvedSpell? resolvedSpell;

        if (request.SkipSpellRouting)
        {
            resolvedSpell = null;
        }
        else
        {
            string? spellWorkspaceRoot = RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy.TryNormalizeWorkspace(
                request.WorkingDirectory,
                out string? spellRoot,
                out _)
                ? spellRoot
                : null;

            Result<ResolvedSpell?> routedSpell = await ResolveRoutedSpellAsync(
                request,
                chatClient,
                spellWorkspaceRoot,
                inferenceToken).ConfigureAwait(false);

            if (routedSpell.IsFailure)
            {
                if (!grimoireTurn.IsFinalized)
                {
                    await grimoireTurnWriter
                        .ResolveInterruptedAsync(grimoireTurn, null, inferenceToken)
                        .ConfigureAwait(false);
                }

                return new InferenceAttemptResult(Result<PromptTurnResult>.Failure(routedSpell.Error), IsConnectivityFailure: false);
            }

            resolvedSpell = routedSpell.Value;
        }

        ParsedSpell? activeSpell = resolvedSpell?.Primary;

        IReadOnlyList<ParsedSpell>? resonants = resolvedSpell?.Resonants;

        Embedding<float>? queryEmbedding = await ResolveRagQueryEmbeddingAsync(request, inferenceToken).ConfigureAwait(false);

        SemanticContextChunk[]? semanticContext = await RetrieveSemanticContextAsync(request, queryEmbedding, inferenceToken).ConfigureAwait(false);

        SagaMemory[]? sagaMemories = await RetrieveSagaMemoriesAsync(queryEmbedding, inferenceToken).ConfigureAwait(false);

        IReadOnlyList<LexiconEntryDto>? lexiconEntries = await RetrieveLexiconEntriesAsync(
            request,
            resolvedSpell?.Entities ?? Array.Empty<string>(),
            chatClient,
            inferenceToken).ConfigureAwait(false);

        string builtSystemPrompt = SystemPromptBuilder.Build(
            request,
            codexContent,
            activeSpell,
            request.AttachedFiles,
            dependencySpells: resonants,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(settings.Value.Spells.MaxResonantBytes),
            semanticContext: semanticContext,
            sagaMemories: sagaMemories,
            lexiconEntries: lexiconEntries,
            maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(settings.Value.Intelligence.LexiconMaxInjectedBytes),
            sessionAttachmentsIndex: attachmentPrep.IndexItems,
            maxIndexItems: ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(attachmentSettings.MaxIndexItemsInPrompt),
            maxIndexBytes: ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt(attachmentSettings.MaxIndexBytesInPrompt));

        List<AITool> toolSet = request.ForwardClientTools
            ? BuildClientForwardedToolSet(request)
            : await BuildToolSetWithMcpAsync(
                request,
                resolvedSpell,
                grimoireTurn.SessionId ?? request.SessionId,
                inferenceToken).ConfigureAwait(false);

        ToolExecutionPipeline.TurnContext turnContext = await BuildTurnContextAsync(request, toolSet, inferenceToken).ConfigureAwait(false);

        bool inferenceUsesTools = true;

        while (true)
        {
            try
            {
                List<MeAiChatMessage> chatMessages = InferenceContextBuilder.BuildInitialMeAiChatMessages(request, thread, prompt);

                InferenceContextBuilder.PrependDynamicSystemMessage(chatMessages, builtSystemPrompt);

                (bool compressedSync, List<MeAiChatMessage> syncMessages) = inferenceContextBuilder.TryApplyContextCompressionIfNeeded(
                    request,
                    chatMessages,
                    codexContent,
                    activeSpell,
                    resonants,
                    thread,
                    prompt,
                    lease,
                    semanticContext: semanticContext,
                    sagaMemories: sagaMemories,
                    lexiconEntries: lexiconEntries,
                    sessionAttachmentsIndex: attachmentPrep.IndexItems,
                    maxIndexItems: ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(attachmentSettings.MaxIndexItemsInPrompt),
                    maxIndexBytes: ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt(attachmentSettings.MaxIndexBytesInPrompt));

                chatMessages = syncMessages;

                InferenceContextBuilder.AppendContentsToLastMessage(chatMessages, attachmentPrep.RehydratedContents);

                if (compressedSync)
                {
                    logger.LogInformation(IntelligenceStatusMessages.MemoryCompressionNotice);
                }

                ChatOptions chatOptions = CreateInferenceChatOptions(inferenceUsesTools, turnContext.InferenceTools, request, lease);

                ChatResponse? response;

                int toolRoundsExecuted = 0;

                int maxToolRounds = ArcanumSettingClamps.MaxToolInferenceRounds(settings.Value.Intelligence.MaxToolInferenceRounds);

                ChatCompletionUsage? accumulatedUsage = null;

                List<PromptToolCall>? observedToolCalls = null;

                while (true)
                {
                    response = await chatClient
                        .GetResponseAsync(chatMessages, chatOptions, inferenceToken)
                        .ConfigureAwait(false);

                    accumulatedUsage = AccumulateUsage(accumulatedUsage, MapUsageDetails(response.Usage));

                List<FunctionCallContent> calls = ToolExecutionPipeline.CollectActionableFunctionCalls(response);

                if (calls.Count == 0)
                {
                    break;
                }

                if (request.ForwardClientTools)
                {
                    foreach (FunctionCallContent fcc in calls)
                    {
                        (observedToolCalls ??= []).Add(new PromptToolCall(
                            toolExecutionPipeline.ResolveCallId(fcc),
                            fcc.Name ?? string.Empty,
                            ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(fcc)));
                    }

                    break;
                }

                toolRoundsExecuted++;

                if (toolRoundsExecuted > maxToolRounds)
                {
                        if (!grimoireTurn.IsFinalized)
                        {
                            await grimoireTurnWriter
                                .ResolveInterruptedAsync(grimoireTurn, null, inferenceToken)
                                .ConfigureAwait(false);
                        }

                        return new InferenceAttemptResult(
                            Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.ToolLoop, "Tool invocation limit reached.")),
                            IsConnectivityFailure: false);
                    }

                    Guid? ambientSessionId = grimoireTurn.SessionId ?? request.SessionId;

                    SessionAttachmentToolAmbient.CurrentSessionId = ambientSessionId;

                    try
                    {
                        foreach (FunctionCallContent fcc in calls)
                        {
                            ToolExecutionPipeline.ProcessedToolCall processed = await toolExecutionPipeline
                                .ProcessSingleToolCallAsync(
                                    fcc,
                                    request,
                                    chatOptions,
                                    activeSpell,
                                    grimoireTurn.SessionId?.ToString(),
                                    turnContext,
                                    suppressInvocationFailures: settings.Value.Intelligence.TolerateToolFailures,
                                    inferenceToken)
                                .ConfigureAwait(false);

                            (observedToolCalls ??= []).Add(new PromptToolCall(processed.CallId, processed.ToolName, processed.ArgsSnapshot));

                            auditContext?.ToolNames.Add(processed.ToolName);

                            auditContext?.ToolArgumentsJson.Add(processed.ArgsSnapshot);

                            ToolExecutionPipeline.AppendToolExchangeToMessages(
                                chatMessages,
                                fcc,
                                processed.CallId,
                                processed.ResultText);

                            if (processed.AdditionalContextContents is { Count: > 0 } extras)
                            {
                                // Prefer a User message so vision providers receive DataContent on the next round
                                // (Tool-role messages are a poor carrier for multimodal payload).
                                chatMessages.Add(new MeAiChatMessage(ChatRole.User, extras.ToList()));
                            }

                            await grimoireTurnWriter.TryAppendToolInteractionAsync(
                                grimoireTurn.SessionId,
                                processed.ToolName,
                                processed.ArgsSnapshot,
                                processed.ResultText,
                                targetModel,
                                inferenceToken)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        SessionAttachmentToolAmbient.CurrentSessionId = null;
                    }
                }

                IReadOnlyList<string> structuredOutputWarnings = [];

                if (request.ResponseFormat is "json_schema"
                    && request.ResponseFormatJsonSchema is { } jsonSchemaWrapper
                    && settings.Value.StructuredOutput.Enabled)
                {

                    JsonElement schemaElement = jsonSchemaWrapper;

                    if (jsonSchemaWrapper.ValueKind == JsonValueKind.Object
                        && jsonSchemaWrapper.TryGetProperty("schema", out JsonElement nestedSchema))
                    {

                        schemaElement = nestedSchema;

                    }

                    using JsonDocument schema = JsonDocument.Parse(schemaElement.GetRawText());

                    int maxRetries = ArcanumSettingClamps.StructuredOutputMaxValidationRetries(
                        settings.Value.StructuredOutput.MaxValidationRetries);

                    int schemaMaxDepth = ArcanumSettingClamps.JsonSchemaMaxDepth(
                        settings.Value.StructuredOutput.SchemaMaxDepth);

                    int contextWindowLimit = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);

                    Func<string, int> estimateTokenCount = text =>
                    {

                        try
                        {

                            return tokenizerResolver
                                .ResolveTokenizer(settings.Value.Intelligence.TokenizerEncoding)
                                .CountTokens(text);

                        }
                        catch
                        {

                            return Math.Max(1, text.Length / 4);

                        }

                    };

                    Result<StructuredOutputResult> validationResult = await structuredOutputValidator
                        .ValidateAndRetryAsync(
                            response,
                            schema,
                            maxRetries,
                            settings.Value.StructuredOutput.StrictMode,
                            schemaMaxDepth,
                            contextWindowLimit,
                            estimateTokenCount,
                            async (errorMessage, ct) =>
                            {

                                chatMessages.Add(new ChatMessage(ChatRole.Assistant, response.Text));

                                chatMessages.Add(new ChatMessage(ChatRole.System, errorMessage));

                                ChatResponse retryResponse = await chatClient
                                    .GetResponseAsync(chatMessages, chatOptions, ct)
                                    .ConfigureAwait(false);

                                response = retryResponse;

                                accumulatedUsage = AccumulateUsage(accumulatedUsage, MapUsageDetails(retryResponse.Usage));

                                return retryResponse;

                            },
                            inferenceToken)
                        .ConfigureAwait(false);

                    if (validationResult.IsFailure)
                    {

                        return new InferenceAttemptResult(
                            Result<PromptTurnResult>.Failure(validationResult.Error),
                            IsConnectivityFailure: false);

                    }

                    response = validationResult.Value.Response;

                    structuredOutputWarnings = validationResult.Value.Warnings;

                }

                string finalText = response.Text;

                Result guardrailsOutput = await FilterGuardrailsOutputAsync(
                    finalText,
                    grimoireTurn.SessionId,
                    targetModel,
                    inferenceToken).ConfigureAwait(false);

                if (guardrailsOutput.IsFailure)
                {
                    if (!grimoireTurn.IsFinalized)
                    {
                        await grimoireTurnWriter
                            .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    return new InferenceAttemptResult(
                        Result<PromptTurnResult>.Failure(guardrailsOutput.Error),
                        IsConnectivityFailure: false);
                }

                bool finalizeOk = await grimoireTurnWriter
                    .TryFinalizeBufferedAssistantEntryAsync(grimoireTurn, finalText, targetModel, inferenceToken)
                    .ConfigureAwait(false);

                if (!finalizeOk)
                {
                    return new InferenceAttemptResult(
                        Result<PromptTurnResult>.Failure(
                            new Error(ErrorCodes.Hub.Error, GrimoireTurnWriter.PublicFinalizeFailureMessage)),
                        IsConnectivityFailure: false);
                }

                await TryIncrementSessionTokensAsync(
                    grimoireTurn.SessionId,
                    accumulatedUsage,
                    targetModel,
                    inferenceToken)
                    .ConfigureAwait(false);

                TryEnqueueSagaExtraction(grimoireTurn.SessionId);

                string finishReason = request.ForwardClientTools && observedToolCalls is { Count: > 0 }
                    ? "tool_calls"
                    : MapChatFinishReasonToOpenAi(response.FinishReason);

                RecordInferenceMetrics(lease.Provider, targetModel, inferenceStopwatch.Elapsed, accumulatedUsage);

                if (auditContext is not null)
                {
                    await TryLogInferenceAuditAsync(
                        auditContext,
                        grimoireTurn.SessionId,
                        lease.Provider.Name,
                        targetModel,
                        accumulatedUsage,
                        finishReason,
                        activeSpell?.Name,
                        request.CampaignId,
                        inferenceStopwatch.Elapsed,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return new InferenceAttemptResult(
                    Result<PromptTurnResult>.Success(new PromptTurnResult(finalText, accumulatedUsage, observedToolCalls, finishReason) { Warnings = structuredOutputWarnings, PreserveProviderToolCallIds = request.ForwardClientTools }),
                    IsConnectivityFailure: false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {

                if (!grimoireTurn.IsFinalized)
                {

                    await grimoireTurnWriter
                        .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                        .ConfigureAwait(false);

                }

                logger.LogWarning("Inference wall-clock timeout exceeded for model {ModelName}.", targetModel);

                return new InferenceAttemptResult(
                    Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Timeout, PublicInferenceTimeoutMessage)),
                    IsConnectivityFailure: true);

            }
            catch (OperationCanceledException)
            {

                if (!grimoireTurn.IsFinalized)
                {

                    // W3.5: clean up with CancellationToken.None — callerToken is already cancelled
                    // here, so passing it would make the discard/finalize itself throw OCE before it
                    // ran, leaving an orphaned in-flight assistant row on caller disconnect.
                    await grimoireTurnWriter
                        .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                        .ConfigureAwait(false);

                }

                throw;

            }
            catch (Exception ex)
            {
                if (inferenceUsesTools && LooksLikeModelDoesNotSupportTools(ex.Message))
                {
                    logger.LogInformation(
                        ex,
                        "Model {ModelName} does not support tools; retrying without local tools.",
                        targetModel);

                    inferenceUsesTools = false;

                    continue;
                }

                logger.LogError(
                    ex,
                    "Hub inference failed for model {ModelName}.",
                    targetModel);

                if (!grimoireTurn.IsFinalized)
                {
                    // W3.5: non-cancellable cleanup so a cancelled inferenceToken cannot abort the
                    // discard and orphan the in-flight assistant row.
                    await grimoireTurnWriter
                        .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return new InferenceAttemptResult(
                    Result<PromptTurnResult>.Failure(
                        new Error(
                            ErrorCodes.Hub.Error,
                            BuildInferenceFailureMessage(lease))),
                    IsConnectivityFailure: IsConnectivityFailure(ex, callerToken));
            }
        }
        }
        finally
        {
            SessionAttachmentTurnBudget.EndTurn();
        }
        }
    }

    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
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

        if (!TryValidateAttachedFiles(request, out Error streamAttachedError))
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, streamAttachedError.Message);

            yield break;
        }

        Result streamBounds = PingRequestBoundsValidator.Validate(request, settings.Value);

        if (streamBounds.IsFailure)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, streamBounds.Error.Message);

            yield break;
        }

        Result streamScryingGate = ValidateScryingGate(request);

        if (streamScryingGate.IsFailure)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, streamScryingGate.Error.Message);

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

        using CancellationTokenSource inferenceTimeoutCts = CreateInferenceTimeoutSource(callerToken);

        CancellationToken inferenceToken = inferenceTimeoutCts.Token;

        bool resilienceEnabled = settings.Value.Resilience?.Enabled == true && healthTracker is not null;

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
                logger.LogWarning(resolveFailure, "Hub model resolution failed for requested model {RequestedModel}.", request.Model);

                yield return new IntelligenceEvent(IntelligenceEventType.Error, PublicModelResolutionFailureMessage);

                yield break;
            }

            ChatClientLease singleLease = leaseOrNull!;

            StreamFailureClassification singleClassification = new();

            IAsyncEnumerator<IntelligenceEvent> singleEnumerator = StreamCommittedInferenceAsync(
                singleLease,
                request,
                prompt,
                singleClassification,
                inferenceToken,
                callerToken,
                auditContext).GetAsyncEnumerator(inferenceToken);

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
                    singleMoveFailure,
                    "Streaming inference threw after start for model {RequestedModel}.",
                    request.Model);

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

        int streamMaxAttempts = Math.Min(
            streamCandidates.Count,
            ArcanumSettingClamps.MaxFallbackAttempts(
                settings.Value.Resilience?.MaxFallbackAttempts ?? new ResilienceSettings().MaxFallbackAttempts));

        for (int attemptIndex = 0; attemptIndex < streamMaxAttempts; attemptIndex++)
        {
            (ProviderSettings candidateProvider, string candidateModel) = streamCandidates[attemptIndex];

            bool isLastAttempt = attemptIndex == streamMaxAttempts - 1;

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
                    leaseBuildFailure,
                    "Provider {ProviderName} unavailable while resolving streaming client (fallback attempt {Attempt}/{MaxAttempts}).",
                    candidateProvider.Name,
                    attemptIndex + 1,
                    streamMaxAttempts);

                if (retryableBuildFailure)
                {
                    continue;
                }

                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    BuildInferenceFailureMessage(candidateProvider));

                yield break;
            }

            ChatClientLease lease = candidateLease!;

            StreamFailureClassification classification = new();

            IAsyncEnumerator<IntelligenceEvent> enumerator = StreamCommittedInferenceAsync(lease, request, prompt, classification, inferenceToken, callerToken, auditContext).GetAsyncEnumerator();

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
                    moveNextFailure,
                    "Provider {ProviderName} failed to start streaming (fallback attempt {Attempt}/{MaxAttempts}).",
                    candidateProvider.Name,
                    attemptIndex + 1,
                    streamMaxAttempts);

                await enumerator.DisposeAsync().ConfigureAwait(false);

                lease.Dispose();

                if (retryableMoveFailure)
                {
                    continue;
                }

                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    BuildInferenceFailureMessage(candidateProvider));

                yield break;
            }

            if (!hasFirst)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);

                lease.Dispose();

                yield break;
            }

            IntelligenceEvent firstEvent = enumerator.Current;

            bool firstIsRetryableConnectivityError = firstEvent.Type == IntelligenceEventType.Error
                && classification.IsConnectivityFailure
                && !isLastAttempt;

            if (firstIsRetryableConnectivityError)
            {
                healthTracker!.MarkFailed(candidateProvider.Name);

                logger.LogWarning(
                    "Provider {ProviderName} streaming connection failed (fallback attempt {Attempt}/{MaxAttempts}); trying next candidate.",
                    candidateProvider.Name,
                    attemptIndex + 1,
                    streamMaxAttempts);

                await enumerator.DisposeAsync().ConfigureAwait(false);

                lease.Dispose();

                continue;
            }

            healthTracker!.MarkHealthy(candidateProvider.Name);

            Exception? midStreamFailure = null;

            try
            {
                yield return firstEvent;

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
                logger.LogError(
                    midStreamFailure,
                    "Streaming inference threw mid-stream for provider {ProviderName} model {RequestedModel}.",
                    candidateProvider.Name,
                    request.Model);

                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    PublicInferenceFailureMessage);
            }

            yield break;
        }
    }

    // CS8425: intentionally no [EnumeratorCancellation] parameter — every caller (StreamPromptAsync)
    // passes inferenceToken/callerToken as ordinary arguments and drives the enumerator manually via
    // GetAsyncEnumerator()/MoveNextAsync() rather than `await foreach ... .WithCancellation(...)`.
    // Attributing a parameter here would let an incidental token passed to GetAsyncEnumerator silently
    // override the explicit per-candidate inferenceToken, which is not what the fallback loop wants.
#pragma warning disable CS8425
    private async IAsyncEnumerable<IntelligenceEvent> StreamCommittedInferenceAsync(
        ChatClientLease lease,
        PingRequest request,
        string prompt,
        StreamFailureClassification classification,
        CancellationToken inferenceToken,
        CancellationToken callerToken,
        InferenceAuditContext? auditContext)
#pragma warning restore CS8425
    {

        GrimoireTurnWriter.TurnHandle grimoireTurn = new();

        StringBuilder streamAccumulator = new(1024);

        Stopwatch inferenceStopwatch = Stopwatch.StartNew();

        try
        {
            string targetModel = lease.ResolvedModel;

            IChatClient chatClient = lease.ChatClient;

            yield return new IntelligenceEvent(IntelligenceEventType.Status, "Mage is generating response...");

        Session? thread = await inferenceContextBuilder
            .LoadThreadAsync(request, inferenceToken)
            .ConfigureAwait(false);

        bool attachmentsEnabled = settings.Value.Attachments.Enabled;

        string? streamPendingTurnId = null;

        if (attachmentsEnabled
            && request.SessionId is null
            && HasSessionAttachmentPayload(request))
        {
            streamPendingTurnId = Guid.NewGuid().ToString("N");
        }

        bool streamTurnBegunEarly = false;

        if (attachmentsEnabled && !InferenceContextBuilder.HasStatelessMessages(request))
        {
            grimoireTurn = await grimoireTurnWriter
                .TryBeginStreamedAssistantReplyAsync(request, prompt, targetModel, inferenceToken)
                .ConfigureAwait(false);

            streamTurnBegunEarly = true;
        }

        SessionAttachmentTurnPreparation streamAttachmentPrep = await SessionAttachmentTurnService
            .PrepareAsync(
                request,
                sessionAttachmentStore,
                settings.Value,
                grimoireTurn.SessionId,
                grimoireTurn.AssistantEntryId,
                streamPendingTurnId,
                inferenceToken)
            .ConfigureAwait(false);

        if (streamAttachmentPrep.ErrorMessage is not null)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                streamAttachmentPrep.ErrorMessage);

            if (!grimoireTurn.IsFinalized)
            {
                await grimoireTurnWriter
                    .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            yield break;
        }

        AttachmentsSettings streamAttachmentSettings = settings.Value.Attachments ?? new AttachmentsSettings();

        int streamMaxRefs = ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn(
            streamAttachmentSettings.MaxReferencesPerTurn);

        SessionAttachmentTurnBudget.BeginTurn(
            streamMaxRefs,
            request.AttachmentReferences?.Count ?? 0);

        List<MeAiChatMessage> chatMessages = InferenceContextBuilder.BuildInitialMeAiChatMessages(request, thread, prompt);

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
                streamSpellWorkspaceRoot,
                inferenceToken).ConfigureAwait(false);

            if (streamRoutedSpell.IsFailure)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    streamRoutedSpell.Error.Message);

                yield break;
            }

            streamResolvedSpell = streamRoutedSpell.Value;
        }

        ParsedSpell? streamActiveSpell = streamResolvedSpell?.Primary;

        IReadOnlyList<ParsedSpell>? streamResonants = streamResolvedSpell?.Resonants;

        Embedding<float>? streamQueryEmbedding = await ResolveRagQueryEmbeddingAsync(request, inferenceToken).ConfigureAwait(false);

        SemanticContextChunk[]? streamSemanticContext = await RetrieveSemanticContextAsync(request, streamQueryEmbedding, inferenceToken).ConfigureAwait(false);

        SagaMemory[]? streamSagaMemories = await RetrieveSagaMemoriesAsync(streamQueryEmbedding, inferenceToken).ConfigureAwait(false);

        IReadOnlyList<LexiconEntryDto>? streamLexiconEntries = await RetrieveLexiconEntriesAsync(
            request,
            streamResolvedSpell?.Entities ?? Array.Empty<string>(),
            chatClient,
            inferenceToken).ConfigureAwait(false);

        int streamMaxIndexItems = ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(
            streamAttachmentSettings.MaxIndexItemsInPrompt);

        int streamMaxIndexBytes = ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt(
            streamAttachmentSettings.MaxIndexBytesInPrompt);

        string streamBuiltSystemPrompt = SystemPromptBuilder.Build(
            request,
            streamCodexContent,
            streamActiveSpell,
            request.AttachedFiles,
            dependencySpells: streamResonants,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(settings.Value.Spells.MaxResonantBytes),
            semanticContext: streamSemanticContext,
            sagaMemories: streamSagaMemories,
            lexiconEntries: streamLexiconEntries,
            maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(settings.Value.Intelligence.LexiconMaxInjectedBytes),
            sessionAttachmentsIndex: streamAttachmentPrep.IndexItems,
            maxIndexItems: streamMaxIndexItems,
            maxIndexBytes: streamMaxIndexBytes);

        InferenceContextBuilder.PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

        (bool compressedStream, List<MeAiChatMessage> streamMessages) = inferenceContextBuilder.TryApplyContextCompressionIfNeeded(
            request,
            chatMessages,
            streamCodexContent,
            streamActiveSpell,
            streamResonants,
            thread,
            prompt,
            lease,
            semanticContext: streamSemanticContext,
            sagaMemories: streamSagaMemories,
            lexiconEntries: streamLexiconEntries,
            sessionAttachmentsIndex: streamAttachmentPrep.IndexItems,
            maxIndexItems: streamMaxIndexItems,
            maxIndexBytes: streamMaxIndexBytes);

        chatMessages = streamMessages;

        InferenceContextBuilder.AppendContentsToLastMessage(chatMessages, streamAttachmentPrep.RehydratedContents);

        if (!streamTurnBegunEarly && !InferenceContextBuilder.HasStatelessMessages(request))
        {
            grimoireTurn = await grimoireTurnWriter
                .TryBeginStreamedAssistantReplyAsync(request, prompt, targetModel, inferenceToken)
                .ConfigureAwait(false);
        }

        if (grimoireTurn.SessionId is { } bcid)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.SessionBound,
                "Session started",
                bcid.ToString());

            yield return new IntelligenceEvent(
                IntelligenceEventType.ConversationBound,
                "Conversation started",
                bcid.ToString());

            if (streamAttachmentPrep.PendingTurnId is not null)
            {
                string? promoteError = null;

                try
                {
                    await sessionAttachmentStore
                        .PromotePendingAsync(
                            streamAttachmentPrep.PendingTurnId,
                            bcid,
                            grimoireTurn.AssistantEntryId,
                            inferenceToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to promote pending session attachments for session {SessionId}.", bcid);

                    promoteError = string.IsNullOrWhiteSpace(ex.Message)
                        ? "Session attachment promotion failed."
                        : ex.Message;
                }

                if (promoteError is not null)
                {
                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        promoteError);

                    if (!grimoireTurn.IsFinalized)
                    {
                        await grimoireTurnWriter
                            .ResolveInterruptedAsync(grimoireTurn, null, inferenceToken)
                            .ConfigureAwait(false);
                    }

                    yield break;
                }
            }
        }

        if (compressedStream)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Status, IntelligenceStatusMessages.MemoryCompressionNotice);
        }

        List<AITool> streamToolSet = request.ForwardClientTools
            ? BuildClientForwardedToolSet(request)
            : await BuildToolSetWithMcpAsync(
                request,
                streamResolvedSpell,
                grimoireTurn.SessionId ?? request.SessionId,
                inferenceToken).ConfigureAwait(false);

        ToolExecutionPipeline.TurnContext streamTurnContext = await BuildTurnContextAsync(request, streamToolSet, inferenceToken).ConfigureAwait(false);

        bool streamUsesTools = true;

        string? inferenceError;

        string? streamFinishReason = null;

        ChatCompletionUsage? streamAccumulatedUsage = null;

        int streamMaxToolRounds = ArcanumSettingClamps.MaxToolInferenceRounds(settings.Value.Intelligence.MaxToolInferenceRounds);

        string guardrailsStreamingMode = ArcanumSettingClamps.GuardrailsStreamingMode(
            settings.Value.Guardrails.StreamingMode);

        bool guardrailsOutputActive = settings.Value.Guardrails.Enabled
            && (settings.Value.Guardrails.BlockToxicity
                || settings.Value.Guardrails.BlockedTopics is { Length: > 0 });

        bool bufferTokens = (guardrailsStreamingMode == "buffered" && guardrailsOutputActive)
            || (request.ResponseFormat is "json_schema" && settings.Value.StructuredOutput.Enabled && settings.Value.StructuredOutput.StrictMode);

        while (true)
        {
            bool streamOuterRestart = false;

            streamAccumulator.Clear();

            streamAccumulatedUsage = null;

            ChatOptions streamChatOptions = CreateInferenceChatOptions(streamUsesTools, streamTurnContext.InferenceTools, request, lease);

            int streamToolRoundCount = 0;

            inferenceError = null;

            Exception? streamingMoveNextFailure = null;

            while (true)
            {
                List<ChatResponseUpdate> roundUpdates = [];

                IAsyncEnumerator<ChatResponseUpdate> streamEnumerator = chatClient
                    .GetStreamingResponseAsync(chatMessages, streamChatOptions, inferenceToken)
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
                        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
                        {

                            logger.LogWarning("Inference wall-clock timeout exceeded for model {ModelName}.", targetModel);

                            inferenceError = PublicInferenceTimeoutMessage;

                            classification.IsConnectivityFailure = true;

                            break;

                        }
                        catch (OperationCanceledException)
                        {

                            throw;

                        }
                        catch (Exception ex)
                        {
                            streamingMoveNextFailure = ex;

                            logger.LogError(ex, "Streaming read failed for model {ModelName}.", targetModel);

                            inferenceError = BuildInferenceFailureMessage(lease);

                            // Matches the buffered inference path (AttemptBufferedInferenceAsync):
                            // only a genuine connectivity failure should trigger provider fallback.
                            // A blanket `true` here would also fall back on model/auth/400-class
                            // errors (e.g. a bad request shape, an invalid API key, content-policy
                            // rejections) that have nothing to do with the provider being reachable.
                            classification.IsConnectivityFailure = IsConnectivityFailure(ex, callerToken);

                            break;
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        ChatResponseUpdate update = streamEnumerator.Current;

                        if (string.IsNullOrEmpty(update.Text))
                        {

                            roundUpdates.Add(update);

                            continue;

                        }

                        _ = streamAccumulator.Append(update.Text);

                        if (!bufferTokens)
                        {

                            yield return new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, update.Text);

                        }
                    }
                }
                finally
                {
                    await streamEnumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (inferenceError is not null)
                {
                    if (streamUsesTools

                        && streamingMoveNextFailure is { Message: var moveMsg }

                        && LooksLikeModelDoesNotSupportTools(moveMsg)

                        && streamAccumulator.Length == 0)
                    {
                        logger.LogInformation(
                            streamingMoveNextFailure,
                            "Model {ModelName} does not support tools; retrying stream without local tools.",
                            targetModel);

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Status,
                            "This model does not support tools; continuing without local tools.");

                        streamUsesTools = false;

                        inferenceError = null;

                        streamingMoveNextFailure = null;

                        classification.IsConnectivityFailure = false;

                        streamOuterRestart = true;
                    }

                    break;
                }

                ChatResponse combinedRound = roundUpdates.ToChatResponse();

                streamAccumulatedUsage = AccumulateUsage(streamAccumulatedUsage, MapUsageDetails(combinedRound.Usage));

                List<FunctionCallContent> toolCalls = ToolExecutionPipeline.CollectActionableFunctionCalls(combinedRound);

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

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.ToolCall,
                            fcc.Name ?? string.Empty,
                            toolCallData,
                            null,
                            new IntelligenceToolCallEvent(callId, fcc.Name ?? string.Empty, argsSnapshot, forwardToolCallIndex, PreserveProviderCallId: true));

                        forwardToolCallIndex++;
                    }

                    streamFinishReason = "tool_calls";

                    break;
                }

                streamToolRoundCount++;

                if (streamToolRoundCount > streamMaxToolRounds)
                {
                    logger.LogError(
                        "Streaming inference exceeded tool round limit for model {ModelName}.",
                        targetModel);

                    inferenceError = "Tool invocation limit reached.";

                    break;
                }

                int toolCallIndex = 0;

                Guid? streamAmbientSessionId = grimoireTurn.SessionId ?? request.SessionId;

                SessionAttachmentToolAmbient.CurrentSessionId = streamAmbientSessionId;

                try
                {
                    foreach (FunctionCallContent fcc in toolCalls)
                    {
                        string argsSnapshot = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(fcc);

                        string toolCallData = ToolExecutionPipeline.FormatToolCallEventData(fcc, argsSnapshot);

                        string callId = toolExecutionPipeline.ResolveCallId(fcc);

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.ToolCall,
                            fcc.Name ?? string.Empty,
                            toolCallData,
                            null,
                            new IntelligenceToolCallEvent(callId, fcc.Name ?? string.Empty, argsSnapshot, toolCallIndex));

                        ToolExecutionPipeline.ProcessedToolCall processed = await toolExecutionPipeline
                            .ProcessSingleToolCallAsync(
                                fcc,
                                request,
                                streamChatOptions,
                                streamActiveSpell,
                                grimoireTurn.SessionId?.ToString(),
                                streamTurnContext,
                                suppressInvocationFailures: true,
                                inferenceToken)
                            .ConfigureAwait(false);

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
                                new IntelligenceToolCallEvent(processed.CallId, processed.ToolName, processed.ResultText, toolCallIndex));
                        }

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.ToolResult,
                            processed.ToolName,
                            processed.ResultText,
                            null,
                            new IntelligenceToolCallEvent(processed.CallId, processed.ToolName, processed.ResultText, toolCallIndex));

                        auditContext?.ToolNames.Add(processed.ToolName);

                        auditContext?.ToolArgumentsJson.Add(processed.ArgsSnapshot);

                        ToolExecutionPipeline.AppendToolExchangeToMessages(
                            chatMessages,
                            fcc,
                            processed.CallId,
                            processed.ResultText);

                        if (processed.AdditionalContextContents is { Count: > 0 } extras)
                        {
                            // Prefer a User message so vision providers receive DataContent on the next round
                            // (Tool-role messages are a poor carrier for multimodal payload).
                            chatMessages.Add(new MeAiChatMessage(ChatRole.User, extras.ToList()));
                        }

                        await grimoireTurnWriter.TryAppendToolInteractionAsync(
                            grimoireTurn.SessionId,
                            processed.ToolName,
                            processed.ArgsSnapshot,
                            processed.ResultText,
                            targetModel,
                            inferenceToken)
                            .ConfigureAwait(false);

                        toolCallIndex++;
                    }
                }
                finally
                {
                    SessionAttachmentToolAmbient.CurrentSessionId = null;
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
            if (!grimoireTurn.IsFinalized)
            {
                // W3.5: cleanup must use CancellationToken.None, not the (often already-cancelled)
                // inferenceToken — otherwise ResolveInterruptedAssistantEntryAsync rethrows OCE here
                // and the terminal Error event below is never emitted to the client.
                await grimoireTurnWriter.ResolveInterruptedAndMarkFinalizedAsync(
                    grimoireTurn,
                    streamAccumulator.Length > 0 ? streamAccumulator.ToString() : null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            yield return new IntelligenceEvent(IntelligenceEventType.Error, inferenceError);

            yield break;
        }

        string finalText = streamAccumulator.ToString();

        Result guardrailsStreamOutput = await FilterGuardrailsOutputAsync(
            finalText,
            grimoireTurn.SessionId,
            targetModel,
            inferenceToken).ConfigureAwait(false);

        if (guardrailsStreamOutput.IsFailure)
        {
            if (!grimoireTurn.IsFinalized)
            {
                await grimoireTurnWriter
                    .ResolveInterruptedAndMarkFinalizedAsync(grimoireTurn, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            yield return new IntelligenceEvent(IntelligenceEventType.Error, guardrailsStreamOutput.Error.Message);

            yield break;
        }

        IReadOnlyList<string> streamWarnings = [];

        if (request.ResponseFormat is "json_schema"
            && request.ResponseFormatJsonSchema is { } streamJsonSchemaWrapper
            && settings.Value.StructuredOutput.Enabled)
        {

            JsonElement streamSchemaElement = streamJsonSchemaWrapper;

            if (streamJsonSchemaWrapper.ValueKind == JsonValueKind.Object
                && streamJsonSchemaWrapper.TryGetProperty("schema", out JsonElement streamNestedSchema))
            {

                streamSchemaElement = streamNestedSchema;

            }

            using JsonDocument streamSchema = JsonDocument.Parse(streamSchemaElement.GetRawText());

            Result<JsonSchemaDefinition> streamParseResult = JsonSchemaHelper.Parse(
                streamSchema,
                ArcanumSettingClamps.JsonSchemaMaxDepth(settings.Value.StructuredOutput.SchemaMaxDepth));

            if (streamParseResult.IsSuccess)
            {

                ValidationResult streamValidation = JsonSchemaHelper.Validate(
                    finalText,
                    streamParseResult.Value,
                    ArcanumSettingClamps.JsonSchemaMaxDepth(settings.Value.StructuredOutput.SchemaMaxDepth));

                if (!streamValidation.IsValid)
                {

                    if (settings.Value.StructuredOutput.StrictMode)
                    {

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            ErrorCodes.StructuredOutput.ValidationFailed + ": streamed response failed JSON schema validation after generation: "
                                + string.Join("; ", streamValidation.Errors));

                        yield break;

                    }

                    streamWarnings = ["streamed response failed JSON schema validation: " + string.Join("; ", streamValidation.Errors)];

                }

            }
            else
            {

                if (settings.Value.StructuredOutput.StrictMode)
                {

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        ErrorCodes.StructuredOutput.SchemaInvalid + ": invalid JSON schema for streamed structured output: " + streamParseResult.Error.Message);

                    yield break;

                }

                streamWarnings = ["invalid JSON schema for streamed structured output: " + streamParseResult.Error.Message];

            }

        }

        if (bufferTokens)
        {

            yield return new IntelligenceEvent(
                IntelligenceEventType.Token,
                string.Empty,
                finalText);

        }

        bool streamFinalizeOk = await grimoireTurnWriter
            .TryFinalizeStreamedAssistantEntryAsync(grimoireTurn, finalText, targetModel, inferenceToken)
            .ConfigureAwait(false);

        if (!streamFinalizeOk)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                GrimoireTurnWriter.PublicFinalizeFailureMessage);

            yield break;
        }

        await TryIncrementSessionTokensAsync(grimoireTurn.SessionId, streamAccumulatedUsage, targetModel, inferenceToken)
            .ConfigureAwait(false);

        TryEnqueueSagaExtraction(grimoireTurn.SessionId);

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
                CancellationToken.None).ConfigureAwait(false);
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
            SessionAttachmentTurnBudget.EndTurn();

            await grimoireTurnWriter
                .TryResolveInterruptedOnStreamExitAsync(
                    grimoireTurn,
                    streamAccumulator.Length > 0 ? streamAccumulator.ToString() : null)
                .ConfigureAwait(false);
        }
    }

    private async Task<ToolExecutionPipeline.TurnContext> BuildTurnContextAsync(
        PingRequest request,
        IReadOnlyList<AITool> toolSet,
        CancellationToken cancellationToken)
    {
        Campaign? campaign = null;

        string? campaignId = null;

        string? workspaceRoot = null;

        bool campaignRequiresWard = true;

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

                CampaignSettings campaignSettings = CampaignRepository.DeserializeSettings(campaign.Settings);

                campaignRequiresWard = campaignSettings.RequireWardForForbiddenArts;

                SanctumConfig sanctumConfig = CampaignRepository.GetSanctumConfig(campaign);

                sanctumEnabled = sanctumConfig.Enabled;

                sanctumMode = sanctumConfig.Mode;
            }
        }

        IReadOnlyList<AITool> inferenceTools = ApplyToolPolicyFilters(request, toolSet);

        inferenceTools = request.UnattendedMode
            ? FilterToolsForUnattended(inferenceTools)
            : inferenceTools;

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
            CampaignRequiresWard = campaignRequiresWard,
            SanctumEnabled = sanctumEnabled,
            SanctumMode = sanctumMode,
            InferenceTools = inferenceTools,
            SpellScriptRoots = spellScriptRoots,
        };
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

    private async Task<Result<ResolvedSpell?>> ResolveRoutedSpellAsync(
        PingRequest request,
        IChatClient chatClient,
        string? spellWorkspaceRoot,
        CancellationToken cancellationToken)
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
                    logger,
                    maxResonantDependencies: ArcanumSettingClamps.MaxResonantDependencies(settings.Value.Spells.MaxResonantDependencies))
                .ConfigureAwait(false);

            return Result<ResolvedSpell?>.Success(resolvedOverride);
        }

        IReadOnlyList<SpellMetadata> spellMetadata = await SpellScanner
            .ScanMetadataAsync(
                spellWorkspaceRoot,
                cancellationToken,
                maxSpellFileSizeBytes,
                ArcanumSettingClamps.MetadataScanCacheTtlSeconds(settings.Value.Spells.MetadataScanCacheTtlSeconds))
            .ConfigureAwait(false);

        SpellMetadata? matchedMetadata;

        IReadOnlyList<string> routerEntities = Array.Empty<string>();

        if (!string.IsNullOrWhiteSpace(request.OverrideSpellName))
        {
            if (!TryResolveOverrideSpellMetadata(request.OverrideSpellName, spellMetadata, out SpellMetadata? overridePick))
            {
                return Result<ResolvedSpell?>.Failure(
                    new Error(
                        "Validation.SpellOverride",
                        $"No spell matches OverrideSpellName '{request.OverrideSpellName.Trim()}'. Expected a SPELL.md frontmatter name or parent folder name."));
            }

            matchedMetadata = overridePick;
        }
        else
        {
            string semanticProbe = GetSemanticRouterUserProbe(request);

            SpellRoutingDecision routingDecision = await semanticSpellRouter
                .ResolveAsync(spellMetadata, semanticProbe, cancellationToken)
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
                    ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds(settings.Value.Intelligence.SemanticRouterPreflightTimeoutSeconds));

                int routerMaxTokens = ArcanumSettingClamps.SemanticRouterMaxTokens(
                    settings.Value.Intelligence.SemanticRouterMaxTokens);

                float routerTemperature = ArcanumSettingClamps.SemanticRouterTemperature(
                    settings.Value.Intelligence.SemanticRouterTemperature);

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
                    if (settings.Value.Intelligence.UseFastModelForSpellRouting
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
                            candidates)
                        .ConfigureAwait(false);

                    matchedMetadata = routingResult?.Spell;

                    routerEntities = routingResult?.Entities ?? Array.Empty<string>();
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
                spellMetadata,
                maxResonantDependencies: ArcanumSettingClamps.MaxResonantDependencies(settings.Value.Spells.MaxResonantDependencies))
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
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Intelligence.EnableLexiconSystem)
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
                if (settings.Value.Intelligence.UseFastModelForSpellRouting
                    && !string.IsNullOrWhiteSpace(settings.Value.FastModel))
                {
                    extractorLease = await chatClientFactory
                        .ResolveClientAsync(settings.Value.FastModel.Trim(), cancellationToken)
                        .ConfigureAwait(false);

                    extractorClient = extractorLease.ChatClient;
                }

                TimeSpan preflight = TimeSpan.FromSeconds(
                    ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds(settings.Value.Intelligence.SemanticRouterPreflightTimeoutSeconds));

                entities = await LexiconEntityExtractor
                    .ExtractAsync(extractorClient, GetSemanticRouterUserProbe(request), preflight, cancellationToken, logger)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Lexicon entity extraction failed; continuing without Lexicon context.");

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

        int limit = ArcanumSettingClamps.LexiconMaxMatchedEntries(settings.Value.Intelligence.LexiconMaxMatchedEntries);

        Result<IReadOnlyList<LexiconEntryDto>> match = await lexiconService
            .MatchEntitiesAsync(entities, limit, cancellationToken)
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
        EmbeddingSettings embeddings = settings.Value.Embeddings ?? new EmbeddingSettings();

        bool needsCodebaseEmbedding = embeddings.Enabled
            && embeddings.CodebaseRetrievalEnabled
            && !string.IsNullOrWhiteSpace(request.WorkingDirectory);

        bool needsSagaEmbedding = embeddings.Enabled && embeddings.SagaEnabled;

        if (!needsCodebaseEmbedding && !needsSagaEmbedding)
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
            logger.LogDebug(ex, "RAG query embedding threw; semantic retrieval for this turn will be skipped.");

            return null;
        }
    }

    /// <summary>
    /// RAG Phase 3 — retrieves semantically relevant workspace file chunks for the current turn's
    /// prompt, for injection into the system prompt (see <c>SystemPromptBuilder.Build</c>'s
    /// <c>semanticContext</c> parameter). Called from both <see cref="AttemptBufferedInferenceAsync"/>
    /// and <see cref="StreamCommittedInferenceAsync"/> before the system prompt is built, sharing
    /// <paramref name="queryEmbedding"/> with <see cref="RetrieveSagaMemoriesAsync"/> (see
    /// <see cref="ResolveRagQueryEmbeddingAsync"/>) to avoid embedding the same prompt twice.
    ///
    /// Graceful degradation: returns <c>null</c> (never throws for expected failure modes) when the
    /// feature is disabled, <see cref="PingRequest.WorkingDirectory"/> is empty, the query embedding is
    /// unavailable, Divination fails, or no chunks are found above the similarity threshold — in every
    /// case the inference turn proceeds with an unchanged system prompt (DESIGN.md §21.4).
    /// </summary>
    private async Task<SemanticContextChunk[]?> RetrieveSemanticContextAsync(
        PingRequest request,
        Embedding<float>? queryEmbedding,
        CancellationToken cancellationToken)
    {
        EmbeddingSettings embeddings = settings.Value.Embeddings ?? new EmbeddingSettings();

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
            logger.LogDebug(ex, "Failed to register workspace {WorkingDirectory} for background indexing.", request.WorkingDirectory);
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
            logger.LogDebug(ex, "Semantic context retrieval failed; continuing without it.");

            return null;
        }
    }

    /// <summary>
    /// RAG Phase 4 — retrieves Saga memories relevant to the current turn's prompt, for injection into
    /// the system prompt (see <c>SystemPromptBuilder.Build</c>'s <c>sagaMemories</c> parameter). Called
    /// from both <see cref="AttemptBufferedInferenceAsync"/> and <see cref="StreamCommittedInferenceAsync"/>
    /// alongside <see cref="RetrieveSemanticContextAsync"/>, sharing the same
    /// <paramref name="queryEmbedding"/> (see <see cref="ResolveRagQueryEmbeddingAsync"/>).
    ///
    /// Graceful degradation: returns <c>null</c> (never throws for expected failure modes) when the
    /// feature is disabled, the query embedding is unavailable, Divination fails, or no memories are
    /// found above the similarity threshold — in every case the inference turn proceeds with an
    /// unchanged system prompt (DESIGN.md §21.4).
    /// </summary>
    private async Task<SagaMemory[]?> RetrieveSagaMemoriesAsync(Embedding<float>? queryEmbedding, CancellationToken cancellationToken)
    {
        EmbeddingSettings embeddings = settings.Value.Embeddings ?? new EmbeddingSettings();

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

            Result<DivinationResult[]> searchResult = await divinationService
                .SearchAsync(
                    "saga_memory_embeddings_vec",
                    "MemoryId",
                    "Embedding",
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
                    memories.Add(new SagaMemory(memory.Content, hit.Similarity, memory.CreatedAt));
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
            logger.LogDebug(ex, "Saga memory retrieval failed; continuing without it.");

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
        CancellationToken cancellationToken)
    {
        string workingDirectory = request.WorkingDirectory;

        ParsedSpell? activeSpell = resolvedSpell?.Primary;

        List<AITool> tools = [_localTimeTool, _systemInfoTool];

        List<string> scriptRoots = CollectScriptRoots(resolvedSpell);

        if (scriptRoots.Count > 0)
        {
            int sec = ArcanumSettingClamps.ExecuteCommandTimeoutSeconds(settings.Value.Intelligence.ExecuteCommandTimeoutSeconds);

            long outputCap = ArcanumSettingClamps.ToolOutputCapBytes(settings.Value.Intelligence.ToolOutputCapBytes);

            tools.Add(new ArcanumSpellScriptTool(
                scriptRoots,
                TimeSpan.FromSeconds(sec),
                sec,
                outputCap,
                logger,
                sanctumGuard,
                processResourceLimiter,
                workingDirectory,
                settings.Value.Security.AllowUnsandboxedToolChildren));
        }

        if (settings.Value.WebBrowsing.Enabled)
        {
            tools.Add(new ArcanumBrowseWebTool(httpClientFactory, settings, logger));
        }

        if (ShouldDisableMcpTools(request))
        {
            return tools;
        }

        IReadOnlyList<AITool> mcpTools = await mcpConnectionManager
            .GetAvailableToolsAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        AttunementResult attunement = ArtifactAttunement.ApplyAttunement(
            mcpTools,
            activeSpell?.SkillMetadata?.DeclaredTools);

        if (attunement.Excluded.Count > 0)
        {
            logger.LogDebug(
                "Artifact Attunement: spell {Spell} restricted MCP toolset to {Allowed}/{Total}; excluded {Count}: {Names}",
                activeSpell?.Name ?? "(none)",
                attunement.Allowed.Count,
                mcpTools.Count,
                attunement.Excluded.Count,
                string.Join(", ", attunement.Excluded));
        }

        AttachmentsSettings attachments = settings.Value.Attachments ?? new AttachmentsSettings();

        bool advertiseAttachTool = attachments.Enabled
            && attachments.EnableModelAttachTool
            && (request.SessionId.HasValue || sessionIdForTurn.HasValue);

        foreach (AITool t in attunement.Allowed)
        {
            if (!advertiseAttachTool
                && string.Equals(t.Name, "attach_session_file", StringComparison.Ordinal))
            {
                continue;
            }

            tools.Add(t);
        }

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

    private static bool ShouldDisableMcpTools(PingRequest request)
    {
        if (request.ToolPolicy is ToolPolicy.NoTools)
        {
            return true;
        }

        if (request.ToolPolicy is null or ToolPolicy.AllTools)
        {
            return request.DisableMcpTools;
        }

        return false;
    }

    private IReadOnlyList<AITool> ApplyToolPolicyFilters(PingRequest request, IReadOnlyList<AITool> tools)
    {
        if (request.ToolPolicy is null or ToolPolicy.AllTools or ToolPolicy.NoTools)
        {
            return tools;
        }

        if (request.ToolPolicy is ToolPolicy.ReadOnlyTools)
        {
            return FilterToolsToAllowlist(tools, ReadOnlyToolNames);
        }

        if (request.ToolPolicy is ToolPolicy.NoForbiddenArts)
        {
            WardSettings wardSettings = settings.Value.Ward ?? new WardSettings();

            return FilterToolsExcludingNames(tools, wardSettings.ForbiddenArts);
        }

        return tools;
    }

    private static readonly HashSet<string> ReadOnlyToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file_chunk",
        "list_directory",
        "read_lore",
        "search_archives",
        "ask_human",
        "use_commlink",
        "petition_dungeon_master",
        ArcanumLocalTimeTool.ToolName,
        ArcanumSystemInfoTool.ToolName,
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

        ApplyClientToolMode(options, request);

        if (!includeTools || tools is null)
        {
            return options;
        }

        options.Tools = tools.ToList();

        return options;
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

        int prompt = ClampUsageToInt(usage.InputTokenCount ?? 0L);

        int completion = ClampUsageToInt(usage.OutputTokenCount ?? 0L);

        int total = ClampUsageToInt((long)prompt + completion);

        int cached = ReadCachedTokens(usage);

        return new ChatCompletionUsage(prompt, completion, total, cached);
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

    private static ChatCompletionUsage AccumulateUsage(ChatCompletionUsage? running, ChatCompletionUsage? round)
    {
        int p = (running?.PromptTokens ?? 0) + (round?.PromptTokens ?? 0);

        int c = (running?.CompletionTokens ?? 0) + (round?.CompletionTokens ?? 0);

        int cached = (running?.CachedTokens ?? 0) + (round?.CachedTokens ?? 0);

        return new ChatCompletionUsage(p, c, p + c, cached);
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

        // Prompt-cache metrics: only record when the provider has not explicitly disabled caching
        // (SupportsPromptCaching defaults to true for OpenAI-compatible providers). Labels are
        // strictly low-cardinality provider + model to keep Prometheus cardinality bounded.
        bool cachingSupported = provider.SupportsPromptCaching ?? provider.Type == AiProviderKind.OpenAICompatible;

        if (cachingSupported && usage.CachedTokens > 0)
        {

            ArcanumMetrics.PromptCacheTokensTotal.Add(
                usage.CachedTokens,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", model));

            ArcanumMetrics.PromptCacheHitsTotal.Add(
                1,
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
                CampaignId: campaignId?.ToString());

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
            logger.LogWarning(ex, "Failed to write inference audit record; continuing without it.");

        }

    }

    private async Task TryIncrementSessionTokensAsync(
        Guid? sessionId,
        ChatCompletionUsage? usage,
        string? model,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Intelligence.EnableTokenTracking || !sessionId.HasValue || usage is null || usage.TotalTokens <= 0)
        {
            return;
        }

        try
        {
            ModelPricingEntry pricing = settings.Value.Pricing.DefaultPricing;

            if (model is not null
                && settings.Value.Pricing.ModelPricing.TryGetValue(model, out ModelPricingEntry? explicitPricing))
            {
                pricing = explicitPricing;
            }

            long billablePromptTokens = Math.Max(0L, usage.PromptTokens - usage.CachedTokens);

            decimal costUsd = CostCalculator.CalculateCost(billablePromptTokens, usage.CompletionTokens, pricing);

            await grimoire
                .IncrementSessionTokensAndCostAsync(sessionId.Value, usage.TotalTokens, costUsd, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grimoire could not increment token totals for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// RAG Phase 4 — enqueues Saga memory extraction for <paramref name="sessionId"/> after a
    /// successful inference turn. Fire-and-forget: enqueue failures never affect the completed turn
    /// (see <see cref="SagaExtractionService.EnqueueExtraction"/>, which itself never throws — the
    /// try/catch here is defense in depth against unexpected failures resolving settings).
    /// </summary>
    private void TryEnqueueSagaExtraction(Guid? sessionId)
    {
        if (!sessionId.HasValue)
        {
            return;
        }

        try
        {
            EmbeddingSettings embeddings = settings.Value.Embeddings ?? new EmbeddingSettings();

            if (!embeddings.Enabled || !embeddings.SagaEnabled || !embeddings.Saga.ExtractionEnabled)
            {
                return;
            }

            sagaExtractionService.EnqueueExtraction(sessionId.Value);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enqueue Saga extraction for session {SessionId}.", sessionId);
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


    /// <summary>
    /// Scrying — early capability/shape gate, run before any inference token is consumed (right
    /// alongside the other request-shape validators, ahead of model-resolution/lease work). Validates
    /// image count/MIME/size via <see cref="ScryingValidator"/>, then — only when the request
    /// actually carries images — resolves the intended model (mirroring
    /// <see cref="ProviderResolver.TryResolveProviderForModel"/>'s no-resilience resolution, since
    /// vision support is a client-input mismatch, not a provider-connectivity concern, so it is never
    /// retried across fallback candidates) and rejects with <see cref="ErrorCodes.Scrying.VisionNotSupported"/>
    /// when that model does not declare vision support. Model-resolution failure here is not itself an
    /// error — the existing Hub.Model failure path (single-lease or fallback resolution) reports it.
    /// </summary>
    private Result ValidateScryingGate(PingRequest request)
    {

        if (!ScryingValidator.RequestContainsImages(request))
        {
            return Result.Success();
        }

        ScryingSettings scrying = settings.Value.Scrying ?? new ScryingSettings();

        Result shapeValidation = ScryingValidator.ValidateRequestImages(request, scrying);

        if (shapeValidation.IsFailure)
        {
            return shapeValidation;
        }

        if (ProviderResolver.TryResolveProviderForModel(settings.Value, request.Model, out ProviderSettings? provider, out string resolvedModel)
            && provider is not null
            && !ProviderResolver.SupportsVision(provider, resolvedModel))
        {
            return Result.Failure(new Error(
                ErrorCodes.Scrying.VisionNotSupported,
                $"Model '{resolvedModel}' does not support vision. Use a vision-capable model."));
        }

        return Result.Success();

    }

    private static bool HasSessionAttachmentPayload(PingRequest request) =>
        request.AttachedFiles is { Count: > 0 }
        || request.ScryingFoci is { Count: > 0 }
        || request.AttachmentReferences is { Count: > 0 };

    private bool TryValidateAttachedFiles(PingRequest request, out Error error)
    {
        List<AttachedFileDto>? files = request.AttachedFiles;

        if (files is null || files.Count == 0)
        {
            error = Error.None;

            return true;
        }

        long maxBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(settings.Value.Cli.MaxAttachFileSizeBytes);

        int maxFiles = ArcanumSettingClamps.MaxAttachedFilesPerRequest(settings.Value.Cli.MaxAttachedFilesPerRequest);

        int maxPathChars = ArcanumSettingClamps.MaxAttachedFileRelativePathChars(
            settings.Value.Cli.MaxAttachedFileRelativePathChars);

        if (files.Count > maxFiles)
        {
            error = new Error(
                ErrorCodes.Validation.AttachedFiles,
                $"At most {maxFiles} attached files are allowed per request.");

            return false;
        }

        long maxTotalBytes = maxBytes * maxFiles;

        long totalUtf8 = 0;

        for (int i = 0; i < files.Count; i++)
        {
            AttachedFileDto? item = files[i];

            if (item is null)
            {
                error = new Error(ErrorCodes.Validation.AttachedFiles, "Attached file entries cannot be null.");

                return false;
            }

            if (string.IsNullOrWhiteSpace(item.RelativePath))
            {
                error = new Error(
                    ErrorCodes.Validation.AttachedFiles,
                    "Each attached file must have a non-empty relative path.");

                return false;
            }

            if (item.RelativePath.Length > maxPathChars)
            {
                error = new Error(ErrorCodes.Validation.AttachedFiles, "Attached file path is too long.");

                return false;
            }

            string content = item.Content ?? string.Empty;

            long utf8Len = Encoding.UTF8.GetByteCount(content);

            if (utf8Len > maxBytes)
            {
                error = new Error(
                    ErrorCodes.Validation.AttachedFiles,
                    $"Attached file content exceeds the maximum size ({maxBytes} bytes UTF-8).");

                return false;
            }

            totalUtf8 += utf8Len;

            if (totalUtf8 > maxTotalBytes)
            {
                error = new Error(
                    ErrorCodes.Validation.AttachedFiles,
                    "Total size of attached files exceeds the allowed limit for this request.");

                return false;
            }
        }

        error = Error.None;

        return true;

    }

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

    private static string BuildInferenceFailureMessage(ChatClientLease lease) =>
        BuildInferenceFailureMessage(lease.Provider);

    private static string BuildInferenceFailureMessage(ProviderSettings provider)
    {

        // W3.5: do NOT embed provider.Endpoint — this message surfaces to clients via the
        // native /api inference envelopes and the raw endpoint URL can leak internal hostnames/paths.
        // The operator-chosen provider name is retained; endpoint detail stays in server logs.
        return $"Provider '{provider.Name}' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly.";

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

    private readonly record struct InferenceAttemptResult(Result<PromptTurnResult> Result, bool IsConnectivityFailure);

    private sealed class StreamFailureClassification
    {

        public bool IsConnectivityFailure { get; set; }

    }

    private CancellationTokenSource CreateInferenceTimeoutSource(CancellationToken callerToken)
    {

        IntelligenceSettings intelligence = settings.Value.Intelligence ?? new IntelligenceSettings();

        int seconds = ArcanumSettingClamps.InferenceTimeoutSeconds(intelligence.InferenceTimeoutSeconds);

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        linked.CancelAfter(TimeSpan.FromSeconds(seconds));

        return linked;

    }

}
