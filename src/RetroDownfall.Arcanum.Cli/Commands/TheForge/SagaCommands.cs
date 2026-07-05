using System.Globalization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

/// <summary>
/// RAG Phase 4 — <c>arcanum saga list</c>: paginated listing of Saga memories (GET /api/saga).
/// </summary>
public sealed class SagaListCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SagaListCommand.Settings>
{

    private const int ContentPreviewChars = 80;

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Guid? sessionId = null;

        if (!string.IsNullOrWhiteSpace(settings.Session))
        {

            if (!CliArgReader.TryParseGuid(settings.Session, out Guid parsedSessionId))
            {

                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--session must be a valid GUID.")));

                return 1;

            }

            sessionId = parsedSessionId;

        }

        Result<SagaMemoryDto[]> result = await apiClient
            .SagaListAsync(settings.Query, sessionId, settings.Limit, settings.Offset, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        SagaMemoryDto[] memories = result.Value;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Id")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Content")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Session")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Source")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Created")));

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
                new Markup(themePalette.MutedMarkup(Markup.Escape(memory.CreatedAt.ToString("u", CultureInfo.InvariantCulture)))));

        }

        AnsiConsole.Write(table);

        if (memories.Length == 0)
        {

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No Saga memories found.")));

        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--query <QUERY>")]
        public string? Query { get; init; }

        [CommandOption("--session <SESSION>")]
        public string? Session { get; init; }

        [CommandOption("--limit <LIMIT>")]
        public int? Limit { get; init; }

        [CommandOption("--offset <OFFSET>")]
        public int? Offset { get; init; }

    }

}

/// <summary>
/// RAG Phase 4 — <c>arcanum saga divine &lt;QUERY&gt;</c>: semantic search over Saga memories
/// (POST /api/saga/divine).
/// </summary>
public sealed class SagaDivineCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SagaDivineCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Query))
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<QUERY> is required.")));

            return 1;

        }

        SagaSearchRequest request = new(settings.Query.Trim(), settings.Limit);

        Result<SagaSearchResult> result = await apiClient.SagaDivineAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        SagaMemoryDto[] memories = result.Value.Memories;

        float[] similarities = result.Value.Similarities;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Memory")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Similarity")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Created")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Session")));

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
                new Markup(themePalette.MutedMarkup(Markup.Escape(sessionText))));

        }

        AnsiConsole.Write(table);

        if (memories.Length == 0)
        {

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No Saga memories matched.")));

        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<QUERY>")]
        public required string Query { get; init; }

        [CommandOption("--limit <LIMIT>")]
        public int? Limit { get; init; }

    }

}

/// <summary>
/// RAG Phase 4 — <c>arcanum saga delete &lt;ID&gt;</c>: deletes a single Saga memory
/// (DELETE /api/saga/{id}).
/// </summary>
public sealed class SagaDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SagaDeleteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result result = await apiClient.SagaDeleteAsync(settings.Id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape($"Saga memory '{settings.Id}' was forgotten.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

/// <summary>
/// RAG Phase 4 — <c>arcanum saga stats</c>: aggregate summary of Saga memory storage
/// (GET /api/saga/stats).
/// </summary>
public sealed class SagaStatsCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        Result<SagaStats> result = await apiClient.SagaStatsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

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

}
