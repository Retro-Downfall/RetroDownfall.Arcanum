using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Persists the CLI last-session id without Spectre (Command Center quiet path).</summary>
internal interface ILastSessionStore
{
    Guid? GetLastSessionId();

    Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(
        Guid id,
        Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
        CancellationToken cancellationToken);
}

/// <summary>Adapts <see cref="CliSessionManager"/> for Command Center (quiet — no Spectre).</summary>
internal sealed class CliLastSessionStore(CliSessionManager sessionManager) : ILastSessionStore
{
    public Guid? GetLastSessionId() => sessionManager.GetLastSessionId(quiet: true);

    public Task<ArcanumClientMutationResult<CliContextDocument>>
        SaveSessionIdAsync(
            Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
            CancellationToken cancellationToken) =>
        sessionManager.SaveSessionIdAsync(
            id,
            revalidateAsync,
            quiet: true,
            cancellationToken);
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
    string? ErrorMessage = null,
    SessionForkPreviewDto? Preview = null);

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
                .QuerySessionsAsync(limit: CommandCenterState.SessionPageSize, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                state.LastError = string.IsNullOrWhiteSpace(result.Error.Message)
                    ? "Failed to refresh sessions."
                    : result.Error.Message;
                return;
            }

            if (result.Value?.HasMore == true
                && result.Value.NextBeforeUpdatedAt is null)
            {
                state.LastError =
                    "The session provider reported more rows without an advancing cursor. Retry the refresh.";

                return;
            }

            SessionSummaryDto[] summaries = result.Value?.Summaries ?? [];
            // API returns UpdatedAt DESC; keep that order.
            state.Sessions = summaries.Select(SessionListItem.FromSummary).ToArray();

            state.SessionPageBeforeUpdatedAt = null;

            state.NextSessionPageBeforeUpdatedAt = result.Value?.HasMore == true
                ? result.Value.NextBeforeUpdatedAt
                : null;

            state.SessionPageHistory.Clear();

            state.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Session list refresh failed.");
            state.LastError = ex.Message;
        }
        finally
        {
            state.TransientStatus = null;
        }
    }

    public async Task<bool> LoadOlderSessionsPageAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.NextSessionPageBeforeUpdatedAt is not { } nextCursor)
        {
            return false;
        }

        DateTimeOffset? currentCursor = state.SessionPageBeforeUpdatedAt;

        if (!await LoadSessionsPageAsync(state, nextCursor, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        state.SessionPageHistory.Add(currentCursor);

        return true;
    }

    public async Task<bool> LoadNewerSessionsPageAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SessionPageHistory.Count == 0)
        {
            return false;
        }

        int previousIndex = state.SessionPageHistory.Count - 1;

        DateTimeOffset? previousCursor = state.SessionPageHistory[previousIndex];

        if (!await LoadSessionsPageAsync(state, previousCursor, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        state.SessionPageHistory.RemoveAt(previousIndex);

        return true;
    }

    private async Task<bool> LoadSessionsPageAsync(
        CommandCenterState state,
        DateTimeOffset? beforeUpdatedAt,
        CancellationToken cancellationToken)
    {
        state.TransientStatus = "Loading session page…";

        try
        {
            Result<SessionQueryResult> result = await apiClient
                .QuerySessionsAsync(
                    limit: CommandCenterState.SessionPageSize,
                    beforeUpdatedAt: beforeUpdatedAt,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                state.LastError = string.IsNullOrWhiteSpace(result.Error.Message)
                    ? "Failed to load the session page."
                    : result.Error.Message;

                return false;
            }

            SessionQueryResult page = result.Value;

            if (page.HasMore
                && (page.NextBeforeUpdatedAt is null
                    || page.NextBeforeUpdatedAt == beforeUpdatedAt))
            {
                state.LastError =
                    "The session provider reported more rows without an advancing cursor. Refresh sessions to restart paging.";

                return false;
            }

            state.Sessions = page.Summaries
                .Select(SessionListItem.FromSummary)
                .ToArray();

            state.SessionPageBeforeUpdatedAt = beforeUpdatedAt;

            state.NextSessionPageBeforeUpdatedAt = page.HasMore
                ? page.NextBeforeUpdatedAt
                : null;

            state.SelectedSessionId = state.Sessions.FirstOrDefault()?.Id;

            state.LastError = null;

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Session page load failed.");

            state.LastError = ex.Message;

            return false;
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
                    limit: CommandCenterState.TranscriptPageSize + 1,
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

            bool hadOlder = descending.Length > CommandCenterState.TranscriptPageSize;

            ArcanumClientMutationResult<CliContextDocument> persisted =
                await lastSessionStore
                    .SaveSessionIdAsync(
                        detail.Id,
                        (id, token) => SessionMutationRevalidator
                            .RevalidateAsync(
                                apiClient,
                                id,
                                detail,
                                token),
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!persisted.IsCompleted)
            {
                string message = persisted.Error.Message;

                state.LastError = message;

                state.Log.Append(SessionLogEntryKind.Error, message);

                return new SessionResumeResult(
                    SessionResumeOutcome.Failed,
                    message);
            }

            // Commit only after successful load.
            state.ApplySessionMeta(
                detail.Id, detail.Title, detail.Status, detail.EntryCount, detail.ForkedFromSessionId);

            ApplyTranscriptPage(state, descending, hasNewer: false);

            return new SessionResumeResult(
                SessionResumeOutcome.Success,
                WasEmpty: descending.Length == 0,
                HadOlderMessages: hadOlder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    public Task<bool> LoadOlderTranscriptPageAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.HasOlderTranscriptEntries || state.SessionId is null)
        {
            return Task.FromResult(false);
        }

        EntryDto oldest = state.LoadedTranscriptEntries[0];

        return LoadTranscriptPageAsync(state, cancellationToken, oldest);
    }

    public Task<bool> LoadNewerTranscriptPageAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.HasNewerTranscriptEntries || state.SessionId is null)
        {
            return Task.FromResult(false);
        }

        return LoadTranscriptPageAsync(state, cancellationToken);
    }

    private async Task<bool> LoadTranscriptPageAsync(
        CommandCenterState state,
        CancellationToken cancellationToken,
        EntryDto? before = null)
    {
        if (state.SessionId is not { } sessionId)
        {
            return false;
        }

        state.TransientStatus = "Loading transcript page…";

        try
        {
            Result<EntryDto[]> result = await apiClient
                .GetSessionEntriesAsync(
                    sessionId,
                    offset: 0,
                    limit: CommandCenterState.TranscriptPageSize + 1,
                    cancellationToken,
                    beforeCreatedAt: before?.CreatedAt,
                    beforeId: before?.Id)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                state.LastError = string.IsNullOrWhiteSpace(result.Error.Message)
                    ? "Failed to load the transcript page."
                    : result.Error.Message;

                return false;
            }

            EntryDto[] descending = result.Value ?? [];

            if (descending.Length == 0)
            {
                state.LastError =
                    "The requested transcript page is no longer available. Refresh the session and retry.";

                return false;
            }

            ApplyTranscriptPage(state, descending, hasNewer: before is not null);

            state.LastError = null;

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex,
                "Transcript page load failed for {SessionId}.",
                sessionId);

            state.LastError = ex.Message;

            return false;
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
        bool attachmentCopyConfirmed = false,
        string? confirmedCopyFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SessionId is not { } sourceId)
        {
            return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: "Open a session before forking.");
        }

        Guid? createdForkId = null;
        string? createdFailureMessage = null;
        bool cancelled = false;

        SessionForkResult CreatedFailure(string reason)
        {
            createdFailureMessage = $"Branch {createdForkId:D} was created. {reason} Use /resume {createdForkId:D} to open that branch instead of repeating /fork.";
            state.LastError = createdFailureMessage;
            return new SessionForkResult(SessionForkOutcome.Failed, createdForkId, createdFailureMessage);
        }

        state.TransientStatus = "Forking session…";
        try
        {
            Result<SessionForkPreviewDto> previewResult = await apiClient
                .PreviewSessionForkAsync(sourceId, request, cancellationToken).ConfigureAwait(false);
            if (previewResult.IsFailure)
            {
                return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: FormatForkError(previewResult.Error));
            }
            SessionForkPreviewDto preview = previewResult.Value;
            bool changed = confirmedCopyFingerprint is not null
                && !string.Equals(confirmedCopyFingerprint, preview.CopyFingerprint, StringComparison.Ordinal);
            if (changed || (!attachmentCopyConfirmed && (preview.AttachmentCount >= 10 || preview.AttachmentBytes >= 10 * 1024 * 1024)))
            {
                string message = (changed ? "The fork selection changed. " : string.Empty)
                    + $"This fork will copy {preview.AttachmentCount} attachment(s) ({preview.AttachmentBytes / 1024d / 1024d:F1} MiB). "
                    + "Run `/fork confirm` to continue.";
                return new SessionForkResult(SessionForkOutcome.ConfirmationRequired, ErrorMessage: message, Preview: preview);
            }

            if (state.SessionId != sourceId)
            {
                return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: "The active session changed. Start the fork again.");
            }
            request = request with { ExpectedCopyFingerprint = preview.CopyFingerprint };

            Result<SessionDetailDto> forkResult = await apiClient
                .ForkSessionAsync(sourceId, request, cancellationToken).ConfigureAwait(false);
            if (!forkResult.IsSuccess || forkResult.Value is null)
            {
                string message = FormatForkError(forkResult.Error);
                state.LastError = message;
                return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: message);
            }

            Guid forkId = forkResult.Value.Id;
            createdForkId = forkId;
            Result<SessionDetailDto> detailResult = await apiClient
                .GetSessionAsync(forkId, cancellationToken).ConfigureAwait(false);
            Result<EntryDto[]> entriesResult = await apiClient
                .GetSessionEntriesAsync(
                    forkId, 0, CommandCenterState.TranscriptPageSize + 1, cancellationToken)
                .ConfigureAwait(false);
            Result<SessionAttachmentDto[]> attachmentsResult = await apiClient
                .GetSessionAttachmentsAsync(forkId, cancellationToken).ConfigureAwait(false);

            if (!detailResult.IsSuccess || detailResult.Value is null
                || !entriesResult.IsSuccess || !attachmentsResult.IsSuccess)
            {
                Error error = !detailResult.IsSuccess
                    ? detailResult.Error
                    : !entriesResult.IsSuccess ? entriesResult.Error : attachmentsResult.Error;
                return CreatedFailure(string.IsNullOrWhiteSpace(error.Message) ? "Opening the branch did not finish." : error.Message);
            }

            SessionDetailDto detail = detailResult.Value;

            EntryDto[] descending = entriesResult.Value ?? [];

            ArcanumClientMutationResult<CliContextDocument> persisted =
                await lastSessionStore
                    .SaveSessionIdAsync(
                        detail.Id,
                        (id, token) => SessionMutationRevalidator
                            .RevalidateAsync(
                                apiClient,
                                id,
                                detail,
                                token),
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!persisted.IsCompleted)
            {
                return CreatedFailure(persisted.Error.Message);
            }

            state.ApplySessionMeta(
                detail.Id, detail.Title, detail.Status, detail.EntryCount, detail.ForkedFromSessionId);

            ApplyTranscriptPage(state, descending, hasNewer: false);

            state.LastError = null;
            await RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
            return new SessionForkResult(SessionForkOutcome.Success, detail.Id);
        }
        catch (OperationCanceledException) when (createdForkId is not null)
        {
            cancelled = true;
            _ = CreatedFailure("The operation was cancelled.");
            state.Log.Append(SessionLogEntryKind.Error, createdFailureMessage!);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Session fork failed for {SessionId}.", sourceId);
            if (createdForkId is not null)
            {
                return CreatedFailure(ex.Message);
            }
            state.LastError = ex.Message;
            return new SessionForkResult(SessionForkOutcome.Failed, ErrorMessage: ex.Message);
        }
        finally
        {
            try
            {
                if (createdFailureMessage is not null && !cancelled && !cancellationToken.IsCancellationRequested)
                {
                    await RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation during the recovery listing must still leave the known branch visible.
                state.Log.Append(SessionLogEntryKind.Error, createdFailureMessage!);
                throw;
            }
            finally
            {
                if (createdFailureMessage is not null)
                {
                    state.LastError = createdFailureMessage;
                }
                state.TransientStatus = null;
            }
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

    /// <summary>
    /// Unbinds the session. A cleared transcript opens with the welcome block as one entry, so it reads
    /// as consecutive lines; a resumed session never gets it, since it would interleave with history.
    /// </summary>
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

            state.Log.Append(SessionLogEntryKind.Status, string.Join('\n', CommandCenterWelcome.Lines));
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

    public async Task<bool> PersistBoundSessionAsync(
        CommandCenterState state,
        Guid sessionId,
        CancellationToken cancellationToken,
        (Guid? SessionId, long Revision)? expectedBinding = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (expectedBinding is { } sourceVisit && state.AttachmentBinding != sourceVisit)
        {
            return false;
        }

        ArcanumClientMutationResult<CliContextDocument> persisted =
            await lastSessionStore
                .SaveSessionIdAsync(
                    sessionId,
                    (id, token) => SessionMutationRevalidator
                        .RevalidateAsync(
                            apiClient,
                            id,
                            expected: null,
                            cancellationToken: token),
                    cancellationToken)
                .ConfigureAwait(false);

        if (!persisted.IsCompleted)
        {
            string message = "Warning: " + persisted.Error.Message;

            state.LastError = message;

            state.Log.Append(SessionLogEntryKind.Error, message);

            return false;
        }

        lock (state.AttachmentProjectionGate)
        {
            if (expectedBinding is { } expected && state.AttachmentBinding != expected)
            {
                return false;
            }

            state.SelectedSessionId = sessionId;

            if (state.SessionId != sessionId)
            {
                state.ApplySessionMeta(sessionId, state.SessionTitle, state.SessionStatus, state.SessionEntryCount);
            }

            if (string.IsNullOrWhiteSpace(state.SessionTitle))
            {
                state.SessionTitle = "Untitled";
            }

            if (string.IsNullOrWhiteSpace(state.SessionStatus))
            {
                state.SessionStatus = "Active";
            }
        }

        return true;
    }

    internal const string TranscriptRecoveryMessage =
        "Live output retained. Load the newest transcript page or use /resume for persisted entry selection.";

    /// <summary>Admits a bounded durable snapshot only for the turn's accepted Session visit.</summary>
    public async Task ReconcileCompletedTurnAsync(
        CommandCenterState state,
        TurnTranscriptReceipt receipt,
        Func<bool> preserveTranscriptPage,
        CancellationToken cancellationToken)
    {
        if (!receipt.DurableResult || receipt.Binding.SessionId is not { } sessionId
            || state.AttachmentBinding != receipt.Binding)
        {
            return;
        }

        try
        {
            Result<SessionDetailDto> detailResult = await apiClient
                .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!detailResult.IsSuccess || detailResult.Value is not { } detail || detail.Id != sessionId)
            {
                state.Log.Append(SessionLogEntryKind.Status, TranscriptRecoveryMessage);
                return;
            }

            Result<EntryDto[]> entriesResult = await apiClient.GetSessionEntriesAsync(
                sessionId, offset: 0, limit: CommandCenterState.TranscriptPageSize + 1,
                cancellationToken).ConfigureAwait(false);
            if (!entriesResult.IsSuccess || entriesResult.Value is not { } descending
                || descending.Any(entry => entry.SessionId != sessionId || entry.Id == Guid.Empty)
                || descending.Select(entry => entry.Id).Distinct().Count() != descending.Length)
            {
                state.Log.Append(SessionLogEntryKind.Status, TranscriptRecoveryMessage);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (state.AttachmentProjectionGate)
            {
                if (state.AttachmentBinding != receipt.Binding)
                {
                    return;
                }

                state.ApplySessionMeta(sessionId, detail.Title, detail.Status, detail.EntryCount, detail.ForkedFromSessionId);
                if (state.HasNewerTranscriptEntries || preserveTranscriptPage())
                {
                    // Keep the operator's historical page or scrolled selection anchored.
                    // The explicit newest-page action obtains durable IDs when they want to leave it.
                    state.HasNewerTranscriptEntries = true;
                    return;
                }

                EntryDto[] chronological = descending.Take(CommandCenterState.TranscriptPageSize).Reverse().ToArray();
                List<(SessionLogEntryKind Kind, string Text, Guid? SourceEntryId)> mapped = [];
                HashSet<string> liveCalls = state.Incantations.Snapshot().Select(record => record.CallId).ToHashSet(StringComparer.Ordinal);
                foreach (EntryDto entry in chronological)
                {
                    if (SessionLogBuffer.IsEphemeralReasoningRole(entry.Role))
                    {
                        continue;
                    }

                    SessionLogEntryKind kind = SessionLogBuffer.MapEntryRole(entry.Role);
                    if (PersistedToolInteraction.IsToolInteraction(entry) || kind == SessionLogEntryKind.Tool)
                    {
                        // Legacy bracket pairs have no durable call identity. Explicit resume owns
                        // their presentation; automatic refresh cannot match them to a live call.
                        if (!string.IsNullOrWhiteSpace(entry.ToolCallId))
                        {
                            string callId = entry.ToolCallId.Trim();
                            if (!liveCalls.Contains(callId))
                            {
                                bool bracketResult = PersistedToolInteraction.TryParseToolResult(entry.Content, out string result);
                                if (bracketResult || kind == SessionLogEntryKind.Tool)
                                {
                                    // A tool/function row is a result even when its payload is JSON.
                                    // Its verified call ID, never the most recent same-named call, owns it.
                                    result = bracketResult ? result : entry.Content ?? string.Empty;
                                    if (entry.Role.Contains("error", StringComparison.OrdinalIgnoreCase)
                                        || result.Contains("[Tool error:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _ = state.Incantations.UpsertError(callId, entry.ToolName, result);
                                    }
                                    else
                                    {
                                        _ = state.Incantations.UpsertResult(callId, entry.ToolName, result);
                                    }
                                }
                                else if (PersistedToolInteraction.TryParseToolCall(entry.Content, out string parsedName, out string? parsedArguments))
                                {
                                    string name = string.IsNullOrWhiteSpace(entry.ToolName) ? parsedName : entry.ToolName;
                                    _ = state.Incantations.UpsertCall(callId, name, parsedArguments);
                                }
                                else if (kind == SessionLogEntryKind.Assistant)
                                {
                                    _ = state.Incantations.UpsertCall(callId, entry.ToolName, entry.Content);
                                }
                            }
                        }

                        continue;
                    }

                    mapped.Add((kind, FormatEntryContent(entry), entry.Id));
                }

                state.LoadedTranscriptEntries = chronological;
                state.HasOlderTranscriptEntries = descending.Length > CommandCenterState.TranscriptPageSize;
                state.Log.ReconcileApiHistory(mapped, receipt.ProvisionalEntryIds, state.HasOlderTranscriptEntries);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Post-turn transcript refresh failed for {SessionId}; exception type {ExceptionType}.", sessionId, ex.GetType().Name);
            state.Log.Append(SessionLogEntryKind.Status, TranscriptRecoveryMessage);
        }
    }

    private static void ApplyTranscriptPage(
        CommandCenterState state,
        IReadOnlyList<EntryDto> descending,
        bool hasNewer)
    {
        state.HasOlderTranscriptEntries = descending.Count > CommandCenterState.TranscriptPageSize;

        EntryDto[] chronological = descending.Take(CommandCenterState.TranscriptPageSize).Reverse().ToArray();

        List<(SessionLogEntryKind Kind, string Text, Guid? SourceEntryId)> mapped = [];

        state.Incantations.Clear();

        foreach (EntryDto entry in chronological)
        {
            if (SessionLogBuffer.IsEphemeralReasoningRole(entry.Role))
            {
                continue;
            }

            SessionLogEntryKind kind = SessionLogBuffer.MapEntryRole(entry.Role);

            if (PersistedToolInteraction.IsToolInteraction(entry)
                || kind == SessionLogEntryKind.Tool)
            {
                IngestHistoryTool(state.Incantations, entry);

                continue;
            }

            mapped.Add((kind, FormatEntryContent(entry), entry.Id));
        }

        state.LoadedTranscriptEntries = chronological;

        state.HasNewerTranscriptEntries = hasNewer;

        state.Log.ReplaceWithApiHistory(
            mapped,
            showOlderMessagesMarker: state.HasOlderTranscriptEntries,
            showNewerMessagesMarker: state.HasNewerTranscriptEntries);
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
