using System.Buffers;
using System.Collections.Concurrent;
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
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed class HubIntelligenceProvider(
    IChatClientFactory chatClientFactory,
    IOptionsSnapshot<ArcanumSettings> settings,
    ILogger<HubIntelligenceProvider> logger,
    IGrimoireRepository grimoire,
    IMcpConnectionManager mcpConnectionManager,
    InferenceTokenizerResolver inferenceTokenizerResolver,
    IWard ward,
    ICampaignRepository campaignRepository,
    ISanctumGuard sanctumGuard,
    SessionEventHub sessionEventHub) : IArcanumIntelligenceProvider
{
    private const string PublicInferenceFailureMessage =
        "Inference failed. Ensure Ollama is running and reachable, then try again. See server logs for details.";

    private const string PublicHubInferenceFailureMessage =
        "Inference failed. See server logs for details.";

    private const string PublicListLocalModelsFailureMessage =
        "Could not list local Ollama models. Ensure Ollama is running and reachable. See server logs for details.";

    private const string PublicModelPullFailureMessage =
        "Model download failed. Ensure Ollama is running and has network access. See server logs for details.";

    private const string PublicToolFailureMessageForGrimoire =
        "A tool invocation failed. See server logs for details.";

    private const string WardTimeoutReason =
        "The ward held until timeout — action was not allowed";

    private const string PublicModelResolutionFailureMessage =
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.";

    private static readonly ArcanumLocalTimeTool _localTimeTool = new();

    private static readonly ArcanumSystemInfoTool _systemInfoTool = new();

    private static readonly ConcurrentDictionary<string, OllamaModelListCacheEntry> OllamaModelListCache = new(StringComparer.Ordinal);

    private sealed record OllamaModelListCacheEntry(DateTime ExpiresUtc, IReadOnlyList<Model> Models);

    private sealed class TurnContext
    {

        public Campaign? Campaign { get; init; }

        public string? CampaignId { get; init; }

        public string? WorkspaceRoot { get; init; }

        public bool CampaignRequiresWard { get; init; }

        public bool SanctumEnabled { get; init; }

        public SanctumMode SanctumMode { get; init; }

        public IReadOnlyList<AITool> InferenceTools { get; init; } = [];

    }

    public async Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt;

        if (!TryValidateAttachedFiles(request, out Error attachedFilesError))
        {
            return Result<PromptTurnResult>.Failure(attachedFilesError);
        }

        if (!HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            return Result<PromptTurnResult>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required."));
        }

        ChatClientLease lease;

        try
        {
            lease = await chatClientFactory.ResolveClientAsync(request.Model, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Hub model resolution failed for requested model {RequestedModel}.", request.Model);

            return Result<PromptTurnResult>.Failure(new Error("Hub.Model", PublicModelResolutionFailureMessage));
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
                    cancellationToken,
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
                .GetSessionAsync(existingSessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        Guid? assistantEntryId = null;

        Guid? grimoireSessionId = null;

        if (!HasStatelessMessages(request))
        {
            try
            {
                (Guid sid, Guid aid) = await grimoire
                    .BeginAssistantReplyAsync(request.SessionId, prompt, targetModel, cancellationToken)
                    .ConfigureAwait(false);

                grimoireSessionId = sid;

                assistantEntryId = aid;

                await PublishLatestSavedEntriesAsync(sid, 2, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception beginEx)
            {
                logger.LogWarning(beginEx, "Grimoire could not begin assistant reply for model {ModelName}.", targetModel);
            }
        }

        string? codexContent = await CodexReader
            .ReadCodexAsync(
                request.WorkingDirectory,
                ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value),
                cancellationToken)
            .ConfigureAwait(false);

        ParsedSpell? activeSpell;

        if (request.SkipSpellRouting)
        {
            activeSpell = null;
        }
        else
        {
            string? spellWorkspaceRoot = RetroDownfall.Arcanum.Infrastructure.Mcp.ToolHelpers.TryNormalizeWorkspace(
                request.WorkingDirectory,
                out string? spellRoot,
                out _)
                ? spellRoot
                : null;

            Result<ParsedSpell?> routedSpell = await ResolveRoutedSpellAsync(
                request,
                chatClient,
                spellWorkspaceRoot,
                cancellationToken).ConfigureAwait(false);

            if (routedSpell.IsFailure)
            {
                return Result<PromptTurnResult>.Failure(routedSpell.Error);
            }

            activeSpell = routedSpell.Value;
        }

        string builtSystemPrompt = SystemPromptBuilder.Build(request, codexContent, activeSpell, request.AttachedFiles);

        List<AITool> toolSet = await BuildToolSetWithMcpAsync(request, activeSpell, cancellationToken).ConfigureAwait(false);

        TurnContext turnContext = await BuildTurnContextAsync(request, toolSet, cancellationToken).ConfigureAwait(false);

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
                        .GetResponseAsync(chatMessages, chatOptions, cancellationToken)
                        .ConfigureAwait(false);

                    accumulatedUsage = AccumulateUsage(accumulatedUsage, MapUsageDetails(response.Usage));

                    List<FunctionCallContent> calls = CollectFunctionCalls(response)
                        .Where(static c => !c.InformationalOnly)
                        .ToList();

                    if (calls.Count == 0)
                    {
                        break;
                    }

                    toolRoundsExecuted++;

                    if (toolRoundsExecuted > maxToolRounds)
                    {
                        return Result<PromptTurnResult>.Failure(new Error("Hub.ToolLoop", "Tool invocation limit reached."));
                    }

                    foreach (FunctionCallContent fcc in calls)
                    {
                        string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

                        string callId = string.IsNullOrEmpty(fcc.CallId) ? fcc.Name : fcc.CallId;

                        (observedToolCalls ??= []).Add(new PromptToolCall(callId, fcc.Name, argsSnapshot));

                        WardedToolExecutionResult wardedExecution = await ExecuteToolCallWithWardAsync(
                            fcc,
                            request,
                            chatOptions,
                            activeSpell,
                            grimoireSessionId?.ToString(),
                            turnContext,
                            argsSnapshot,
                            cancellationToken)
                            .ConfigureAwait(false);

                        string resultText = wardedExecution.ResultText;

                        FunctionCallContent normalizedCall = string.IsNullOrEmpty(fcc.CallId)
                            ? new FunctionCallContent(callId, fcc.Name, fcc.Arguments)
                            : fcc;

                        chatMessages.Add(new MeAiChatMessage(ChatRole.Assistant, [normalizedCall]));

                        chatMessages.Add(
                            new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, resultText)]));

                        await TryAppendToolInteractionToGrimoireAsync(
                            grimoireSessionId,
                            fcc.Name,
                            argsSnapshot,
                            resultText,
                            targetModel,
                            cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                string finalText = response.Text;

                if (assistantEntryId is { } finalizeId)
                {
                    try
                    {
                        await grimoire
                            .FinalizeAssistantEntryAsync(finalizeId, finalText, cancellationToken)
                            .ConfigureAwait(false);

                        if (grimoireSessionId is { } publishSessionId)
                        {
                            await PublishSavedEntryByIdAsync(publishSessionId, finalizeId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception persistEx)
                    {
                        logger.LogWarning(persistEx, "Grimoire could not finalize assistant entry for model {ModelName}.", targetModel);
                    }
                }

                await TryIncrementSessionTokensAsync(
                    grimoireSessionId,
                    accumulatedUsage,
                    cancellationToken)
                    .ConfigureAwait(false);

                return Result<PromptTurnResult>.Success(new PromptTurnResult(finalText, accumulatedUsage, observedToolCalls, "stop"));
            }
            catch (OperationCanceledException)
            {
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

                return Result<PromptTurnResult>.Failure(
                    new Error(
                        lease.IsOllama ? "Ollama.Error" : "Hub.Error",
                        lease.IsOllama ? PublicInferenceFailureMessage : PublicHubInferenceFailureMessage));
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

        if (!HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, "Prompt is required.");

            yield break;
        }

        InvalidOperationException? resolveFailure = null;

        ChatClientLease? leaseOrNull = null;

        try
        {
            leaseOrNull = await chatClientFactory.ResolveClientAsync(request.Model, cancellationToken).ConfigureAwait(false);
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
                    cancellationToken).ConfigureAwait(false);

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

                    IAsyncEnumerator<PullModelResponse> pullEnumerator = EnumeratePullModelAsync(lease.OllamaApi!, targetModel, cancellationToken).GetAsyncEnumerator(cancellationToken);

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
                .GetSessionAsync(existingSessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        List<MeAiChatMessage> chatMessages = BuildInitialMeAiChatMessages(request, thread, prompt);

        string? streamCodexContent = await CodexReader
            .ReadCodexAsync(
                request.WorkingDirectory,
                ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value),
                cancellationToken)
            .ConfigureAwait(false);

        ParsedSpell? streamActiveSpell;

        if (request.SkipSpellRouting)
        {
            streamActiveSpell = null;
        }
        else
        {
            string? streamSpellWorkspaceRoot = RetroDownfall.Arcanum.Infrastructure.Mcp.ToolHelpers.TryNormalizeWorkspace(
                request.WorkingDirectory,
                out string? streamSpellRoot,
                out _)
                ? streamSpellRoot
                : null;

            Result<ParsedSpell?> streamRoutedSpell = await ResolveRoutedSpellAsync(
                request,
                chatClient,
                streamSpellWorkspaceRoot,
                cancellationToken).ConfigureAwait(false);

            if (streamRoutedSpell.IsFailure)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.Error,
                    streamRoutedSpell.Error.Message);

                yield break;
            }

            streamActiveSpell = streamRoutedSpell.Value;
        }

        string streamBuiltSystemPrompt = SystemPromptBuilder.Build(
            request,
            streamCodexContent,
            streamActiveSpell,
            request.AttachedFiles);

        PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

        (bool compressedStream, List<MeAiChatMessage> streamMessages) = TryApplyContextCompressionIfNeeded(
            request,
            chatMessages,
            streamCodexContent,
            streamActiveSpell,
            thread,
            prompt,
            lease);

        chatMessages = streamMessages;

        Guid? assistantEntryId = null;

        Guid? boundSessionId = null;

        if (!HasStatelessMessages(request))
        {
            try
            {
                (Guid sessionId, Guid aid) = await grimoire
                    .BeginAssistantReplyAsync(request.SessionId, prompt, targetModel, cancellationToken)
                    .ConfigureAwait(false);

                assistantEntryId = aid;

                boundSessionId = sessionId;

                await PublishLatestSavedEntriesAsync(sessionId, 2, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Grimoire could not start streamed session persistence for model {ModelName}.", targetModel);
            }
        }

        if (boundSessionId is { } bcid)
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

        StringBuilder accumulator;

        List<AITool> streamToolSet = await BuildToolSetWithMcpAsync(request, streamActiveSpell, cancellationToken).ConfigureAwait(false);

        TurnContext streamTurnContext = await BuildTurnContextAsync(request, streamToolSet, cancellationToken).ConfigureAwait(false);

        bool streamUsesTools = true;

        string? inferenceError;

        ChatCompletionUsage? streamAccumulatedUsage = null;

        int streamMaxToolRounds = ArcanumSettingClamps.MaxToolInferenceRounds(settings.Value.Intelligence.MaxToolInferenceRounds);

        while (true)
        {
            bool streamOuterRestart = false;

            accumulator = new StringBuilder(1024);

            streamAccumulatedUsage = null;

            ChatOptions streamChatOptions = CreateInferenceChatOptions(streamUsesTools, streamTurnContext.InferenceTools, request, lease);

            int streamToolRoundCount = 0;

            inferenceError = null;

            Exception? streamingMoveNextFailure = null;

            while (true)
            {
                List<ChatResponseUpdate> roundUpdates = [];

                IAsyncEnumerator<ChatResponseUpdate> streamEnumerator = chatClient
                    .GetStreamingResponseAsync(chatMessages, streamChatOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

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

                            logger.LogError(ex, "Streaming read failed for model {ModelName}.", targetModel);

                            inferenceError = PublicInferenceFailureMessage;

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

                        _ = accumulator.Append(update.Text);

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

                        && accumulator.Length == 0)
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

                List<FunctionCallContent> toolCalls = CollectFunctionCalls(combinedRound)
                    .Where(static c => !c.InformationalOnly)
                    .ToList();

                if (toolCalls.Count == 0)
                {
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
                    string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

                    string toolCallData = FormatToolCallEventData(fcc, argsSnapshot);

                    string callId = string.IsNullOrEmpty(fcc.CallId) ? fcc.Name : fcc.CallId;

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolCall,
                        fcc.Name,
                        toolCallData,
                        null,
                        new IntelligenceToolCallEvent(callId, fcc.Name, argsSnapshot, toolCallIndex));

                    string resultText;

                    WardedToolExecutionResult? wardedExecution = null;

                    try
                    {
                        wardedExecution = await ExecuteToolCallWithWardAsync(
                            fcc,
                            request,
                            streamChatOptions,
                            streamActiveSpell,
                            boundSessionId?.ToString(),
                            streamTurnContext,
                            argsSnapshot,
                            cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Tool {ToolName} failed during streaming inference.", fcc.Name);
                    }

                    if (wardedExecution is not null)
                    {
                        foreach (IntelligenceEvent wardEvent in wardedExecution.WardEvents)
                        {
                            yield return wardEvent;
                        }

                        resultText = wardedExecution.ResultText;
                    }
                    else
                    {
                        resultText = PublicToolFailureMessageForGrimoire;
                    }

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolResult,
                        fcc.Name,
                        resultText,
                        null,
                        new IntelligenceToolCallEvent(callId, fcc.Name, resultText, toolCallIndex));

                    chatMessages.Add(new MeAiChatMessage(ChatRole.Assistant, [
                        string.IsNullOrEmpty(fcc.CallId)
                            ? new FunctionCallContent(callId, fcc.Name, fcc.Arguments)
                            : fcc]));

                    chatMessages.Add(
                        new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, resultText)]));

                    await TryAppendToolInteractionToGrimoireAsync(
                        boundSessionId,
                        fcc.Name,
                        argsSnapshot,
                        resultText,
                        targetModel,
                        cancellationToken)
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
            yield return new IntelligenceEvent(IntelligenceEventType.Error, inferenceError);

            yield break;
        }

        string finalText = accumulator.ToString();

        if (assistantEntryId is { } finalizeId)
        {
            try
            {
                await grimoire.FinalizeAssistantEntryAsync(finalizeId, finalText, cancellationToken).ConfigureAwait(false);

                if (boundSessionId is { } publishSessionId)
                {
                    await PublishSavedEntryByIdAsync(publishSessionId, finalizeId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Grimoire could not finalize streamed assistant entry for model {ModelName}.", targetModel);
            }
        }

        await TryIncrementSessionTokensAsync(boundSessionId, streamAccumulatedUsage, cancellationToken)
            .ConfigureAwait(false);

        string usageData = streamAccumulatedUsage?.TotalTokens.ToString(CultureInfo.InvariantCulture) ?? "0";

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            "Complete",
            usageData,
            streamAccumulatedUsage);
        }
        finally
        {
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

            return Result.Failure(new Error("Ollama.Pull", PublicModelPullFailureMessage));
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

            return Result<bool>.Failure(new Error("Ollama.ListModels", PublicListLocalModelsFailureMessage));
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

    private async Task<TurnContext> BuildTurnContextAsync(
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

        return new TurnContext
        {
            Campaign = campaign,
            CampaignId = campaignId,
            WorkspaceRoot = workspaceRoot,
            CampaignRequiresWard = campaignRequiresWard,
            SanctumEnabled = sanctumEnabled,
            SanctumMode = sanctumMode,
            InferenceTools = inferenceTools,
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

    private async Task<Result<ParsedSpell?>> ResolveRoutedSpellAsync(
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
                return Result<ParsedSpell?>.Failure(validated.Error);
            }

            ParsedSpell? overrideSpell = await SpellScanner
                .LoadFullAsync(validated.Value, cancellationToken, maxSpellFileSizeBytes)
                .ConfigureAwait(false);

            return Result<ParsedSpell?>.Success(overrideSpell);
        }

        IReadOnlyList<SpellMetadata> spellMetadata = await SpellScanner
            .ScanMetadataAsync(spellWorkspaceRoot, cancellationToken, maxSpellFileSizeBytes)
            .ConfigureAwait(false);

        SpellMetadata? matchedMetadata;

        if (!string.IsNullOrWhiteSpace(request.OverrideSpellName))
        {
            if (!TryResolveOverrideSpellMetadata(request.OverrideSpellName, spellMetadata, out SpellMetadata? overridePick))
            {
                return Result<ParsedSpell?>.Failure(
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

            matchedMetadata = await SemanticRouter
                .DetermineActiveSpellAsync(
                    chatClient,
                    semanticProbe,
                    spellMetadata,
                    spellPreflight,
                    routerMaxTokens,
                    routerTemperature,
                    cancellationToken,
                    logger)
                .ConfigureAwait(false);
        }

        if (matchedMetadata is null)
        {
            return Result<ParsedSpell?>.Success(null);
        }

        ParsedSpell? activeSpell = await SpellScanner
            .LoadFullAsync(matchedMetadata.FilePath, cancellationToken, maxSpellFileSizeBytes)
            .ConfigureAwait(false);

        return Result<ParsedSpell?>.Success(activeSpell);
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

        if (InferenceTokenCounter.ShouldSkipCompressionPreflight(chatMessages, minMessagesForPreflight))
        {

            return (false, chatMessages);

        }

        Tokenizer tokenizer = inferenceTokenizerResolver.ResolveTokenizer(settings.Value.Intelligence.TokenizerEncoding);

        int perMessageOverhead = ArcanumSettingClamps.PerMessageTemplateOverheadTokens(
            settings.Value.Intelligence.PerMessageTemplateOverheadTokens);

        int totalTokens = InferenceTokenCounter.CountTokens(chatMessages, tokenizer, perMessageOverhead);

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
            thread.Summary);

        PrependDynamicSystemMessage(rebuilt, augmentedSystem);

        int afterTokens = InferenceTokenCounter.CountTokens(rebuilt, tokenizer, perMessageOverhead);

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
        ParsedSpell? activeSpell,
        CancellationToken cancellationToken)
    {
        string workingDirectory = request.WorkingDirectory;

        List<AITool> tools = [_localTimeTool, _systemInfoTool];

        if (activeSpell?.AvailableScripts is { Count: > 0 })
        {
            int sec = ArcanumSettingClamps.ExecuteCommandTimeoutSeconds(settings.Value.Intelligence.ExecuteCommandTimeoutSeconds);

            long outputCap = ArcanumSettingClamps.ToolOutputCapBytes(settings.Value.Intelligence.ToolOutputCapBytes);

            string scriptsRoot = Path.Combine(activeSpell.DirectoryPath, "scripts");

            tools.Add(new ArcanumSpellScriptTool(scriptsRoot, TimeSpan.FromSeconds(sec), sec, outputCap));
        }

        if (ShouldDisableMcpTools(request))
        {
            return tools;
        }

        IReadOnlyList<AITool> mcpTools = await mcpConnectionManager
            .GetAvailableToolsAsync(workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        foreach (AITool t in mcpTools)
        {
            tools.Add(t);
        }

        return tools;
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

            options.AdditionalProperties!["num_ctx"] = numCtx;
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

    private static List<FunctionCallContent> CollectFunctionCalls(ChatResponse response)
    {
        var results = new List<FunctionCallContent>();

        foreach (MeAiChatMessage message in response.Messages)
        {
            AppendFunctionCallsFromContents(message.Contents, results);
        }

        return results;
    }

    private static void AppendFunctionCallsFromContents(IList<AIContent>? contents, List<FunctionCallContent> sink)
    {
        if (contents is null)
        {
            return;
        }

        foreach (AIContent item in contents)
        {
            if (item is FunctionCallContent fcc)
            {
                sink.Add(fcc);
            }
        }
    }

    private static string SerializeToolArgumentsForGrimoire(FunctionCallContent fcc)
    {
        if (fcc.Arguments is null || fcc.Arguments.Count == 0)
        {
            return string.Empty;
        }

        ArrayBufferWriter<byte> buffer = new(256);

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            foreach (KeyValuePair<string, object?> pair in fcc.Arguments)
            {
                writer.WritePropertyName(pair.Key);

                WriteArgumentValue(writer, pair.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteArgumentValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonElement je:
                je.WriteTo(writer);
                break;

            case string s:
                writer.WriteStringValue(s);
                break;

            case bool b:
                writer.WriteBooleanValue(b);
                break;

            case sbyte sb:
                writer.WriteNumberValue(sb);
                break;

            case byte by:
                writer.WriteNumberValue(by);
                break;

            case short sh:
                writer.WriteNumberValue(sh);
                break;

            case ushort us:
                writer.WriteNumberValue(us);
                break;

            case int i:
                writer.WriteNumberValue(i);
                break;

            case uint ui:
                writer.WriteNumberValue(ui);
                break;

            case long l:
                writer.WriteNumberValue(l);
                break;

            case ulong ul:
                writer.WriteNumberValue(ul);
                break;

            case double d:
                writer.WriteNumberValue(d);
                break;

            case float f:
                writer.WriteNumberValue(f);
                break;

            case decimal dec:
                writer.WriteNumberValue(dec);
                break;

            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;

            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;

            case Guid g:
                writer.WriteStringValue(g.ToString("D", CultureInfo.InvariantCulture));
                break;

            case Uri uri:
                writer.WriteStringValue(uri.ToString());
                break;

            case IReadOnlyDictionary<string, object?> dict:
                writer.WriteStartObject();

                foreach (KeyValuePair<string, object?> kv in dict)
                {
                    writer.WritePropertyName(kv.Key);

                    WriteArgumentValue(writer, kv.Value);
                }

                writer.WriteEndObject();
                break;

            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();

                foreach (object? item in enumerable)
                {
                    WriteArgumentValue(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string FormatToolCallEventData(FunctionCallContent fcc, string argsSnapshot)
    {
        return string.IsNullOrEmpty(argsSnapshot) ? fcc.Name : $"{fcc.Name}: {argsSnapshot}";
    }

    private static AIFunction? ResolveRegisteredFunction(ChatOptions chatOptions, string? functionName)
    {
        if (string.IsNullOrEmpty(functionName) || chatOptions.Tools is null)
        {
            return null;
        }

        foreach (AITool tool in chatOptions.Tools)
        {
            if (tool is AIFunction fn && string.Equals(fn.Name, functionName, StringComparison.Ordinal))
            {
                return fn;
            }
        }

        return null;
    }

    private static async Task<string> InvokeToolCallAsync(
        FunctionCallContent fcc,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        AIFunction? func = ResolveRegisteredFunction(chatOptions, fcc.Name);

        if (func is null)
        {
            return $"No local tool registered for '{fcc.Name}'.";
        }

        AIFunctionArguments args = fcc.Arguments is { Count: > 0 }
            ? new AIFunctionArguments(fcc.Arguments)
            : [];

        object? output = await func
            .InvokeAsync(args, cancellationToken)
            .ConfigureAwait(false);

        return output switch
        {
            null => string.Empty,
            string s => s,
            _ => output.ToString() ?? string.Empty,
        };
    }

    private sealed record WardedToolExecutionResult(string ResultText, IReadOnlyList<IntelligenceEvent> WardEvents);

    private async Task<WardedToolExecutionResult> ExecuteToolCallWithWardAsync(
        FunctionCallContent fcc,
        PingRequest request,
        ChatOptions chatOptions,
        ParsedSpell? activeSpell,
        string? sessionId,
        TurnContext turnContext,
        string argsSnapshot,
        CancellationToken cancellationToken)
    {
        string toolName = fcc.Name ?? string.Empty;

        WardSettings wardSettings = settings.Value.Ward ?? new WardSettings();

        if (IsWardCandidate(toolName, turnContext.CampaignRequiresWard, wardSettings)
            && request.UnattendedMode
            && wardSettings.AutoDenyInUnattendedMode)
        {
            return new WardedToolExecutionResult(UnattendedDenyMessage(toolName), []);
        }

        if (!IsForbiddenArt(request, toolName, turnContext.CampaignRequiresWard, wardSettings))
        {
            string directResult = await InvokeToolCallWithSanctumAsync(
                fcc,
                activeSpell,
                chatOptions,
                turnContext,
                argsSnapshot,
                cancellationToken).ConfigureAwait(false);

            return new WardedToolExecutionResult(directResult, []);
        }

        string wardId = Guid.NewGuid().ToString();

        JsonDocument? argsDocument = TryParseToolArgumentsDocument(argsSnapshot);

        JsonElement? wardArguments = argsDocument?.RootElement.Clone();

        DateTimeOffset wardTimestamp = DateTimeOffset.UtcNow;

        var wardEvents = new List<IntelligenceEvent>(2);

        wardEvents.Add(new IntelligenceEvent(
            IntelligenceEventType.Warded,
            toolName,
            null,
            null,
            null,
            wardId,
            toolName,
            wardArguments,
            null,
            null,
            wardTimestamp));

        int timeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(wardSettings.TimeoutSeconds);

        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);

        WardResolution resolution;

        try
        {
            resolution = await ward
                .WardAsync(wardId, toolName, argsDocument, sessionId, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            argsDocument?.Dispose();
        }

        DateTimeOffset resolvedTimestamp = DateTimeOffset.UtcNow;

        wardEvents.Add(new IntelligenceEvent(
            IntelligenceEventType.WardResolved,
            toolName,
            null,
            null,
            null,
            wardId,
            toolName,
            null,
            resolution.Allowed,
            resolution.Reason,
            resolvedTimestamp));

        if (!resolution.Allowed)
        {
            string denialMessage = string.Equals(resolution.Reason, WardTimeoutReason, StringComparison.Ordinal)
                ? TimeoutDenyMessage(resolution.Reason)
                : OperatorDenyMessage(resolution.Reason);

            return new WardedToolExecutionResult(denialMessage, wardEvents);
        }

        string allowedResult = await InvokeToolCallWithSanctumAsync(
            fcc,
            activeSpell,
            chatOptions,
            turnContext,
            argsSnapshot,
            cancellationToken).ConfigureAwait(false);

        return new WardedToolExecutionResult(allowedResult, wardEvents);
    }

    private async Task<string> InvokeToolCallWithSanctumAsync(
        FunctionCallContent fcc,
        ParsedSpell? activeSpell,
        ChatOptions chatOptions,
        TurnContext turnContext,
        string argsSnapshot,
        CancellationToken cancellationToken)
    {
        SanctumEnforcementOutcome outcome = await EnforceSanctumAsync(
            fcc,
            turnContext,
            activeSpell,
            argsSnapshot,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Result.Allowed && outcome.Enabled && outcome.Mode == SanctumMode.Strict)
        {
            return SanctumDenialMessage(outcome.Result);
        }

        return await InvokeToolCallAsync(fcc, chatOptions, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SanctumEnforcementOutcome(SanctumResult Result, SanctumMode Mode, bool Enabled);

    private async Task<SanctumEnforcementOutcome> EnforceSanctumAsync(
        FunctionCallContent fcc,
        TurnContext turnContext,
        ParsedSpell? activeSpell,
        string argsSnapshot,
        CancellationToken cancellationToken)
    {
        SanctumResult allowed = new() { Allowed = true };

        if (turnContext.Campaign is null)
        {
            return new SanctumEnforcementOutcome(allowed, SanctumMode.Strict, false);
        }

        if (!turnContext.SanctumEnabled)
        {
            return new SanctumEnforcementOutcome(allowed, turnContext.SanctumMode, false);
        }

        string campaignId = turnContext.CampaignId!;

        string toolName = fcc.Name ?? string.Empty;

        SanctumResult toolResult = await sanctumGuard
            .ValidateToolAsync(campaignId, toolName, cancellationToken)
            .ConfigureAwait(false);

        if (!toolResult.Allowed)
        {
            return new SanctumEnforcementOutcome(toolResult, turnContext.SanctumMode, true);
        }

        using JsonDocument? argsDocument = TryParseToolArgumentsDocument(argsSnapshot);

        JsonElement argsRoot = argsDocument?.RootElement ?? default;

        string workspaceRoot = turnContext.WorkspaceRoot!;

        SanctumResult? pathOrNetworkResult = await ValidateToolPathsAndNetworkAsync(
            campaignId,
            toolName,
            workspaceRoot,
            activeSpell,
            argsRoot,
            cancellationToken).ConfigureAwait(false);

        if (pathOrNetworkResult is not null)
        {
            return new SanctumEnforcementOutcome(pathOrNetworkResult, turnContext.SanctumMode, true);
        }

        return new SanctumEnforcementOutcome(allowed, turnContext.SanctumMode, true);
    }

    private async Task<SanctumResult?> ValidateToolPathsAndNetworkAsync(
        string campaignId,
        string toolName,
        string workspaceRoot,
        ParsedSpell? activeSpell,
        JsonElement argsRoot,
        CancellationToken cancellationToken)
    {
        switch (toolName)
        {
            case "execute_command":
            {
                string cwd = workspaceRoot;

                if (TryGetJsonStringProperty(argsRoot, "workingDirectory", out string? relativeCwd)
                    && !string.IsNullOrWhiteSpace(relativeCwd))
                {
                    if (!TryResolvePathUnderWorkspace(workspaceRoot, relativeCwd, out string? resolvedCwd))
                    {
                        return await sanctumGuard.ValidatePathAsync(
                            campaignId,
                            relativeCwd,
                            "working directory",
                            toolName,
                            cancellationToken).ConfigureAwait(false);
                    }

                    cwd = resolvedCwd;
                }

                SanctumResult cwdResult = await sanctumGuard
                    .ValidatePathAsync(campaignId, cwd, "working directory", toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!cwdResult.Allowed)
                {
                    return cwdResult;
                }

                break;
            }

            case "write_file":
            case "replace_text_block":
            case "read_file_chunk":
            {
                if (!TryGetJsonStringProperty(argsRoot, "relativePath", out string? relativePath)
                    || string.IsNullOrWhiteSpace(relativePath))
                {
                    break;
                }

                if (!TryResolvePathUnderWorkspace(workspaceRoot, relativePath, out string? absolutePath))
                {
                    return await sanctumGuard.ValidatePathAsync(
                        campaignId,
                        relativePath,
                        "file path",
                        toolName,
                        cancellationToken).ConfigureAwait(false);
                }

                SanctumResult pathResult = await sanctumGuard
                    .ValidatePathAsync(campaignId, absolutePath, "file path", toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!pathResult.Allowed)
                {
                    return pathResult;
                }

                break;
            }

            case "run_spell_script":
            {
                if (activeSpell is null)
                {
                    break;
                }

                if (!TryGetJsonStringProperty(argsRoot, "script_name", out string? scriptName)
                    || string.IsNullOrWhiteSpace(scriptName))
                {
                    break;
                }

                scriptName = scriptName.Trim();

                if (!string.Equals(Path.GetFileName(scriptName), scriptName, StringComparison.Ordinal))
                {
                    string scriptsRoot = Path.Combine(activeSpell.DirectoryPath, "scripts");

                    string invalidCandidate = Path.GetFullPath(Path.Combine(scriptsRoot, scriptName));

                    SanctumResult invalidResult = await sanctumGuard
                        .ValidatePathAsync(campaignId, invalidCandidate, "script path", toolName, cancellationToken)
                        .ConfigureAwait(false);

                    if (!invalidResult.Allowed)
                    {
                        return invalidResult;
                    }

                    break;
                }

                string scriptsDirectory = Path.Combine(activeSpell.DirectoryPath, "scripts");

                string scriptPath = Path.GetFullPath(Path.Combine(scriptsDirectory, scriptName));

                SanctumResult scriptResult = await sanctumGuard
                    .ValidatePathAsync(campaignId, scriptPath, "script path", toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!scriptResult.Allowed)
                {
                    return scriptResult;
                }

                SanctumResult scriptsRootResult = await sanctumGuard
                    .ValidatePathAsync(campaignId, scriptsDirectory, "working directory", toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!scriptsRootResult.Allowed)
                {
                    return scriptsRootResult;
                }

                break;
            }

            case "use_commlink":
            {
                string? webhookUrl = settings.Value.CommLink?.WebhookUrl;

                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    break;
                }

                SanctumResult networkResult = await sanctumGuard
                    .ValidateNetworkAsync(campaignId, webhookUrl, toolName, cancellationToken)
                    .ConfigureAwait(false);

                if (!networkResult.Allowed)
                {
                    return networkResult;
                }

                break;
            }
        }

        return null;
    }

    private static bool TryResolvePathUnderWorkspace(string workspaceRoot, string relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;

        try
        {
            string root = Path.GetFullPath(workspaceRoot.Trim());

            absolutePath = Path.IsPathRooted(relativePath)
                ? Path.GetFullPath(relativePath.Trim())
                : Path.GetFullPath(Path.Combine(root, relativePath.Trim()));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetJsonStringProperty(JsonElement root, string propertyName, out string? value)
    {
        value = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();

        return true;
    }

    private static string SanctumDenialMessage(SanctumResult result)
    {
        string breachType = result.Breach?.BreachType ?? "PolicyViolation";

        string reason = result.DenyReason ?? "This operation is not permitted in the current Sanctum.";

        return $"The Sanctum Guard has blocked this action: {breachType} — {reason}.\n"
            + "The Dungeon Master must update the Sanctum config to allow this operation.";
    }

    private static bool IsWardCandidate(string toolName, bool campaignRequiresWard, WardSettings wardSettings) =>
        RequiresWardForTool(toolName, campaignRequiresWard, wardSettings);

    private static bool IsForbiddenArt(
        PingRequest request,
        string toolName,
        bool campaignRequiresWard,
        WardSettings wardSettings) =>
        RequiresWardForTool(toolName, campaignRequiresWard, wardSettings)
        && !(request.UnattendedMode && wardSettings.AutoDenyInUnattendedMode);

    private static bool RequiresWardForTool(string toolName, bool campaignRequiresWard, WardSettings wardSettings)
    {

        if (!wardSettings.Enabled
            || !wardSettings.ForbiddenArts.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {

            return false;

        }

        if (string.Equals(toolName, "execute_command", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        return campaignRequiresWard;

    }

    private static string UnattendedDenyMessage(string toolName) =>
        $"Forbidden art denied: unattended mode — this action requires an operator to resolve the ward";

    private static string OperatorDenyMessage(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Operator denied this action."
            : $"Operator denied this action: {reason}";

    private static string TimeoutDenyMessage(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Operator denied this action: The ward held until timeout — action was not allowed"
            : $"Operator denied this action: {reason}";

    private static JsonDocument? TryParseToolArgumentsDocument(string argsSnapshot)
    {
        if (string.IsNullOrWhiteSpace(argsSnapshot))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(argsSnapshot);
        }
        catch (JsonException)
        {
            string encoded = JsonSerializer.Serialize(argsSnapshot, ArcanumJsonContext.Default.String);

            return JsonDocument.Parse($"{{\"raw\":{encoded}}}");
        }
    }

    private async Task TryAppendToolInteractionToGrimoireAsync(
        Guid? sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken)
    {
        if (!sessionId.HasValue)
        {
            return;
        }

        try
        {
            await grimoire
                .AppendToolInteractionAsync(
                    sessionId.Value,
                    toolName,
                    arguments,
                    result,
                    modelUsed,
                    cancellationToken)
                .ConfigureAwait(false);

            await PublishLatestSavedEntriesAsync(sessionId.Value, 2, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grimoire could not append tool interaction for tool {ToolName}.", toolName);
        }
    }

    private async Task PublishLatestSavedEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken)
    {
        List<GrimoireEntryDto>? entries = await grimoire
            .GetRecentSessionEntriesAsync(sessionId, takeLast, cancellationToken)
            .ConfigureAwait(false);

        if (entries is null || entries.Count == 0)
        {
            return;
        }

        foreach (GrimoireEntryDto dto in entries)
        {
            sessionEventHub.Publish(sessionId, ToEntry(dto, sessionId));
        }
    }

    private async Task PublishSavedEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken)
    {
        GrimoireEntryDto? dto = await grimoire
            .GetEntryByIdAsync(sessionId, entryId, cancellationToken)
            .ConfigureAwait(false);

        if (dto is null)
        {
            return;
        }

        sessionEventHub.Publish(sessionId, ToEntry(dto, sessionId));
    }

    private static Entry ToEntry(GrimoireEntryDto dto, Guid sessionId) =>
        new()
        {
            Id = dto.Id,
            SessionId = sessionId,
            Role = dto.Role,
            Content = dto.Content,
            ModelUsed = dto.ModelUsed,
            CreatedAt = dto.CreatedAt,
        };

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
                "Validation.AttachedFiles",
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
                error = new Error("Validation.AttachedFiles", "Attached file entries cannot be null.");

                return false;
            }

            if (string.IsNullOrWhiteSpace(item.RelativePath))
            {
                error = new Error(
                    "Validation.AttachedFiles",
                    "Each attached file must have a non-empty relative path.");

                return false;
            }

            if (item.RelativePath.Length > maxPathChars)
            {
                error = new Error("Validation.AttachedFiles", "Attached file path is too long.");

                return false;
            }

            string content = item.Content ?? string.Empty;

            long utf8Len = Encoding.UTF8.GetByteCount(content);

            if (utf8Len > maxBytes)
            {
                error = new Error(
                    "Validation.AttachedFiles",
                    $"Attached file content exceeds the maximum size ({maxBytes} bytes UTF-8).");

                return false;
            }

            totalUtf8 += utf8Len;

            if (totalUtf8 > maxTotalBytes)
            {
                error = new Error(
                    "Validation.AttachedFiles",
                    "Total size of attached files exceeds the allowed limit for this request.");

                return false;
            }
        }

        error = Error.None;

        return true;
    }

}
