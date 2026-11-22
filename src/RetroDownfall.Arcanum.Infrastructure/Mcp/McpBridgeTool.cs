using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

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

    private readonly GuildToolInvocationContext? _guildContext;

    private readonly GuildToolGrant? _guildTool;

    private readonly bool _guildClientChanged;

    internal long ToolOutputCapBytes => _toolOutputCapBytes;

    internal bool OwnsWorkspaceCheckResult =>
        _trustedStructuredResultKind == TrustedStructuredToolResultKind.WorkspaceCheck
        && IsHostResultTransport(_client);

    internal bool OwnsWorkspacePatchResult =>
        _name == ToolRiskClassifier.ApplyPatchToolName
        && _trustedStructuredResultKind == TrustedStructuredToolResultKind.WorkspacePatch
        && IsHostResultTransport(_client);

    internal bool OwnsSessionAttachmentResult =>
        _name == "attach_session_file" && IsHostResultTransport(_client);

    public McpBridgeTool(
        string name,
        string description,
        JsonElement inputSchema,
        IMcpClient client,
        long toolOutputCapBytes,
        IMcpClient? fallbackClient = null,
        ILogger? fallbackLogger = null,
        TrustedStructuredToolResultKind? trustedStructuredResultKind = null,
        TimeSpan? requestTimeout = null,
        GuildToolInvocationContext? guildContext = null,
        GuildToolGrant? guildTool = null,
        bool guildClientChanged = false)
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

        if ((guildContext is null) != (guildTool is null)
            || (guildTool is not null && !string.Equals(guildTool.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A Guild MCP binding requires its exact host context and tool identity.");
        }

        _guildContext = guildContext;

        _guildTool = guildTool;

        _guildClientChanged = guildClientChanged;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _inputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_guildClientChanged)
        {
            return GuildToolInvocationContext.Format(GuildToolInvocationContext.Unavailable);
        }

        if (_guildContext is { } guild && _guildTool is { } identity)
        {
            if (identity.SourceKind == GuildToolSourceKind.InternalMcp && identity.SourceRegistrationId == "arcanum"
                && (_client is SdkMcpClientWrapper { IsHostOwned: true } or McpClientGeneration { IsGuildHostTransport: true })
                && guild.CaptureMcpInvocation(_name) is not null)
            {
                using IDisposable scope = GuildToolInvocationAmbient.Begin(guild);

                // A qualified Guild route has one exact host transport and no fallback dispatch.
                return await CallAndFormatAsync(_client, arguments, trustStructuredResult: false,
                    cancellationToken).ConfigureAwait(false);
            }

            return GuildToolInvocationContext.Format(
                await guild.RefuseDispatchAsync(identity, cancellationToken).ConfigureAwait(false));
        }

        if (GuildToolInvocationAmbient.Current is not null)
        {
            return GuildToolInvocationContext.Format(GuildToolInvocationContext.Unavailable);
        }

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
                "MCP tool {ToolName} returned via global fallback after local transport failure.",
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
        WorkspaceMutationFence.OpaqueEffect? effect = null;

        if (!client.IsHostOwned && !WorkspaceProcessBoundary.TryBeginOpaqueEffect(out effect))
        {
            cancellationToken.ThrowIfCancellationRequested();

            return WorkspaceProcessBoundary.Message;
        }

        using WorkspaceMutationFence.OpaqueEffect? opaqueEffect = effect;

        TimeSpan? callTimeout = RunsUntilCallerCancellation(_name)
            ? Timeout.InfiniteTimeSpan
            : _requestTimeout;

        opaqueEffect?.MarkDispatched();

        CallToolResult result = await client
            .CallToolAsync(_name, arguments, callTimeout, cancellationToken)
            .ConfigureAwait(false);

        bool receiptHandled =
            trustStructuredResult
            && _trustedStructuredResultKind
                == TrustedStructuredToolResultKind.WorkspacePatch
            && ApplyPatchInvocationAmbient.Current?.ReceiptHandled == true;

        bool isHostTransport = IsHostResultTransport(client);

        if (result.IsError == true)
        {
            if (!isHostTransport)
            {
                return new McpToolFailure(new ToolOutcome(ToolOutcomeCode.ExternalToolError));
            }

            if (McpToolFailure.ReadReported(result, _toolOutputCapBytes) is { } reportedFailure)
            {
                return reportedFailure;
            }

            if (McpToolFailure.ReadNative(result.StructuredContent) is { } knownFailure)
            {
                // The model observes the finished lookup. The ledger uses the host's proof that
                // this missing read never reached file content.
                return trustStructuredResult && _name == "read_file" && knownFailure.Outcome.Code == ToolOutcomeCode.NotFound
                    ? knownFailure with { OwnerProvedNoDispatch = true }
                    : knownFailure;
            }

            // Unknown native errors remain infrastructure failures. Neither their text nor an
            // external server's content can select a trusted outcome or become public exception copy.
            throw new InvalidOperationException("The internal MCP tool returned an unclassified error.");
        }

        string text = McpToolResultFormatter.FormatContentText(
            result,
            receiptHandled ? long.MaxValue : _toolOutputCapBytes);

        WorkspaceMutationFact[]? mutations = trustStructuredResult && isHostTransport
            ? ReadOwnerMutations(result, text)
            : null;

        TrustedStructuredToolResultKind? kind = _trustedStructuredResultKind;

        if (mutations is not null && kind is null)
        {
            kind = TrustedStructuredToolResultKind.WorkspaceFileMutation;
        }

        return trustStructuredResult && kind is { } trustedKind
            ? new TrustedStructuredToolResult(
                trustedKind,
                text,
                ReceiptHandled: receiptHandled)
            {
                FailureEvidence = isHostTransport
                    && trustedKind is (TrustedStructuredToolResultKind.WorkspacePatch or TrustedStructuredToolResultKind.WorkspaceCheck)
                    ? ReadOwnerFailureEvidence(result)
                    : null,
                CommittedWorkspaceMutations = mutations,
            }
            : text;
    }

    private WorkspaceMutationFact[]? ReadOwnerMutations(CallToolResult result, string text)
    {
        if (_name is not ("write_file" or "edit_file" or ToolRiskClassifier.ApplyPatchToolName)
            || result.Content is not [TextContentBlock { Text: { } original }] || !string.Equals(text, original, StringComparison.Ordinal)
            || result.Meta is not { } metadata || !metadata.TryGetPropertyValue(WorkspaceMutationFact.MetadataName, out var node)
            || node is null)
        {
            return null;
        }

        try
        {
            WorkspaceMutationFact[]? facts = node.Deserialize(McpJsonSerializerContext.Default.WorkspaceMutationFactArray);

            return facts is { Length: > 0 }
                && (_name == ToolRiskClassifier.ApplyPatchToolName || facts.Length == 1)
                && facts.All(fact => fact is not null && WorkspaceMutationFact.IsPublishablePath(fact.RelativePath) && MutationMatchesTool(fact.Operation))
                ? facts
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool MutationMatchesTool(WorkspaceMutationOperation operation) => _name switch
    {
        "write_file" => operation == WorkspaceMutationOperation.Write,
        "edit_file" => operation == WorkspaceMutationOperation.Edit,
        ToolRiskClassifier.ApplyPatchToolName => operation is WorkspaceMutationOperation.Create or WorkspaceMutationOperation.Modify
            or WorkspaceMutationOperation.Rename or WorkspaceMutationOperation.Delete,
        _ => false,
    };

    private static bool IsHostResultTransport(IMcpClient client) =>
        client is SdkMcpClientWrapper { IsHostOwned: true } or McpClientGeneration { IsGuildHostTransport: true };

    private static TurnFailureEvidence? ReadOwnerFailureEvidence(CallToolResult result)
    {
        if (result.Meta is not { } metadata
            || !metadata.TryGetPropertyValue("arcanum/turnFailureEvidence", out var node)
            || node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize(McpJsonSerializerContext.Default.TurnFailureEvidence);
        }
        catch (JsonException)
        {
            return null;
        }
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
            _requestTimeout,
            _guildContext,
            _guildTool,
            _guildClientChanged);

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
            _requestTimeout,
            _guildContext,
            _guildTool,
            _guildClientChanged || (_guildContext is not null && !ReferenceEquals(client, _client)));

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
            requestTimeout,
            _guildContext,
            _guildTool,
            _guildClientChanged);

    internal McpBridgeTool WithGuildAuthority(GuildToolInvocationContext context, GuildToolGrant tool) =>
        new(_name, _description, _inputSchema, _client, _toolOutputCapBytes, _fallbackClient, _fallbackLogger,
            _trustedStructuredResultKind, _requestTimeout, context, tool, _guildClientChanged);

    internal bool TryGetGuildBinding(GuildToolInvocationContext context, out GuildToolGrant? tool)
    {
        tool = !_guildClientChanged && ReferenceEquals(_guildContext, context) ? _guildTool : null;

        return tool is not null;
    }

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
    public static string FormatContentText(CallToolResult result, long maxUtf8Bytes = long.MaxValue, bool allowStructuredFallback = true, bool strictUtf8Cap = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        IList<ContentBlock>? content = result.Content;

        if (content is not { Count: > 0 })
        {
            string fallback = allowStructuredFallback && result.StructuredContent is { } structured ? structured.GetRawText() : string.Empty;

            return McpSecurityLimits.TruncateUtf8(fallback, maxUtf8Bytes);
        }

        if (maxUtf8Bytes <= 0L)
        {
            return string.Empty;
        }

        string? boundedMarker = null;

        long contentBudget = maxUtf8Bytes;

        if (strictUtf8Cap)
        {
            long requiredBytes = 0L;

            foreach (ContentBlock block in content)
            {
                string piece = ContentPiece(block);

                requiredBytes += (requiredBytes > 0L ? Encoding.UTF8.GetByteCount(global::System.Environment.NewLine) : 0L)
                    + Encoding.UTF8.GetByteCount(piece);

                if (requiredBytes > maxUtf8Bytes)
                {
                    boundedMarker = maxUtf8Bytes >= 12L ? "\n[truncated]" : maxUtf8Bytes >= 3L ? "…" : ".";

                    contentBudget -= Encoding.UTF8.GetByteCount(boundedMarker);

                    break;
                }
            }
        }

        // Accumulate only as far as the UTF-8 budget reaches. Building the whole joined text and cutting
        // it afterwards bounds the output but not the allocation: a result of many large blocks cost the
        // host a full copy first. Ordinary formatting retains its existing marker behavior. Reported
        // native errors reserve the marker inside the cap before accumulating their content.
        StringBuilder sb = new();

        long usedBytes = 0L;

        foreach (ContentBlock block in content)
        {
            string piece = ContentPiece(block);

            string separator = sb.Length > 0 ? global::System.Environment.NewLine : string.Empty;

            long separatorBytes = Encoding.UTF8.GetByteCount(separator);

            long blockBytes = separatorBytes + Encoding.UTF8.GetByteCount(piece);

            if (usedBytes + blockBytes <= contentBudget)
            {
                sb.Append(separator).Append(piece);

                usedBytes += blockBytes;

                continue;
            }

            // This block crosses the budget: keep the part of it that still fits, mark the cut, and
            // read no further block.
            long remaining = contentBudget - usedBytes;

            if (remaining < separatorBytes)
            {
                sb.Append(separator, 0, Utf8Truncation.ChooseSafeCharCount(separator, remaining));
            }
            else
            {
                sb.Append(separator);

                sb.Append(piece, 0, Utf8Truncation.ChooseSafeCharCount(piece, remaining - separatorBytes));
            }

            sb.Append(boundedMarker ?? McpSecurityLimits.TruncationMarker(maxUtf8Bytes));

            break;
        }

        return sb.ToString();
    }

    private static string ContentPiece(ContentBlock block) =>
        block is TextContentBlock { Text.Length: > 0 } textBlock
            ? textBlock.Text
            : $"[{block.Type} content omitted]";
}

