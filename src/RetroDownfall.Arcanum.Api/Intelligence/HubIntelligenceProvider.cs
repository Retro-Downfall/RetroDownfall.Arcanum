using System.Buffers;
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
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed class HubIntelligenceProvider(
    IChatClientFactory chatClientFactory,
    IOptionsSnapshot<ArcanumSettings> settings,
    ILogger<HubIntelligenceProvider> logger,
    IGrimoireRepository grimoire,
    McpConnectionManager mcpConnectionManager,
    InferenceTokenizerResolver inferenceTokenizerResolver) : IArcanumIntelligenceProvider
{
    private const int MaxToolInferenceRounds = 8;

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

    private const string PublicModelResolutionFailureMessage =
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.";

    private static readonly ArcanumLocalTimeTool _localTimeTool = new();

    private static readonly ArcanumSystemInfoTool _systemInfoTool = new();

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
            lease = chatClientFactory.ResolveClient(request.Model);
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
                Result ensure = await EnsureModelExistsAsync(lease.OllamaApi!, targetModel, cancellationToken, pullProgress: null).ConfigureAwait(false);

                if (ensure.IsFailure)
                {
                    return Result<PromptTurnResult>.Failure(ensure.Error);
                }
            }

            Conversation? thread = null;

        if (!HasStatelessMessages(request) && request.ConversationId is { } existingConversationId)
        {
            thread = await grimoire
                .GetConversationAsync(existingConversationId, cancellationToken)
                .ConfigureAwait(false);
        }

        Guid? assistantMessageId = null;

        Guid? grimoireConversationId = null;

        if (!HasStatelessMessages(request))
        {
            try
            {
                (Guid cid, Guid aid) = await grimoire
                    .BeginAssistantReplyAsync(request.ConversationId, prompt, targetModel, cancellationToken)
                    .ConfigureAwait(false);

                grimoireConversationId = cid;

                assistantMessageId = aid;
            }
            catch (Exception beginEx)
            {
                logger.LogWarning(beginEx, "Grimoire could not begin assistant reply for model {ModelName}.", targetModel);
            }
        }

        string? codexContent = await CodexReader
            .ReadCodexAsync(request.WorkingDirectory, cancellationToken)
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

            IReadOnlyList<ParsedSpell> spells = await SpellScanner
                .ScanAsync(spellWorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.OverrideSpellName))
            {
                if (!TryResolveOverrideSpell(request.OverrideSpellName, spells, out ParsedSpell? overridePick))
                {
                    return Result<PromptTurnResult>.Failure(
                        new Error(
                            "Validation.SpellOverride",
                            $"No spell matches OverrideSpellName '{request.OverrideSpellName.Trim()}'. Expected a SPELL.md frontmatter name or parent folder name."));
                }

                activeSpell = overridePick;
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

                activeSpell = await SemanticRouter
                    .DetermineActiveSpellAsync(
                        chatClient,
                        semanticProbe,
                        spells,
                        spellPreflight,
                        routerMaxTokens,
                        routerTemperature,
                        cancellationToken,
                        logger)
                    .ConfigureAwait(false);
            }
        }

        string builtSystemPrompt = SystemPromptBuilder.Build(request, codexContent, activeSpell, request.AttachedFiles);

        List<AITool> toolSet = await BuildToolSetWithMcpAsync(request, activeSpell, cancellationToken).ConfigureAwait(false);

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

                ChatOptions chatOptions = CreateInferenceChatOptions(inferenceUsesTools, toolSet, request, lease);

                ChatResponse? response;

                int toolRoundsExecuted = 0;

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

                    if (toolRoundsExecuted > MaxToolInferenceRounds)
                    {
                        return Result<PromptTurnResult>.Failure(new Error("Hub.ToolLoop", "Tool invocation limit reached."));
                    }

                    foreach (FunctionCallContent fcc in calls)
                    {
                        string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

                        string callId = string.IsNullOrEmpty(fcc.CallId) ? fcc.Name : fcc.CallId;

                        (observedToolCalls ??= []).Add(new PromptToolCall(callId, fcc.Name, argsSnapshot));

                        string resultText = await InvokeToolCallAsync(fcc, chatOptions, cancellationToken).ConfigureAwait(false);

                        chatMessages.Add(new MeAiChatMessage(ChatRole.Assistant, [fcc]));

                        chatMessages.Add(
                            new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(fcc.CallId, resultText)]));

                        await TryAppendToolInteractionToGrimoireAsync(
                            grimoireConversationId,
                            fcc.Name,
                            argsSnapshot,
                            resultText,
                            targetModel,
                            cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                string finalText = response.Text;

                if (assistantMessageId is { } finalizeId)
                {
                    try
                    {
                        await grimoire
                            .FinalizeAssistantMessageAsync(finalizeId, finalText, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception persistEx)
                    {
                        logger.LogWarning(persistEx, "Grimoire could not finalize assistant message for model {ModelName}.", targetModel);
                    }
                }

                await TryIncrementConversationTokensAsync(
                    grimoireConversationId,
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
            leaseOrNull = chatClientFactory.ResolveClient(request.Model);
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

                Result<bool> localCheck = await IsModelLocalAsync(lease.OllamaApi!, targetModel, cancellationToken).ConfigureAwait(false);

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

        Conversation? thread = null;

        if (!HasStatelessMessages(request) && request.ConversationId is { } existingConversationId)
        {
            thread = await grimoire
                .GetConversationAsync(existingConversationId, cancellationToken)
                .ConfigureAwait(false);
        }

        List<MeAiChatMessage> chatMessages = BuildInitialMeAiChatMessages(request, thread, prompt);

        string? streamCodexContent = await CodexReader
            .ReadCodexAsync(request.WorkingDirectory, cancellationToken)
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

            IReadOnlyList<ParsedSpell> streamSpells = await SpellScanner
                .ScanAsync(streamSpellWorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.OverrideSpellName))
            {
                if (!TryResolveOverrideSpell(request.OverrideSpellName, streamSpells, out ParsedSpell? streamOverridePick))
                {
                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Error,
                        $"No spell matches OverrideSpellName '{request.OverrideSpellName.Trim()}'. Expected a SPELL.md frontmatter name or parent folder name.");

                    yield break;
                }

                streamActiveSpell = streamOverridePick;
            }
            else
            {
                TimeSpan streamSpellPreflight = TimeSpan.FromSeconds(
                    ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds(settings.Value.Intelligence.SemanticRouterPreflightTimeoutSeconds));

                int streamRouterMaxTokens = ArcanumSettingClamps.SemanticRouterMaxTokens(
                    settings.Value.Intelligence.SemanticRouterMaxTokens);

                float streamRouterTemperature = ArcanumSettingClamps.SemanticRouterTemperature(
                    settings.Value.Intelligence.SemanticRouterTemperature);

                string streamSemanticProbe = GetSemanticRouterUserProbe(request);

                streamActiveSpell = await SemanticRouter
                    .DetermineActiveSpellAsync(
                        chatClient,
                        streamSemanticProbe,
                        streamSpells,
                        streamSpellPreflight,
                        streamRouterMaxTokens,
                        streamRouterTemperature,
                        cancellationToken,
                        logger)
                    .ConfigureAwait(false);
            }
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

        Guid? assistantMessageId = null;

        Guid? boundConversationId = null;

        if (!HasStatelessMessages(request))
        {
            try
            {
                (Guid conversationId, Guid aid) = await grimoire
                    .BeginAssistantReplyAsync(request.ConversationId, prompt, targetModel, cancellationToken)
                    .ConfigureAwait(false);

                assistantMessageId = aid;

                boundConversationId = conversationId;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Grimoire could not start streamed conversation persistence for model {ModelName}.", targetModel);
            }
        }

        if (boundConversationId is { } bcid)
        {
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

        bool streamUsesTools = true;

        string? inferenceError;

        ChatCompletionUsage? streamAccumulatedUsage = null;

        while (true)
        {
            bool streamOuterRestart = false;

            accumulator = new StringBuilder(1024);

            streamAccumulatedUsage = null;

            ChatOptions streamChatOptions = CreateInferenceChatOptions(streamUsesTools, streamToolSet, request, lease);

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

                        roundUpdates.Add(update);

                        if (string.IsNullOrEmpty(update.Text))
                        {
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

                if (streamToolRoundCount > MaxToolInferenceRounds)
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
                    string toolCallData = FormatToolCallEventData(fcc);

                    string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

                    string callId = string.IsNullOrEmpty(fcc.CallId) ? fcc.Name : fcc.CallId;

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolCall,
                        fcc.Name,
                        toolCallData,
                        null,
                        new IntelligenceToolCallEvent(callId, fcc.Name, argsSnapshot, toolCallIndex));

                    string resultText;

                    try
                    {
                        resultText = await InvokeToolCallAsync(fcc, streamChatOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Tool {ToolName} failed during streaming inference.", fcc.Name);

                        resultText = PublicToolFailureMessageForGrimoire;
                    }

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolResult,
                        fcc.Name,
                        resultText,
                        null,
                        new IntelligenceToolCallEvent(callId, fcc.Name, resultText, toolCallIndex));

                    chatMessages.Add(new MeAiChatMessage(ChatRole.Assistant, [fcc]));

                    chatMessages.Add(
                        new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(fcc.CallId, resultText)]));

                    await TryAppendToolInteractionToGrimoireAsync(
                        boundConversationId,
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

        if (assistantMessageId is { } finalizeId)
        {
            try
            {
                await grimoire.FinalizeAssistantMessageAsync(finalizeId, finalText, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Grimoire could not finalize streamed assistant message for model {ModelName}.", targetModel);
            }
        }

        await TryIncrementConversationTokensAsync(boundConversationId, streamAccumulatedUsage, cancellationToken)
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

    private async Task<Result> EnsureModelExistsAsync(IOllamaApiClient ollamaClient, string modelName, CancellationToken cancellationToken, IProgress<string>? pullProgress)
    {
        Result<bool> localCheck = await IsModelLocalAsync(ollamaClient, modelName, cancellationToken).ConfigureAwait(false);

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

    private async Task<Result<bool>> IsModelLocalAsync(IOllamaApiClient ollamaClient, string modelName, CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<Model> models = await ollamaClient.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);

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
        Conversation? thread,
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

        if (InferenceTokenCounter.ShouldSkipCompressionPreflight(chatMessages))
        {

            return (false, chatMessages);

        }

        Tokenizer tokenizer = inferenceTokenizerResolver.ResolveTokenizer(lease.Provider.Type, lease.ResolvedModel);

        int totalTokens = InferenceTokenCounter.CountTokens(chatMessages, tokenizer);

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
                "Context ({TotalTokens} tokens) exceeds compression threshold ({EffectiveLimit} tokens) but no campaign summary is available for conversation {ConversationId}; proceeding unfiltered.",
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

        int afterTokens = InferenceTokenCounter.CountTokens(rebuilt, tokenizer);

        if (afterTokens > effectiveLimit)
        {

            logger.LogWarning(
                "After memory compression, context is still {AfterTokens} tokens (threshold {EffectiveLimit}) for conversation {ConversationId}.",
                afterTokens,
                effectiveLimit,
                thread.Id);

        }

        return (true, rebuilt);

    }

    private static List<MeAiChatMessage> MapFilteredGrimoireToMeAiMessages(
        Conversation conversation,
        DateTime watermarkExclusive,
        string newUserPrompt)
    {

        List<Core.Storage.Entities.ChatMessage> ordered = conversation.Messages
            .Where(m => m.Timestamp > watermarkExclusive)
            .OrderBy(m => m.Timestamp)
            .ToList();

        while (ordered.Count > 0

            && ordered[^1].Role == MessageRole.Assistant

            && string.IsNullOrEmpty(ordered[^1].Content))
        {

            ordered.RemoveAt(ordered.Count - 1);

        }

        var list = new List<MeAiChatMessage>(ordered.Count + 1);

        foreach (Core.Storage.Entities.ChatMessage m in ordered)
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

    private static bool TryResolveOverrideSpell(
        string? overrideSpellName,
        IReadOnlyList<ParsedSpell> spells,
        out ParsedSpell? matched)
    {
        matched = null;

        if (string.IsNullOrWhiteSpace(overrideSpellName))
        {
            return false;
        }

        string needle = overrideSpellName.Trim();

        for (int i = 0; i < spells.Count; i++)
        {
            ParsedSpell s = spells[i];

            if (string.Equals(s.Name, needle, StringComparison.OrdinalIgnoreCase))
            {
                matched = s;

                return true;
            }

            string leaf = GetSpellDirectoryLeafName(s.DirectoryPath);

            if (string.Equals(leaf, needle, StringComparison.OrdinalIgnoreCase))
            {
                matched = s;

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
        Conversation? thread,
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

        return ChatRole.User;
    }

    private static List<MeAiChatMessage> MapGrimoireToMeAiMessages(Conversation? conversation, string newUserPrompt)
    {
        if (conversation is null)
        {
            return [new MeAiChatMessage(ChatRole.User, newUserPrompt)];
        }

        var ordered = conversation.Messages.ToList();

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

        if (request.DisableMcpTools)
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

    private ChatOptions CreateInferenceChatOptions(bool includeTools, List<AITool>? tools, PingRequest request, ChatClientLease lease)
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

        if (!request.UnattendedMode)
        {
            options.Tools = tools;

            return options;
        }

        List<AITool> filtered = [];

        foreach (AITool t in tools)
        {
            if (t is AIFunction fn && string.Equals(fn.Name, "ask_human", StringComparison.Ordinal))
            {
                continue;
            }

            filtered.Add(t);
        }

        options.Tools = filtered;

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
            options.ResponseFormat = responseFormatType.ToLowerInvariant() switch
            {
                "json_object" or "json_schema" => ChatResponseFormat.Json,
                "text" => ChatResponseFormat.Text,
                _ => options.ResponseFormat,
            };
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

    private async Task TryIncrementConversationTokensAsync(
        Guid? conversationId,
        ChatCompletionUsage? usage,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Intelligence.EnableTokenTracking || !conversationId.HasValue || usage is null || usage.TotalTokens <= 0)
        {
            return;
        }

        try
        {
            await grimoire
                .IncrementConversationTokensAsync(conversationId.Value, usage.TotalTokens, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grimoire could not increment token totals for conversation {ConversationId}.", conversationId);
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

            case int i:
                writer.WriteNumberValue(i);
                break;

            case long l:
                writer.WriteNumberValue(l);
                break;

            case double d:
                writer.WriteNumberValue(d);
                break;

            case float f:
                writer.WriteNumberValue(f);
                break;

            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string FormatToolCallEventData(FunctionCallContent fcc)
    {
        string args = SerializeToolArgumentsForGrimoire(fcc);

        return string.IsNullOrEmpty(args) ? (fcc.Name) : $"{fcc.Name}: {args}";
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

    private async Task TryAppendToolInteractionToGrimoireAsync(
        Guid? conversationId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken)
    {
        if (!conversationId.HasValue)
        {
            return;
        }

        try
        {
            await grimoire
                .AppendToolInteractionAsync(
                    conversationId.Value,
                    toolName,
                    arguments,
                    result,
                    modelUsed,
                    cancellationToken)
                .ConfigureAwait(false);
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
