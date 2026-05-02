using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Bridges a remote MCP tool to <see cref="AIFunction"/> via <see cref="McpClient.SendRequestAsync"/> (<c>tools/call</c>).
/// </summary>
internal sealed class McpBridgeTool : AIFunction
{

    private readonly string _name;

    private readonly string _description;

    private readonly JsonElement _inputSchema;

    private readonly McpClient _client;

    private readonly McpClient? _fallbackClient;

    private readonly ILogger? _fallbackLogger;

    private readonly McpJsonSerializerContext _json;

    public McpBridgeTool(
        string name,
        string description,
        JsonElement inputSchema,
        McpClient client,
        McpClient? fallbackClient = null,
        ILogger? fallbackLogger = null,
        McpJsonSerializerContext? jsonContext = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ArgumentNullException.ThrowIfNull(client);

        _name = name;

        _description = description;

        _inputSchema = inputSchema.Clone();

        _client = client;

        _fallbackClient = fallbackClient;

        _fallbackLogger = fallbackLogger;

        _json = jsonContext ?? McpJsonSerializerContext.Default;

    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _inputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {

        try
        {

            return await SendToolsCallAsync(_client, arguments, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            if (_fallbackClient is null)
            {

                throw;

            }

            object? result = await SendToolsCallAsync(_fallbackClient, arguments, cancellationToken).ConfigureAwait(false);

            _fallbackLogger?.LogWarning(
                ex,
                "MCP tool {ToolName} succeeded via global fallback after local failure.",
                _name);

            return result;

        }

    }

    private async Task<object?> SendToolsCallAsync(McpClient client, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {

        Dictionary<string, JsonElement> argsMap = BuildArgumentsMap(arguments, _json);

        JsonElement argumentsElement = JsonSerializer.SerializeToElement(argsMap, _json.DictionaryStringJsonElement);

        McpToolsCallParams callParams = new()
        {

            Name = _name,

            Arguments = argumentsElement,

        };

        JsonElement paramsElement = JsonSerializer.SerializeToElement(callParams, _json.McpToolsCallParams);

        JsonElement result = await client
            .SendRequestAsync("tools/call", paramsElement, cancellationToken)
            .ConfigureAwait(false);

        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
        {

            string errText = McpToolResultFormatter.FormatContentText(result);

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errText) ? "MCP tool returned isError: true." : errText);

        }

        return McpToolResultFormatter.FormatContentText(result);

    }

    private static Dictionary<string, JsonElement> BuildArgumentsMap(AIFunctionArguments arguments, McpJsonSerializerContext json)
    {

        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, object?> pair in arguments)
        {

            if (pair.Value is null)
            {

                continue;

            }

            JsonElement valueElement = CoerceArgumentValue(pair.Value, json);

            map[pair.Key] = valueElement;

        }

        return map;

    }

    private static JsonElement CoerceArgumentValue(object? raw, McpJsonSerializerContext json)
    {

        switch (raw)
        {

            case JsonElement je:

                return je.Clone();

            case string s:

                return JsonSerializer.SerializeToElement(s, json.String);

            case bool b:

                return JsonSerializer.SerializeToElement(b, json.Boolean);

            case byte by:

                return JsonSerializer.SerializeToElement((int)by, json.Int32);

            case short sh:

                return JsonSerializer.SerializeToElement((int)sh, json.Int32);

            case ushort us:

                return JsonSerializer.SerializeToElement((int)us, json.Int32);

            case int i:

                return JsonSerializer.SerializeToElement(i, json.Int32);

            case uint ui:

                return JsonSerializer.SerializeToElement((long)ui, json.Int64);

            case long l:

                return JsonSerializer.SerializeToElement(l, json.Int64);

            case ulong ul:

                return JsonSerializer.SerializeToElement((double)ul, json.Double);

            case float f:

                return JsonSerializer.SerializeToElement((double)f, json.Double);

            case double d:

                return JsonSerializer.SerializeToElement(d, json.Double);

            case decimal m:

                return JsonSerializer.SerializeToElement(m.ToString(CultureInfo.InvariantCulture), json.String);

            case Guid g:

                return JsonSerializer.SerializeToElement(g.ToString("D"), json.String);

            default:

                return JsonSerializer.SerializeToElement(raw?.ToString() ?? string.Empty, json.String);

        }

    }

}

/// <summary>
/// Extracts human-readable text from MCP <c>tools/call</c> <c>result.content</c> payloads.
/// </summary>
internal static class McpToolResultFormatter
{

    public static string FormatContentText(JsonElement result)
    {

        if (!result.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {

            return result.GetRawText();

        }

        System.Text.StringBuilder sb = new();

        foreach (JsonElement block in content.EnumerateArray())
        {

            if (block.ValueKind != JsonValueKind.Object)
            {

                continue;

            }

            if (!block.TryGetProperty("type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {

                sb.Append(block.GetRawText());

                continue;

            }

            string? type = typeEl.GetString();

            if (string.Equals(type, "text", StringComparison.Ordinal)
                && block.TryGetProperty("text", out JsonElement textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {

                string? text = textEl.GetString();

                if (!string.IsNullOrEmpty(text))
                {

                    if (sb.Length > 0)
                    {

                        sb.AppendLine();

                    }

                    sb.Append(text);

                }

            }
            else
            {

                if (sb.Length > 0)
                {

                    sb.AppendLine();

                }

                sb.Append(block.GetRawText());

            }

        }

        return sb.Length == 0 ? result.GetRawText() : sb.ToString();

    }

}
