using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Streams a chat turn via <see cref="ArcanumApiClient"/>. Does not call <c>ChatCommand</c>.
/// Updates flow through <see cref="CommandCenterState"/> and a UI channel — never mutate Terminal.Gui views here.
/// </summary>
internal sealed class CommandCenterChatRunner(
    ArcanumApiClient apiClient,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    SessionWorkspaceService sessionWorkspace,
    CommandCenterHumanPromptCoordinator humanPromptCoordinator,
    ILogger<CommandCenterChatRunner> logger)
{
    public async Task RunTurnAsync(
        string prompt,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> uiUpdates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(uiUpdates);

        state.FooterHint = null;
        state.StreamingAssistantText = string.Empty;
        state.ThinkingActive = true;
        state.ThinkingTick = 0;

        // Snapshot staging at turn start — clear only this snapshot after terminal Result.
        string[] stagedPathSnapshot = state.StagedAttachmentPaths.ToArray();
        Guid[] stagedRefSnapshot = state.StagedAttachmentReferences.ToArray();

        // Off the caller's thread on purpose: submit reaches here straight from the Terminal.Gui key
        // handler with nothing awaited in between, and the build reads every staged text file and
        // base64-encodes every staged image. Doing that inline freezes the main loop — no redraw, no
        // spinner, and Ctrl+C never pumped — for as long as the reads take, which on a FIFO or a
        // stalled mount is forever. This await is the turn's first yield, so the loop keeps pumping.
        string workingDirectory = state.WorkingDirectory;
        ArcanumSettings settings = settingsMonitor.CurrentValue;
        TurnAttachmentBuildResult attachments = await Task.Run(
                () => CommandCenterTurnAttachmentBuilder.Build(
                    prompt,
                    workingDirectory,
                    stagedPathSnapshot,
                    settings),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (string line in attachments.StatusLines)
        {
            state.Log.Append(SessionLogEntryKind.Status, line);
        }

        List<Guid>? attachmentReferences = stagedRefSnapshot.Length == 0
            ? null
            : stagedRefSnapshot.ToList();

        state.Log.Append(SessionLogEntryKind.User, attachments.Prompt);
        SessionLogEntry assistantEntry = state.Log.Append(SessionLogEntryKind.Assistant, string.Empty, streaming: true);
        await uiUpdates.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), cancellationToken)
            .ConfigureAwait(false);

        BoundedStreamingTextBuffer assistant = new(
            state.Log.MaxAssistantChars,
            SessionLogBuffer.TruncationMarker);
        BoundedStreamingTextBuffer reasoning = new(
            state.Log.MaxReasoningChars,
            SessionLogBuffer.ReasoningTruncationMarker);
        SessionLogEntry? reasoningEntry = null;
        bool cancelled = false;
        bool sawError = false;
        bool sawResult = false;

        void SnapshotStreamingText()
        {
            state.StreamingAssistantText = assistant.Snapshot();
            state.Log.UpdateStreaming(assistantEntry, state.StreamingAssistantText);
            if (reasoningEntry is not null)
            {
                state.Log.UpdateStreaming(reasoningEntry, reasoning.Snapshot());
            }
        }

        await using StreamingUiCoalescer coalescer = new(
            uiUpdates,
            beforeFlush: SnapshotStreamingText);

        try
        {
            string? model = state.Model ?? settingsMonitor.CurrentValue.DefaultModel;

            PingRequest ping = new(
                Prompt: attachments.Prompt,
                Model: model,
                WorkingDirectory: state.WorkingDirectory,
                SessionId: state.SessionId,
                AttachedFiles: attachments.AttachedFiles?.ToList(),
                CliTerminalFormatting: true,
                UnattendedMode: OperatorFacingUnattendedMode.Resolve(
                    cliUnattendedFlag: false,
                    settingsMonitor.CurrentValue.Security?.Ward),
                CampaignId: state.CampaignId,
                ScryingFoci: attachments.ScryingFoci?.ToList(),
                AttachmentReferences: attachmentReferences);

            await foreach (IntelligenceEvent evt in apiClient
                               .AskStreamAsync(ping, cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (evt.Type)
                {
                    case IntelligenceEventType.Reasoning
                        when evt.Reasoning is { Text.Length: > 0 } reasoningSegment:
                        bool stoppedThinking = await StopThinkingAsync(
                                state,
                                uiUpdates,
                                cancellationToken)
                            .ConfigureAwait(false);
                        reasoningEntry ??= state.Log.InsertBefore(
                            assistantEntry,
                            SessionLogEntryKind.Reasoning,
                            string.Empty,
                            streaming: true);
                        reasoning.Append(reasoningSegment.Text);
                        await coalescer
                            .NoteTokenAsync(reasoningSegment.Text, cancellationToken)
                            .ConfigureAwait(false);
                        if (stoppedThinking)
                        {
                            await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        }

                        break;

                    case IntelligenceEventType.Reasoning:
                        break;

                    case IntelligenceEventType.Token:
                        string chunk = evt.Data ?? string.Empty;
                        if (chunk.Length == 0)
                        {
                            break;
                        }

                        _ = await StopThinkingAsync(state, uiUpdates, cancellationToken).ConfigureAwait(false);
                        assistant.Append(chunk);
                        await coalescer.NoteTokenAsync(chunk, cancellationToken).ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.Context when evt.ContextBreakdown is { } breakdown:
                        state.LastContextBreakdown = breakdown;
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshSidebar),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.Context:
                        break;

                    case IntelligenceEventType.Status:
                        if (SessionLogBuffer.IsEphemeralGeneratingStatus(evt.Message))
                        {
                            await uiUpdates.WriteAsync(
                                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshHeader),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        }

                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        state.Log.Append(SessionLogEntryKind.Status, evt.Message);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.ToolCall:
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        IngestToolCall(state, evt);
                        _ = await StopThinkingAsync(state, uiUpdates, cancellationToken).ConfigureAwait(false);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                                cancellationToken)
                            .ConfigureAwait(false);
                        TryBeginHumanPrompt(evt);
                        break;

                    case IntelligenceEventType.ToolResult:
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        _ = await StopThinkingAsync(state, uiUpdates, cancellationToken).ConfigureAwait(false);
                        IngestToolResult(state, evt);
                        TryCloseHumanPromptOnToolOutcome(evt, isError: false);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.ToolError:
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        _ = await StopThinkingAsync(state, uiUpdates, cancellationToken).ConfigureAwait(false);
                        IngestToolError(state, evt);
                        TryCloseHumanPromptOnToolOutcome(evt, isError: true);
                        // Tolerated tool failures stay in Incantations — not Transcript Error.
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.Warded:
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        await HandleWardedAsync(evt, state, uiUpdates, cancellationToken).ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.WardResolved:
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        _ = state.Incantations.AppendWardNote(
                            NormalizeToolName(evt.WardToolName ?? evt.Message),
                            ResolvedWardNote(evt),
                            evt.WardId);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.AttachmentRefreshed

                        when evt.AttachmentRefresh is { } refresh:

                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);

                        state.Log.Append(

                            SessionLogEntryKind.Status,

                            $"[Live] Refreshed {refresh.LogicalKey} v{refresh.Version} "

                            + $"loaded={ShortHash(refresh.ContentSha256)} "

                            + $"at {refresh.SourceFreshnessTimestamp:O}.");

                        if (state.SessionId is { } refreshedSessionId)

                        {

                            Result<SessionAttachmentDto[]> current = await apiClient

                                .GetSessionAttachmentsAsync(refreshedSessionId, cancellationToken)

                                .ConfigureAwait(false);

                            if (current.IsSuccess)

                            {

                                _ = CommandCenterAttachmentDriftMonitor.ApplyBackendSnapshot(

                                    state,

                                    current.Value ?? []);

                            }

                        }

                        await uiUpdates.WriteAsync(

                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog),

                                cancellationToken)

                            .ConfigureAwait(false);

                        break;

                    case IntelligenceEventType.AttachmentRefreshed:

                        break;

                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:
                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid bound))
                        {
                            _ = await sessionWorkspace
                                .PersistBoundSessionAsync(
                                    state,
                                    bound,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await uiUpdates.WriteAsync(
                                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshHeader),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await uiUpdates.WriteAsync(
                                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshSidebar),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        break;

                    case IntelligenceEventType.Error:
                        sawError = true;
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        _ = humanPromptCoordinator.TryCloseActive(
                            HumanPromptCloseReason.Expired,
                            "Turn ended with an error — human prompt closed.");
                        state.Log.Append(SessionLogEntryKind.Error, evt.Message);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.Result:
                        sawResult = true;
                        _ = humanPromptCoordinator.TryCloseActive(
                            HumanPromptCloseReason.Expired,
                            "Turn completed — human prompt closed.");
                        await coalescer.FlushFinalAsync(cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _ = humanPromptCoordinator.TryCloseActive(HumanPromptCloseReason.Cancelled);
            await coalescer.FlushCancelledAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command Center chat turn failed.");
            _ = humanPromptCoordinator.TryCloseActive(
                HumanPromptCloseReason.Expired,
                "Turn failed — human prompt closed.");
            await coalescer.FlushBeforeBlockAsync(CancellationToken.None).ConfigureAwait(false);
            state.Log.Append(SessionLogEntryKind.Error, ex.Message);
        }
        finally
        {
            // Stream ended unexpectedly (or normally) — never leave HITL modal orphaned.
            _ = humanPromptCoordinator.TryCloseActive(
                HumanPromptCloseReason.Expired,
                "Stream ended — human prompt closed.");

            await coalescer.FlushFinalAsync(CancellationToken.None).ConfigureAwait(false);

            string finalText = assistant.Snapshot();
            if (cancelled)
            {
                finalText = string.IsNullOrEmpty(finalText)
                    ? "(cancelled)"
                    : finalText + "\n… [cancelled]";
            }

            state.Log.CompleteStreaming(assistantEntry, finalText);
            if (reasoningEntry is not null)
            {
                state.Log.CompleteStreaming(reasoningEntry, reasoning.Snapshot());
            }

            _ = state.Log.RemoveEphemeralGeneratingStatuses();
            state.ThinkingActive = false;
            state.StreamingAssistantText = string.Empty;
            // Host owns turn gate (TryBeginTurn/EndTurn) and TurnCts lifetime.

            if (sawResult && !cancelled && !sawError)
            {
                foreach (string path in stagedPathSnapshot)
                {
                    _ = state.StagedAttachmentPaths.Remove(path);
                }

                foreach (Guid id in stagedRefSnapshot)
                {
                    _ = state.StagedAttachmentReferences.Remove(id);
                }
            }

            try
            {
                await uiUpdates.WriteAsync(
                        new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Channel may be completed on exit.
            }
        }
    }

    private async Task HandleWardedAsync(
        IntelligenceEvent evt,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> uiUpdates,
        CancellationToken cancellationToken)
    {
        string wardId = evt.WardId ?? string.Empty;
        string toolName = NormalizeToolName(evt.WardToolName ?? evt.Message);
        string argsPreview = FormatWardArgumentsPreview(evt.WardArguments);

        _ = state.Incantations.AppendWardNote(
            toolName,
            InformationalWardNote(evt.WardOrigin, toolName, argsPreview),
            wardId);

        await uiUpdates.WriteAsync(
                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void TryBeginHumanPrompt(IntelligenceEvent evt)
    {
        string toolName = NormalizeToolName(evt.ToolCall?.Name ?? evt.Message);
        // Do not route petition_dungeon_master / send_commlink_alert (or aliases) through ask_human.
        if (!string.Equals(toolName, "ask_human", StringComparison.Ordinal))
        {
            return;
        }

        if (!AskHumanToolCallStreamHandler.TryParseAskHumanArgs(evt, out var args, out _)
            || args is null)
        {
            return;
        }

        string callId = evt.ToolCall?.CallId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        humanPromptCoordinator.BeginPrompt(
            new HumanPromptRequest(callId, args.PromptId.Trim(), args.Question.Trim()));
    }

    private void TryCloseHumanPromptOnToolOutcome(IntelligenceEvent evt, bool isError)
    {
        if (!humanPromptCoordinator.IsActive)
        {
            return;
        }

        string? callId = evt.ToolCall?.CallId;
        if (!humanPromptCoordinator.MatchesCallId(callId))
        {
            return;
        }

        string? text = evt.Data ?? evt.ToolCall?.ArgumentsJson ?? evt.Message;
        string notice = isError
            ? "Human prompt closed (tool error)."
            : CommandCenterHumanPromptCoordinator.IsTimeoutText(text)
                ? "Human prompt timed out."
                : "Human prompt closed (tool result).";

        string? promptId = humanPromptCoordinator.Pending?.PromptId;
        _ = humanPromptCoordinator.TryClose(HumanPromptCloseReason.Expired, notice, promptId);
    }

    private static async ValueTask<bool> StopThinkingAsync(
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> uiUpdates,
        CancellationToken cancellationToken)
    {
        if (!state.ThinkingActive)
        {
            return false;
        }

        state.ThinkingActive = false;
        await uiUpdates.WriteAsync(
                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshHeader),
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static string ShortHash(string? value)

    {

        string hash = value?.Trim() ?? string.Empty;

        return hash.Length <= 8 ? hash : hash[..8];

    }

    private static void IngestToolCall(CommandCenterState state, IntelligenceEvent evt)
    {
        string? callId = evt.ToolCall?.CallId;
        string name = NormalizeToolName(evt.ToolCall?.Name ?? evt.Message);
        string? args = evt.ToolCall?.ArgumentsJson
            ?? (LooksLikeJsonObject(evt.Data) ? evt.Data : null);
        _ = state.Incantations.UpsertCall(callId, name, args);
    }

    private static void IngestToolResult(CommandCenterState state, IntelligenceEvent evt)
    {
        string? callId = evt.ToolCall?.CallId;
        string? name = NormalizeToolName(evt.ToolCall?.Name ?? evt.Message);
        string? result = evt.Data ?? evt.ToolCall?.ArgumentsJson;
        _ = state.Incantations.UpsertResult(callId, name, result);
    }

    private static void IngestToolError(CommandCenterState state, IntelligenceEvent evt)
    {
        string? callId = evt.ToolCall?.CallId;
        string? name = NormalizeToolName(evt.ToolCall?.Name ?? evt.Message);
        // toolCall.argumentsJson is the serialized call arguments, never failure text — the human
        // readable reason rides on Data, with Message (the tool name) as the last resort.
        string? error = evt.Data ?? evt.Message;
        _ = state.Incantations.UpsertError(callId, name, error);
    }

    private static string NormalizeToolName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim();

    /// <summary>
    /// Transcript line for an informational Ward record. Command Center never turns a stream frame
    /// into an approval request or a tool restriction.
    /// </summary>
    private static string InformationalWardNote(
        WardResolutionOrigin? origin,
        string toolName,
        string argumentsPreview)
    {
        string headline = origin switch
        {
            WardResolutionOrigin.Ungated =>
                $"Ward recorded (ungated): {toolName}",
            WardResolutionOrigin.AutoApproved =>
                $"Ward recorded (host allowed): {toolName}",
            WardResolutionOrigin.AutoDenied =>
                $"Ward recorded (host denied): {toolName}",
            WardResolutionOrigin.TimedOut =>
                $"Ward recorded (timed out): {toolName}",
            WardResolutionOrigin.Human =>
                $"Ward recorded (human resolution): {toolName}",
            _ => $"Ward recorded: {toolName}",
        };

        return string.IsNullOrEmpty(argumentsPreview)
            ? headline
            : $"{headline} — {argumentsPreview}";
    }

    private static string ResolvedWardNote(IntelligenceEvent evt)
    {
        string toolName = NormalizeToolName(evt.WardToolName ?? evt.Message);
        string outcome = evt.WardAllowed == true ? "allowed" : "denied";
        string headline = evt.WardOrigin switch
        {
            WardResolutionOrigin.Ungated => $"Ward record resolved (ungated, {outcome}): {toolName}",
            WardResolutionOrigin.AutoApproved => $"Ward record resolved (host allowed): {toolName}",
            WardResolutionOrigin.AutoDenied => $"Ward record resolved (host denied): {toolName}",
            WardResolutionOrigin.TimedOut => $"Ward record resolved (timed out): {toolName}",
            WardResolutionOrigin.Human => $"Ward record resolved (human, {outcome}): {toolName}",
            _ => $"Ward record resolved ({outcome}): {toolName}",
        };

        return string.IsNullOrWhiteSpace(evt.WardReason)
            ? headline
            : $"{headline} ({evt.WardReason})";
    }

    internal static string FormatWardArgumentsPreview(JsonElement? arguments, int maxChars = 480)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        string raw = arguments.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? string.Empty
            : arguments.Value.ToString();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        raw = raw.Replace('\r', ' ').Replace('\n', ' ');
        return raw.Length <= maxChars ? raw : raw[..maxChars] + "…";
    }

    private static bool LooksLikeJsonObject(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{");
}
