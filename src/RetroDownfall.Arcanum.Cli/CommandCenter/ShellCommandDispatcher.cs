using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Explicit slash-command handlers. No reflection; no System.CommandLine recursion.
/// UI-agnostic: mutates state / log / workspace only — never Terminal.Gui controls.
/// </summary>
internal sealed class ShellCommandDispatcher(
    ArcanumApiClient apiClient,
    ShellCommandParser parser,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    SessionWorkspaceService sessionWorkspace,
    CommandCenterWardCoordinator wardCoordinator,
    ILogger<ShellCommandDispatcher> logger)
{
    private const int TerminalListPageSize = 50;

    /// <summary>
    /// The one canonical resume spelling every operator-facing hint below is built from, so a
    /// removed form cannot be taught back in a corner of the UI.
    /// </summary>
    internal const string ResumeUsage = "/resume <id>";

    internal const string ResumeUsageMessage = $"Usage: {ResumeUsage}";

    internal const string SessionListResumeHint = $"Resume: {ResumeUsage}  or  Ctrl+O then Enter";

    internal const string NoActiveSessionMessage =
        $"No active session. Send a message or `{ResumeUsage}` before using /attachments.";

    /// <summary>The canonical pin spellings; <c>/context pin|unpin|list</c> were removed.</summary>
    internal const string PinUsage = "/pin <kind> <target>";

    internal const string UnpinUsage = "/unpin <pin-id>";

    internal const string PinUsageMessage =
        $"Usage: {PinUsage}  ·  Kinds: file, directorySnapshot, symbolRange, sessionEntry, attachment, url, diagnostic.";

    internal const string UnpinUsageMessage = $"Usage: {UnpinUsage}";

    public async Task<ShellDispatchResult> DispatchAsync(
        string input,
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        ParsedShellCommand parsed = parser.Parse(input);

        switch (parsed.Kind)
        {
            case ShellCommandKind.Exit:
            case ShellCommandKind.Quit:
                state.RequestExit = true;
                state.ExitCode = 0;
                return ShellDispatchResult.Exit;

            // Claude's /clear starts a fresh conversation rather than only wiping the view, so it
            // absorbs what used to be /session new.
            case ShellCommandKind.Clear:
                if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? denyClear))
                {
                    if (denyClear is not null)
                    {
                        state.Log.Append(SessionLogEntryKind.Status, CommandCenterSessionMutationGuard.GeneratingDenyMessage);
                    }

                    return ShellDispatchResult.Continue;
                }

                state.Log.Clear();
                state.Incantations.Clear();
                sessionWorkspace.StartNewSession(state);
                state.Log.Append(SessionLogEntryKind.Status, "Started a new session thread.");
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Help:
                state.Log.Append(SessionLogEntryKind.Command, SlashCommandRegistry.BuildHelpText());
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Denied:
            case ShellCommandKind.Unknown:
                state.Log.Append(
                    SessionLogEntryKind.Error,
                    parsed.DenialMessage ?? "Command rejected.");
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Attach:
                return AttachPath(state, parsed.Argument);

            case ShellCommandKind.AttachmentsList:
                return await ListAttachmentsAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.AttachmentsAdd:
                return await AddAttachmentReferenceAsync(
                        state,
                        parsed.Argument,
                        parsed.Version,
                        cancellationToken)
                    .ConfigureAwait(false);

            case ShellCommandKind.AttachmentsReveal:
                return await RevealAttachmentAsync(
                        state,
                        parsed.Argument,
                        parsed.Version,
                        cancellationToken)
                    .ConfigureAwait(false);

            case ShellCommandKind.AttachmentsRefresh:

                return await RefreshAttachmentAsync(

                        state,

                        parsed.Argument,

                        cancellationToken)

                    .ConfigureAwait(false);

            case ShellCommandKind.PinList:
                return await ListContextPinsAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Pin:
                return await PinContextAsync(
                    state, parsed.Argument, parsed.SecondaryArgument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Unpin:
                return await UnpinContextAsync(state, parsed.Argument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Status:
                state.Log.Append(SessionLogEntryKind.Command, BuildStatusText(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Doctor:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await BuildDoctorTextAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Model:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatModelsAsync(cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.ModelSelect:
                return await SelectModelAsync(state, parsed.Argument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Compact:
                return await CompactSessionAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Memory:
                return await ShowSessionMemoryAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Cost:
                return await ShowSessionCostAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Context:
                state.Log.Append(SessionLogEntryKind.Command, FormatContext(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Config:
                state.Log.Append(SessionLogEntryKind.Command, FormatConfig(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Look:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    $"Working directory: {state.WorkingDirectory}"
                    + Environment.NewLine
                    + "Full Eye of the World snapshot: run `arcanum look`.");
                return ShellDispatchResult.Continue;

            case ShellCommandKind.ProviderList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatProvidersAsync(cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Mcp:
                await RefreshMcpAsync(state, cancellationToken).ConfigureAwait(false);
                state.Log.Append(SessionLogEntryKind.Command, FormatMcp(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.McpReload:
                return await ReloadMcpAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Arsenal:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatArsenalAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.CampaignList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatCampaignsAsync(parsed.Argument, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.SessionList:
                await sessionWorkspace.RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    FormatSessionSidebar(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.SessionResume:
                if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? denyUpdate))
                {
                    if (denyUpdate is not null)
                    {
                        state.Log.Append(SessionLogEntryKind.Status, CommandCenterSessionMutationGuard.GeneratingDenyMessage);
                    }

                    return ShellDispatchResult.Continue;
                }

                return await ResumeSessionAsync(state, parsed.Argument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.SessionArchive:
                return await ArchiveSessionAsync(state, parsed.Argument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.SessionFork:
                Guid? cutoff = null;
                string? alternativePrompt = null;
                if (parsed.SecondaryArgument == "alternative")
                {
                    int answerIndex = state.LoadedTranscriptEntries
                        .ToList()
                        .FindIndex(entry => entry.Id == state.SelectedTranscriptEntryId);
                    if (answerIndex <= 0
                        || !state.LoadedTranscriptEntries[answerIndex].Role.Equals(
                            "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        state.Log.Append(
                            SessionLogEntryKind.Error,
                            "Select an assistant answer, then run `/fork alternative`.");
                        return ShellDispatchResult.Continue;
                    }

                    int promptIndex = answerIndex - 1;
                    while (promptIndex >= 0
                           && !state.LoadedTranscriptEntries[promptIndex].Role.Equals(
                               "user", StringComparison.OrdinalIgnoreCase))
                    {
                        promptIndex--;
                    }
                    if (promptIndex < 0)
                    {
                        state.Log.Append(SessionLogEntryKind.Error, "No user prompt precedes the selected answer.");
                        return ShellDispatchResult.Continue;
                    }

                    cutoff = state.LoadedTranscriptEntries[promptIndex].Id;
                    alternativePrompt = state.LoadedTranscriptEntries[promptIndex].Content;
                }
                if (parsed.SecondaryArgument == "selected")
                {
                    cutoff = state.SelectedTranscriptEntryId;
                    if (cutoff is null)
                    {
                        state.Log.Append(
                            SessionLogEntryKind.Error,
                            "Select a transcript entry, then run `/fork at` again.");
                        return ShellDispatchResult.Continue;
                    }
                }
                if (parsed.Argument is not null && !Guid.TryParse(parsed.Argument, out _))
                {
                    state.Log.Append(SessionLogEntryKind.Error, "Usage: /fork [at <entry-id>]");
                    return ShellDispatchResult.Continue;
                }
                if (parsed.Argument is not null)
                {
                    cutoff = Guid.Parse(parsed.Argument);
                }
                SessionForkResult fork = await sessionWorkspace
                    .ForkSessionAsync(
                        state,
                        new ForkSessionRequest(UpToEntryId: cutoff),
                        cancellationToken,
                        attachmentCopyConfirmed: parsed.SecondaryArgument == "confirm")
                    .ConfigureAwait(false);
                state.Log.Append(
                    fork.Outcome == SessionForkOutcome.Success ? SessionLogEntryKind.Status : SessionLogEntryKind.Error,
                    fork.Outcome == SessionForkOutcome.Success
                        ? $"Opened branch {fork.ForkSessionId:D}."
                        : fork.ErrorMessage ?? "Fork failed.");
                if (fork.Outcome == SessionForkOutcome.Success && alternativePrompt is not null)
                {
                    state.PendingAlternativePrompt = alternativePrompt;
                }
                return ShellDispatchResult.Continue;

            case ShellCommandKind.BranchParent:
                if (state.ForkedFromSessionId is not { } parentId)
                {
                    state.Log.Append(SessionLogEntryKind.Status, "This session has no parent branch.");
                    return ShellDispatchResult.Continue;
                }
                await sessionWorkspace.ResumeSessionAsync(state, parentId, cancellationToken).ConfigureAwait(false);
                return ShellDispatchResult.Continue;

            case ShellCommandKind.BranchChild:
                SessionListItem? child = state.Sessions.FirstOrDefault(s => s.ForkedFromSessionId == state.SessionId);
                if (child is null)
                {
                    state.Log.Append(SessionLogEntryKind.Status, "No child branch is visible in the recent session list.");
                    return ShellDispatchResult.Continue;
                }
                await sessionWorkspace.ResumeSessionAsync(state, child.Id, cancellationToken).ConfigureAwait(false);
                return ShellDispatchResult.Continue;

            case ShellCommandKind.SpellList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatSpellsAsync(state, parsed.Argument, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Tools:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatToolsAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;


            case ShellCommandKind.WardList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatWardsAsync(parsed.Argument, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.WardAllow:
                return await ResolveWardSlashAsync(state, parsed.Argument, allow: true, cancellationToken)
                    .ConfigureAwait(false);

            case ShellCommandKind.WardDeny:
                return await ResolveWardSlashAsync(state, parsed.Argument, allow: false, cancellationToken)
                    .ConfigureAwait(false);

            case ShellCommandKind.Keys:
                state.Log.Append(SessionLogEntryKind.Command, BuildKeysHelp());
                return ShellDispatchResult.Continue;

            default:
                state.Log.Append(SessionLogEntryKind.Error, "Unhandled command kind.");
                return ShellDispatchResult.Continue;
        }
    }

    private ShellDispatchResult AttachPath(CommandCenterState state, string? argument)
    {
        if (!CommandCenterTurnAttachmentBuilder.TryStagePathForNextTurn(
                state.WorkingDirectory,
                argument ?? string.Empty,
                settingsMonitor.CurrentValue,
                out string fullPath,
                out string statusLine))
        {
            state.Log.Append(SessionLogEntryKind.Error, statusLine);
            return ShellDispatchResult.Continue;
        }

        _ = state.StagedAttachmentPaths.Add(fullPath);
        state.Log.Append(SessionLogEntryKind.Status, statusLine);
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> ListAttachmentsAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }

        Result<SessionAttachmentDto[]> result = await apiClient
            .GetSessionAttachmentsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);
            return ShellDispatchResult.Continue;
        }

        SessionAttachmentDto[] current = result.Value ?? [];

        _ = CommandCenterAttachmentDriftMonitor.ApplyBackendSnapshot(state, current);

        state.Log.Append(SessionLogEntryKind.Command, FormatAttachmentsList(current));
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> AddAttachmentReferenceAsync(
        CommandCenterState state,
        string? logicalName,
        int? version,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }

        if (string.IsNullOrWhiteSpace(logicalName))
        {
            state.Log.Append(
                SessionLogEntryKind.Error,
                "Usage: /attachments add <logicalName> [vN]");
            return ShellDispatchResult.Continue;
        }

        Result<SessionAttachmentDto[]> result = await apiClient
            .GetSessionAttachmentsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);
            return ShellDispatchResult.Continue;
        }

        if (!TryResolveAttachment(result.Value ?? [], logicalName, version, out SessionAttachmentDto row, out string resolveError))
        {
            state.Log.Append(SessionLogEntryKind.Error, resolveError);
            return ShellDispatchResult.Continue;
        }

        _ = state.StagedAttachmentReferences.Add(row.Id);
        state.Log.Append(
            SessionLogEntryKind.Status,
            $"Staged attachment reference: {row.LogicalKey} v{row.Version} ({row.Id:D})");
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> RevealAttachmentAsync(
        CommandCenterState state,
        string? logicalName,
        int? version,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }

        if (string.IsNullOrWhiteSpace(logicalName))
        {
            state.Log.Append(
                SessionLogEntryKind.Error,
                "Usage: /attachments reveal <logicalName> [vN]");
            return ShellDispatchResult.Continue;
        }

        Result<SessionAttachmentDto[]> result = await apiClient
            .GetSessionAttachmentsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);
            return ShellDispatchResult.Continue;
        }

        if (!TryResolveAttachment(result.Value ?? [], logicalName, version, out SessionAttachmentDto row, out string resolveError))
        {
            state.Log.Append(SessionLogEntryKind.Error, resolveError);
            return ShellDispatchResult.Continue;
        }

        if (!TryBuildAbsoluteAttachmentPath(row.RelativePath, out string absolutePath, out string pathError))
        {
            state.Log.Append(SessionLogEntryKind.Error, pathError);
            return ShellDispatchResult.Continue;
        }

        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        string revealStatus = SessionAttachmentReveal.TryReveal(absolutePath, interactive, out bool started);
        string line = started
            ? revealStatus
            : $"{absolutePath} — {revealStatus}";
        state.Log.Append(SessionLogEntryKind.Status, line);
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> RefreshAttachmentAsync(

        CommandCenterState state,

        string? logicalName,

        CancellationToken cancellationToken)

    {

        if (!TryRequireSession(state, out Guid sessionId))

        {

            return ShellDispatchResult.Continue;

        }

        if (string.IsNullOrWhiteSpace(logicalName))

        {

            state.Log.Append(

                SessionLogEntryKind.Error,

                "Usage: /attachments refresh <logicalName>");

            return ShellDispatchResult.Continue;

        }

        Result<SessionAttachmentDto[]> listed = await apiClient

            .GetSessionAttachmentsAsync(sessionId, cancellationToken)

            .ConfigureAwait(false);

        if (listed.IsFailure)

        {

            state.Log.Append(SessionLogEntryKind.Error, listed.Error.Message);

            return ShellDispatchResult.Continue;

        }

        if (!TryResolveAttachment(

                listed.Value ?? [],

                logicalName,

                version: null,

                out SessionAttachmentDto row,

                out string resolveError))

        {

            state.Log.Append(SessionLogEntryKind.Error, resolveError);

            return ShellDispatchResult.Continue;

        }

        if (row.SourceKind != AttachmentSourceKind.WorkspaceFile)

        {

            state.Log.Append(

                SessionLogEntryKind.Error,

                $"Attachment `{row.LogicalKey}` is a Snapshot and cannot be refreshed.");

            return ShellDispatchResult.Continue;

        }

        state.TransientStatus = $"Refreshing {row.LogicalKey}…";

        try

        {

            Result<AttachmentRefreshEvent> refreshed = await apiClient

                .RefreshSessionAttachmentAsync(sessionId, row.Id, cancellationToken)

                .ConfigureAwait(false);

            if (refreshed.IsFailure || refreshed.Value is null)

            {

                state.Log.Append(SessionLogEntryKind.Error, refreshed.Error.Message);

                return ShellDispatchResult.Continue;

            }

            AttachmentRefreshEvent detail = refreshed.Value;

            Result<SessionAttachmentDto[]> confirmed = await apiClient

                .GetSessionAttachmentsAsync(sessionId, cancellationToken)

                .ConfigureAwait(false);

            if (confirmed.IsSuccess)

            {

                _ = CommandCenterAttachmentDriftMonitor.ApplyBackendSnapshot(

                    state,

                    confirmed.Value ?? []);

            }

            state.Log.Append(

                SessionLogEntryKind.Status,

                $"[Live] Refreshed {detail.LogicalKey} v{detail.Version} "

                + $"loaded={ShortHash(detail.ContentSha256)} "

                + $"at {detail.SourceFreshnessTimestamp:O}.");

            return ShellDispatchResult.Continue;

        }

        finally

        {

            state.TransientStatus = null;

        }

    }

    private static bool TryRequireSession(CommandCenterState state, out Guid sessionId)
    {
        if (state.SessionId is { } id)
        {
            sessionId = id;
            return true;
        }

        sessionId = default;
        state.Log.Append(SessionLogEntryKind.Error, NoActiveSessionMessage);
        return false;
    }

    private static bool TryResolveAttachment(
        IReadOnlyList<SessionAttachmentDto> rows,
        string logicalName,
        int? version,
        out SessionAttachmentDto row,
        out string error)
    {
        row = null!;
        error = string.Empty;

        List<SessionAttachmentDto> matches = rows
            .Where(r => string.Equals(r.LogicalKey, logicalName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static r => r.Version)
            .ToList();

        if (matches.Count == 0)
        {
            string available = rows.Count == 0
                ? "(none)"
                : string.Join(", ", rows.Select(static r => r.LogicalKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static s => s, StringComparer.OrdinalIgnoreCase));
            error = $"Attachment `{logicalName}` not found. Available: {available}";
            return false;
        }

        if (version is { } v)
        {
            SessionAttachmentDto? exact = matches.FirstOrDefault(r => r.Version == v);
            if (exact is null)
            {
                string versions = string.Join(", ", matches.Select(static m => $"v{m.Version}"));
                error = $"Attachment `{logicalName}` has no v{v}. Versions: {versions}";
                return false;
            }

            row = exact;
            return true;
        }

        row = matches[0];
        return true;
    }

    private static bool TryBuildAbsoluteAttachmentPath(
        string relativePath,
        out string absolutePath,
        out string error)
    {
        absolutePath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            error = "Reveal: relative path from API was invalid.";
            return false;
        }

        string root = Path.GetFullPath(ArcanumPaths.AttachmentsDirectory);
        string combined = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            error = "Reveal: resolved path escaped AttachmentsDirectory.";
            return false;
        }

        absolutePath = combined;
        return true;
    }

    private static string FormatAttachmentsList(IReadOnlyList<SessionAttachmentDto> rows)
    {
        if (rows.Count == 0)
        {
            return "No bound session attachments.";
        }

        List<string> lines =
        [
            "Session attachments:",
        ];

        foreach (SessionAttachmentDto row in rows.OrderBy(static r => r.LogicalKey, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static r => r.Version))
        {

            string badge = row.SourceKind == AttachmentSourceKind.SnapshotOnly

                ? "Snapshot"

                : row.SourceStatus == AttachmentSourceStatus.Refreshable && row.IsRefreshable

                    ? "Live"

                    : "Stale";

            string version = $"loaded={ShortHash(row.ContentSha256)}";

            string observed = row.SourceKind == AttachmentSourceKind.WorkspaceFile

                ? $"  disk={ShortHash(row.LastObservedSourceContentSha256)}"

                  + (row.LastObservedSourceWriteTime is { } observedAt

                      ? $"  observed={observedAt:O}"

                      : string.Empty)

                : string.Empty;

            lines.Add(
                $"  [{badge}] {row.LogicalKey}  v{row.Version}  {version}{observed}  "

                + $"{row.Kind}  {row.ByteLength} B  {row.OriginalFileName}  {row.Id:D}");
        }

        lines.Add("");
        lines.Add("Re-attach: /attachments add <logicalName> [vN]");
        lines.Add("Reveal:    /attachments reveal <logicalName> [vN]");

        lines.Add("Refresh:   /attachments refresh <logicalName>");

        return string.Join(Environment.NewLine, lines);
    }

    private static string ShortHash(string? value)

    {

        string hash = value?.Trim() ?? string.Empty;

        return hash.Length <= 8 ? hash : hash[..8];

    }

    private async Task<ShellDispatchResult> ListContextPinsAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }
        Result<SessionContextPinDto[]> result =
            await apiClient.GetSessionContextPinsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);
            return ShellDispatchResult.Continue;
        }
        string[] lines = result.Value is { Length: > 0 } rows
            ? ["Pinned context:", .. rows.Select(pin =>
                $"  {pin.Id:D}  {pin.Kind}  {pin.DisplayLabel}  version={pin.ContentVersion ?? "-"}")]
            : ["No pinned session context."];
        state.Log.Append(SessionLogEntryKind.Command, string.Join(Environment.NewLine, lines));
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> PinContextAsync(
        CommandCenterState state,
        string? kindText,
        string? targetText,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }
        if (!Enum.TryParse(kindText, ignoreCase: true, out SessionContextPinKind kind)
            || string.IsNullOrWhiteSpace(targetText))
        {
            state.Log.Append(SessionLogEntryKind.Error, PinUsageMessage);
            return ShellDispatchResult.Continue;
        }

        string target = targetText.Trim();
        string? version = null;
        if (kind is SessionContextPinKind.File)
        {
            string candidate = Path.GetFullPath(target, state.WorkingDirectory);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.WorkingDirectory));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(candidate))
            {
                state.Log.Append(SessionLogEntryKind.Error, "File pins must name an existing file inside the workspace.");
                return ShellDispatchResult.Continue;
            }
            target = Path.GetRelativePath(root, candidate);
            version = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(candidate, cancellationToken)))
                .ToLowerInvariant();
        }

        Result<SessionContextPinDto> result = await apiClient.CreateSessionContextPinAsync(
            sessionId,
            new CreateSessionContextPinRequest(kind, target, target, version),
            cancellationToken).ConfigureAwait(false);
        state.Log.Append(
            result.IsSuccess ? SessionLogEntryKind.Status : SessionLogEntryKind.Error,
            result.IsSuccess
                ? $"Pinned {kind}: {result.Value!.DisplayLabel} ({result.Value.Id:D})"
                : result.Error.Message);
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> UnpinContextAsync(
        CommandCenterState state,
        string? idText,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }
        if (!Guid.TryParse(idText, out Guid pinId))
        {
            state.Log.Append(SessionLogEntryKind.Error, UnpinUsageMessage);
            return ShellDispatchResult.Continue;
        }
        Result result = await apiClient.DeleteSessionContextPinAsync(sessionId, pinId, cancellationToken)
            .ConfigureAwait(false);
        state.Log.Append(
            result.IsSuccess ? SessionLogEntryKind.Status : SessionLogEntryKind.Error,
            result.IsSuccess ? $"Unpinned context {pinId:D}." : result.Error.Message);
        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> ResumeSessionAsync(
        CommandCenterState state,
        string? argument,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(argument, out Guid id))
        {
            state.Log.Append(SessionLogEntryKind.Error, ResumeUsageMessage);
            return ShellDispatchResult.Continue;
        }

        SessionResumeResult result = await sessionWorkspace
            .ResumeSessionAsync(state, id, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome == SessionResumeOutcome.Success)
        {
            await sessionWorkspace.RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return ShellDispatchResult.Continue;
    }

    private static string FormatSessionSidebar(CommandCenterState state)
    {
        if (state.Sessions.Count == 0)
        {
            return "No sessions yet.";
        }

        List<string> lines = ["Sessions (UpdatedAt desc):"];
        foreach (SessionListItem item in state.Sessions)
        {
            string mark = state.SessionId == item.Id ? "*" : " ";
            lines.Add($"{mark} {item.Id:D}");
            lines.Add($"    {item.Title} · {item.Status} · {item.UpdatedAt:u}");
        }

        lines.Add("");
        lines.Add(SessionListResumeHint);
        return string.Join(Environment.NewLine, lines);
    }

    public async Task RefreshMcpAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        try
        {
            Result<IReadOnlyList<McpServerInfo>> mcp = await apiClient
                .GetMcpServersAsync(cancellationToken)
                .ConfigureAwait(false);

            if (mcp.IsSuccess)
            {
                state.McpServers = mcp.Value ?? [];
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MCP refresh failed.");
        }
    }

    private async Task<string> BuildDoctorTextAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        List<string> lines =
        [
            "Command Center doctor (compact)",
            $"Serve: {state.ServeLaunch?.Status.ToString() ?? "unknown"} — {state.ServeLaunch?.Guidance ?? state.HealthSummary ?? "-"}",
            $"Model: {state.Model ?? settingsMonitor.CurrentValue.DefaultModel ?? "(unset)"}",
        ];

        await RefreshMcpAsync(state, cancellationToken).ConfigureAwait(false);
        int mcpUp = state.McpServers.Count(static s => s.State == McpServerState.Running);
        lines.Add($"MCP: {mcpUp}/{state.McpServers.Count} running");

        Result<IReadOnlyList<McpServerInfo>> healthProbe = await apiClient
            .GetMcpServersAsync(cancellationToken)
            .ConfigureAwait(false);
        lines.Add(healthProbe.IsSuccess
            ? "API: reachable (MCP list OK)"
            : $"API: {(string.IsNullOrWhiteSpace(healthProbe.Error.Message) ? "unreachable" : healthProbe.Error.Message)}");

        lines.Add("Grimoire: see /api/health via `arcanum doctor` for full readiness detail.");
        lines.Add("Run `arcanum doctor` outside the Command Center for the full report.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildStatusText(CommandCenterState state) =>
        string.Join(
            Environment.NewLine,
            [
                state.HeaderText,
                $"CWD: {state.WorkingDirectory}",
                $"Serve: {state.ServeLaunch?.Status.ToString() ?? "-"}",
                state.ServeLaunch?.Guidance ?? "",
            ]);

    private static string BuildKeysHelp() =>
        string.Join(
            Environment.NewLine,
            [
                "Keyboard:",
                "  F1            Help overlay",
                "  Ctrl+K        Command palette",
                "  Ctrl+O        Sessions (sidebar or picker)",
                "  Ctrl+N        New session",
                "  Ctrl+R / F5   Refresh sessions",
                "  Tab/S-Tab     Cycle focus (Composer→Sessions→Transcript→Incantations→Model)",
                "  Enter/Space   Open the model drop-down (Model header control)",
                "  Enter         Newline (composer) / resume selected session",
                "  Ctrl+Enter    Send (composer)",
                "  ↑↓ / j k      Move session selection",
                "  PgUp/PgDn     Scroll transcript",
                "  Ctrl+PgUp/Dn Load newer/older transcript or session catalog page",
                "  Home/End      Jump transcript top / bottom",
                "  Esc           Close overlay / focus composer",
                "  Ctrl+C        Cancel turn / clear composer / quit hint",
                "  Ctrl+Q        Quit (confirm if generating)",
                "  /keys         Show this help",
                "  /exit         Leave Command Center",
            ]);

    /// <summary>
    /// The <c>/context</c> view: how the context window is being spent on this turn. This is the
    /// readout <c>/mana</c> used to show; <c>/cost</c> now owns durable spend.
    /// </summary>
    private static string FormatContext(CommandCenterState state)
    {
        if (state.LastContextBreakdown is { } context)
        {
            string reported = context.ProviderReportedInputTokens is { } providerReported
                ? $", provider reported={providerReported}, variance={context.EstimationVarianceTokens ?? 0}"
                : ", provider reported=unavailable";
            return $"Context: input={context.InputTokens}, reserved={context.ReservedTokens}, "
                + $"total={context.TotalTokens}, quality={context.OverallClassification}, "
                + $"profile={context.Profile.ProfileId}{reported}.";
        }

        if (state.ManaLimit is > 0)
        {
            return $"Context: {state.ManaUsed ?? 0} / {state.ManaLimit} tokens "
                + "(Command Center counters are best-effort until the first turn completes).";
        }

        if (state.ManaUsed is { } used)
        {
            return $"Context tokens (last known): {used}";
        }

        return "Context: no counters yet this session. Full report: `arcanum context inspect`.";
    }

    /// <summary>
    /// The <c>/cost</c> view: durable token and spend totals for the session, read from the same
    /// authenticated session detail that <c>arcanum session show</c> uses.
    /// </summary>
    private async Task<ShellDispatchResult> ShowSessionCostAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }

        Result<SessionDetailDto> result = await apiClient
            .GetSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);

            return ShellDispatchResult.Continue;
        }

        SessionDetailDto detail = result.Value!;

        string spend = detail.TotalCostUsd is { } cost
            ? $"${cost:0.####} USD"
            : "not reported by this provider";

        state.Log.Append(
            SessionLogEntryKind.Command,
            $"Cost: {detail.TotalTokensUsed:N0} tokens over {detail.EntryCount:N0} entries; spend {spend}.");

        return ShellDispatchResult.Continue;
    }

    /// <summary>
    /// The <c>/memory</c> view: the compressed Campaign Summary the Loremaster maintains.
    /// </summary>
    private async Task<ShellDispatchResult> ShowSessionMemoryAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }

        Result<SessionDetailDto> result = await apiClient
            .GetSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);

            return ShellDispatchResult.Continue;
        }

        string? summary = result.Value!.Summary;

        state.Log.Append(
            SessionLogEntryKind.Command,
            string.IsNullOrWhiteSpace(summary)
                ? "Memory: no Campaign Summary yet. Run `/compact` to queue consolidation."
                : $"Campaign Summary:{Environment.NewLine}{summary}");

        return ShellDispatchResult.Continue;
    }

    /// <summary>
    /// <c>/compact</c> queues Campaign Logger consolidation for the active session. The server owns
    /// acceptance, so a full queue is reported rather than silently dropped.
    /// </summary>
    private async Task<ShellDispatchResult> CompactSessionAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        if (!TryRequireSession(state, out Guid sessionId))
        {
            return ShellDispatchResult.Continue;
        }

        Result result = await apiClient
            .RestAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        state.Log.Append(
            result.IsSuccess ? SessionLogEntryKind.Status : SessionLogEntryKind.Error,
            result.IsSuccess
                ? "Memory consolidation queued for this session."
                : result.Error.Message);

        return ShellDispatchResult.Continue;
    }

    /// <summary>
    /// <c>/config</c> shows the effective configuration summary. It reports only safe values: no
    /// endpoint, credential, or credential reference reaches the transcript.
    /// </summary>
    private string FormatConfig(CommandCenterState state)
    {
        ArcanumSettings settings = settingsMonitor.CurrentValue;

        string campaign = state.CampaignId is { } campaignId
            ? campaignId.ToString("D")
            : "-";

        return string.Join(
            Environment.NewLine,
            [
                "Configuration:",
                $"  Default model     {settings.DefaultModel ?? "-"}",
                $"  Session model     {state.Model ?? settings.DefaultModel ?? "-"}",
                $"  Providers         {settings.Providers.Length} configured",
                $"  Workspace         {state.WorkingDirectory}",
                $"  Campaign          {campaign}",
                "  Full detail       run `arcanum config list`",
            ]);
    }

    private async Task<ShellDispatchResult> SelectModelAsync(
        CommandCenterState state,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            state.Log.Append(SessionLogEntryKind.Error, "Usage: /model [<name>]");

            return ShellDispatchResult.Continue;
        }

        // The listing is used only to echo a provider's canonical casing back. It is deliberately not
        // a gate: it omits models the operator hid, and a Familiar's catalogue belongs to the vendor
        // rather than to arcanum.json, so refusing an unlisted name here would break both "hidden is
        // not blocked" and "a new vendor model works with no configuration edit". The host resolves
        // the name at turn time and says why if it cannot.
        Result<ModelInfoDto[]> result = await apiClient
            .GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        ModelInfoDto? listed = result.IsSuccess
            ? Array.Find(
                result.Value!,
                candidate => string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase))
            : null;

        string chosen = listed?.Model ?? model.Trim();

        state.Model = chosen;

        state.Log.Append(SessionLogEntryKind.Status, $"Model set to {chosen} for this session.");

        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> ReloadMcpAsync(
        CommandCenterState state,
        CancellationToken cancellationToken)
    {
        Result<string> result = await apiClient
            .ReloadMcpAsync(new OptionalWorkspaceRequest(state.WorkingDirectory), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);

            return ShellDispatchResult.Continue;
        }

        await RefreshMcpAsync(state, cancellationToken).ConfigureAwait(false);

        state.Log.Append(SessionLogEntryKind.Status, result.Value ?? "MCP configuration reloaded.");

        return ShellDispatchResult.Continue;
    }

    private async Task<ShellDispatchResult> ArchiveSessionAsync(
        CommandCenterState state,
        string? identifier,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(identifier, out Guid sessionId))
        {
            state.Log.Append(SessionLogEntryKind.Error, "Usage: /session archive <session-id>");

            return ShellDispatchResult.Continue;
        }

        Result result = await apiClient
            .ArchiveSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, result.Error.Message);

            return ShellDispatchResult.Continue;
        }

        // Archiving the thread we are standing in would leave the composer bound to a session the
        // server will refuse to append to, so drop the pointer and start clean.
        if (state.SessionId == sessionId)
        {
            sessionWorkspace.StartNewSession(state);
        }

        await sessionWorkspace.RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);

        state.Log.Append(SessionLogEntryKind.Status, $"Archived session {sessionId:D}.");

        return ShellDispatchResult.Continue;
    }

    private async Task<string> FormatToolsAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        Result<WorkspaceArsenalDto> result = await apiClient
            .GetWorkspaceArsenalAsync(new OptionalWorkspaceRequest(state.WorkingDirectory), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        WorkspaceArsenalDto dto = result.Value!;
        if (dto.NativeTools.Count == 0)
        {
            return "No native tools reported.";
        }

        return "Native tools:\n" + string.Join(Environment.NewLine, dto.NativeTools.Select(static t => $"- {t}"));
    }

    private async Task<string> FormatWardsAsync(
        string? offsetArgument,
        CancellationToken cancellationToken)
    {
        Result<WardDto[]> result = await apiClient.GetWardsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        WardDto[] wards = result.Value ?? [];

        if (wards.Length == 0)
        {
            return "No wards.";
        }

        int offset = ParseListOffset(offsetArgument);

        WardDto[] page = wards
            .Skip(offset)
            .Take(TerminalListPageSize)
            .ToArray();

        if (page.Length == 0)
        {
            return $"No wards at offset {offset}. Restart with `/ward list 0`.";
        }

        string body = string.Join(
            Environment.NewLine,
            page.Select(static w =>
            {
                string id = w.WardId.Length > 8 ? w.WardId[..8] : w.WardId;

                return $"- {id}  {w.ToolName}  expires={w.ExpiresAt:u}";
            }));

        return AppendDisplayContinuation(
            body,
            "ward",
            offset,
            page.Length,
            wards.Length,
            offset + page.Length < wards.Length);
    }

    private async Task<ShellDispatchResult> ResolveWardSlashAsync(
        CommandCenterState state,
        string? idArgument,
        bool allow,
        CancellationToken cancellationToken)
    {
        WardApprovalRequest? pending = wardCoordinator.PendingRequest;
        string? wardId = idArgument;

        if (string.IsNullOrWhiteSpace(wardId))
        {
            wardId = pending?.WardId;
        }

        if (string.IsNullOrWhiteSpace(wardId))
        {
            state.Log.Append(
                SessionLogEntryKind.Error,
                "No ward id. Use `/ward list`, then `/ward allow <id>` or `/ward deny <id>`.");
            return ShellDispatchResult.Continue;
        }

        wardId = wardId.Trim();

        // Prefer completing the in-turn prompt so ChatRunner submits the resolve once.
        if (pending is not null
            && (string.IsNullOrWhiteSpace(idArgument)
                || pending.WardId.StartsWith(wardId, StringComparison.OrdinalIgnoreCase)
                || wardId.StartsWith(pending.WardId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pending.WardId, wardId, StringComparison.OrdinalIgnoreCase)))
        {
            WardApprovalDecision decision = allow ? WardApprovalDecision.Allow : WardApprovalDecision.Deny;
            if (wardCoordinator.TryCompletePending(decision))
            {
                state.Log.Append(
                    SessionLogEntryKind.Status,
                    allow ? $"Allowing ward {pending.WardId}…" : $"Denying ward {pending.WardId}…");
                return ShellDispatchResult.Continue;
            }
        }

        // Prefix match against active wards when the operator pasted a short id.
        if (wardId.Length < 36)
        {
            Result<WardDto[]> listed = await apiClient.GetWardsAsync(cancellationToken).ConfigureAwait(false);
            if (listed.IsSuccess)
            {
                WardDto[] matches = (listed.Value ?? [])
                    .Where(w => w.WardId.StartsWith(wardId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 1)
                {
                    wardId = matches[0].WardId;
                }
                else if (matches.Length > 1)
                {
                    state.Log.Append(SessionLogEntryKind.Error, "Ambiguous ward id prefix; use the full id.");
                    return ShellDispatchResult.Continue;
                }
            }
        }

        Result<WardResolutionDto> resolve = await apiClient
            .ResolveWardAsync(wardId, allow, reason: null, cancellationToken)
            .ConfigureAwait(false);

        if (resolve.IsFailure)
        {
            state.Log.Append(SessionLogEntryKind.Error, resolve.Error.Message);
            return ShellDispatchResult.Continue;
        }

        state.Log.Append(
            SessionLogEntryKind.Status,
            allow ? $"Ward {wardId} allowed." : $"Ward {wardId} denied.");
        return ShellDispatchResult.Continue;
    }

    private async Task<string> FormatModelsAsync(CancellationToken cancellationToken)
    {
        Result<ModelInfoDto[]> result = await apiClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        ModelInfoDto[] models = result.Value ?? [];
        if (models.Length == 0)
        {
            return "No models configured.";
        }

        return string.Join(
            Environment.NewLine,
            models.Select(static m => $"- {m.Model} ({m.ProviderName})"));
    }

    private async Task<string> FormatProvidersAsync(CancellationToken cancellationToken)
    {
        Result<ProviderInfoDto[]> result = await apiClient.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        ProviderInfoDto[] providers = result.Value ?? [];
        if (providers.Length == 0)
        {
            return "No providers configured.";
        }

        return string.Join(
            Environment.NewLine,
            providers.Select(static p => $"- {p.Name} ({p.Type})"));
    }

    private static string FormatMcp(CommandCenterState state)
    {
        if (state.McpServers.Count == 0)
        {
            return "No MCP servers reported.";
        }

        return string.Join(
            Environment.NewLine,
            state.McpServers.Select(static s => $"- {s.Name}: {s.State} tools={s.Tools.Length}"));
    }

    private async Task<string> FormatArsenalAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        Result<WorkspaceArsenalDto> result = await apiClient
            .GetWorkspaceArsenalAsync(new OptionalWorkspaceRequest(state.WorkingDirectory), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        WorkspaceArsenalDto dto = result.Value!;
        return string.Join(
            Environment.NewLine,
            [
                $"Spells: {dto.ActiveSpells.Count}",
                $"Native tools: {string.Join(", ", dto.NativeTools)}",
                $"MCP servers: {dto.McpServers.Count}",
            ]);
    }

    private async Task<string> FormatCampaignsAsync(
        string? offsetArgument,
        CancellationToken cancellationToken)
    {
        int offset = ParseListOffset(offsetArgument);

        Result<ListPageResult<CampaignDto>> result = await apiClient
            .GetCampaignsPageAsync(
                limit: TerminalListPageSize,
                offset: offset,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        CampaignDto[] items = result.Value?.Items ?? [];

        if (items.Length == 0)
        {
            return offset == 0
                ? "No campaigns."
                : $"No campaigns at offset {offset}. Restart with `/campaign list 0`.";
        }

        string body = string.Join(
            Environment.NewLine,
            items.Select(static c => $"- {c.Id.ToString("D")[..8]}  {c.Name}"));

        return AppendDisplayContinuation(
            body,
            "campaign",
            offset,
            items.Length,
            total: null,
            result.Value?.HasMore == true);
    }

    private async Task<string> FormatSpellsAsync(
        CommandCenterState state,
        string? cursor,
        CancellationToken cancellationToken)
    {
        Result<SpellCatalogPage> result = await apiClient
            .GetSpellCatalogPageAsync(
                workspace: state.WorkingDirectory,
                cursor: cursor,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        SpellCatalogPage page = result.Value;

        if (page.Items.Length == 0)
        {
            return "No spells found.";
        }

        string body = string.Join(
            Environment.NewLine,
            page.Items.Select(static spell => $"- {spell.Name}"));

        return AppendOpaqueDisplayContinuation(
            body,
            page.Items.Length,
            page.HasMore,
            page.NextCursor);
    }

    internal static string AppendOpaqueDisplayContinuation(
        string body,
        int shown,
        bool hasMore,
        string? nextCursor)
    {

        if (!hasMore
            || string.IsNullOrWhiteSpace(nextCursor))
        {

            return body;

        }

        return string.Join(
            Environment.NewLine,
            [
                body,
                string.Empty,
                $"Physical terminal-rendering boundary: showing {shown} items. Server state was not changed.",
                $"Continue with `/spell list {nextCursor}`.",
            ]);

    }

    private static int ParseListOffset(string? value) =>
        int.TryParse(value, out int offset) && offset >= 0
            ? offset
            : 0;

    internal static string AppendDisplayContinuation(
        string body,
        string command,
        int offset,
        int shown,
        int? total,
        bool hasMore)
    {
        if (!hasMore)
        {
            return body;
        }

        int nextOffset = offset + shown;

        string measured = total is { } totalCount
            ? $"{nextOffset} of {totalCount} items"
            : $"{shown} items from offset {offset}";

        return body
            + Environment.NewLine
            + $"Physical terminal-rendering boundary: showed {measured} in a {TerminalListPageSize}-line page. "
            + $"Server state was not changed; continue with `/{command} list {nextOffset}`.";
    }
}

internal enum ShellDispatchResult
{
    Continue,
    Exit,
}
