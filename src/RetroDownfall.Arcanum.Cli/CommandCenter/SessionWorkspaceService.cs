using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Persists the CLI last-session id without Spectre (Command Center quiet path).</summary>
internal interface ILastSessionStore
{
    Guid? GetLastSessionId();

    void SaveSessionId(Guid id);
}

/// <summary>Adapts <see cref="CliSessionManager"/> for Command Center (quiet — no Spectre).</summary>
internal sealed class CliLastSessionStore(CliSessionManager sessionManager) : ILastSessionStore
{
    public Guid? GetLastSessionId() => sessionManager.GetLastSessionId(quiet: true);

    public void SaveSessionId(Guid id) => sessionManager.SaveSessionId(id, quiet: true);
}

internal enum SessionResumeOutcome
{
    Success,
    Failed,
}

internal sealed record SessionResumeResult(
    SessionResumeOutcome Outcome,
    string? ErrorMessage = null,
    bool WasEmpty = false,
    bool HadOlderMessages = false);

internal enum SessionForkOutcome
{
    Success,
    Failed,
    ConfirmationRequired,
}

internal sealed record SessionForkResult(
    SessionForkOutcome Outcome,
    Guid? ForkSessionId = null,
    string? ErrorMessage = null);

/// <summary>
/// Session list / resume / new / startup restore for Command Center.
/// Mutates <see cref="CommandCenterState"/> and log buffer; never touches Terminal.Gui.
/// </summary>
internal sealed class SessionWorkspaceService(
    ArcanumApiClient apiClient,
    ILastSessionStore lastSessionStore,
    ILogger<SessionWorkspaceService> logger)
{
    public async Task RefreshSessionsAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.TransientStatus = "Refreshing sessions…";
        try
        {
            Result<SessionQueryResult> result = await apiClient
                .QuerySessionsAsync(limit: CommandCenterState.RecentSessionLimit, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                state.LastError = string.IsNullOrWhiteSpace(result.Error.Message)
                    ? "Failed to refresh sessions."
                    : result.Error.Message;
                return;
            }

            SessionSummaryDto[] summaries = result.Value?.Summaries ?? [];
            // API returns UpdatedAt DESC; keep that order.
            state.Sessions = summaries.Select(SessionListItem.FromSummary).ToArray();
            state.LastError = null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Session list refresh failed.");
            state.LastError = ex.Message;
        }
        finally
        {
            state.TransientStatus = null;
        }
    }

    /// <summary>
    /// Transactional resume: on failure previous session/transcript stay intact.
    /// Sets <see cref="CommandCenterState.TransientStatus"/> while loading.
    /// </summary>
    public async Task<SessionResumeResult> ResumeSessionAsync(
        CommandCenterState state,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.TransientStatus = "Loading session…";
        state.LastError = null;

        try
        {
            Result<SessionDetailDto> detailResult = await apiClient
                .GetSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (!detailResult.IsSuccess || detailResult.Value is null)
            {
                string message = string.IsNullOrWhiteSpace(detailResult.Error.Message)
                    ? "Session was not found."
                    : detailResult.Error.Message;

                if (IsArchivedOrMissing(detailResult, message))
                {
                    state.LastError = message;
                    state.Log.Append(SessionLogEntryKind.Error, message);
                    return new SessionResumeResult(SessionResumeOutcome.Failed, message);
                }

                state.LastError = message;
                state.Log.Append(SessionLogEntryKind.Error, message);
                return new SessionResumeResult(SessionResumeOutcome.Failed, message);
            }

            SessionDetailDto detail = detailResult.Value;
            if (IsArchivedStatus(detail.Status))
            {
                string message = $"Session {sessionId:D} is archived and cannot be resumed in Command Center.";
                state.LastError = message;
                state.Log.Append(SessionLogEntryKind.Error, message);
                return new SessionResumeResult(SessionResumeOutcome.Failed, message);
            }

            Result<EntryDto[]> entriesResult = await apiClient
                .GetSessionEntriesAsync(
                    sessionId,
                    offset: 0,
                    limit: CommandCenterState.TranscriptEntryLimit,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!entriesResult.IsSuccess)
            {
                string message = string.IsNullOrWhiteSpace(entriesResult.Error.Message)
                    ? "Failed to load session entries."
                    : entriesResult.Error.Message;
                state.LastError = message;
                state.Log.Append(SessionLogEntryKind.Error, message);
                return new SessionResumeResult(SessionResumeOutcome.Failed, message);
            }

            EntryDto[] descending = entriesResult.Value ?? [];
            // API returns newest-first; render chronological.
            EntryDto[] chronological = descending.Reverse().ToArray();
            bool hadOlder = detail.EntryCount > chronological.Length;

            List<(SessionLogEntryKind Kind, string Text, Guid? SourceEntryId)> mapped = [];
            state.Incantations.Clear();
            foreach (EntryDto e in chronological)
            {
                if (SessionLogBuffer.IsEphemeralReasoningRole(e.Role))
                {
                    continue;
                }

                SessionLogEntryKind kind = SessionLogBuffer.MapEntryRole(e.Role);
                if (PersistedToolInteraction.IsToolInteraction(e)
                    || kind == SessionLogEntryKind.Tool)
                {
                    IngestHistoryTool(state.Incantations, e);
                    continue;
                }

                mapped.Add((kind, FormatEntryContent(e), e.Id));
            }

            // Commit only after successful load.
            state.ApplySessionMeta(
                detail.Id, detail.Title, detail.Status, detail.EntryCount, detail.ForkedFromSessionId);
            state.LoadedTranscriptEntries = chronological;
            state.Log.ReplaceWithApiHistory(mapped, showOlderMessagesMarker: hadOlder);
            lastSessionStore.SaveSessionId(detail.Id);

            return new SessionResumeResult(
                SessionResumeOutcome.Success,
                WasEmpty: chronological.Length == 0,
                HadOlderMessages: hadOlder);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Session resume failed for {SessionId}.", sessionId);
            state.LastError = ex.Message;
            state.Log.Append(SessionLogEntryKind.Error, ex.Message);
            return new SessionResumeResult(SessionResumeOutcome.Failed, ex.Message);
        }
        finally
        {
            state.TransientStatus = null;
        }
    }

    /// <summary>
    /// Forks through the server contract and switches only after the new branch, transcript, and
    /// attachment metadata have all loaded successfully.
    /// </summary>
    public async Task<SessionForkResult> ForkSessionAsync(
        CommandCenterState state,
        ForkSessionRequest request,
        CancellationToken cancellationToken,
        bool attachmentCopyConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SessionId is not { } sourceId)
        {
            return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: "Open a session before forking.");
        }

        state.TransientStatus = "Forking session…";
        try
        {
            Result<SessionAttachmentDto[]> sourceAttachments = await apiClient
                .GetSessionAttachmentsAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (!sourceAttachments.IsSuccess)
            {
                string message = FormatForkError(sourceAttachments.Error);
                return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: message);
            }

            SessionAttachmentDto[] attachments = sourceAttachments.Value ?? [];
            long attachmentBytes = attachments.Sum(static attachment => attachment.ByteLength);
            if (!attachmentCopyConfirmed && (attachments.Length >= 10 || attachmentBytes >= 10 * 1024 * 1024))
            {
                string message =
                    $"This fork will copy {attachments.Length} attachment(s) ({attachmentBytes / 1024d / 1024d:F1} MiB). "
                    + "Run `/fork confirm` to continue.";
                return new SessionForkResult(SessionForkOutcome.ConfirmationRequired, ErrorMessage: message);
            }

            Result<SessionDetailDto> forkResult = await apiClient
                .ForkSessionAsync(sourceId, request, cancellationToken).ConfigureAwait(false);
            if (!forkResult.IsSuccess || forkResult.Value is null)
            {
                string message = FormatForkError(forkResult.Error);
                state.LastError = message;
                return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: message);
            }

            Guid forkId = forkResult.Value.Id;
            Result<SessionDetailDto> detailResult = await apiClient
                .GetSessionAsync(forkId, cancellationToken).ConfigureAwait(false);
            Result<EntryDto[]> entriesResult = await apiClient
                .GetSessionEntriesAsync(
                    forkId, 0, CommandCenterState.TranscriptEntryLimit, cancellationToken)
                .ConfigureAwait(false);
            Result<SessionAttachmentDto[]> attachmentsResult = await apiClient
                .GetSessionAttachmentsAsync(forkId, cancellationToken).ConfigureAwait(false);

            if (!detailResult.IsSuccess || detailResult.Value is null
                || !entriesResult.IsSuccess || !attachmentsResult.IsSuccess)
            {
                Error error = !detailResult.IsSuccess
                    ? detailResult.Error
                    : !entriesResult.IsSuccess ? entriesResult.Error : attachmentsResult.Error;
                string message = FormatForkError(error);
                state.LastError = message;
                return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: message);
            }

            SessionDetailDto detail = detailResult.Value;
            EntryDto[] chronological = (entriesResult.Value ?? []).Reverse().ToArray();
            List<(SessionLogEntryKind Kind, string Text, Guid? SourceEntryId)> mapped = chronological
                .Where(static e => !SessionLogBuffer.IsEphemeralReasoningRole(e.Role))
                .Where(static e => !PersistedToolInteraction.IsToolInteraction(e))
                .Select(static e => (
                    SessionLogBuffer.MapEntryRole(e.Role), FormatEntryContent(e), (Guid?)e.Id))
                .ToList();

            state.ApplySessionMeta(
                detail.Id, detail.Title, detail.Status, detail.EntryCount, detail.ForkedFromSessionId);
            state.LoadedTranscriptEntries = chronological;
            state.Log.ReplaceWithApiHistory(mapped, detail.EntryCount > chronological.Length);
            lastSessionStore.SaveSessionId(detail.Id);
            state.LastError = null;
            await RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
            return new SessionForkResult(SessionForkOutcome.Success, detail.Id);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Session fork failed for {SessionId}.", sourceId);
            state.LastError = ex.Message;
            return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: ex.Message);
        }
        finally
        {
            state.TransientStatus = null;
        }
    }

    private static string FormatForkError(Error error) =>
        error.Code switch
        {
            ErrorCodes.Session.ForkDepthExceeded =>
                "This session is already at the maximum branch depth. Open an earlier parent and fork there.",
            ErrorCodes.Session.EntryNotFound =>
                "The selected cutoff entry is no longer available. Refresh the transcript and select another entry.",
            _ when error.Message.Contains("attachment", StringComparison.OrdinalIgnoreCase) =>
                $"Attachment copy failed; the source remains active. {error.Message}",
            _ => string.IsNullOrWhiteSpace(error.Message) ? "The session could not be forked." : error.Message,
        };

    public void StartNewSession(CommandCenterState state, bool clearTranscript = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.ClearSessionBinding();
        state.LastError = null;
        state.TransientStatus = null;
        if (clearTranscript)
        {
            state.Log.Clear();
            state.Incantations.Clear();
            state.Log.Append(
                SessionLogEntryKind.Status,
                "New Session — first message will create it.");
        }
    }

    /// <summary>
    /// Startup restore. On stale/missing/archived/load failure → New Session with non-fatal hint.
    /// Does not ClearSession on the store.
    /// </summary>
    public async Task RestoreStartupSessionAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        await RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);

        Guid? lastId = lastSessionStore.GetLastSessionId();
        if (lastId is null)
        {
            StartNewSession(state);
            return;
        }

        SessionResumeResult resume = await ResumeSessionAsync(state, lastId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (resume.Outcome == SessionResumeOutcome.Success)
        {
            return;
        }

        // Failure path may have appended an error to the prior (empty) log — reset to New Session.
        StartNewSession(state);
        state.FooterHint = $"Last session unavailable — started New Session. ({resume.ErrorMessage ?? "not found"})";
        state.LastError = null;
    }

    public void PersistBoundSession(CommandCenterState state, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(state);
        lastSessionStore.SaveSessionId(sessionId);
        state.SelectedSessionId = sessionId;
        if (state.SessionId != sessionId)
        {
            state.SessionId = sessionId;
        }
    }

    private static string FormatEntryContent(EntryDto entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ToolName))
        {
            string body = string.IsNullOrWhiteSpace(entry.Content) ? string.Empty : entry.Content;
            return string.IsNullOrEmpty(body) ? entry.ToolName! : $"{entry.ToolName}: {body}";
        }

        return entry.Content ?? string.Empty;
    }

    private static void IngestHistoryTool(IncantationStore store, EntryDto entry)
    {
        string? callId = entry.ToolCallId;
        string content = entry.Content ?? string.Empty;
        bool isErrorRole = entry.Role.Contains("error", StringComparison.OrdinalIgnoreCase)
            || entry.Role.Contains("tool_error", StringComparison.OrdinalIgnoreCase);

        if (PersistedToolInteraction.TryParseToolCall(content, out string parsedName, out string? parsedArgs))
        {
            string name = !string.IsNullOrWhiteSpace(entry.ToolName) ? entry.ToolName!.Trim() : parsedName;
            _ = store.UpsertCall(callId, name, parsedArgs);
            return;
        }

        if (PersistedToolInteraction.TryParseToolResult(content, out string parsedResult))
        {
            bool looksLikeError = isErrorRole
                || parsedResult.Contains("[Tool error:", StringComparison.OrdinalIgnoreCase);
            _ = store.CompleteLatestPending(entry.ToolName, parsedResult, looksLikeError);
            return;
        }

        string? toolName = entry.ToolName;
        bool hasStructure = !string.IsNullOrWhiteSpace(callId) || !string.IsNullOrWhiteSpace(toolName);
        if (!hasStructure && string.IsNullOrWhiteSpace(content))
        {
            _ = store.AddFromHistory(
                callId: null,
                toolName: null,
                argumentsJson: null,
                resultOrContent: null,
                isError: false,
                unparseable: true);
            return;
        }

        bool looksJson = content.TrimStart().StartsWith('{');
        if (!hasStructure && !looksJson)
        {
            _ = store.AddFromHistory(
                callId,
                toolName,
                argumentsJson: null,
                resultOrContent: null,
                isError: false,
                unparseable: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            // Structured ToolName without bracket markup — treat content as args when JSON, else result.
            if (looksJson)
            {
                _ = store.UpsertCall(callId, toolName, content);
            }
            else
            {
                _ = store.UpsertCall(callId, toolName, argumentsJson: null);
                _ = store.CompleteLatestPending(toolName, content, isErrorRole);
            }

            return;
        }

        _ = store.AddFromHistory(callId, toolName, looksJson ? content : null, looksJson ? null : content, isErrorRole, unparseable: false);
    }

    private static bool IsArchivedStatus(string? status) =>
        string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchivedOrMissing(Result<SessionDetailDto> detailResult, string message) =>
        !detailResult.IsSuccess
        && (message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("archived", StringComparison.OrdinalIgnoreCase));
}
