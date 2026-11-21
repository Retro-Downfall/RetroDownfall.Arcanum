using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Infrastructure.Intelligence;

using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal sealed record ContextCompressionRequest
{
    public required PingRequest Request { get; init; }

    public required List<MeAiChatMessage> Messages { get; init; }

    public required Session? Thread { get; init; }

    public required string NewUserPrompt { get; init; }

    public bool PreserveToolHistory { get; init; }

    public Guid? RecoveryQuestionEntryId { get; init; }

    public bool RestoreNativeRecoveryQuestion { get; init; } = true;

    public required ChatClientLease Lease { get; init; }

    public required ChatOptions ChatOptions { get; init; }

    public string? CodexContent { get; init; }

    public ParsedSpell? ActiveSpell { get; init; }

    public IReadOnlyList<ParsedSpell>? DependencySpells { get; init; }

    public int ReservedAnswerTokens { get; init; }

    public int ReservedReasoningTokens { get; init; }

    public IReadOnlyList<AIContent>? AppendedContents { get; init; }

    /// <summary>The Covenant sections this turn admitted, so a compressed rebuild keeps them.</summary>
    /// <remarks>
    /// Compression rebuilds the system prompt from the summarized transcript. Rebuilding it without the
    /// Covenant would drop the operator's standing agreement exactly on the long sessions where it has
    /// been restated least — and the dispatch gate compares the rebuilt transcript against the frozen
    /// document, so the mismatch does not degrade quietly: it refuses the turn.
    /// </remarks>
    public CovenantPromptContent? Covenant { get; init; }

    public CampaignRollupArtifact? CampaignRollup { get; init; }

    public bool EnableCampaignRollups { get; init; }

    /// <summary>
    /// The Scrying foci the ledger accepted for this turn — the images
    /// <see cref="InferenceContextBuilder.BuildInitialMeAiChatMessages"/> threaded onto the last
    /// message. Compression rebuilds the transcript from Grimoire entries, which are text only, so
    /// they have to be handed in separately or the turn reaches the model blind.
    /// </summary>
    public List<ScryingFocusDto>? ScryingFoci { get; init; }

    public SemanticContextChunk[]? SemanticContext { get; init; }

    public SagaMemory[]? SagaMemories { get; init; }

    public SessionAttachmentRetrievedChunk[]? SessionAttachmentContext { get; init; }

    public TapestryContextNode[]? TapestryContext { get; init; }

    public IReadOnlyList<LexiconEntryDto>? LexiconEntries { get; init; }

    public IReadOnlyList<SessionAttachmentIndexItem>? SessionAttachmentsIndex { get; init; }

    public int MaxIndexItems { get; init; } = 40;

    public int MaxIndexBytes { get; init; } = 4096;
}

