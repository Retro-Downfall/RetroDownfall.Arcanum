using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Bridges a remote MCP tool to <see cref="AIFunction"/> via <see cref="McpClient.SendRequestAsync"/> (<c>tools/call</c>).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: remote MCP tool AIFunction bridge; covered via McpBridgeTool tests and in-process MCP integration paths.
internal sealed class McpBridgeTool : AIFunction
{
    private readonly string _name;

    private readonly string _description;

    private readonly JsonElement _inputSchema;

    private readonly McpClient _client;

    private readonly McpClient? _fallbackClient;

    private readonly ILogger? _fallbackLogger;

    private readonly long _toolOutputCapBytes;

    internal long ToolOutputCapBytes => _toolOutputCapBytes;

    public McpBridgeTool(
        string name,
        string description,
        JsonElement inputSchema,
        McpClient client,
        long toolOutputCapBytes,
        McpClient? fallbackClient = null,
        ILogger? fallbackLogger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(client);
        _name = name;
        _description = description;
        _inputSchema = inputSchema.Clone();
        _client = client;
        _toolOutputCapBytes = toolOutputCapBytes;
        _fallbackClient = fallbackClient;
        _fallbackLogger = fallbackLogger;
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
        JsonElement paramsElement = BuildToolsCallParamsElement(arguments);

        TimeSpan? callTimeout = string.Equals(_name, "ask_human", StringComparison.Ordinal)
            ? Timeout.InfiniteTimeSpan
            : null;

        JsonElement result = await client
            .SendRequestAsync("tools/call", paramsElement, cancellationToken, callTimeout)
            .ConfigureAwait(false);

        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
        {
            string errText = McpToolResultFormatter.FormatContentText(result, _toolOutputCapBytes);

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errText) ? "MCP tool returned isError: true." : errText);
        }

        return McpToolResultFormatter.FormatContentText(result, _toolOutputCapBytes);
    }

    private JsonElement BuildToolsCallParamsElement(AIFunctionArguments arguments)
    {
        ArrayBufferWriter<byte> buffer = new(512);

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", _name);
            writer.WritePropertyName("arguments");
            writer.WriteStartObject();

            foreach (KeyValuePair<string, object?> pair in arguments)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                writer.WritePropertyName(pair.Key);
                WriteArgumentValue(writer, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);

        return doc.RootElement.Clone();
    }

    private static void WriteArgumentValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case JsonElement je:
                je.WriteTo(writer);

                break;

            case string s:
                writer.WriteStringValue(s);

                break;

            case bool b:
                writer.WriteBooleanValue(b);

                break;

            case byte by:
                writer.WriteNumberValue(by);

                break;

            case short sh:
                writer.WriteNumberValue(sh);

                break;

            case ushort us:
                writer.WriteNumberValue(us);

                break;

            case int i:
                writer.WriteNumberValue(i);

                break;

            case uint ui:
                writer.WriteNumberValue((long)ui);

                break;

            case long l:
                writer.WriteNumberValue(l);

                break;

            case ulong ul:
                writer.WriteNumberValue((double)ul);

                break;

            case float f:
                writer.WriteNumberValue((double)f);

                break;

            case double d:
                writer.WriteNumberValue(d);

                break;

            case decimal m:
                writer.WriteStringValue(m.ToString(CultureInfo.InvariantCulture));

                break;

            case Guid g:
                writer.WriteStringValue(g.ToString("D"));

                break;

            default:
                writer.WriteStringValue(value.ToString() ?? string.Empty);

                break;
        }
    }
}

/// <summary>
/// Extracts human-readable text from MCP <c>tools/call</c> <c>result.content</c> payloads.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: MCP JSON content formatting; covered indirectly via McpBridgeTool integration tests.
internal static class McpToolResultFormatter
{
    public static string FormatContentText(JsonElement result, long maxUtf8Bytes = long.MaxValue)
    {
        if (!result.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return McpSecurityLimits.TruncateUtf8(result.GetRawText(), maxUtf8Bytes);
        }

        StringBuilder sb = new();

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

        string formatted = sb.Length == 0 ? result.GetRawText() : sb.ToString();

        return McpSecurityLimits.TruncateUtf8(formatted, maxUtf8Bytes);
    }
}
