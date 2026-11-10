using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Memory;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private async Task<McpToolsCallResultWire> ExecuteSearchMemoryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        SearchMemoryParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.SearchMemoryParams);
        }
        catch (JsonException)
        {
            return ToolError("Invalid arguments for search_memory.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Query))
        {
            return ToolError("search_memory requires a non-empty 'query'.");
        }

        MemorySearchScope requested = MemorySearchScope.All;

        if (args.Scope is not null && (!Enum.TryParse(args.Scope, true, out requested)
            || !Enum.IsDefined(requested) || int.TryParse(args.Scope, out _)))
        {
            return ToolError("search_memory scope must be session, attachments, workspace, saga, lexicon, or all.");
        }

        if (args.Limit is < 1 or > 10_000)
        {
            return ToolError("search_memory limit must be between 1 and 10000.");
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IMemorySearchService? search = scope.ServiceProvider.GetService<IMemorySearchService>();

            if (search is null)
            {
                return ToolError("Memory search is unavailable on this host.");
            }

            IMemoryScopeResolver resolver = scope.ServiceProvider.GetRequiredService<IMemoryScopeResolver>();

            if (await ResolveToolMemoryScopeAsync(resolver, cancellationToken).ConfigureAwait(false) is null)
            {
                return ToolError(UnresolvedMemoryScopeMessage);
            }

            long cap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(_settings.ToolOutputCapBytes, _maxJsonRpcLineBytes);

            // Reserve response metadata and one bounded excerpt per result before asking the sources.
            int limit = Math.Min(args.Limit ?? 20, (int)Math.Clamp(cap / 4096, 1, 10_000));

            var result = await search.SearchForInvocationAsync(new(args.Query, requested,
                SessionAttachmentToolAmbient.CurrentSessionId, WorkspaceId: _workspaceRoot, Limit: limit, Continuation: args.Continuation),
                cap, cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError($"{result.Error.Code}: {result.Error.Message}");
            }

            var formatted = MemorySearchToolResponseFormatter.Format(result.Value, cap);

            if (formatted.IsFailure) return ToolError($"{formatted.Error.Code}: {formatted.Error.Message}");

            MemorySearchResponse response = formatted.Value;

            string text = JsonSerializer.Serialize(response, ArcanumCoreJsonContext.Default.MemorySearchResponse);

            if (Encoding.UTF8.GetByteCount(text) > cap)
            {
                return ToolError("Memory search metadata exceeds one tool response allocation; request a smaller limit or narrower scope.");
            }

            return new McpToolsCallResultWire { Content = [new McpToolContentTextWire { Text = text }], IsError = false };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("search_memory failed ({ExceptionType}).", ex.GetType().Name);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

}
