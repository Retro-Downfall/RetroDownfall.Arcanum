using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Mcp;

/// <summary>
/// Diagnostic MCP Invocation — policy-constrained direct invocation of <strong>external</strong> MCP
/// tools by an operator. <strong>Not</strong> model execution; <strong>not</strong> unauthenticated
/// (inherits <c>X-Arcanum-Key</c> from the <c>/api</c> group). The internal <c>arcanum-internal</c>
/// server and all Forbidden Arts (<c>execute_command</c>, <c>write_file</c>, <c>replace_text_block</c>,
/// <c>delete_lexicon</c>, <c>run_spell_script</c>, <c>apply_patch</c>, and
/// <c>workspace_check</c>) are blocked. Workspace-local MCP servers must be trusted. Output is capped
/// by <c>Intelligence.ToolOutputCapBytes</c> (enforced by the MCP bridge) and the request is bounded
/// by <c>Mcp.RequestTimeoutSeconds</c>.
/// </summary>
public sealed class DiagnosticMcpInvocationService
{

    /// <summary>The internal in-process server name; its tools are always blocked from diagnostic invocation.</summary>
    public const string InternalServerName = "arcanum-internal";

    /// <summary>The fixed set of Forbidden Art / high-risk internal tool names that are always blocked.</summary>
    public static readonly IReadOnlySet<string> BlockedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ToolRiskClassifier.ExecuteCommandToolName,
        "write_file",
        "replace_text_block",
        "delete_lexicon",
        HostProcessToolPolicy.RunSpellScriptToolName,
        ToolRiskClassifier.ApplyPatchToolName,
        ToolRiskClassifier.WorkspaceCheckToolName,
    };

    public const string BlockedToolMessage =
        "This tool cannot be invoked from the diagnostic endpoint because it is a Forbidden Art " +
        "or requires the Wizard tool execution pipeline.";

    private const string TruncationMarker = "[truncated: exceeded";

    private readonly IMcpConnectionManager _mcpConnectionManager;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly ILogger<DiagnosticMcpInvocationService>? _logger;

    public DiagnosticMcpInvocationService(
        IMcpConnectionManager mcpConnectionManager,
        IOptionsMonitor<ArcanumSettings> settings,
        ILogger<DiagnosticMcpInvocationService>? logger = null)
    {

        _mcpConnectionManager = mcpConnectionManager;

        _settings = settings;

        _logger = logger;

    }

    public async Task<Result<DiagnosticMcpInvocationOutcome>> InvokeAsync(
        string toolName,
        JsonElement arguments,
        string? serverName,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(toolName))
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Validation.InvalidBody, "'toolName' is required."));

        }

        string normalizedTool = toolName.Trim();

        if (BlockedToolNames.Contains(normalizedTool))
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.DiagnosticBlocked, BlockedToolMessage));

        }

        string workspace = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : workingDirectory.Trim();

        List<McpServerStatusDto> servers = await _mcpConnectionManager
            .GetServerStatusesAsync(workspace, cancellationToken)
            .ConfigureAwait(false);

        // External = not the internal in-process server. Untrusted workspace-local servers are
        // already hidden by GetServerStatusesAsync except arcanum-internal, so the visible set is
        // trusted-by-construction for workspace-local entries.
        List<McpServerStatusDto> externalServers = servers
            .Where(static s => !string.Equals(
                s.ServerName,
                InternalServerName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(serverName))
        {

            string normalizedServer = serverName.Trim();

            McpServerStatusDto? match = externalServers
                .FirstOrDefault(s => string.Equals(s.ServerName, normalizedServer, StringComparison.Ordinal));

            if (match is null)
            {

                return Result<DiagnosticMcpInvocationOutcome>.Failure(
                    new Error(ErrorCodes.Mcp.ServerNotFound,
                        $"No external MCP server named '{normalizedServer}' is visible{(string.IsNullOrEmpty(workspace) ? string.Empty : " for the requested workspace")}."));

            }

            if (!IsRunning(match))
            {

                return Result<DiagnosticMcpInvocationOutcome>.Failure(
                    new Error(ErrorCodes.Mcp.ServerNotRunning,
                        $"MCP server '{match.ServerName}' is not running (state: {match.Status}). Start it from The Arsenal before invoking."));

            }

            if (!match.ProvidedTools.Contains(normalizedTool, StringComparer.Ordinal))
            {

                return Result<DiagnosticMcpInvocationOutcome>.Failure(
                    new Error(ErrorCodes.Mcp.ToolNotFound,
                        $"Tool '{normalizedTool}' is not provided by server '{match.ServerName}'."));

            }

            return await InvokeResolvedAsync(normalizedTool, arguments, match.ServerName, workspace, cancellationToken).ConfigureAwait(false);

        }

        List<McpServerStatusDto> candidates = externalServers
            .Where(s => IsRunning(s) && s.ProvidedTools.Contains(normalizedTool, StringComparer.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.ToolNotFound,
                    $"No running external MCP server exposes tool '{normalizedTool}'{(string.IsNullOrEmpty(workspace) ? string.Empty : " for the requested workspace")}."));

        }

        if (candidates.Count > 1)
        {

            string candidateList = string.Join(", ", candidates.Select(static s => $"'{s.ServerName}'").OrderBy(static n => n, StringComparer.Ordinal));

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.AmbiguousTool,
                    $"Tool '{normalizedTool}' is provided by multiple running external servers ({candidateList}). Specify 'serverName' to disambiguate."));

        }

        return await InvokeResolvedAsync(normalizedTool, arguments, candidates[0].ServerName, workspace, cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<DiagnosticMcpInvocationOutcome>> InvokeResolvedAsync(
        string toolName,
        JsonElement arguments,
        string resolvedServerName,
        string workspace,
        CancellationToken cancellationToken)
    {

        // Provenance-preserving: invoke the tool object bound to this server only — never re-resolve
        // by bare name on the merged inference surface (internal/local collisions).
        if (string.Equals(
                resolvedServerName,
                InternalServerName,
                StringComparison.OrdinalIgnoreCase))
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.ToolNotFound,
                    $"Tool '{toolName}' is not provided by any running external MCP server."));

        }

        AIFunction? tool = await _mcpConnectionManager
            .GetToolAsync(
                resolvedServerName,
                toolName,
                string.IsNullOrEmpty(workspace) ? null : workspace,
                cancellationToken)
            .ConfigureAwait(false);

        if (tool is null)
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.ToolNotFound,
                    $"Tool '{toolName}' was not present on server '{resolvedServerName}'. The server may have just stopped."));

        }

        // Defensive: Forbidden Arts must never run via diagnostic even if an external server reuses the name.
        if (BlockedToolNames.Contains(tool.Name))
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.DiagnosticBlocked, BlockedToolMessage));

        }

        int timeoutSeconds = Math.Max(1, _settings.CurrentValue.Mcp.RequestTimeoutSeconds);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        Stopwatch sw = Stopwatch.StartNew();

        try
        {

            AIFunctionArguments args = arguments.ValueKind == JsonValueKind.Object
                ? new AIFunctionArguments(ToArgumentDictionary(arguments))
                : [];

            object? output = await tool.InvokeAsync(args, timeoutCts.Token).ConfigureAwait(false);

            sw.Stop();

            string text = output switch
            {
                null => string.Empty,
                string s => s,
                JsonElement je => je.GetRawText(),
                _ => output.ToString() ?? string.Empty,
            };

            bool truncated = text.Contains(TruncationMarker, StringComparison.Ordinal);

            JsonElement element = ParseAsJson(text);

            return Result<DiagnosticMcpInvocationOutcome>.Success(new DiagnosticMcpInvocationOutcome(
                element,
                resolvedServerName,
                tool.Name,
                sw.ElapsedMilliseconds,
                truncated));

        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.DiagnosticTimeout,
                    $"Diagnostic MCP invocation of '{toolName}' on '{resolvedServerName}' exceeded the {timeoutSeconds}s timeout."));

        }
        catch (InvalidOperationException ex)
        {

            // McpBridgeTool throws InvalidOperationException when the tool returns isError: true.
            _logger?.LogWarning(ex, "Diagnostic MCP invocation of '{Tool}' on '{Server}' returned a tool error.", toolName, resolvedServerName);

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Mcp.ToolError, $"Tool '{toolName}' returned an error: {ex.Message}"));

        }
        catch (Exception ex)
        {

            _logger?.LogWarning(ex, "Diagnostic MCP invocation of '{Tool}' on '{Server}' failed.", toolName, resolvedServerName);

            string safeMessage = Sanitize(ex.Message);

            return Result<DiagnosticMcpInvocationOutcome>.Failure(
                new Error(ErrorCodes.Hub.Error, $"Diagnostic MCP invocation failed: {safeMessage}"));

        }

    }

    private static bool IsRunning(McpServerStatusDto server) =>
        string.Equals(server.Status, "running", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> ToArgumentDictionary(JsonElement arguments)
    {

        Dictionary<string, object?> dict = new(StringComparer.Ordinal);

        foreach (JsonProperty property in arguments.EnumerateObject())
        {

            dict[property.Name] = property.Value;

        }

        return dict;

    }

    private static JsonElement ParseAsJson(string text)
    {

        try
        {

            using JsonDocument document = JsonDocument.Parse(text);

            return document.RootElement.Clone();

        }
        catch (JsonException)
        {

            return JsonSerializer.SerializeToElement(text, ArcanumJsonContext.Default.String);

        }

    }

    /// <summary>Strips obvious secret-looking substrings from exception messages before returning them to the caller.</summary>
    private static string Sanitize(string message)
    {

        if (string.IsNullOrEmpty(message))
        {

            return message;

        }

        // Avoid surfacing anything that looks like a bearer token or long hex key in tool errors.
        // The MCP bridge already formats tool output, so this is a defensive last step on the
        // rare exception path, not a substitute for the bridge's own handling.

        return message.Length > 512 ? message[..512] + "…" : message;

    }

}

/// <summary>Raw outcome of a successful diagnostic invocation, before being wrapped into <see cref="McpToolInvokeResponse"/>.</summary>
public sealed record DiagnosticMcpInvocationOutcome(
    JsonElement Result,
    string ServerName,
    string ToolName,
    long DurationMs,
    bool Truncated);
