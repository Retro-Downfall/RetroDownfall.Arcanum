using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteSearchWorkspaceAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {

            return gate;

        }

        SearchWorkspaceParams? args;

        try
        {

            args = JsonSerializer.Deserialize(arguments, _json.SearchWorkspaceParams);

        }
        catch (JsonException)
        {

            return ToolError("Invalid arguments for search_workspace.");

        }

        if (args is null)
        {

            return ToolError(
                "search_workspace requires 'pattern', 'mode', and 'caseSensitive'.");

        }

        WorkspaceSearchMode mode;

        if (string.Equals(args.Mode, "literal", StringComparison.OrdinalIgnoreCase))
        {

            mode = WorkspaceSearchMode.Literal;

        }
        else if (string.Equals(args.Mode, "regex", StringComparison.OrdinalIgnoreCase))
        {

            mode = WorkspaceSearchMode.Regex;

        }
        else
        {

            return BuildBoundedStructuredResult(
                new WorkspaceSearchToolResultEnvelope
                {
                    Status = "invalid_request",
                    Code = "invalid_mode",
                    Message = "Search mode must be 'literal' or 'regex'.",
                },
                _json.WorkspaceSearchToolResultEnvelope,
                isError: false);

        }

        WorkspaceSearchEngine engine = new(_workspaceSearchSettings);

        WorkspaceSearchToolResultEnvelope result = await engine.SearchAsync(
            _workspaceRoot!,
            new WorkspaceSearchRequest
            {
                Pattern = args.Pattern,
                Mode = mode,
                CaseSensitive = args.CaseSensitive,
                Root = args.Root,
                Globs = args.Globs ?? [],
                Extensions = args.Extensions ?? [],
            },
            cancellationToken).ConfigureAwait(false);

        return BuildBoundedStructuredResult(
            result,
            _json.WorkspaceSearchToolResultEnvelope,
            isError: false);

    }

    private McpToolsCallResultWire BuildBoundedStructuredResult<T>(
        T result,
        JsonTypeInfo<T> jsonTypeInfo,
        bool isError,
        Func<T, T>? fallbackCandidateFactory = null)
        where T : IStructuredToolResult<T>
    {

        long budget = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            _settings.ToolOutputCapBytes,
            _maxJsonRpcLineBytes);

        string serialized = JsonSerializer.Serialize(
            result,
            jsonTypeInfo);

        if (Encoding.UTF8.GetByteCount(serialized) <= budget)
        {

            return StructuredTextResult(serialized, isError);

        }

        int low = 0;
        int high = result.MaterializationItemCount - 1;
        string? bestFit = null;

        while (low <= high)
        {

            int retainedCount = low + ((high - low) / 2);

            T candidate =
                result.RetainLeadingItems(retainedCount);

            serialized = JsonSerializer.Serialize(
                candidate,
                jsonTypeInfo);

            if (Encoding.UTF8.GetByteCount(serialized) <= budget)
            {

                bestFit = serialized;
                low = retainedCount + 1;

            }
            else
            {

                high = retainedCount - 1;

            }

        }

        if (bestFit is not null)
        {

            return StructuredTextResult(bestFit, isError);

        }

        if (fallbackCandidateFactory is not null)
        {
            serialized = JsonSerializer.Serialize(
                fallbackCandidateFactory(result),
                jsonTypeInfo);

            if (Encoding.UTF8.GetByteCount(serialized) <= budget)
            {
                return StructuredTextResult(serialized, isError);
            }
        }

        string fallback = JsonSerializer.Serialize(
            new MinimalStructuredToolResultEnvelope(),
            _json.MinimalStructuredToolResultEnvelope);

        return StructuredTextResult(fallback, isError);

    }

    private static McpToolsCallResultWire StructuredTextResult(
        string text,
        bool isError) =>
        new()
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = isError,
        };

}
