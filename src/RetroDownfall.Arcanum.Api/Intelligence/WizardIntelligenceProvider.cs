using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using OllamaSharp;
using OllamaSharp.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Serialization;
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
    IGrimoireRepository grimoire,
    IMcpConnectionManager mcpConnectionManager,
    InferenceTokenizerResolver inferenceTokenizerResolver,
    ManaPreflight manaPreflight,
    ICampaignRepository campaignRepository,
    ToolExecutionPipeline toolExecutionPipeline,
    GrimoireTurnWriter grimoireTurnWriter) : IArcanumIntelligenceProvider
{
    private const string PublicInferenceFailureMessage =
        "Inference failed. Ensure Ollama is running and reachable, then try again. See server logs for details.";

    private const string PublicListLocalModelsFailureMessage =
        "Could not list local Ollama models. Ensure Ollama is running and reachable. See server logs for details.";

    private const string PublicModelPullFailureMessage =
        "Model download failed. Ensure Ollama is running and has network access. See server logs for details.";

    private const string PublicModelResolutionFailureMessage =
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.";

    private const string PublicInferenceTimeoutMessage =
        "Inference timed out. Increase Arcanum:Intelligence:InferenceTimeoutSeconds or retry with a shorter prompt.";

    private static readonly ArcanumLocalTimeTool _localTimeTool = new();

    private static readonly ArcanumSystemInfoTool _systemInfoTool = new();

    private static readonly ConcurrentDictionary<string, OllamaModelListCacheEntry> OllamaModelListCache = new(StringComparer.Ordinal);

    private sealed record OllamaModelListCacheEntry(DateTime ExpiresUtc, IReadOnlyList<Model> Models);

    public async Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt;

        if (!TryValidateAttachedFiles(request, out Error attachedFilesError))
        {
            return Result<PromptTurnResult>.Failure(attachedFilesError);
        }

        Result bounds = PingRequestBoundsValidator.Validate(request, settings.Value);

        if (bounds.IsFailure)
        {
            return Result<PromptTurnResult>.Failure(bounds.Error);
        }

        if (!HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Validation.InvalidPrompt, "Prompt is required."));
        }

        CancellationToken callerToken = cancellationToken;

        using CancellationTokenSource inferenceTimeoutCts = CreateInferenceTimeoutSource(callerToken);

        CancellationToken inferenceToken = inferenceTimeoutCts.Token;

        ChatClientLease lease;

        try
        {
            lease = await chatClientFactory.ResolveClientAsync(request.Model, inferenceToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Hub model resolution failed for requested model {RequestedModel}.", request.Model);

            return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Model, PublicModelResolutionFailureMessage));
        }

        using (lease)
        {
            string targetModel = lease.ResolvedModel;

            IChatClient chatClient = lease.ChatClient;

            if (lease.IsOllama)
            {
                Result ensure = await EnsureModelExistsAsync(
                    lease.OllamaApi!,
                    lease.Provider.Endpoint,
                    targetModel,
                    inferenceToken,
                    pullProgress: null).ConfigureAwait(false);

                if (ensure.IsFailure)
                {
                    return Result<PromptTurnResult>.Failure(ensure.Error);
                }
            }

            Session? thread = null;

        if (!HasStatelessMessages(request) && request.SessionId is { } existingSessionId)
        {
            thread = await grimoire
                .GetSessionAsync(existingSessionId, inferenceToken)
                .ConfigureAwait(false);
        }

        GrimoireTurnWriter.TurnHandle grimoireTurn = await grimoireTurnWriter
            .TryBeginBufferedAssistantReplyAsync(request, prompt, targetModel, inferenceToken)
            .ConfigureAwait(false);

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

                return Result<PromptTurnResult>.Failure(routedSpell.Error);
            }

            resolvedSpell = routedSpell.Value;
        }

        ParsedSpell? activeSpell = resolvedSpell?.Primary;

        IReadOnlyList<ParsedSpell>? resonants = resolvedSpell?.Resonants;

        string builtSystemPrompt = SystemPromptBuilder.Build(
            request,
            codexContent,
            activeSpell,
            request.AttachedFiles,
            dependencySpells: resonants,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(settings.Value.Spells.MaxResonantBytes));

        List<AITool> toolSet = await BuildToolSetWithMcpAsync(request, resolvedSpell, inferenceToken).ConfigureAwait(false);

        ToolExecutionPipeline.TurnContext turnContext = await BuildTurnContextAsync(request, toolSet, inferenceToken).ConfigureAwait(false);

        bool inferenceUsesTools = true;

        while (true)
        {
            try
            {
                List<MeAiChatMessage> chatMessages = BuildInitialMeAiChatMessages(request, thread, prompt);

                PrependDynamicSystemMessage(chatMessages, builtSystemPrompt);

                (bool compressedSync, List<MeAiChatMessage> syncMessages) = TryApplyContextCompressionIfNeeded(
                    request,
                    chatMessages,
                    codexContent,
                    activeSpell,
                    resonants,
                    thread,
                    prompt,
                    lease);

                chatMessages = syncMessages;

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

                    toolRoundsExecuted++;

                    if (toolRoundsExecuted > maxToolRounds)
                    {
                        if (!grimoireTurn.IsFinalized)
                        {
                            await grimoireTurnWriter
                                .ResolveInterruptedAsync(grimoireTurn, null, inferenceToken)
                                .ConfigureAwait(false);
                        }

                        return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.ToolLoop, "Tool invocation limit reached."));
                    }

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
                                suppressInvocationFailures: false,
                                inferenceToken)
                            .ConfigureAwait(false);

                        (observedToolCalls ??= []).Add(new PromptToolCall(processed.CallId, processed.ToolName, processed.ArgsSnapshot));

                        ToolExecutionPipeline.AppendToolExchangeToMessages(
                            chatMessages,
                            fcc,
                            processed.CallId,
                            processed.ResultText);

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

                string finalText = response.Text;

                await grimoireTurnWriter
                    .TryFinalizeBufferedAssistantEntryAsync(grimoireTurn, finalText, targetModel, inferenceToken)
                    .ConfigureAwait(false);

                await TryIncrementSessionTokensAsync(
                    grimoireTurn.SessionId,
                    accumulatedUsage,
                    inferenceToken)
                    .ConfigureAwait(false);

                string finishReason = MapChatFinishReasonToOpenAi(response.FinishReason);

                return Result<PromptTurnResult>.Success(new PromptTurnResult(finalText, accumulatedUsage, observedToolCalls, finishReason));
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

                return Result<PromptTurnResult>.Failure(new Error(ErrorCodes.Hub.Timeout, PublicInferenceTimeoutMessage));

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
                        "Model {ModelName} does not support tools in Ollama; retrying without local tools.",
                        targetModel);

                    inferenceUsesTools = false;

                    continue;
                }

                logger.LogError(
                    ex,
                    "{Provider} inference failed for model {ModelName}.",
                    lease.IsOllama ? "Ollama" : "Hub",
                    targetModel);

                if (!grimoireTurn.IsFinalized)
                {
                    // W3.5: non-cancellable cleanup so a cancelled inferenceToken cannot abort the
                    // discard and orphan the in-flight assistant row.
                    await grimoireTurnWriter
                        .ResolveInterruptedAsync(grimoireTurn, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return Result<PromptTurnResult>.Failure(
                    new Error(
                        lease.IsOllama ? ErrorCodes.Ollama.Error : ErrorCodes.Hub.Error,
                        BuildInferenceFailureMessage(lease)));
            }
        }
        }
    }

    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt;

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

        if (!HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, "Prompt is required.");

            yield break;
        }

        CancellationToken callerToken = cancellationToken;

        using CancellationTokenSource inferenceTimeoutCts = CreateInferenceTimeoutSource(callerToken);

        CancellationToken inferenceToken = inferenceTimeoutCts.Token;

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

        ChatClientLease lease = leaseOrNull!;

        GrimoireTurnWriter.TurnHandle grimoireTurn = new();

        StringBuilder streamAccumulator = new(1024);

        try
        {
            string targetModel = lease.ResolvedModel;

            IChatClient chatClient = lease.ChatClient;

            if (lease.IsOllama)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Status,
                    $"Checking local availability for {targetModel}...");

                Result<bool> localCheck = await IsModelLocalAsync(
                    lease.OllamaApi!,
                    lease.Provider.Endpoint,
                    targetModel,
                    inferenceToken).ConfigureAwait(false);

                if (localCheck.IsFailure)
                {
                    yield return new IntelligenceEvent(IntelligenceEventType.Error, PublicListLocalModelsFailureMessage);

                    yield break;
                }

                if (!localCheck.Value)
                {
                    logger.LogInformation(
                        "Model {ModelName} not found locally. Downloading from Ollama... This may take a moment.",
                        targetModel);

                    int lastReportedPercent = -1;

                    IAsyncEnumerator<PullModelResponse> pullEnumerator = EnumeratePullModelAsync(lease.OllamaApi!, targetModel, inferenceToken).GetAsyncEnumerator(inferenceToken);

                    bool pullMoveFailed = false;

                    string? pullMoveError = null;

                    try
                    {
                        while (true)
                        {
                            bool hasNext;

                            try
                            {
                                hasNext = await pullEnumerator.MoveNextAsync().ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Pull stream failed while downloading model {ModelName}.", targetModel);

                                pullMoveFailed = true;

                                pullMoveError = PublicModelPullFailureMessage;

                                break;
                            }

                            if (!hasNext)
                            {
                                break;
                            }

                            PullModelResponse pull = pullEnumerator.Current;

                            int rounded = (int)Math.Round(pull.Percent, MidpointRounding.AwayFromZero);

                            if (rounded != lastReportedPercent)
                            {
                                lastReportedPercent = rounded;

                                yield return new IntelligenceEvent(
                                    IntelligenceEventType.Status,
                                    $"Downloading model {targetModel}: {rounded}%");
                            }
                        }
                    }
                    finally
                    {
                        await pullEnumerator.DisposeAsync().ConfigureAwait(false);
                    }

                    if (pullMoveFailed)
                    {
                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Error,
                            pullMoveError ?? PublicModelPullFailureMessage);

                        yield break;
                    }
                }
            }

            yield return new IntelligenceEvent(IntelligenceEventType.Status, "Mage is generating response...");

        Session? thread = null;

        if (!HasStatelessMessages(request) && request.SessionId is { } existingSessionId)
        {
            thread = await grimoire
                .GetSessionAsync(existingSessionId, inferenceToken)
                .ConfigureAwait(false);
        }

        List<MeAiChatMessage> chatMessages = BuildInitialMeAiChatMessages(request, thread, prompt);

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

        string streamBuiltSystemPrompt = SystemPromptBuilder.Build(
            request,
            streamCodexContent,
            streamActiveSpell,
            request.AttachedFiles,
            dependencySpells: streamResonants,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(settings.Value.Spells.MaxResonantBytes));

        PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

        (bool compressedStream, List<MeAiChatMessage> streamMessages) = TryApplyContextCompressionIfNeeded(
            request,
            chatMessages,
            streamCodexContent,
            streamActiveSpell,
            streamResonants,
            thread,
            prompt,
            lease);

        chatMessages = streamMessages;

        if (!HasStatelessMessages(request))
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
        }

        if (compressedStream)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Status, IntelligenceStatusMessages.MemoryCompressionNotice);
        }

        List<AITool> streamToolSet = await BuildToolSetWithMcpAsync(request, streamResolvedSpell, inferenceToken).ConfigureAwait(false);

        ToolExecutionPipeline.TurnContext streamTurnContext = await BuildTurnContextAsync(request, streamToolSet, inferenceToken).ConfigureAwait(false);

        bool streamUsesTools = true;

        string? inferenceError;

        string? streamFinishReason = null;

        ChatCompletionUsage? streamAccumulatedUsage = null;

        int streamMaxToolRounds = ArcanumSettingClamps.MaxToolInferenceRounds(settings.Value.Intelligence.MaxToolInferenceRounds);

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

                        yield return new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, update.Text);
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
                            "Model {ModelName} does not support tools in Ollama; retrying stream without local tools.",
                            targetModel);

                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Status,
                            "This Ollama model does not support tools; continuing without local tools.");

                        streamUsesTools = false;

                        inferenceError = null;

                        streamingMoveNextFailure = null;

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

                foreach (FunctionCallContent fcc in toolCalls)
                {
                    string argsSnapshot = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(fcc);

                    string toolCallData = ToolExecutionPipeline.FormatToolCallEventData(fcc, argsSnapshot);

                    string callId = ToolExecutionPipeline.ResolveCallId(fcc);

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

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolResult,
                        processed.ToolName,
                        processed.ResultText,
                        null,
                        new IntelligenceToolCallEvent(processed.CallId, processed.ToolName, processed.ResultText, toolCallIndex));

                    ToolExecutionPipeline.AppendToolExchangeToMessages(
                        chatMessages,
                        fcc,
                        processed.CallId,
                        processed.ResultText);

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

        await grimoireTurnWriter
            .TryFinalizeStreamedAssistantEntryAsync(grimoireTurn, finalText, targetModel, inferenceToken)
            .ConfigureAwait(false);

        await TryIncrementSessionTokensAsync(grimoireTurn.SessionId, streamAccumulatedUsage, inferenceToken)
            .ConfigureAwait(false);

        string usageData = streamAccumulatedUsage?.TotalTokens.ToString(CultureInfo.InvariantCulture) ?? "0";

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            "Complete",
            usageData,
            streamAccumulatedUsage,
            FinishReason: streamFinishReason ?? "stop");
        }
        finally
        {
            await grimoireTurnWriter
                .TryResolveInterruptedOnStreamExitAsync(
                    grimoireTurn,
                    streamAccumulator.Length > 0 ? streamAccumulator.ToString() : null)
                .ConfigureAwait(false);

            lease.Dispose();
        }
    }

    private static async IAsyncEnumerable<PullModelResponse> EnumeratePullModelAsync(
        IOllamaApiClient ollamaClient,
        string modelName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (PullModelResponse? response in ollamaClient.PullModelAsync(new PullModelRequest { Model = modelName }, cancellationToken).ConfigureAwait(false))
        {
            if (response is null)
            {
                continue;
            }

            yield return response;
        }
    }

    private async Task<Result> EnsureModelExistsAsync(
        IOllamaApiClient ollamaClient,
        string ollamaEndpoint,
        string modelName,
        CancellationToken cancellationToken,
        IProgress<string>? pullProgress)
    {
        Result<bool> localCheck = await IsModelLocalAsync(ollamaClient, ollamaEndpoint, modelName, cancellationToken).ConfigureAwait(false);

        if (localCheck.IsFailure)
        {
            return Result.Failure(localCheck.Error);
        }

        if (localCheck.Value)
        {
            return Result.Success();
        }

        logger.LogInformation(
            "Model {ModelName} not found locally. Downloading from Ollama... This may take a moment.",
            modelName);

        try
        {
            int lastReportedPercent = -1;

            await foreach (PullModelResponse pull in EnumeratePullModelAsync(ollamaClient, modelName, cancellationToken).ConfigureAwait(false))
            {
                int rounded = (int)Math.Round(pull.Percent, MidpointRounding.AwayFromZero);

                if (rounded != lastReportedPercent)
                {
                    lastReportedPercent = rounded;

                    pullProgress?.Report($"Downloading model {modelName}: {rounded}%");
                }
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Model pull failed for {ModelName}.", modelName);

            return Result.Failure(new Error(ErrorCodes.Ollama.Pull, PublicModelPullFailureMessage));
        }
    }

    private async Task<Result<bool>> IsModelLocalAsync(
        IOllamaApiClient ollamaClient,
        string ollamaEndpoint,
        string modelName,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<Model> models = await ListOllamaModelsCachedAsync(ollamaClient, ollamaEndpoint, cancellationToken)
                .ConfigureAwait(false);

            return models.Any(m => ProviderResolver.ModelNameMatches(m.Name, modelName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list local Ollama models while checking {ModelName}.", modelName);

            return Result<bool>.Failure(new Error(ErrorCodes.Ollama.ListModels, PublicListLocalModelsFailureMessage));
        }
    }

    private static async Task<IReadOnlyList<Model>> ListOllamaModelsCachedAsync(
        IOllamaApiClient ollamaClient,
        string ollamaEndpoint,
        CancellationToken cancellationToken)
    {
        string cacheKey = string.IsNullOrWhiteSpace(ollamaEndpoint)
            ? "_default"
            : ollamaEndpoint.Trim();

        if (OllamaModelListCache.TryGetValue(cacheKey, out OllamaModelListCacheEntry? cached)
            && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Models;
        }

        IEnumerable<Model> models = await ollamaClient.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Model> modelList = models.ToList();

        OllamaModelListCache[cacheKey] = new OllamaModelListCacheEntry(DateTime.UtcNow.AddSeconds(60), modelList);

        return modelList;
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
            TimeSpan spellPreflight = TimeSpan.FromSeconds(
                ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds(settings.Value.Intelligence.SemanticRouterPreflightTimeoutSeconds));

            int routerMaxTokens = ArcanumSettingClamps.SemanticRouterMaxTokens(
                settings.Value.Intelligence.SemanticRouterMaxTokens);

            float routerTemperature = ArcanumSettingClamps.SemanticRouterTemperature(
                settings.Value.Intelligence.SemanticRouterTemperature);

            string semanticProbe = GetSemanticRouterUserProbe(request);

            IChatClient routerClient = chatClient;

            ChatClientLease? routerLease = null;

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

                matchedMetadata = await SemanticRouter
                    .DetermineActiveSpellAsync(
                        routerClient,
                        semanticProbe,
                        spellMetadata,
                        spellPreflight,
                        routerMaxTokens,
                        routerTemperature,
                        cancellationToken,
                        logger)
                    .ConfigureAwait(false);
            }
            finally
            {
                routerLease?.Dispose();
            }
        }

        if (matchedMetadata is null)
        {
            return Result<ResolvedSpell?>.Success(null);
        }

        ParsedSpell? activeSpell = await SpellScanner
            .LoadFullAsync(matchedMetadata.FilePath, cancellationToken, maxSpellFileSizeBytes)
            .ConfigureAwait(false);

        if (activeSpell is null)
        {
            return Result<ResolvedSpell?>.Success(null);
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

        return Result<ResolvedSpell?>.Success(resolved);
    }

    private static void PrependDynamicSystemMessage(List<MeAiChatMessage> messages, string systemText)
    {
        if (string.IsNullOrWhiteSpace(systemText))
        {
            return;
        }

        messages.Insert(0, new MeAiChatMessage(ChatRole.System, systemText));
    }

    private (bool Compressed, List<MeAiChatMessage> Messages) TryApplyContextCompressionIfNeeded(
        PingRequest request,
        List<MeAiChatMessage> chatMessages,
        string? codexContent,
        ParsedSpell? activeSpell,
        IReadOnlyList<ParsedSpell>? dependencySpells,
        Session? thread,
        string newUserPrompt,
        ChatClientLease lease)
    {

        if (!settings.Value.Intelligence.EnableContextCompression)
        {

            return (false, chatMessages);

        }

        if (HasStatelessMessages(request) || thread is null)
        {

            return (false, chatMessages);

        }

        int minMessagesForPreflight = ArcanumSettingClamps.CompressionPreflightMinMessages(
            settings.Value.Intelligence.CompressionPreflightMinMessages);

        if (manaPreflight.ShouldSkipCompressionPreflight(chatMessages, minMessagesForPreflight))
        {

            return (false, chatMessages);

        }

        string encodingName = settings.Value.Intelligence.TokenizerEncoding ?? InferenceTokenizerResolver.DefaultEncodingName;

        Tokenizer tokenizer = inferenceTokenizerResolver.ResolveTokenizer(encodingName);

        int perMessageOverhead = ArcanumSettingClamps.PerMessageTemplateOverheadTokens(
            settings.Value.Intelligence.PerMessageTemplateOverheadTokens);

        int totalTokens = manaPreflight.CountTokens(chatMessages, tokenizer, perMessageOverhead, encodingName);

        int thresholdPct = ArcanumSettingClamps.ContextWindowCompressionThreshold(
            settings.Value.Intelligence.ContextWindowCompressionThreshold);

        int contextLimit = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);

        long effectiveLong = (long)contextLimit * thresholdPct / 100L;

        int effectiveLimit = effectiveLong > int.MaxValue ? int.MaxValue : (int)effectiveLong;

        if (totalTokens <= effectiveLimit)
        {

            return (false, chatMessages);

        }

        if (string.IsNullOrWhiteSpace(thread.Summary) || thread.LastSummarizedMessageAt is null)
        {

            logger.LogWarning(
                "Context ({TotalTokens} tokens) exceeds compression threshold ({EffectiveLimit} tokens) but no campaign summary is available for session {SessionId}; proceeding unfiltered.",
                totalTokens,
                effectiveLimit,
                thread.Id);

            return (false, chatMessages);

        }

        List<MeAiChatMessage> rebuilt = MapFilteredGrimoireToMeAiMessages(
            thread,
            thread.LastSummarizedMessageAt.Value,
            newUserPrompt);

        string augmentedSystem = SystemPromptBuilder.Build(
            request,
            codexContent,
            activeSpell,
            request.AttachedFiles,
            thread.Summary,
            dependencySpells,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(settings.Value.Spells.MaxResonantBytes));

        PrependDynamicSystemMessage(rebuilt, augmentedSystem);

        int afterTokens = manaPreflight.CountTokens(rebuilt, tokenizer, perMessageOverhead, encodingName);

        if (afterTokens > effectiveLimit)
        {

            logger.LogWarning(
                "After memory compression, context is still {AfterTokens} tokens (threshold {EffectiveLimit}) for session {SessionId}.",
                afterTokens,
                effectiveLimit,
                thread.Id);

        }

        return (true, rebuilt);

    }

    private static List<MeAiChatMessage> MapFilteredGrimoireToMeAiMessages(
        Session session,
        DateTime watermarkExclusive,
        string newUserPrompt)
    {

        List<Entry> ordered = session.Entries
            .Where(m => m.CreatedAt.UtcDateTime > watermarkExclusive)
            .OrderBy(m => m.CreatedAt)
            .ToList();

        while (ordered.Count > 0

            && ordered[^1].Role == MessageRole.Assistant

            && string.IsNullOrEmpty(ordered[^1].Content))
        {

            ordered.RemoveAt(ordered.Count - 1);

        }

        var list = new List<MeAiChatMessage>(ordered.Count + 1);

        foreach (Entry m in ordered)
        {

            ChatRole role = m.Role switch
            {

                MessageRole.User => ChatRole.User,

                MessageRole.Assistant => ChatRole.Assistant,

                MessageRole.System => ChatRole.System,

                _ => ChatRole.User,

            };

            list.Add(new MeAiChatMessage(role, m.Content));

        }

        list.Add(new MeAiChatMessage(ChatRole.User, newUserPrompt));

        return list;

    }

    private static bool HasStatelessMessages(PingRequest request) =>
        request.StatelessMessages is { Count: > 0 };

    private static string GetSemanticRouterUserProbe(PingRequest request)
    {
        if (!HasStatelessMessages(request))
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

    private static List<MeAiChatMessage> BuildInitialMeAiChatMessages(
        PingRequest request,
        Session? thread,
        string newUserPrompt)
    {
        if (HasStatelessMessages(request))
        {
            return MapStatelessMessagesToMeAi(request.StatelessMessages!);
        }

        return MapGrimoireToMeAiMessages(thread, newUserPrompt);
    }

    private static List<MeAiChatMessage> MapStatelessMessagesToMeAi(IReadOnlyList<CoreChatMessage> messages)
    {
        var list = new List<MeAiChatMessage>(messages.Count);

        foreach (CoreChatMessage m in messages)
        {
            list.Add(MapStatelessMessageToMeAi(m));
        }

        return list;
    }

    private static MeAiChatMessage MapStatelessMessageToMeAi(CoreChatMessage m)
    {
        ChatRole role = MapOpenAiStyleRoleToChatRole(m.Role);

        if (role == ChatRole.Tool && !string.IsNullOrEmpty(m.ToolCallId))
        {
            return new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(m.ToolCallId, m.Content ?? string.Empty)]);
        }

        if (role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 } toolCalls)
        {
            List<AIContent> contents = new(toolCalls.Count + 1);

            if (!string.IsNullOrEmpty(m.Content))
            {
                contents.Add(new TextContent(m.Content));
            }

            foreach (CoreToolCall tc in toolCalls)
            {
                Dictionary<string, object?>? args = ParseToolCallArgumentsForAiFunction(tc.ArgumentsJson);

                contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));
            }

            return new MeAiChatMessage(ChatRole.Assistant, contents);
        }

        if (m.ContentParts is { Count: > 0 } parts)
        {
            List<AIContent> contents = new(parts.Count);

            foreach (CoreContentPart part in parts)
            {
                if (string.Equals(part.Kind, "image_url", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(part.ImageUrl)
                    && Uri.TryCreate(part.ImageUrl, UriKind.Absolute, out Uri? imageUri))
                {
                    contents.Add(new UriContent(imageUri, "image/*"));

                    continue;
                }

                string text = part.Text ?? string.Empty;

                if (text.Length > 0)
                {
                    contents.Add(new TextContent(text));
                }
            }

            if (contents.Count == 0)
            {
                contents.Add(new TextContent(string.Empty));
            }

            MeAiChatMessage built = new(role, contents);

            if (!string.IsNullOrEmpty(m.Name))
            {
                built.AuthorName = m.Name;
            }

            return built;
        }

        MeAiChatMessage textOnly = new(role, m.Content ?? string.Empty);

        if (!string.IsNullOrEmpty(m.Name))
        {
            textOnly.AuthorName = m.Name;
        }

        return textOnly;
    }

    private static Dictionary<string, object?>? ParseToolCallArgumentsForAiFunction(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(argumentsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = doc.RootElement.Clone(),
                };
            }

            Dictionary<string, object?> map = new(StringComparer.Ordinal);

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["raw"] = argumentsJson,
            };
        }
    }

    private static ChatRole MapOpenAiStyleRoleToChatRole(string role)
    {
        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.System;
        }

        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.Assistant;
        }

        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.Tool;
        }

        if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.System;
        }

        return ChatRole.User;
    }

    private static List<MeAiChatMessage> MapGrimoireToMeAiMessages(Session? session, string newUserPrompt)
    {
        if (session is null)
        {
            return [new MeAiChatMessage(ChatRole.User, newUserPrompt)];
        }

        var ordered = session.Entries.ToList();

        while (ordered.Count > 0

            && ordered[^1].Role == MessageRole.Assistant

            && string.IsNullOrEmpty(ordered[^1].Content))
        {
            ordered.RemoveAt(ordered.Count - 1);
        }

        var list = new List<MeAiChatMessage>(ordered.Count + 1);

        foreach (var m in ordered)
        {
            ChatRole role = m.Role switch
            {
                MessageRole.User => ChatRole.User,
                MessageRole.Assistant => ChatRole.Assistant,
                MessageRole.System => ChatRole.System,
                _ => ChatRole.User,
            };

            list.Add(new MeAiChatMessage(role, m.Content));
        }

        list.Add(new MeAiChatMessage(ChatRole.User, newUserPrompt));

        return list;
    }

    private async Task<List<AITool>> BuildToolSetWithMcpAsync(
        PingRequest request,
        ResolvedSpell? resolvedSpell,
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

            tools.Add(new ArcanumSpellScriptTool(scriptRoots, TimeSpan.FromSeconds(sec), sec, outputCap, logger));
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

        foreach (AITool t in attunement.Allowed)
        {
            tools.Add(t);
        }

        return tools;
    }

    private static List<string> CollectScriptRoots(ResolvedSpell? resolvedSpell)
    {
        if (resolvedSpell is null)
        {
            return [];
        }

        var roots = new List<string>();

        if (resolvedSpell.Primary.AvailableScripts is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(resolvedSpell.Primary.DirectoryPath))
        {
            roots.Add(Path.Combine(resolvedSpell.Primary.DirectoryPath, "scripts"));
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

        if (lease.IsOllama)
        {
            int numCtx = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);

            options.AdditionalProperties ??= [];

            options.AdditionalProperties["num_ctx"] = numCtx;
        }

        ApplyInferenceParameters(options, request);

        if (!includeTools || tools is null)
        {
            return options;
        }

        options.Tools = tools.ToList();

        return options;
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

        return new ChatCompletionUsage(prompt, completion, total);
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

        return new ChatCompletionUsage(p, c, p + c);
    }

    private async Task TryIncrementSessionTokensAsync(
        Guid? sessionId,
        ChatCompletionUsage? usage,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Intelligence.EnableTokenTracking || !sessionId.HasValue || usage is null || usage.TotalTokens <= 0)
        {
            return;
        }

        try
        {
            await grimoire
                .IncrementSessionTokensAsync(sessionId.Value, usage.TotalTokens, cancellationToken)
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

    private static string BuildInferenceFailureMessage(ChatClientLease lease)
    {

        // W3.5: do NOT embed lease.Provider.Endpoint — this message surfaces to clients via the
        // native /api inference envelopes and the raw endpoint URL can leak internal hostnames/paths.
        // The operator-chosen provider name is retained; endpoint detail stays in server logs.
        if (lease.IsOllama)
        {

            return $"Ollama provider '{lease.Provider.Name}' is unreachable. Ensure Ollama is running and the configured endpoint is correct.";

        }

        return $"Provider '{lease.Provider.Name}' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly.";

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
