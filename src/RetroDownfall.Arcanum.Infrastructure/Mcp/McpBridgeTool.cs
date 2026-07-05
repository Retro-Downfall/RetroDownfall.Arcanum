using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Bridges a remote or in-process MCP tool to <see cref="AIFunction"/> via <see cref="IMcpClient.CallToolAsync"/>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: remote MCP tool AIFunction bridge; covered via McpBridgeTool tests and in-process MCP integration paths.
internal sealed class McpBridgeTool : AIFunction
{
    private readonly string _name;

    private readonly string _description;

    private readonly JsonElement _inputSchema;

    private readonly IMcpClient _client;

    private readonly IMcpClient? _fallbackClient;

    private readonly ILogger? _fallbackLogger;

    private readonly long _toolOutputCapBytes;

    internal long ToolOutputCapBytes => _toolOutputCapBytes;

    public McpBridgeTool(
        string name,
        string description,
        JsonElement inputSchema,
        IMcpClient client,
        long toolOutputCapBytes,
        IMcpClient? fallbackClient = null,
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
            return await CallAndFormatAsync(_client, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // W3.4 Group C #6: caller cancel / per-request timeout must NEVER trigger the
            // fallback. The tool may still be executing on the local server; re-running it on
            // the fallback could double-execute a mutating operation. The SDK dispatches the
            // wire-cancel notification to the local server so it stops.
            throw;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            // W3.4 Group C #6: restrict the global fallback to TRANSPORT/CONNECTIVITY failures
            // only (local server down/unreachable / channel closed / transport disposed before
            // a response). A tools/call that returned an error (isError: true) is a
            // tool-execution failure — the tool already ran, possibly with side effects — so it
            // must NOT be re-run on the fallback. Those surface as InvalidOperationException and
            // propagate without a fallback attempt below.
            if (_fallbackClient is null)
            {
                throw;
            }

            object? result = await CallAndFormatAsync(_fallbackClient, arguments, cancellationToken).ConfigureAwait(false);

            _fallbackLogger?.LogWarning(
                ex,
                "MCP tool {ToolName} succeeded via global fallback after local transport failure.",
                _name);

            return result;
        }
    }

    private async Task<object?> CallAndFormatAsync(IMcpClient client, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        TimeSpan? callTimeout = string.Equals(_name, "ask_human", StringComparison.Ordinal)
            ? Timeout.InfiniteTimeSpan
            : null;

        CallToolResult result = await client
            .CallToolAsync(_name, arguments, callTimeout, cancellationToken)
            .ConfigureAwait(false);

        string text = McpToolResultFormatter.FormatContentText(result, _toolOutputCapBytes);

        if (result.IsError == true)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? "MCP tool returned isError: true." : text);
        }

        return text;
    }

    // W3.4 Group C #6: classifies exceptions raised while calling the SDK client as a
    // transport/connectivity failure (the local server is down, unreachable, or the connection
    // closed before a response was received). ClientTransportClosedException (the SDK's own
    // transport-closed signal) derives from IOException, so it — along with general
    // IOException/ObjectDisposedException/HttpRequestException/TimeoutException — is eligible for
    // fallback. Tool-execution failures (InvalidOperationException from isError / a JSON-RPC
    // error response, surfaced by the SDK as McpProtocolException) are intentionally excluded so
    // McpBridgeTool does not re-run a possibly-mutating tool on the fallback server.
    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException
            or ObjectDisposedException
            or System.Net.Http.HttpRequestException
            or TimeoutException
            or McpTransportUnavailableException;
}

/// <summary>
/// Extracts human-readable text from an MCP <c>tools/call</c> <see cref="CallToolResult"/>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: MCP content formatting; covered indirectly via McpBridgeTool integration tests.
internal static class McpToolResultFormatter
{
    public static string FormatContentText(CallToolResult result, long maxUtf8Bytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(result);

        IList<ContentBlock>? content = result.Content;

        if (content is not { Count: > 0 })
        {
            string fallback = result.StructuredContent is { } structured ? structured.GetRawText() : string.Empty;

            return McpSecurityLimits.TruncateUtf8(fallback, maxUtf8Bytes);
        }

        StringBuilder sb = new();

        foreach (ContentBlock block in content)
        {
            string piece = block is TextContentBlock { Text.Length: > 0 } textBlock
                ? textBlock.Text
                : $"[{block.Type} content omitted]";

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(piece);
        }

        string formatted = sb.Length == 0 ? string.Empty : sb.ToString();

        return McpSecurityLimits.TruncateUtf8(formatted, maxUtf8Bytes);
    }
}
