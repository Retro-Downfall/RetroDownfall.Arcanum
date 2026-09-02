using System.Globalization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

/// <summary>
/// Saga long-term associative memory (requires arcanum serve).
/// </summary>
public sealed class SagaCommands(ArcanumApiClient apiClient, IThemePalette themePalette, IConfirmationPrompt confirmationPrompt)
{

    private const int ContentPreviewChars = 80;

    /// <summary>
    /// Paginated listing of Saga memories (GET /api/saga).
    /// </summary>
    /// <param name="query">Free-text query.</param>
    /// <param name="session">Filter by session GUID.</param>
    /// <param name="limit">Maximum number of memories to return.</param>
    /// <param name="offset">Pagination offset.</param>
    public async Task<int> List(
        string? query = null,
        string? session = null,
        int? limit = null,
        int? offset = null,
        CancellationToken cancellationToken = default)
    {

        Guid? sessionId = null;

        if (!string.IsNullOrWhiteSpace(session))
        {

            if (!CliArgReader.TryParseGuid(session, out Guid parsedSessionId))
            {

                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--session must be a valid GUID.")));

                return (int)CliExitCode.ConfigurationError;

            }

            sessionId = parsedSessionId;

        }

        Result<SagaMemoryDto[]> result = await apiClient
            .SagaListAsync(query, sessionId, limit, offset, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        SagaMemoryDto[] memories = result.Value;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Id")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Content")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Session")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Source")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Created")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Scope")));

        foreach (SagaMemoryDto memory in memories)
        {

            string shortId = memory.Id.Length > 8 ? memory.Id[..8] : memory.Id;

            string preview = memory.Content.Length > ContentPreviewChars
                ? string.Concat(memory.Content.AsSpan(0, Utf8Truncation.SafeCharSliceLength(memory.Content, ContentPreviewChars)), "...")
                : memory.Content;

            string sessionText = memory.SessionId is { } sid ? sid.ToString("D")[..8] : "-";

            table.AddRow(
                new Markup(themePalette.MutedMarkup(Markup.Escape(shortId))),
                new Markup(themePalette.TextMarkup(Markup.Escape(preview))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(sessionText))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(memory.Source ?? "-"))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(memory.CreatedAt.ToString("u", CultureInfo.InvariantCulture)))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(DescribeScope(memory)))));

        }

        AnsiConsole.Write(table);

        if (memories.Length == 0)
        {

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No Saga memories found.")));

        }

        return 0;

    }

    /// <summary>
    /// Semantic search over Saga memories (POST /api/saga/divine).
    /// </summary>
    /// <param name="query">Search query text.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="session">
    /// The session to search as. Its Campaign binding decides the scope, so this is a request to see
    /// what that session's turns would recall rather than a way to name a Campaign directly.
    /// </param>
    public async Task<int> Divine(
        string query,
        int? limit = null,
        string? session = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(query))
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("<QUERY> is required.")));

            return (int)CliExitCode.ConfigurationError;

        }

        Guid? sessionId = null;

        if (!string.IsNullOrWhiteSpace(session))
        {

            if (!Guid.TryParse(session, out Guid parsedSessionId))
            {

                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--session must be a GUID.")));

                return (int)CliExitCode.ConfigurationError;

            }

            sessionId = parsedSessionId;

        }

        SagaSearchRequest request = new(query.Trim(), limit, sessionId);

        Result<SagaSearchResult> result = await apiClient.SagaDivineAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        SagaMemoryDto[] memories = result.Value.Memories;

        float[] similarities = result.Value.Similarities;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Memory")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Similarity")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Created")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Session")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Scope")));

        for (int i = 0; i < memories.Length; i++)
        {

            SagaMemoryDto memory = memories[i];

            float similarity = i < similarities.Length ? similarities[i] : 0f;

            string similarityPercent = (similarity * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

            string sessionText = memory.SessionId is { } sid ? sid.ToString("D")[..8] : "-";

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(memory.Content))),
                new Markup(themePalette.HighlightMarkup(Markup.Escape(similarityPercent))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(memory.CreatedAt.ToString("u", CultureInfo.InvariantCulture)))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(sessionText))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(DescribeScope(memory)))));

        }

        AnsiConsole.Write(table);

        if (memories.Length == 0)
        {

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No Saga memories matched.")));

        }

        return 0;

    }

    /// <summary>
    /// Delete a single Saga memory (DELETE /api/saga/{id}).
    /// </summary>
    /// <param name="id">Saga memory ID.</param>
    public async Task<int> Delete(string id, CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
                .PromptForConfirmationAsync($"Delete Saga memory '{id}'?", cancellationToken)
                .ConfigureAwait(false))
        {

            CliErrorOutput.WriteMarkupLine(themePalette.MutedMarkup(Markup.Escape("Saga memory deletion cancelled.")));

            return 0;

        }

        Result result = await apiClient.SagaDeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape($"Saga memory '{id}' was forgotten.")));

        return 0;

    }

    /// <summary>
    /// Aggregate summary of Saga memory storage (GET /api/saga/stats).
    /// </summary>
    public async Task<int> Stats(CancellationToken cancellationToken)
    {

        Result<SagaStats> result = await apiClient.SagaStatsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        SagaStats stats = result.Value;

        Table table = new();

        table.HideHeaders();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Metric")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Value")));

        table.AddRow(
            new Markup(themePalette.MutedMarkup(Markup.Escape("Total memories"))),
            new Markup(themePalette.HighlightMarkup(Markup.Escape(stats.TotalCount.ToString(CultureInfo.InvariantCulture)))));

        table.AddRow(
            new Markup(themePalette.MutedMarkup(Markup.Escape("Sessions represented"))),
            new Markup(themePalette.TextMarkup(Markup.Escape(stats.SessionCount.ToString(CultureInfo.InvariantCulture)))));

        table.AddRow(
            new Markup(themePalette.MutedMarkup(Markup.Escape("Oldest memory"))),
            new Markup(themePalette.TextMarkup(Markup.Escape(
                stats.OldestCreatedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-"))));

        table.AddRow(
            new Markup(themePalette.MutedMarkup(Markup.Escape("Newest memory"))),
            new Markup(themePalette.TextMarkup(Markup.Escape(
                stats.NewestCreatedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-"))));

        AnsiConsole.Write(new Panel(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Saga (Associative Memory)"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });

        return 0;

    }

    /// <summary>
    /// Which Campaign owns a memory, in one short cell.
    /// </summary>
    /// <remarks>
    /// The two unresolved kinds are named rather than blanked. "unresolved" is an operator's cue that a
    /// Session binding needs resolving before that memory can be recalled anywhere, and a blank cell
    /// would read as "installation-scoped" - the one thing it is not.
    /// </remarks>
    private static string DescribeScope(SagaMemoryDto memory) =>
        memory.ScopeKind switch
        {

            SagaMemoryScopeKind.Campaign =>
                memory.ScopeCampaignId is { } campaignId ? campaignId.ToString("D")[..8] : "campaign",

            SagaMemoryScopeKind.Global => "global",

            SagaMemoryScopeKind.LegacyUnresolved => "unresolved",

            _ => "unclassified",

        };

}
