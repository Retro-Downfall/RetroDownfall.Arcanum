using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;


namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteReadLoreAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ReadLoreParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ReadLoreParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "read_lore argument deserialization failed.");

            return ToolError("Invalid arguments for read_lore.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key))
        {
            return ToolError("read_lore requires a non-empty 'key'.");
        }

        string key = args.Key.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            string? value = await repo.ReadLoreAsync(key, cancellationToken).ConfigureAwait(false);

            string text = value is null ? "Key not found." : value;

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
            _logger?.LogError(ex, "read_lore failed for key {Key}.", key);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteScribeLoreAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ScribeLoreParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ScribeLoreParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "scribe_lore argument deserialization failed.");

            return ToolError("Invalid arguments for scribe_lore.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key) || string.IsNullOrWhiteSpace(args.Value))
        {
            return ToolError("scribe_lore requires non-empty 'key' and 'value'.");
        }

        string key = args.Key.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            await repo.ScribeLoreAsync(key, args.Value, cancellationToken).ConfigureAwait(false);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = $"Lore saved for key '{key}'." },
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
            _logger?.LogError(ex, "scribe_lore failed for key {Key}.", key);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteDeleteLoreAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        DeleteLoreParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.DeleteLoreParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "delete_lore argument deserialization failed.");

            return ToolError("Invalid arguments for delete_lore.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key))
        {
            return ToolError("delete_lore requires a non-empty 'key'.");
        }

        string key = args.Key.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            bool removed = await repo.DeleteLoreAsync(key, cancellationToken).ConfigureAwait(false);

            string text = removed
                ? $"Key '{key}' was removed from lore."
                : $"Key '{key}' did not exist; nothing was deleted.";

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
            _logger?.LogError(ex, "delete_lore failed for key {Key}.", key);

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
