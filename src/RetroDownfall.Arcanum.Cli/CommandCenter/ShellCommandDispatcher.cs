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
using RetroDownfall.Arcanum.Core.TheForge;
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

            case ShellCommandKind.Clear:
                state.Log.Clear();
                state.Incantations.Clear();
                state.Log.Append(SessionLogEntryKind.Status, "Log cleared.");
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Help:
                state.Log.Append(SessionLogEntryKind.Command, BuildHelpText());
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

            case ShellCommandKind.ContextList:
                return await ListContextPinsAsync(state, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.ContextPin:
                return await PinContextAsync(
                    state, parsed.Argument, parsed.SecondaryArgument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.ContextUnpin:
                return await UnpinContextAsync(state, parsed.Argument, cancellationToken).ConfigureAwait(false);

            case ShellCommandKind.Status:
                state.Log.Append(SessionLogEntryKind.Command, BuildStatusText(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Doctor:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await BuildDoctorTextAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.ModelList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatModelsAsync(cancellationToken).ConfigureAwait(false));
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

            case ShellCommandKind.Arsenal:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatArsenalAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.CampaignList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatCampaignsAsync(cancellationToken).ConfigureAwait(false));
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

            case ShellCommandKind.SessionNew:
                if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? denyNew))
                {
                    if (denyNew is not null)
                    {
                        state.Log.Append(SessionLogEntryKind.Status, CommandCenterSessionMutationGuard.GeneratingDenyMessage);
                    }

                    return ShellDispatchResult.Continue;
                }

                sessionWorkspace.StartNewSession(state);
                return ShellDispatchResult.Continue;

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
                    await FormatSpellsAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Tools:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatToolsAsync(state, cancellationToken).ConfigureAwait(false));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.Mana:
                state.Log.Append(SessionLogEntryKind.Command, FormatMana(state));
                return ShellDispatchResult.Continue;

            case ShellCommandKind.WardList:
                state.Log.Append(
                    SessionLogEntryKind.Command,
                    await FormatWardsAsync(cancellationToken).ConfigureAwait(false));
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

        state.Log.Append(SessionLogEntryKind.Command, FormatAttachmentsList(result.Value ?? []));
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

    private static bool TryRequireSession(CommandCenterState state, out Guid sessionId)
    {
        if (state.SessionId is { } id)
        {
            sessionId = id;
            return true;
        }

        sessionId = default;
        state.Log.Append(
            SessionLogEntryKind.Error,
            "No active session. Send a message or `/session resume <id>` before using /attachments.");
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
            lines.Add(
                $"  {row.LogicalKey}  v{row.Version}  {row.Kind}  {row.ByteLength} B  {row.OriginalFileName}  {row.Id:D}");
        }

        lines.Add("");
        lines.Add("Re-attach: /attachments add <logicalName> [vN]");
        lines.Add("Reveal:    /attachments reveal <logicalName> [vN]");
        return string.Join(Environment.NewLine, lines);
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
            state.Log.Append(
                SessionLogEntryKind.Error,
                "Kinds: file, directorySnapshot, symbolRange, sessionEntry, attachment, url, diagnostic.");
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
            state.Log.Append(SessionLogEntryKind.Error, "Usage: /context unpin <pin-id>");
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
            state.Log.Append(SessionLogEntryKind.Error, "Usage: /session resume <guid>");
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
        lines.Add("Resume: /session resume <guid>  or  Ctrl+O then Enter");
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

    private static string BuildHelpText() =>
        string.Join(
            Environment.NewLine,
            [
                "Command Center commands:",
                "  /help                 Show this help",
                "  /keys                 Keyboard shortcuts",
                "  /status               Session / serve summary",
                "  /doctor               Compact health check",
                "  /clear                Clear the session log",
                "  /mana                 Current mana counters",
                "  /tools                Native tools (arsenal)",
                "  /model list           List models",
                "  /provider list        List providers",
                "  /mcp                  MCP server status",
                "  /arsenal              Workspace arsenal",
                "  /campaign list        List campaigns",
                "  /session list         Refresh + list sessions",
                "  /session new          Start a New Session",
                "  /session resume <id>  Load transcript + continue that session",
                "  /fork                 Fork the entire active session and open the branch",
                "  /fork confirm         Confirm a large attachment-bearing fork",
                "  /fork alternative     Fork before the selected answer and regenerate",
                "  /fork at <entry-id>   Fork through a transcript entry and open the branch",
                "  /fork at              Fork through the selected transcript entry",
                "  /branch parent|child  Open the visible parent or newest child branch",
                "  /attach <path>        Stage a local text file or image (Scrying) for the next turn",
                "  @path                 Inline stage (text attach or Scrying image) in a message",
                "  /attachments          List bound session attachments",
                "  /attachments add <name> [vN]  Stage a prior attachment as AttachmentReferences",
                "  /attachments reveal <name> [vN]  Reveal attachment file in OS file manager",
                "  /context              Inspect persistent session context pins",
                "  /context pin <kind> <target>  Pin file/directorySnapshot/symbolRange/sessionEntry/attachment/url/diagnostic",
                "  /context unpin <id>   Remove a context pin",
                "  /spell list           List spells",
                "  /ward list            List open wards",
                "  /ward allow [id]      Allow pending ward (id optional when prompted)",
                "  /ward deny [id]       Deny pending ward (id optional when prompted)",
                "  /exit | /quit         Leave Command Center",
                "",
                "Ctrl+K opens the command palette. F1 shows shortcuts.",
                "Plain text starts a chat turn.",
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
                "  Tab/S-Tab     Cycle focus (Composer→Sessions→Transcript→Incantations)",
                "  Enter         Newline (composer) / resume selected session",
                "  Ctrl+Enter    Send (composer)",
                "  ↑↓ / j k      Move session selection",
                "  PgUp/PgDn     Scroll transcript",
                "  Home/End      Jump transcript top / bottom",
                "  Esc           Close overlay / focus composer",
                "  Ctrl+C        Cancel turn / clear composer / quit hint",
                "  Ctrl+Q        Quit (confirm if generating)",
                "  /keys         Show this help",
                "  /exit         Leave Command Center",
            ]);

    private static string FormatMana(CommandCenterState state)
    {
        if (state.LastContextBreakdown is { } context)
        {
            string reported = context.ProviderReportedInputTokens is { } providerReported
                ? $", provider reported={providerReported}, variance={context.EstimationVarianceTokens ?? 0}"
                : ", provider reported=unavailable";
            return $"Mana context: input={context.InputTokens}, reserved={context.ReservedTokens}, "
                + $"total={context.TotalTokens}, quality={context.OverallClassification}, "
                + $"profile={context.Profile.ProfileId}{reported}.";
        }

        if (state.ManaLimit is > 0)
        {
            return $"Mana: {state.ManaUsed ?? 0} / {state.ManaLimit} (session counters in Command Center are best-effort).";
        }

        if (state.ManaUsed is { } used)
        {
            return $"Mana used (last known): {used}";
        }

        return "Mana: no counters yet this session. Full report: `arcanum chat` then `/mana`.";
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

    private async Task<string> FormatWardsAsync(CancellationToken cancellationToken)
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

        return string.Join(
            Environment.NewLine,
            wards.Take(50).Select(static w =>
            {
                string id = w.WardId.Length > 8 ? w.WardId[..8] : w.WardId;
                return $"- {id}  {w.ToolName}  expires={w.ExpiresAt:u}";
            }));
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

    private async Task<string> FormatCampaignsAsync(CancellationToken cancellationToken)
    {
        Result<ListPageResult<CampaignDto>> result = await apiClient
            .GetCampaignsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        CampaignDto[] items = result.Value?.Items ?? [];
        if (items.Length == 0)
        {
            return "No campaigns.";
        }

        return string.Join(
            Environment.NewLine,
            items.Take(50).Select(static c => $"- {c.Id.ToString("D")[..8]}  {c.Name}"));
    }

    private async Task<string> FormatSpellsAsync(CommandCenterState state, CancellationToken cancellationToken)
    {
        Result<SpellSummary[]> result = await apiClient
            .GetSpellsAsync(workspace: state.WorkingDirectory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.Message;
        }

        SpellSummary[] spells = result.Value ?? [];
        if (spells.Length == 0)
        {
            return "No spells found.";
        }

        return string.Join(
            Environment.NewLine,
            spells.Take(50).Select(static s => $"- {s.Name}"));
    }
}

internal enum ShellDispatchResult
{
    Continue,
    Exit,
}
