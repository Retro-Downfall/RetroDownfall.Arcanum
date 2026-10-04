using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

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

    private readonly TrustedStructuredToolResultKind? _trustedStructuredResultKind;

    private readonly TimeSpan? _requestTimeout;

    internal long ToolOutputCapBytes => _toolOutputCapBytes;

    public McpBridgeTool(
        string name,
        string description,
        JsonElement inputSchema,
        IMcpClient client,
        long toolOutputCapBytes,
        IMcpClient? fallbackClient = null,
        ILogger? fallbackLogger = null,
        TrustedStructuredToolResultKind? trustedStructuredResultKind = null,
        TimeSpan? requestTimeout = null)
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
        _trustedStructuredResultKind = trustedStructuredResultKind;
        _requestTimeout = requestTimeout;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _inputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAndFormatAsync(
                _client,
                arguments,
                trustStructuredResult: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // W3.4 Group C #6: caller cancel / per-request timeout must NEVER trigger the
            // fallback. The tool may still be executing on the local server; re-running it on
            // the fallback could double-execute a mutating operation. The SDK dispatches the
            // wire-cancel notification to the local server so it stops.
            throw;
        }
        catch (McpTransportUnavailableException ex)
            when (ex.DispatchState
                == McpRequestDispatchState.NotDispatched)
        {
            // A fallback is safe only when the client positively classified the request as never
            // dispatched. Timeout, disposal, channel completion, and generic transport failures
            // after CallToolAsync begins are ambiguous: the tool may already have side effects,
            // so retrying on another server could double-execute a mutation.
            if (_fallbackClient is null)
            {
                throw;
            }

            object? result = await CallAndFormatAsync(
                _fallbackClient,
                arguments,
                trustStructuredResult: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _fallbackLogger?.LogWarning(
                ex,
                "MCP tool {ToolName} succeeded via global fallback after local transport failure.",
                _name);

            return result;
        }
    }

    private async Task<object?> CallAndFormatAsync(
        IMcpClient client,
        AIFunctionArguments arguments,
        bool trustStructuredResult,
        CancellationToken cancellationToken)
    {
        TimeSpan? callTimeout = RunsUntilCallerCancellation(_name)
            ? Timeout.InfiniteTimeSpan
            : _requestTimeout;

        CallToolResult result = await client
            .CallToolAsync(_name, arguments, callTimeout, cancellationToken)
            .ConfigureAwait(false);

        bool receiptHandled =
            trustStructuredResult
            && _trustedStructuredResultKind
                == TrustedStructuredToolResultKind.WorkspacePatch
            && ApplyPatchInvocationAmbient.Current?.ReceiptHandled == true;

        string text = McpToolResultFormatter.FormatContentText(
            result,
            receiptHandled ? long.MaxValue : _toolOutputCapBytes);

        if (result.IsError == true)
        {
            string errorText = string.IsNullOrWhiteSpace(text) ? "MCP tool returned isError: true." : text;

            // Preserve expected HITL timeout as a typed exception so the tool pipeline can
            // surface the fixed public message instead of a generic tolerate failure.
            if (string.Equals(errorText, HumanPromptTimeoutException.DefaultMessage, StringComparison.Ordinal)
                || errorText.Contains(HumanPromptTimeoutException.DefaultMessage, StringComparison.Ordinal))
            {
                throw new HumanPromptTimeoutException();
            }

            throw new InvalidOperationException(errorText);
        }

        return trustStructuredResult
            && _trustedStructuredResultKind is { } kind
            ? new TrustedStructuredToolResult(
                kind,
                text,
                ReceiptHandled: receiptHandled)
            : text;
    }

    internal McpBridgeTool WithTrustedStructuredResult(
        TrustedStructuredToolResultKind kind) =>
        new(
            _name,
            _description,
            _inputSchema,
            _client,
            _toolOutputCapBytes,
            _fallbackClient,
            _fallbackLogger,
            kind,
            _requestTimeout);

    internal McpBridgeTool WithClient(IMcpClient client) =>
        new(
            _name,
            _description,
            _inputSchema,
            client,
            _toolOutputCapBytes,
            _fallbackClient,
            _fallbackLogger,
            _trustedStructuredResultKind,
            _requestTimeout);

    internal McpBridgeTool WithRequestTimeout(TimeSpan requestTimeout) =>
        new(
            _name,
            _description,
            _inputSchema,
            _client,
            _toolOutputCapBytes,
            _fallbackClient,
            _fallbackLogger,
            _trustedStructuredResultKind,
            requestTimeout);

    private static bool RunsUntilCallerCancellation(string toolName) =>
        string.Equals(toolName, "ask_human", StringComparison.Ordinal)
        || string.Equals(toolName, "execute_command", StringComparison.Ordinal)
        || string.Equals(toolName, ArcanumBuiltInToolNames.RunSpellScript, StringComparison.Ordinal);
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

        if (maxUtf8Bytes <= 0L)
        {
            return string.Empty;
        }

        // Accumulate only as far as the UTF-8 budget reaches. Building the whole joined text and cutting
        // it afterwards bounds the output but not the allocation: a result of many large blocks cost the
        // host a full copy first. The text produced is identical to joining every block and passing it
        // through McpSecurityLimits.TruncateUtf8, so the caller sees no difference.
        StringBuilder sb = new();

        long usedBytes = 0L;

        foreach (ContentBlock block in content)
        {
            string piece = block is TextContentBlock { Text.Length: > 0 } textBlock
                ? textBlock.Text
                : $"[{block.Type} content omitted]";

            string separator = sb.Length > 0 ? global::System.Environment.NewLine : string.Empty;

            long separatorBytes = Encoding.UTF8.GetByteCount(separator);

            long blockBytes = separatorBytes + Encoding.UTF8.GetByteCount(piece);

            if (usedBytes + blockBytes <= maxUtf8Bytes)
            {
                sb.Append(separator).Append(piece);

                usedBytes += blockBytes;

                continue;
            }

            // This block crosses the budget: keep the part of it that still fits, mark the cut, and
            // read no further block.
            long remaining = maxUtf8Bytes - usedBytes;

            if (remaining < separatorBytes)
            {
                sb.Append(separator, 0, Utf8Truncation.ChooseSafeCharCount(separator, remaining));
            }
            else
            {
                sb.Append(separator);

                sb.Append(piece, 0, Utf8Truncation.ChooseSafeCharCount(piece, remaining - separatorBytes));
            }

            sb.Append(McpSecurityLimits.TruncationMarker(maxUtf8Bytes));

            break;
        }

        return sb.ToString();
    }
}
