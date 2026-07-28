using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Streams a chat turn via <see cref="ArcanumApiClient"/>. Does not call <c>ChatCommand</c>.
/// Updates flow through <see cref="CommandCenterState"/> and a UI channel — never mutate Terminal.Gui views here.
/// </summary>
internal sealed class CommandCenterChatRunner(
    ArcanumApiClient apiClient,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    SessionWorkspaceService sessionWorkspace,
    CommandCenterWardCoordinator wardCoordinator,
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

        TurnAttachmentBuildResult attachments = CommandCenterTurnAttachmentBuilder.Build(
            prompt,
            state.WorkingDirectory,
            stagedPathSnapshot,
            settingsMonitor.CurrentValue);

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
                    settingsMonitor.CurrentValue.Ward),
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
                        string resolved = evt.WardAllowed == true
                            ? $"Ward allowed: {NormalizeToolName(evt.WardToolName ?? evt.Message)}"
                            : $"Ward denied: {NormalizeToolName(evt.WardToolName ?? evt.Message)}"
                              + (string.IsNullOrWhiteSpace(evt.WardReason) ? string.Empty : $" ({evt.WardReason})");
                        _ = state.Incantations.AppendWardNote(
                            NormalizeToolName(evt.WardToolName ?? evt.Message),
                            resolved,
                            evt.WardId);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:
                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid bound))
                        {
                            state.SessionId = bound;
                            state.SelectedSessionId = bound;
                            if (string.IsNullOrWhiteSpace(state.SessionTitle))
                            {
                                state.SessionTitle = "Untitled";
                            }

                            if (string.IsNullOrWhiteSpace(state.SessionStatus))
                            {
                                state.SessionStatus = "Active";
                            }

                            sessionWorkspace.PersistBoundSession(state, bound);
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
        string argsPreview = CommandCenterWardCoordinator.FormatArgumentsPreview(evt.WardArguments);

        string pendingNote = string.IsNullOrEmpty(argsPreview)
            ? $"Ward pending ({wardId})"
            : $"Ward pending ({wardId}) — {argsPreview}";
        _ = state.Incantations.AppendWardNote(toolName, pendingNote, wardId);
        await uiUpdates.WriteAsync(
                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshIncantations),
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(wardId))
        {
            state.Log.Append(SessionLogEntryKind.Error, "Ward event missing wardId; cannot resolve.");
            await uiUpdates.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool allow;
        if (state.SessionAllowedArts.Contains(toolName))
        {
            allow = true;
            _ = state.Incantations.AppendWardNote(
                toolName,
                $"Auto-allowing {toolName} (session allow-list)",
                wardId);
        }
        else
        {
            WardApprovalDecision decision = await wardCoordinator
                .RequestApprovalAsync(new WardApprovalRequest(wardId, toolName, argsPreview), cancellationToken)
                .ConfigureAwait(false);

            if (decision == WardApprovalDecision.AllowAlwaysThisTool)
            {
                _ = state.SessionAllowedArts.Add(toolName);
                allow = true;
                _ = state.Incantations.AppendWardNote(
                    toolName,
                    $"Always allowing {toolName} for this Command Center session",
                    wardId);
            }
            else
            {
                allow = decision == WardApprovalDecision.Allow;
                if (allow)
                {
                    _ = state.Incantations.AppendWardNote(toolName, $"Allowed once: {toolName}", wardId);
                }
                else
                {
                    _ = state.Incantations.AppendWardNote(toolName, $"Denied: {toolName}", wardId);
                }
            }
        }

        Result<WardResolutionDto> resolve = await apiClient
            .ResolveWardAsync(wardId, allow, reason: null, cancellationToken)
            .ConfigureAwait(false);

        if (resolve.IsFailure)
        {
            state.Log.Append(
                SessionLogEntryKind.Error,
                $"Failed to resolve ward {wardId}: {resolve.Error.Message}");
            await uiUpdates.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _ = state.Incantations.AppendWardNote(
                toolName,
                allow ? $"Submitted allow for ward {wardId}" : $"Submitted deny for ward {wardId}",
                wardId);
        }

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
        // Prefer structured public failure text on ToolCall payload over the generic Data blurb.
        string? error = evt.ToolCall?.ArgumentsJson ?? evt.Data ?? evt.Message;
        _ = state.Incantations.UpsertError(callId, name, error);
    }

    private static string NormalizeToolName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim();

    private static bool LooksLikeJsonObject(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{");
}
