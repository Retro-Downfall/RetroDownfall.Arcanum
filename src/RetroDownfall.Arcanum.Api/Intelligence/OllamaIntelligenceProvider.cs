using System.Diagnostics.CodeAnalysis;
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
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed class OllamaIntelligenceProvider(
    IOllamaApiClient ollamaClient,
    IChatClient chatClient,
    IOptions<ArcanumSettings> settings,
    ILogger<OllamaIntelligenceProvider> logger,
    IGrimoireRepository grimoire) : IArcanumIntelligenceProvider
{
    private const int MaxToolInferenceRounds = 8;

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

        ollamaClient.SelectedModel = targetModel;

        Result ensure = await EnsureModelExistsAsync(targetModel, cancellationToken, pullProgress: null).ConfigureAwait(false);

        if (ensure.IsFailure)
        {
            return Result<string>.Failure(ensure.Error);
        }

        Conversation? thread = null;

        if (request.ConversationId is { } existingConversationId)
        {
            thread = await grimoire
                .GetConversationAsync(existingConversationId, cancellationToken)
                .ConfigureAwait(false);
        }

        Guid? assistantMessageId = null;

        Guid? grimoireConversationId = null;

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

        string? codexContent = await CodexReader
            .ReadCodexAsync(request.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> spells;

        if (ToolHelpers.TryNormalizeWorkspace(request.WorkingDirectory, out string? spellRoot, out _))
        {
            spells = await SpellScanner
                .ScanAsync(spellRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            spells = [];
        }

        ParsedSpell? activeSpell = await SemanticRouter
            .DetermineActiveSpellAsync(chatClient, prompt, spells, cancellationToken)
            .ConfigureAwait(false);

        string builtSystemPrompt = SystemPromptBuilder.Build(request, codexContent, activeSpell);

        List<AITool> toolSet = BuildToolSet(request.WorkingDirectory);

        bool inferenceUsesTools = true;

        while (true)
        {
            try
            {
                var chatMessages = MapGrimoireToMeAiMessages(thread, prompt);

                PrependDynamicSystemMessage(chatMessages, builtSystemPrompt);

                ChatOptions chatOptions = CreateInferenceChatOptions(inferenceUsesTools, toolSet);

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

                return Result<string>.Failure(new Error("Ollama.Error", ex.Message));
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

        ollamaClient.SelectedModel = targetModel;

        yield return new IntelligenceEvent(
            IntelligenceEventType.Status,
            $"Checking local availability for {targetModel}...");

        Result<bool> localCheck = await IsModelLocalAsync(targetModel, cancellationToken).ConfigureAwait(false);

        if (localCheck.IsFailure)
        {
            yield return new IntelligenceEvent(IntelligenceEventType.Error, localCheck.Error.Message);

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
                        pullMoveFailed = true;

                        pullMoveError = ex.Message;

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
                yield return new IntelligenceEvent(IntelligenceEventType.Error, pullMoveError ?? "Pull failed.");

                yield break;
            }
        }

        yield return new IntelligenceEvent(IntelligenceEventType.Status, "Mage is generating response...");

        Conversation? thread = null;

        if (request.ConversationId is { } existingConversationId)
        {
            thread = await grimoire
                .GetConversationAsync(existingConversationId, cancellationToken)
                .ConfigureAwait(false);
        }

        List<MeAiChatMessage> chatMessages = MapGrimoireToMeAiMessages(thread, prompt);

        string? streamCodexContent = await CodexReader
            .ReadCodexAsync(request.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> streamSpells;

        if (ToolHelpers.TryNormalizeWorkspace(request.WorkingDirectory, out string? streamSpellRoot, out _))
        {
            streamSpells = await SpellScanner
                .ScanAsync(streamSpellRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            streamSpells = [];
        }

        ParsedSpell? streamActiveSpell = await SemanticRouter
            .DetermineActiveSpellAsync(chatClient, prompt, streamSpells, cancellationToken)
            .ConfigureAwait(false);

        string streamBuiltSystemPrompt = SystemPromptBuilder.Build(request, streamCodexContent, streamActiveSpell);

        PrependDynamicSystemMessage(chatMessages, streamBuiltSystemPrompt);

        Guid? assistantMessageId = null;

        Guid? boundConversationId = null;

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

        if (boundConversationId is { } bcid)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.ConversationBound,
                "Conversation started",
                bcid.ToString());
        }

        StringBuilder accumulator;

        List<AITool> streamToolSet = BuildToolSet(request.WorkingDirectory);

        bool streamUsesTools = true;

        string? inferenceError;

        while (true)
        {
            bool streamOuterRestart = false;

            accumulator = new StringBuilder(1024);

            ChatOptions streamChatOptions = CreateInferenceChatOptions(streamUsesTools, streamToolSet);

            int streamToolRoundCount = 0;

            inferenceError = null;

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
                        inferenceError = ex.Message;

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

                    if (assistantMessageId is { } aid)
                    {
                        try
                        {
                            await grimoire.AppendAssistantContentAsync(aid, update.Text, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Grimoire could not append streaming token for model {ModelName}.", targetModel);
                        }
                    }

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

                    && LooksLikeModelDoesNotSupportTools(inferenceError)

                    && accumulator.Length == 0)
                {
                    logger.LogInformation(
                        "Model {ModelName} does not support tools in Ollama; retrying stream without local tools.",
                        targetModel);

                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Status,
                        "This Ollama model does not support tools; continuing without local tools.");

                    streamUsesTools = false;

                    inferenceError = null;

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
                break;
            }

            streamToolRoundCount++;

            if (streamToolRoundCount > MaxToolInferenceRounds)
            {
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
                    resultText = $"Error: {ex.Message}";
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
            logger.LogError(
                "Streaming inference failed for model {ModelName}: {InferenceError}",
                targetModel,
                inferenceError);

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

        yield return new IntelligenceEvent(IntelligenceEventType.Result, "Complete", finalText);
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
            return Result.Failure(new Error("Ollama.Pull", ex.Message));
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

            return Result<bool>.Failure(new Error("Ollama.ListModels", ex.Message));
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

    private static List<MeAiChatMessage> MapGrimoireToMeAiMessages(Conversation? conversation, string newUserPrompt)
    {
        if (conversation is null)
        {
            return [new MeAiChatMessage(ChatRole.User, newUserPrompt)];
        }

        var ordered = conversation.Messages.ToList();

        ordered.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));

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

    private static List<AITool> BuildToolSet(string workingDirectory) =>

    [

        new ArcanumLocalTimeTool(),
        new LoreSeekerTool(workingDirectory),
        new RuneExecutorTool(workingDirectory),
    ];

    private static ChatOptions CreateInferenceChatOptions(bool includeTools, List<AITool>? tools)
    {
        if (!includeTools || tools is null)
        {
            return new ChatOptions();
        }

        return new ChatOptions { Tools = tools };
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

    [UnconditionalSuppressMessage(
        "ReflectionAnalysis",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Tool argument dictionaries are small JSON-serializable payloads from the model; keys/values are strings or primitives.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Same as IL2026; Grimoire stores a diagnostic string snapshot of tool arguments.")]

    private static string SerializeToolArgumentsForGrimoire(FunctionCallContent fcc)
    {
        if (fcc.Arguments is null || fcc.Arguments.Count == 0)
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(fcc.Arguments);
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

        var argDict = new Dictionary<string, object?>();

        if (fcc.Arguments is not null)
        {
            foreach (KeyValuePair<string, object?> pair in fcc.Arguments)
            {
                argDict[pair.Key] = pair.Value;
            }
        }

        object? output = await func
            .InvokeAsync(new AIFunctionArguments(argDict), cancellationToken)
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