/// <summary>
/// History load, read-time compression decision, and Microsoft.Extensions.AI message mapping shared
/// by buffered and streaming inference paths in <see cref="WizardIntelligenceProvider"/>.
/// </summary>
public sealed class InferenceContextBuilder(
    IGrimoireRepository grimoire,
    IOptionsSnapshot<ArcanumSettings> settings,
    ILogger<InferenceContextBuilder> logger,
    IContextCompressionService contextCompressionService)
{

    public static bool HasStatelessMessages(PingRequest request) =>
        request.StatelessMessages is { Count: > 0 };

    public async Task<Session?> LoadThreadAsync(PingRequest request, CancellationToken cancellationToken)
    {

        if (HasStatelessMessages(request) || request.SessionId is not { } existingSessionId)
        {

            return null;

        }

        return await grimoire
            .GetSessionAsync(existingSessionId, cancellationToken)
            .ConfigureAwait(false);

    }

    public static List<MeAiChatMessage> BuildInitialMeAiChatMessages(
        PingRequest request,
        Session? thread,
        string newUserPrompt,
        bool preserveToolHistory = false,
        Guid? recoveryQuestionEntryId = null,
        bool restoreNativeRecoveryQuestion = true)
    {

        List<MeAiChatMessage> messages = HasStatelessMessages(request)
            ? MapStatelessMessagesToMeAi(request.StatelessMessages!)
            : MapHistory(thread, newUserPrompt, null, preserveToolHistory, recoveryQuestionEntryId, restoreNativeRecoveryQuestion);

        AttachScryingFociToLastMessage(messages, request.ScryingFoci);

        return messages;

    }

    /// <summary>
    /// Scrying — appends CLI/native <see cref="PingRequest.ScryingFoci"/> images to the current
    /// turn's final message (ephemeral: images are threaded onto the in-memory chat message list
    /// only, never persisted to the Grimoire). No-op when there are no foci to attach.
    /// </summary>
    private static void AttachScryingFociToLastMessage(
        List<MeAiChatMessage> messages,
        List<ScryingFocusDto>? scryingFoci)
    {

        if (scryingFoci is not { Count: > 0 } || messages.Count == 0)
        {

            return;

        }

        MeAiChatMessage last = messages[^1];

        List<AIContent> contents = new(last.Contents.Count + scryingFoci.Count);

        contents.AddRange(last.Contents);

        foreach (ScryingFocusDto focus in scryingFoci)
        {

            try
            {

                contents.Add(new DataContent(Convert.FromBase64String(focus.Data), focus.MimeType));

            }
            catch (FormatException)
            {

                // Malformed base64 — validated earlier by ScryingValidator/ScryingFocusStager for
                // well-formed requests; skip rather than fail deep inside message construction.

            }

        }

        messages[^1] = new MeAiChatMessage(last.Role, contents) { AuthorName = last.AuthorName };

    }

    /// <summary>
    /// Appends additional content parts onto the last message (typically the current user turn).
    /// Used for rehydrated session attachment references. No-op when contents are empty.
    /// </summary>
    public static void AppendContentsToLastMessage(
        List<MeAiChatMessage> messages,
        IReadOnlyList<AIContent>? contents)
    {

        if (contents is not { Count: > 0 } || messages.Count == 0)
        {

            return;

        }

        MeAiChatMessage last = messages[^1];

        List<AIContent> merged = new(last.Contents.Count + contents.Count);

        merged.AddRange(last.Contents);

        merged.AddRange(contents);

        messages[^1] = new MeAiChatMessage(last.Role, merged) { AuthorName = last.AuthorName };

    }

    public static void PrependDynamicSystemMessage(List<MeAiChatMessage> messages, string systemText)
    {

        if (string.IsNullOrWhiteSpace(systemText))
        {

            return;

        }

        messages.Insert(0, new MeAiChatMessage(ChatRole.System, systemText));

    }

    internal (bool Compressed, List<MeAiChatMessage> Messages) TryApplyContextCompressionIfNeeded(
        ContextCompressionRequest context)
    {
        PingRequest request = context.Request;
        List<MeAiChatMessage> chatMessages = context.Messages;
        Session? thread = context.Thread;
        ChatClientLease lease = context.Lease;

        if (HasStatelessMessages(request) || thread is null)
        {

            return (false, chatMessages);

        }

        int totalTokens = contextCompressionService.CountTokens(
            chatMessages,
            lease.Provider,
            lease.ResolvedModel,
            context.ChatOptions,
            context.ReservedAnswerTokens,
            context.ReservedReasoningTokens);

        int effectiveLimit = contextCompressionService.ComputeEffectiveLimit(
            lease.Provider.ContextWindowLimit,
            settings.Value.ResolveIntelligence().ContextWindowCompressionThreshold);

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
            context.NewUserPrompt,
            context.PreserveToolHistory,
            context.RecoveryQuestionEntryId,
            context.RestoreNativeRecoveryQuestion);

        string augmentedSystem = SystemPromptBuilder.Build(
            request,
            context.CodexContent,
            context.ActiveSpell,
            request.AttachedFiles,
            thread.Summary,
            context.DependencySpells,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(
                ArcanumRuntimeDefaults.Spells.MaxResonantBytes),
            semanticContext: context.SemanticContext,
            sagaMemories: context.SagaMemories,
            lexiconEntries: context.LexiconEntries,
            maxLexiconInjectedBytes: ArcanumSettingClamps.LexiconMaxInjectedBytes(
                settings.Value.ResolveIntelligence().LexiconMaxInjectedBytes),
            sessionAttachmentsIndex: context.SessionAttachmentsIndex,
            maxIndexItems: context.MaxIndexItems,
            maxIndexBytes: context.MaxIndexBytes,
            sessionAttachmentContext: context.SessionAttachmentContext,
            tapestryContext: context.TapestryContext,
            covenant: context.Covenant,
            campaignRollup: context.CampaignRollup?.Content,
            enableCampaignRollups: context.EnableCampaignRollups,
            campaignRollupSensitive: context.CampaignRollup?.Sensitivity is ContentSensitivity.CovenantDerived);

        PrependDynamicSystemMessage(rebuilt, augmentedSystem);

        // Grimoire entries are text, so the rebuilt transcript carries no images. Re-attach the
        // turn's foci in the same order the uncompressed path uses — before the appended contents —
        // or a Scrying turn silently reaches the model with nothing to look at.
        AttachScryingFociToLastMessage(rebuilt, context.ScryingFoci);

        AppendContentsToLastMessage(rebuilt, context.AppendedContents);

        int afterTokens = contextCompressionService.CountTokens(
            rebuilt,
            lease.Provider,
            lease.ResolvedModel,
            context.ChatOptions,
            context.ReservedAnswerTokens,
            context.ReservedReasoningTokens);

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

    internal static List<MeAiChatMessage> MapGrimoireToMeAiMessages(
        Session? session,
        string newUserPrompt) =>
        MapHistory(session, newUserPrompt, null, preserveToolHistory: false);

    private static List<MeAiChatMessage> MapHistory(
        Session? session,
        string newUserPrompt,
        DateTime? watermarkExclusive,
        bool preserveToolHistory,
        Guid? recoveryQuestionEntryId = null,
        bool restoreNativeRecoveryQuestion = true)
    {
        if (session is null) return [new MeAiChatMessage(ChatRole.User, newUserPrompt)];

        // Sequence is the append order even when timestamps tie or the clock moves backwards.
        // Stable ordering also keeps synthetic/nonpersisted zero-sequence fixtures as plain text.
        List<Entry> ordered = session.Entries.OrderBy(static entry => entry.Sequence).ToList();

        while (ordered.Count > 0 && ordered[^1].Role == MessageRole.Assistant
            && string.IsNullOrEmpty(ordered[^1].Content) && !ordered[^1].IsPinned)
        {
            ordered.RemoveAt(ordered.Count - 1);
        }

        List<MeAiChatMessage> messages = new(ordered.Count + 1);

        bool Included(Entry entry) => watermarkExclusive is null || entry.IsPinned
            || entry.CreatedAt.UtcDateTime > watermarkExclusive.Value;

        for (int index = 0; index < ordered.Count; index++)
        {
            Entry entry = ordered[index];

            if (index + 1 < ordered.Count
                && TurnContextGuards.IsPersistedToolExchange(entry, ordered[index + 1], session.Id))
            {
                Entry result = ordered[++index];

                if (entry.Id == recoveryQuestionEntryId)
                {
                    (MeAiChatMessage recoveryCall, MeAiChatMessage recoveryResult) = restoreNativeRecoveryQuestion
                        && TryMapRecoveryQuestion(entry, result) is { } native
                        ? native : (MapHistoryEntry(entry), MapHistoryEntry(result));

                    TurnContextGuards.PreserveRecoveryQuestion(recoveryCall, recoveryResult);

                    messages.Add(recoveryCall);

                    messages.Add(recoveryResult);

                    continue;
                }

                // Recognize the original pair before filtering. A pin on either half protects
                // the whole exchange, including the partner preceding a summary watermark.
                if (!Included(entry) && !Included(result)) continue;

                MeAiChatMessage callMessage = MapHistoryEntry(entry);

                MeAiChatMessage resultMessage = MapHistoryEntry(result);

                messages.Add(callMessage);

                messages.Add(resultMessage);

                if (!preserveToolHistory && !entry.IsPinned && !result.IsPinned)
                    TurnContextGuards.RegisterHistoricalToolExchange(callMessage, resultMessage);
            }
            else if (Included(entry))
            {
                // Malformed/orphaned entries and marker lookalikes remain ordinary text.
                messages.Add(MapHistoryEntry(entry));
            }
        }

        messages.Add(new MeAiChatMessage(ChatRole.User, newUserPrompt));

        return messages;
    }

    private static (MeAiChatMessage Call, MeAiChatMessage Result)? TryMapRecoveryQuestion(Entry call, Entry result)
    {
        if (call.ToolName != "ask_human" || call.ToolCallId is not { Length: > 0 } callId
            || call.ToolArguments is not { Length: > 0 } arguments) return null;

        // MapHistory already established the exact persisted tool pair and an object argument.
        using JsonDocument document = JsonDocument.Parse(arguments);

        if (!document.RootElement.TryGetProperty("question", out JsonElement question) || question.ValueKind != JsonValueKind.String)
            return null;

        var callMessage = new MeAiChatMessage(ChatRole.Assistant, [new FunctionCallContent(
            callId, "ask_human", new Dictionary<string, object?> { ["question"] = question.GetString() })]);

        var resultMessage = new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result.Content["[ToolResult: ".Length..^1])]);

        return (callMessage, resultMessage);
    }

    private static MeAiChatMessage MapHistoryEntry(Entry entry) => new(entry.Role switch
    {
        MessageRole.User => ChatRole.User,
        MessageRole.Assistant => ChatRole.Assistant,
        MessageRole.System => ChatRole.System,
        _ => ChatRole.User,
    }, entry.Content);

    internal static int ComputeEffectiveCompressionLimit(int contextWindowLimit, int thresholdPercent)
    {

        int contextLimit = ArcanumSettingClamps.ContextWindowLimit(contextWindowLimit);

        int thresholdPct = ArcanumSettingClamps.ContextWindowCompressionThreshold(thresholdPercent);

        long effectiveLong = (long)contextLimit * thresholdPct / 100L;

        return effectiveLong > int.MaxValue ? int.MaxValue : (int)effectiveLong;

    }

    private static List<MeAiChatMessage> MapFilteredGrimoireToMeAiMessages(
        Session session,
        DateTime watermarkExclusive,
        string newUserPrompt,
        bool preserveToolHistory,
        Guid? recoveryQuestionEntryId,
        bool restoreNativeRecoveryQuestion) =>
        MapHistory(session, newUserPrompt, watermarkExclusive, preserveToolHistory, recoveryQuestionEntryId, restoreNativeRecoveryQuestion);

    /// <summary>
    /// Maps stateless <see cref="CoreChatMessage"/> transcripts to <c>Microsoft.Extensions.AI</c>
    /// chat messages. Shared entry point for the buffered/streaming inference paths above and for
    /// diagnostic callers (for example <c>POST /api/intelligence/tokens</c>) that need the exact
    /// same role/tool-call/content-part mapping without duplicating it.
    /// </summary>
    internal static List<MeAiChatMessage> MapToAiChatMessages(IReadOnlyList<CoreChatMessage> messages) =>
        MapStatelessMessagesToMeAi(messages);

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
                    && !string.IsNullOrWhiteSpace(part.ImageUrl))
                {

                    AIContent? imageContent = TryBuildImageContent(part.ImageUrl);

                    if (imageContent is not null)
                    {

                        contents.Add(imageContent);

                    }

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

    /// <summary>
    /// Builds the provider-facing multimodal content for an <c>image_url</c> part. <c>data:</c>
    /// URIs (CLI Scrying foci and any inline base64 image supplied via <c>/v1</c>) decode to
    /// <see cref="DataContent"/> so the provider receives the raw bytes; <c>http(s)</c> URLs map to
    /// <see cref="UriContent"/> unchanged (provider fetches). Returns <c>null</c> for anything else
    /// (malformed URI/data URI) — the part is dropped rather than failing the whole turn.
    /// </summary>
    private static AIContent? TryBuildImageContent(string imageUrl)
    {

        if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {

            return TryParseDataUri(imageUrl, out string mime, out byte[] bytes)
                ? new DataContent(bytes, string.IsNullOrWhiteSpace(mime) ? "image/*" : mime)
                : null;

        }

        return Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri)
            ? new UriContent(imageUri, "image/*")
            : null;

    }

    private static bool TryParseDataUri(string dataUri, out string mimeType, out byte[] data)
    {

        mimeType = string.Empty;

        data = [];

        ReadOnlySpan<char> body = dataUri.AsSpan("data:".Length);

        int comma = body.IndexOf(',');

        if (comma < 0)
        {

            return false;

        }

        ReadOnlySpan<char> descriptor = body[..comma];

        ReadOnlySpan<char> base64Payload = body[(comma + 1)..];

        int semicolon = descriptor.IndexOf(';');

        ReadOnlySpan<char> mimeSpan = semicolon >= 0 ? descriptor[..semicolon] : descriptor;

        ReadOnlySpan<char> encoding = semicolon >= 0 ? descriptor[(semicolon + 1)..] : ReadOnlySpan<char>.Empty;

        if (!encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        try
        {

            data = Convert.FromBase64String(base64Payload.ToString());

        }
        catch (FormatException)
        {

            return false;

        }

        mimeType = mimeSpan.ToString();

        return true;

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

}
