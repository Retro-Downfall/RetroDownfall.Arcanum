using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

public sealed class OllamaIntelligenceProvider(
    IOllamaApiClient ollamaClient,
    IChatClient chatClient,
    IOptions<ArcanumSettings> settings,
    ILogger<OllamaIntelligenceProvider> logger,
    IGrimoireRepository grimoire,
    McpConnectionManager mcpConnectionManager) : IArcanumIntelligenceProvider
{
    private const int MaxToolInferenceRounds = 8;

    private const string PublicInferenceFailureMessage =
        "Inference failed. Ensure Ollama is running and reachable, then try again. See server logs for details.";

    private const string PublicListLocalModelsFailureMessage =
        "Could not list local Ollama models. Ensure Ollama is running and reachable. See server logs for details.";

    private const string PublicModelPullFailureMessage =
        "Model download failed. Ensure Ollama is running and has network access. See server logs for details.";

    private const string PublicToolFailureMessageForGrimoire =
        "A tool invocation failed. See server logs for details.";

    private static readonly ArcanumLocalTimeTool _localTimeTool = new();

    public async Task<Result<string>> ExecutePromptAsync(PingRequest request, CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt;

        string? modelFromRequest = request.Model;

        string targetModel = !string.IsNullOrWhiteSpace(modelFromRequest)
            ? modelFromRequest.Trim()
            : settings.Value.Ollama.DefaultModel;

        if (string.IsNullOrWhiteSpace(targetModel))
        {
            return Result<string>.Failure(new Error("Ollama.Model", "No model configured. Set Arcanum:Ollama:DefaultModel or pass a model override."));
        }

        if (!TryValidateAttachedFiles(request, out Error attachedFilesError))
        {
            return Result<string>.Failure(attachedFilesError);
        }

        if (!HasStatelessMessages(request) && string.IsNullOrWhiteSpace(prompt))
        {
            return Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required."));
        }

        ollamaClient.SelectedModel = targetModel;

        Result ensure = await EnsureModelExistsAsync(targetModel, cancellationToken, pullProgress: null).ConfigureAwait(false);

        if (ensure.IsFailure)
        {
            return Result<string>.Failure(ensure.Error);
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

        string? spellWorkspaceRoot = RetroDownfall.Arcanum.Infrastructure.Mcp.ToolHelpers.TryNormalizeWorkspace(
            request.WorkingDirectory,
            out string? spellRoot,
            out _)
            ? spellRoot
            : null;

        IReadOnlyList<ParsedSpell> spells = await SpellScanner
            .ScanAsync(spellWorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        TimeSpan spellPreflight = TimeSpan.FromSeconds(
            Math.Clamp(settings.Value.Intelligence.SemanticRouterPreflightTimeoutSeconds, 1, 600));

        int routerMaxTokens = ArcanumSettingClamps.SemanticRouterMaxTokens(
            settings.Value.Intelligence.SemanticRouterMaxTokens);

        float routerTemperature = ArcanumSettingClamps.SemanticRouterTemperature(
            settings.Value.Intelligence.SemanticRouterTemperature);

        string semanticProbe = GetSemanticRouterUserProbe(request);

        ParsedSpell? activeSpell = await SemanticRouter
            .DetermineActiveSpellAsync(
                chatClient,
                semanticProbe,
                spells,
                spellPreflight,
                routerMaxTokens,
                routerTemperature,
                cancellationToken)
            .ConfigureAwait(false);

        string builtSystemPrompt = SystemPromptBuilder.Build(request, codexContent, activeSpell, request.AttachedFiles);

        List<AITool> toolSet = await BuildToolSetWithMcpAsync(request, activeSpell, cancellationToken).ConfigureAwait(false);

        bool inferenceUsesTools = true;

        while (true)
        {
            try
            {
                List<MeAiChatMessage> chatMessages = BuildInitialMeAiChatMessages(request, thread, prompt);

                PrependDynamicSystemMessage(chatMessages, builtSystemPrompt);

                ChatOptions chatOptions = CreateInferenceChatOptions(inferenceUsesTools, toolSet, request);

                ChatResponse? response;

                int toolRoundsExecuted = 0;

                while (true)
                {
                    response = await chatClient
                        .GetResponseAsync(chatMessages, chatOptions, cancellationToken)
                        .ConfigureAwait(false);

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
                        return Result<string>.Failure(new Error("Ollama.ToolLoop", "Tool invocation limit reached."));
                    }

                    foreach (FunctionCallContent fcc in calls)
                    {
                        string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

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

                return Result<string>.Success(finalText);
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

                logger.LogError(ex, "Ollama inference failed for model {ModelName}.", targetModel);

                return Result<string>.Failure(new Error("Ollama.Error", PublicInferenceFailureMessage));
            }
        }
    }

    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt;

        string? modelFromRequest = request.Model;

        string targetModel = !string.IsNullOrWhiteSpace(modelFromRequest)
            ? modelFromRequest.Trim()
            : settings.Value.Ollama.DefaultModel;

        if (string.IsNullOrWhiteSpace(targetModel))
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                "No model configured. Set Arcanum:Ollama:DefaultModel or pass a model override.");

            yield break;
        }

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

        ollamaClient.SelectedModel = targetModel;

        yield return new IntelligenceEvent(
            IntelligenceEventType.Status,
            $"Checking local availability for {targetModel}...");

        Result<bool> localCheck = await IsModelLocalAsync(targetModel, cancellationToken).ConfigureAwait(false);

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

            IAsyncEnumerator<PullModelResponse> pullEnumerator = EnumeratePullModelAsync(targetModel, cancellationToken).GetAsyncEnumerator(cancellationToken);

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

        string? streamSpellWorkspaceRoot = RetroDownfall.Arcanum.Infrastructure.Mcp.ToolHelpers.TryNormalizeWorkspace(
            request.WorkingDirectory,
            out string? streamSpellRoot,
            out _)
            ? streamSpellRoot
            : null;

        IReadOnlyList<ParsedSpell> streamSpells = await SpellScanner
            .ScanAsync(streamSpellWorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        TimeSpan streamSpellPreflight = TimeSpan.FromSeconds(
            Math.Clamp(settings.Value.Intelligence.SemanticRouterPreflightTimeoutSeconds, 1, 600));

        int streamRouterMaxTokens = ArcanumSettingClamps.SemanticRouterMaxTokens(
            settings.Value.Intelligence.SemanticRouterMaxTokens);

        float streamRouterTemperature = ArcanumSettingClamps.SemanticRouterTemperature(
            settings.Value.Intelligence.SemanticRouterTemperature);

        string streamSemanticProbe = GetSemanticRouterUserProbe(request);

        ParsedSpell? streamActiveSpell = await SemanticRouter
            .DetermineActiveSpellAsync(
                chatClient,
                streamSemanticProbe,
                streamSpells,
                streamSpellPreflight,
                streamRouterMaxTokens,
                streamRouterTemperature,
                cancellationToken)
            .ConfigureAwait(false);

        string streamBuiltSystemPrompt = SystemPromptBuilder.Build(
            request,
            streamCodexContent,
            streamActiveSpell,
            request.AttachedFiles);

        PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

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

        StringBuilder accumulator;

        List<AITool> streamToolSet = await BuildToolSetWithMcpAsync(request, streamActiveSpell, cancellationToken).ConfigureAwait(false);

        bool streamUsesTools = true;

        string? inferenceError;

        int streamCompletionTokenTotal = 0;

        while (true)
        {
            bool streamOuterRestart = false;

            accumulator = new StringBuilder(1024);

            ChatOptions streamChatOptions = CreateInferenceChatOptions(streamUsesTools, streamToolSet, request);

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

                List<FunctionCallContent> toolCalls = CollectFunctionCalls(combinedRound)
                    .Where(static c => !c.InformationalOnly)
                    .ToList();

                if (toolCalls.Count == 0)
                {
                    streamCompletionTokenTotal = SumCompletionTokensFromUsage(combinedRound);

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

                foreach (FunctionCallContent fcc in toolCalls)
                {
                    string toolCallData = FormatToolCallEventData(fcc);

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolCall,
                        fcc.Name,
                        toolCallData);

                    string argsSnapshot = SerializeToolArgumentsForGrimoire(fcc);

                    string resultText;

                    try
                    {
                        resultText = await InvokeToolCallAsync(fcc, streamChatOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Tool {ToolName} failed during streaming inference.", fcc.Name);

                        resultText = PublicToolFailureMessageForGrimoire;
                    }

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.ToolResult,
                        fcc.Name,
                        resultText);

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

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            "Complete",
            streamCompletionTokenTotal.ToString(CultureInfo.InvariantCulture));
    }

    private async IAsyncEnumerable<PullModelResponse> EnumeratePullModelAsync(
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

    private async Task<Result> EnsureModelExistsAsync(string modelName, CancellationToken cancellationToken, IProgress<string>? pullProgress)
    {
        Result<bool> localCheck = await IsModelLocalAsync(modelName, cancellationToken).ConfigureAwait(false);

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

            await foreach (PullModelResponse pull in EnumeratePullModelAsync(modelName, cancellationToken).ConfigureAwait(false))
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Model pull failed for {ModelName}.", modelName);

            return Result.Failure(new Error("Ollama.Pull", PublicModelPullFailureMessage));
        }
    }

    private async Task<Result<bool>> IsModelLocalAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<Model> models = await ollamaClient.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);

            return models.Any(m => ModelNameMatches(m.Name, modelName));
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
            list.Add(new MeAiChatMessage(MapOpenAiStyleRoleToChatRole(m.Role), m.Content ?? string.Empty));
        }

        return list;
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

        List<AITool> tools = [_localTimeTool];

        if (activeSpell?.AvailableScripts is { Count: > 0 })
        {
            int sec = Math.Clamp(settings.Value.Intelligence.ExecuteCommandTimeoutSeconds, 1, 600);

            string scriptsRoot = Path.Combine(activeSpell.DirectoryPath, "scripts");

            tools.Add(new ArcanumSpellScriptTool(scriptsRoot, TimeSpan.FromSeconds(sec), sec));
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

    private ChatOptions CreateInferenceChatOptions(bool includeTools, List<AITool>? tools, PingRequest request)
    {
        int numCtx = settings.Value.Ollama.ContextWindowLimit;

        var options = new ChatOptions();

        options.AdditionalProperties!["num_ctx"] = numCtx;

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

    private static int SumCompletionTokensFromUsage(ChatResponse response)
    {
        UsageDetails? usage = response.Usage;

        if (usage is null)
        {
            return 0;
        }

        long input = usage.InputTokenCount ?? 0L;

        long output = usage.OutputTokenCount ?? 0L;

        long sum = input + output;

        if (sum > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)sum;
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

    private static bool ModelNameMatches(string localModelName, string targetModel)
    {
        if (string.Equals(localModelName, targetModel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!targetModel.Contains(':'))
        {
            int colonIndex = localModelName.IndexOf(':');

            if (colonIndex >= 0)
            {
                return localModelName.AsSpan(0, colonIndex).Equals(targetModel, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
