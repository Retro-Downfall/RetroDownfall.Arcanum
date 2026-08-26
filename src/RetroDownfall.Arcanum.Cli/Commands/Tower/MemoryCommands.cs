using System.Globalization;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

public sealed class MemoryCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConsoleDispatcher dispatcher,
    IConfirmationPrompt confirmationPrompt,
    ICliResourceCatalog? resourceCatalog = null)
{

    public async Task<int> Status(
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution resolution = await ResolveOptionalSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<MemoryStatusDto> result = await apiClient
            .GetMemoryStatusAsync(resolution.Id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.MemoryStatusDto);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Source"));

        table.AddColumn(themePalette.HeadingTableColumn("Enabled"));

        table.AddColumn(themePalette.HeadingTableColumn("Count"));

        table.AddColumn(themePalette.HeadingTableColumn("Scope"));

        table.AddColumn(themePalette.HeadingTableColumn("Retention"));

        foreach (MemoryStoreStatusDto store in result.Value.Stores)
        {

            table.AddRow(
                Markup.Escape(store.Name),
                store.Enabled ? "yes" : "no",
                store.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(store.Scope),
                Markup.Escape(store.Retention));

        }

        AnsiConsole.Write(table);

        WriteCampaignScope(result.Value.CampaignScope);

        WriteCovenantStatus(result.Value.Covenant);

        return 0;

    }

    /// <summary>
    /// States which Campaign scope a turn on this session would draw memory from, before one runs.
    /// </summary>
    /// <remarks>
    /// Printed even when nothing is narrowed, because "every memory on the installation is a candidate"
    /// is the fact an operator most needs stated plainly, and silence would read as though scoping were
    /// in force.
    /// </remarks>
    private void WriteCampaignScope(MemoryCampaignScopeDto? scope)
    {

        if (scope is null)
        {

            return;

        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape(scope.Detail)));

    }

    /// <summary>
    /// Renders the Covenant block beside the other memory sources.
    /// </summary>
    /// <remarks>
    /// Counts and ceilings only — the block carries no content, which is what makes it safe on a
    /// surface an operator reaches without protected read authority.
    ///
    /// <para>An installation with no Covenant arm prints nothing rather than a row of zeroes. A zero
    /// is a measurement, and the honest answer for something never measured is absence.</para>
    /// </remarks>
    private void WriteCovenantStatus(CovenantStatusDto? covenant)
    {

        if (covenant is null)
        {

            return;

        }

        dispatcher.WritePayload(
            $"Covenant: {(covenant.Enabled ? "enabled" : "disabled")}, "
            + $"{(covenant.Available ? "available" : "unavailable")}"
            + (covenant.DegradationCode is { Length: > 0 } code ? $" ({code})" : string.Empty));

        // An unread census is reported as unread. Every count and byte total below it is zero in that
        // case, and "you have no standing preferences" is the one sentence this surface must never say
        // about an installation nobody counted.
        if (covenant.Census is not CovenantCensusReadState.Read)
        {

            dispatcher.WritePayload(
                $"  Covenant entries were not counted ({covenant.Census}). This is not a report that you have none.");

            return;

        }

        if (covenant.Counts.Length == 0)
        {

            dispatcher.WritePayload("  No Covenant entries.");

            return;

        }

        Table covenantTable = new();

        covenantTable.AddColumn(themePalette.HeadingTableColumn("Scope"));

        covenantTable.AddColumn(themePalette.HeadingTableColumn("Lane"));

        covenantTable.AddColumn(themePalette.HeadingTableColumn("State"));

        covenantTable.AddColumn(themePalette.HeadingTableColumn("Count"));

        foreach (CovenantScopeCountDto row in covenant.Counts)
        {

            covenantTable.AddRow(
                row.Scope.ToString(),
                row.Lane.ToString(),
                row.Lifecycle.ToString(),
                row.Count.ToString(CultureInfo.InvariantCulture));

        }

        AnsiConsole.Write(covenantTable);

        // The two Campaign figures are the largest single Campaign's, which is what makes them
        // comparable to the ceiling beside them. Printing installation-wide sums here read as nearly
        // full whenever enough Campaigns each held a little.
        dispatcher.WritePayload(
            $"  Rendered bytes — Global Confirmed {covenant.GlobalConfirmedRenderedBytes}, "
            + $"largest Campaign Confirmed {covenant.MaxCampaignConfirmedRenderedBytes}, "
            + $"largest Campaign Proposed {covenant.MaxCampaignProposedRenderedBytes} "
            + $"(ceiling {covenant.RenderedByteCeilingPerSection} per section)");

    }

    public async Task<int> Sources(
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution resolution = await ResolveOptionalSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<MemorySourcesDto> result = await apiClient
            .GetMemorySourcesAsync(resolution.Id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.MemorySourcesDto);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Source"));

        table.AddColumn(themePalette.HeadingTableColumn("Scope"));

        table.AddColumn(themePalette.HeadingTableColumn("Provenance"));

        table.AddColumn(themePalette.HeadingTableColumn("Retention"));

        table.AddColumn(themePalette.HeadingTableColumn("Count"));

        foreach (MemorySourceDto source in result.Value.Sources)
        {

            table.AddRow(
                Markup.Escape(source.Name),
                Markup.Escape(source.Scope),
                Markup.Escape(source.Provenance),
                Markup.Escape(source.Retention),
                source.Count.ToString(CultureInfo.InvariantCulture));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Search(
        string query,
        string? scope,
        string? sessionIdentifier,
        string? workspaceId,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(query))
        {

            dispatcher.WriteDiagnostic("<QUERY> is required.");

            return 1;

        }

        if (!Enum.TryParse(scope ?? "all", true, out MemorySearchScope parsedScope))
        {

            dispatcher.WriteDiagnostic(
                "--scope must be one of: session, attachments, workspace, saga, lexicon, all.");

            return 1;

        }

        SessionResolution resolution = await ResolveOptionalSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        MemorySearchRequest request = new(
            query.Trim(),
            parsedScope,
            resolution.Id,
            workspaceId);

        Result<MemorySearchResponse> result = await apiClient
            .SearchMemoryAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.MemorySearchResponse);

            return 0;

        }

        dispatcher.WritePayload($"Scope: {result.Value.Scope.ToString().ToLowerInvariant()}");

        // Reported once, from the first result that carries it: the Campaign scope is a property of the
        // search, not of a row, and repeating it per match would bury it.
        if (result.Value.Results
                .Select(static match => match.CampaignScope)
                .FirstOrDefault(static campaignScope => campaignScope is not null) is { } searchScope)
        {

            dispatcher.WritePayload($"Campaign scope: {searchScope.Detail}");

        }

        foreach (MemorySearchResultDto match in result.Value.Results)
        {

            dispatcher.WritePayload(
                $"[{match.Scope.ToString().ToLowerInvariant()}] {match.Title}");

            dispatcher.WritePayload(
                $"  {match.Content.ReplaceLineEndings(" ")}");

            dispatcher.WritePayload($"  Provenance: {match.Provenance}");

            dispatcher.WritePayload($"  Retention: {match.Retention}");

        }

        if (result.Value.Results.Length == 0)
        {

            dispatcher.WritePayload("No memory matched.");

        }

        return 0;

    }

    public async Task<int> Explain(
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution resolution = await ResolveOptionalSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<MemoryExplainDto> result = await apiClient
            .ExplainMemoryAsync(resolution.Id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.MemoryExplainDto);

            return 0;

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Source"));

        table.AddColumn(themePalette.HeadingTableColumn("Next turn"));

        table.AddColumn(themePalette.HeadingTableColumn("Why"));

        table.AddColumn(themePalette.HeadingTableColumn("Retention"));

        foreach (MemoryEligibilityDto source in result.Value.Sources)
        {

            table.AddRow(
                Markup.Escape(source.Name),
                source.Eligible ? "eligible" : "not eligible",
                Markup.Escape(source.Reason),
                Markup.Escape(source.Retention));

        }

        AnsiConsole.Write(table);

        WriteCampaignScope(result.Value.CampaignScope);

        return 0;

    }

    public Task<int> LexiconList(CancellationToken cancellationToken) =>
        WriteLexiconList(null, cancellationToken);

    public Task<int> LexiconSearch(
        string query,
        CancellationToken cancellationToken) =>
        WriteLexiconList(query, cancellationToken);

    public async Task<int> LexiconShow(
        string name,
        CancellationToken cancellationToken)
    {

        Result<LexiconEntryDto> result = await apiClient
            .GetLexiconAsync(name, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.LexiconEntryDto);

            return 0;

        }

        WriteLexiconEntry(result.Value);

        return 0;

    }

    public async Task<int> LexiconDelete(
        string name,
        CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Delete Lexicon entity '{name}'? This does not delete session, attachment, Saga, workspace, or Lore data.",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Lexicon deletion cancelled.");

            return 0;

        }

        Result result = await apiClient
            .DeleteLexiconAsync(name, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        dispatcher.WritePayload($"Lexicon entity '{name}' was deleted.");

        return 0;

    }

    private async Task<int> WriteLexiconList(
        string? query,
        CancellationToken cancellationToken)
    {

        Result<LexiconListDto> result = await apiClient
            .ListLexiconAsync(query, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result.Value.Entries,
                ArcanumJsonContext.Default.LexiconEntryDtoArray);

            return 0;

        }

        foreach (LexiconEntryDto entry in result.Value.Entries)
        {

            WriteLexiconEntry(entry);

        }

        if (result.Value.Entries.Length == 0)
        {

            dispatcher.WritePayload("No Lexicon entities found.");

        }

        return 0;

    }

    private void WriteLexiconEntry(LexiconEntryDto entry)
    {

        string facts = entry.Facts.Length == 0
            ? "(no facts)"
            : string.Join("; ", entry.Facts);

        dispatcher.WritePayload(
            $"{entry.Name} [{entry.Type}] {facts} (updated {entry.UpdatedAt:u})");

    }

    private async Task<SessionResolution> ResolveOptionalSessionAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(identifier))
        {

            return new SessionResolution(true, false, null);

        }

        if (Guid.TryParse(identifier, out Guid parsedId))
        {

            return new SessionResolution(true, false, parsedId);

        }

        if (resourceCatalog is null)
        {

            dispatcher.WriteDiagnostic("<SESSION> must be a valid GUID.");

            return default;

        }

        ResourceSelectionResult<SessionSummaryDto> selection = await resourceCatalog
            .SelectSessionAsync(identifier, cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return new SessionResolution(false, true, null);

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            dispatcher.WriteDiagnostic(selection.Error ?? "Session selection failed.");

            return default;

        }

        return new SessionResolution(true, false, selection.Value!.Id);

    }

    private int WriteError(Error error)
    {

        dispatcher.WriteDiagnostic(error.Message);

        return 1;

    }

    private readonly record struct SessionResolution(
        bool Success,
        bool Cancelled,
        Guid? Id);

}
