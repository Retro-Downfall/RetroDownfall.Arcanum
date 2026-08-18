using System.Globalization;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.TheForge;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

/// <summary>
/// Complete session lifecycle management over the authenticated local API.
/// </summary>
public sealed class SessionCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConsoleDispatcher dispatcher,
    IConfirmationPrompt confirmationPrompt,
    WatchCommands watchCommands,
    ICliResourceCatalog? resourceCatalog = null)
{

    public async Task<int> List(
        string? campaign,
        string? status,
        string? search,
        string? model,
        string? from,
        string? to,
        int? limit,
        CancellationToken cancellationToken = default)
    {

        if (!TryParseOptionalGuid(campaign, "--campaign", out Guid? campaignId)
            || !TryParseOptionalDate(from, "--from", out DateTimeOffset? fromDate)
            || !TryParseOptionalDate(to, "--to", out DateTimeOffset? toDate))
        {

            return 1;

        }

        SessionQueryRequest request = new(
            CampaignId: campaignId,
            Status: status,
            Search: search,
            Model: model,
            From: fromDate,
            To: toDate,
            Limit: limit);

        Result<SessionQueryResult> result = await apiClient
            .QuerySessionsAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out SessionQueryResult? page))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                page!.Summaries,
                ArcanumJsonContext.Default.SessionSummaryDtoArray);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Title"));

        table.AddColumn(themePalette.HeadingTableColumn("Campaign"));

        table.AddColumn(themePalette.HeadingTableColumn("Status"));

        table.AddColumn(themePalette.HeadingTableColumn("Entries"));

        table.AddColumn(themePalette.HeadingTableColumn("Updated"));

        foreach (SessionSummaryDto session in page!.Summaries)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(session.Title ?? "(untitled)"))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(session.CampaignId?.ToString("D") ?? "-"))),
                new Markup(themePalette.TextMarkup(Markup.Escape(session.Status))),
                new Markup(themePalette.MutedMarkup(session.EntryCount.ToString(CultureInfo.InvariantCulture))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(session.UpdatedAt.ToString("u", CultureInfo.InvariantCulture)))));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Show(
        string? identifier,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Task<Result<SessionDetailDto>> detailTask = apiClient.GetSessionAsync(resolution.Id, cancellationToken);

        Task<Result<SessionAttachmentDto[]>> attachmentsTask = apiClient.GetSessionAttachmentsAsync(resolution.Id, cancellationToken);

        await Task.WhenAll(detailTask, attachmentsTask).ConfigureAwait(false);

        Result<SessionDetailDto> detailResult = await detailTask.ConfigureAwait(false);

        Result<SessionAttachmentDto[]> attachmentsResult = await attachmentsTask.ConfigureAwait(false);

        if (!TryValue(detailResult, out SessionDetailDto? session)
            || !TryValue(attachmentsResult, out SessionAttachmentDto[]? attachments))
        {

            return 1;

        }

        SessionShowPayload payload = new(
            session!.Id,
            session.CampaignId,
            session.Title,
            session.Status,
            session.EntryCount,
            attachments!.Length,
            session.CreatedAt,
            session.UpdatedAt,
            session.Summary,
            session.TotalTokensUsed,
            session.TotalCostUsd,
            session.ForkedFromSessionId);

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(payload, CliJsonContext.Default.SessionShowPayload);

            return 0;

        }

        Table table = new Table().Border(TableBorder.None).HideHeaders();

        table.AddColumn(string.Empty);

        table.AddColumn(string.Empty);

        table.AddRow("Id:", Markup.Escape(payload.Id.ToString("D")));

        table.AddRow("Title:", Markup.Escape(payload.Title ?? "(untitled)"));

        table.AddRow("Status:", Markup.Escape(payload.Status));

        table.AddRow("Campaign:", Markup.Escape(payload.CampaignId?.ToString("D") ?? "-"));

        table.AddRow("Entries:", payload.EntryCount.ToString(CultureInfo.InvariantCulture));

        table.AddRow("Attachments:", payload.AttachmentCount.ToString(CultureInfo.InvariantCulture));

        table.AddRow("Tokens:", payload.TotalTokensUsed.ToString(CultureInfo.InvariantCulture));

        table.AddRow("Cost (USD):", payload.TotalCostUsd.ToString("0.########", CultureInfo.InvariantCulture));

        table.AddRow("Forked from:", Markup.Escape(payload.ForkedFromSessionId?.ToString("D") ?? "root"));

        table.AddRow("Updated:", Markup.Escape(payload.UpdatedAt.ToString("u", CultureInfo.InvariantCulture)));

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Entries(
        string? identifier,
        int? offset,
        int? limit,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<EntryDto[]> result = await apiClient
            .GetSessionEntriesAsync(resolution.Id, offset, limit, cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out EntryDto[]? entries))
        {

            return 1;

        }

        return WriteEntries(entries!);

    }

    public async Task<int> Watch(
        string? identifier,
        string? since,
        CancellationToken cancellationToken = default) =>
        await watchCommands.Session(
            identifier,
            since,
            new WatchCommandOptions(false, [], []),
            cancellationToken).ConfigureAwait(false);

    public async Task<int> Fork(
        string? identifier,
        string? title,
        string? upToEntry,
        string? campaign,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        if (!TryParseOptionalGuid(upToEntry, "--up-to-entry", out Guid? upToEntryId)
            || !TryParseOptionalGuid(campaign, "--campaign", out Guid? campaignId))
        {

            return 1;

        }

        Result<SessionDetailDto> result = await apiClient
            .ForkSessionAsync(
                resolution.Id,
                new ForkSessionRequest(title, upToEntryId, campaignId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out SessionDetailDto? fork))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(fork!, ArcanumJsonContext.Default.SessionDetailDto);

        }
        else
        {

            dispatcher.WritePayload($"Forked session {fork!.Id:D}.");

        }

        return 0;

    }

    public async Task<int> Rename(
        string? identifier,
        string? title,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(title))
        {

            WriteArgumentError("--title is required.");

            return 1;

        }

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<SessionDetailDto> result = await apiClient
            .UpdateSessionAsync(
                resolution.Id,
                new UpdateSessionRequest(title.Trim(), null),
                cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out SessionDetailDto? renamed))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(renamed!, ArcanumJsonContext.Default.SessionDetailDto);

        }
        else
        {

            dispatcher.WritePayload($"Renamed session {renamed!.Id:D} to {renamed.Title}.");

        }

        return 0;

    }

    public async Task<int> Archive(
        string? identifier,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result result = await apiClient
            .ArchiveSessionAsync(resolution.Id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            WriteError(result.Error);

            return 1;

        }

        dispatcher.WritePayload($"Archived session {resolution.Id:D}.");

        return 0;

    }

    public async Task<int> Export(
        string? identifier,
        string? format,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        SessionExportFormat exportFormat;

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {

            exportFormat = SessionExportFormat.Markdown;

        }
        else if (string.IsNullOrWhiteSpace(format)
            || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {

            exportFormat = SessionExportFormat.Json;

        }
        else
        {

            WriteArgumentError("--format must be 'json' or 'markdown'.");

            return 1;

        }

        Result<SessionExportResult> result = await apiClient
            .ExportSessionAsync(resolution.Id, exportFormat, cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out SessionExportResult? export))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(export!, ArcanumJsonContext.Default.SessionExportResult);

        }
        else
        {

            dispatcher.WritePayload(export!.Content);

        }

        return 0;

    }

    public async Task<int> Rest(
        string? identifier,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result result = await apiClient.RestAsync(resolution.Id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            WriteError(result.Error);

            return 1;

        }

        dispatcher.WritePayload($"Queued Campaign Log consolidation for session {resolution.Id:D}.");

        return 0;

    }

    public async Task<int> Attachments(
        string? identifier,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<SessionAttachmentDto[]> result = await apiClient
            .GetSessionAttachmentsAsync(resolution.Id, cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out SessionAttachmentDto[]? attachments))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                attachments!,
                ArcanumJsonContext.Default.SessionAttachmentDtoArray);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Id"));

        table.AddColumn(themePalette.HeadingTableColumn("File"));

        table.AddColumn(themePalette.HeadingTableColumn("Type"));

        table.AddColumn(themePalette.HeadingTableColumn("Bytes"));

        foreach (SessionAttachmentDto attachment in attachments!)
        {

            table.AddRow(
                Markup.Escape(attachment.Id.ToString("D")),
                Markup.Escape(attachment.OriginalFileName),
                Markup.Escape(attachment.MimeType),
                attachment.ByteLength.ToString(CultureInfo.InvariantCulture));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public Task<int> DeleteEntry(
        string? entry,
        string? session,
        CancellationToken cancellationToken = default) =>
        MutateEntry(
            entry,
            session,
            "delete",
            apiClient.DeleteSessionEntryAsync,
            cancellationToken);

    public Task<int> PinEntry(
        string? entry,
        string? session,
        CancellationToken cancellationToken = default) =>
        MutateEntry(
            entry,
            session,
            "pin",
            apiClient.PinSessionEntryAsync,
            cancellationToken);

    public Task<int> UnpinEntry(
        string? entry,
        string? session,
        CancellationToken cancellationToken = default) =>
        MutateEntry(
            entry,
            session,
            "unpin",
            apiClient.UnpinSessionEntryAsync,
            cancellationToken);

    public async Task<int> Compact(
        string? identifier,
        CancellationToken cancellationToken = default)
    {

        SessionResolution resolution = await ResolveSessionAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<CompactResult> result = await apiClient
            .CompactSessionAsync(resolution.Id, cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out CompactResult? compact))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(compact!, ArcanumJsonContext.Default.CompactResult);

        }
        else
        {

            dispatcher.WritePayload(
                $"Compacted session {resolution.Id:D}: {compact!.TokensBefore} -> {compact.TokensAfter} tokens; {compact.EntriesRemoved} entries removed.");

        }

        return 0;

    }

    /// <summary>
    /// Semantic search over Grimoire entries.
    /// </summary>
    public async Task<int> Divine(
        string query,
        int? limit = null,
        string? campaign = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(query))
        {

            WriteArgumentError("<QUERY> is required.");

            return 1;

        }

        if (!TryParseOptionalGuid(campaign, "--campaign", out Guid? campaignId))
        {

            return 1;

        }

        SemanticSearchRequest request = new(query.Trim(), campaignId, status, limit);

        Result<SemanticSearchResult> result = await apiClient
            .DivineSessionsAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!TryValue(result, out SemanticSearchResult? searchResult))
        {

            return 1;

        }

        SemanticSessionSearchResult[] hits = searchResult!.Results;

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                searchResult,
                ArcanumJsonContext.Default.SemanticSearchResult);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Session ID"));

        table.AddColumn(themePalette.HeadingTableColumn("Title"));

        table.AddColumn(themePalette.HeadingTableColumn("Role"));

        table.AddColumn(themePalette.HeadingTableColumn("Similarity"));

        table.AddColumn(themePalette.HeadingTableColumn("Created"));

        table.AddColumn(themePalette.HeadingTableColumn("Content Preview"));

        foreach (SemanticSessionSearchResult hit in hits)
        {

            string similarity = (hit.Similarity * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

            table.AddRow(
                Markup.Escape(hit.SessionId.ToString("D")[..8]),
                Markup.Escape(string.IsNullOrWhiteSpace(hit.SessionTitle) ? "(untitled)" : hit.SessionTitle),
                Markup.Escape(hit.EntryRole),
                Markup.Escape(similarity),
                Markup.Escape(hit.EntryCreatedAt.ToString("u", CultureInfo.InvariantCulture)),
                Markup.Escape(hit.EntryContentPreview));

        }

        AnsiConsole.Write(table);

        if (hits.Length == 0)
        {

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No sessions matched.")));

        }

        return 0;

    }

    private async Task<int> MutateEntry(
        string? entryIdentifier,
        string? sessionIdentifier,
        string action,
        Func<Guid, Guid, CancellationToken, Task<Result>> mutation,
        CancellationToken cancellationToken)
    {

        SessionResolution session = await ResolveSessionAsync(sessionIdentifier, cancellationToken).ConfigureAwait(false);

        if (!session.Success)
        {

            return session.Cancelled ? 0 : 1;

        }

        EntryResolution entry = await ResolveEntryAsync(
                session.Id,
                entryIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!entry.Success)
        {

            return entry.Cancelled ? 0 : 1;

        }

        if (string.Equals(action, "delete", StringComparison.Ordinal)
            && !await confirmationPrompt
                .PromptForConfirmationAsync(
                    $"Delete entry {entry.Id:D} from session {session.Id:D}?",
                    cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Entry deletion cancelled.");

            return 0;

        }

        Result result = await mutation(session.Id, entry.Id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            WriteError(result.Error);

            return 1;

        }

        dispatcher.WritePayload($"Entry {entry.Id:D} {action} operation completed.");

        return 0;

    }

    private async Task<SessionResolution> ResolveSessionAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (Guid.TryParse(identifier, out Guid parsedId))
        {

            return new SessionResolution(true, false, parsedId);

        }

        if (resourceCatalog is null)
        {

            WriteArgumentError("<SESSION> must be a valid GUID.");

            return default;

        }

        ResourceSelectionResult<SessionSummaryDto> selection = await resourceCatalog
            .SelectSessionAsync(identifier, cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return new SessionResolution(false, true, default);

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            WriteArgumentError(selection.Error ?? "Session selection failed.");

            return default;

        }

        return new SessionResolution(true, false, selection.Value!.Id);

    }

    private async Task<EntryResolution> ResolveEntryAsync(
        Guid sessionId,
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (Guid.TryParse(identifier, out Guid parsedId))
        {

            return new EntryResolution(true, false, parsedId);

        }

        if (resourceCatalog is null)
        {

            WriteArgumentError("<ENTRY> must be a valid GUID.");

            return default;

        }

        ResourceSelectionResult<EntryDto> selection = await resourceCatalog
            .SelectSessionEntryAsync(sessionId, identifier, cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return new EntryResolution(false, true, default);

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            WriteArgumentError(selection.Error ?? "Entry selection failed.");

            return default;

        }

        return new EntryResolution(true, false, selection.Value!.Id);

    }

    private int WriteEntries(EntryDto[] entries)
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(entries, ArcanumJsonContext.Default.EntryDtoArray);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Id"));

        table.AddColumn(themePalette.HeadingTableColumn("Role"));

        table.AddColumn(themePalette.HeadingTableColumn("Pinned"));

        table.AddColumn(themePalette.HeadingTableColumn("Created"));

        table.AddColumn(themePalette.HeadingTableColumn("Content"));

        foreach (EntryDto entry in entries)
        {

            table.AddRow(
                Markup.Escape(entry.Id.ToString("D")),
                Markup.Escape(entry.Role),
                entry.IsPinned ? "yes" : "no",
                Markup.Escape(entry.CreatedAt.ToString("u", CultureInfo.InvariantCulture)),
                Markup.Escape(entry.Content.ReplaceLineEndings(" ")));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    private bool TryValue<T>(Result<T> result, out T? value)
    {

        if (result.IsFailure)
        {

            WriteError(result.Error);

            value = default;

            return false;

        }

        value = result.Value;

        return true;

    }

    private void WriteError(Error error) =>
        CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(error));

    private void WriteArgumentError(string message) =>
        CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(message)));

    private bool TryParseOptionalGuid(
        string? value,
        string option,
        out Guid? parsed)
    {

        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {

            return true;

        }

        if (Guid.TryParse(value, out Guid id))
        {

            parsed = id;

            return true;

        }

        WriteArgumentError($"{option} must be a valid GUID.");

        return false;

    }

    private bool TryParseOptionalDate(
        string? value,
        string option,
        out DateTimeOffset? parsed)
    {

        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {

            return true;

        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {

            parsed = timestamp;

            return true;

        }

        WriteArgumentError($"{option} must be an ISO-8601 timestamp.");

        return false;

    }

    private readonly record struct SessionResolution(
        bool Success,
        bool Cancelled,
        Guid Id);

    private readonly record struct EntryResolution(
        bool Success,
        bool Cancelled,
        Guid Id);

}
