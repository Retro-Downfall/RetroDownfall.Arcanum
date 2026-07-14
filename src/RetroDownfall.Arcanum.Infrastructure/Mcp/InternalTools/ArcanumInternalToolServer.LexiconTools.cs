using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;


namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteScribeLexiconAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ScribeLexiconParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ScribeLexiconParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "scribe_lexicon argument deserialization failed.");

            return ToolError("Invalid arguments for scribe_lexicon.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Name))
        {
            return ToolError("scribe_lexicon requires a non-empty 'name'.");
        }

        if (args.Facts is null || args.Facts.Length == 0)
        {
            return ToolError("scribe_lexicon requires at least one 'facts' entry.");
        }

        string name = args.Name.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            Result<LexiconEntryDto> result = await lexicon
                .UpsertAsync(name, args.Type, args.Facts, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError(result.Error.Message);
            }

            LexiconEntryDto entry = result.Value;

            string text = $"Lexicon entry '{entry.Name}' ({entry.Type}) now holds {entry.Facts.Length} fact(s).";

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "scribe_lexicon failed for name {Name}.", name);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteDeleteLexiconAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        DeleteLexiconParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.DeleteLexiconParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "delete_lexicon argument deserialization failed.");

            return ToolError("Invalid arguments for delete_lexicon.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Name))
        {
            return ToolError("delete_lexicon requires a non-empty 'name'.");
        }

        string name = args.Name.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            Result<bool> result = await lexicon.DeleteByNameAsync(name, cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError(result.Error.Message);
            }

            string text = result.Value
                ? $"Lexicon entry '{name}' was removed."
                : $"Lexicon entry '{name}' did not exist; nothing was deleted.";

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "delete_lexicon failed for name {Name}.", name);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteSearchArchivesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        SearchArchivesParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.SearchArchivesParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "search_archives argument deserialization failed.");

            return ToolError("Invalid arguments for search_archives.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Query))
        {
            return ToolError("search_archives requires a non-empty 'query'.");
        }

        string query = args.Query.Trim();

        int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(_settings.ArchiveSearchMaxQueryLength);

        if (query.Length > maxQueryLen)
        {
            query = query[..maxQueryLen];
        }

        int maxResults = ArcanumSettingClamps.ArchiveSearchMaxResults(_settings.ArchiveSearchMaxResults);

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            string text = await repo
                .SearchArchivesAsync(query, maxResults, cancellationToken)
                .ConfigureAwait(false);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "search_archives failed for query {Query}.", query);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

}
