using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Streams a chat turn via <see cref="ArcanumApiClient"/>. Does not call <c>ChatCommand</c>.
/// Updates flow through <see cref="CommandCenterState"/> and a UI channel — never mutate Terminal.Gui views here.
/// </summary>
internal sealed class CommandCenterChatRunner(
    ArcanumApiClient apiClient,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    SessionWorkspaceService sessionWorkspace,
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

        StringBuilder assistant = new();
        bool cancelled = false;
        bool sawError = false;
        bool sawResult = false;
        await using StreamingUiCoalescer coalescer = new(uiUpdates);

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
                UnattendedMode: true,
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
                    case IntelligenceEventType.Token:
                        string chunk = evt.Data ?? string.Empty;
                        if (chunk.Length == 0)
                        {
                            break;
                        }

                        _ = assistant.Append(chunk);
                        state.StreamingAssistantText = assistant.ToString();
                        state.Log.UpdateStreaming(assistantEntry, state.StreamingAssistantText);
                        await coalescer.NoteTokenAsync(chunk, cancellationToken).ConfigureAwait(false);
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
                    case IntelligenceEventType.ToolResult:
                    case IntelligenceEventType.ToolError:
                        await coalescer.FlushBeforeBlockAsync(cancellationToken).ConfigureAwait(false);
                        string toolLine = string.IsNullOrWhiteSpace(evt.Data)
                            ? evt.Message
                            : $"{evt.Message}: {evt.Data}";
                        state.Log.Append(SessionLogEntryKind.Tool, toolLine);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog),
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
                        state.Log.Append(SessionLogEntryKind.Error, evt.Message);
                        await uiUpdates.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case IntelligenceEventType.Result:
                        sawResult = true;
                        await coalescer.FlushFinalAsync(cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            await coalescer.FlushCancelledAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command Center chat turn failed.");
            await coalescer.FlushBeforeBlockAsync(CancellationToken.None).ConfigureAwait(false);
            state.Log.Append(SessionLogEntryKind.Error, ex.Message);
        }
        finally
        {
            await coalescer.FlushFinalAsync(CancellationToken.None).ConfigureAwait(false);

            string finalText = assistant.ToString();
            if (cancelled)
            {
                finalText = string.IsNullOrEmpty(finalText)
                    ? "(cancelled)"
                    : finalText + "\n… [cancelled]";
            }

            state.Log.CompleteStreaming(assistantEntry, finalText);
            _ = state.Log.RemoveEphemeralGeneratingStatuses();
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
}