/// <summary>
/// Closed observation semantics of exact final in-process wrappers. This is neither a cache proof
/// nor execution/source/network authority, and no wire annotation or copied name can issue it.
/// </summary>
internal static class ReadOnlyToolProvenance
{
    private sealed record Definition(string Name);

    private static readonly ConditionalWeakTable<McpBridgeTool, Definition> Proofs = new();

    internal static IReadOnlyList<string> InternalObservationNames { get; } = Array.AsReadOnly<string>(
    [
        "read_file", "list_directory", "search_workspace", "read_command_output", "read_tool_output",
        "search_memory", "search_history", "ask_human",
    ]);

    /// <summary>Called only after the actual in-process server's final wrapper has been constructed.</summary>
    internal static void IssueFinalInternalTool(McpBridgeTool tool)
    {
        foreach (string name in InternalObservationNames)
        {
            if (string.Equals(name, tool.Name, StringComparison.Ordinal))
            {
                Proofs.AddOrUpdate(tool, new Definition(name));

                return;
            }
        }
    }

    internal static bool IsCurrent(AITool tool) =>
        tool is McpBridgeTool bridge
        && Proofs.TryGetValue(bridge, out Definition? proof)
        && string.Equals(proof.Name, bridge.Name, StringComparison.Ordinal);
}
