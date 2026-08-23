using System.Globalization;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Memory;

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

        return 0;

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
