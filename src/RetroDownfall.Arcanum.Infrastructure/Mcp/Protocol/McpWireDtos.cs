using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

/// <summary>
/// MCP <c>initialize</c> request <c>params</c> (subset used by this client).
/// </summary>
public sealed record McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required McpClientCapabilities Capabilities { get; init; }

    [JsonPropertyName("clientInfo")]
    public required McpClientInfo ClientInfo { get; init; }
}

/// <summary>
/// MCP client capabilities object (may be empty on the wire).
/// </summary>
public sealed record McpClientCapabilities
{
}

/// <summary>
/// MCP <c>clientInfo</c> on initialize.
/// </summary>
public sealed record McpClientInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// MCP <c>tools/call</c> request <c>params</c>.
/// </summary>
public sealed record McpToolsCallParams
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required JsonElement Arguments { get; init; }
}

/// <summary>
/// Empty JSON object used when a tool omits <c>inputSchema</c>.
/// </summary>
public sealed record McpEmptyJsonObject
{
}

/// <summary>
/// MCP server <c>serverInfo</c> on <c>initialize</c> result (in-process server).
/// </summary>
public sealed record McpServerInfoWire
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// MCP server <c>capabilities</c> on <c>initialize</c> result (minimal stub).
/// </summary>
public sealed record McpServerCapabilitiesWire
{
    [JsonPropertyName("tools")]
    public McpEmptyJsonObject Tools { get; init; } = new();
}

/// <summary>
/// MCP <c>initialize</c> <c>result</c> body from the in-process Arcanum server.
/// </summary>
public sealed record McpInitializeServerResult
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required McpServerCapabilitiesWire Capabilities { get; init; }

    [JsonPropertyName("serverInfo")]
    public required McpServerInfoWire ServerInfo { get; init; }
}

/// <summary>
/// One MCP tool descriptor returned in <c>tools/list</c>. <see cref="InputSchema"/> is a verbatim JSON Schema object built ahead-of-time by the in-process server.
/// </summary>
public sealed record McpToolDefinitionWire
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public required JsonElement InputSchema { get; init; }
}

/// <summary>
/// MCP <c>tools/list</c> <c>result</c> body. <see cref="Tools"/> carries real tool descriptors for the in-process server.
/// </summary>
public sealed record McpToolsListResultWire
{
    [JsonPropertyName("tools")]
    public McpToolDefinitionWire[] Tools { get; init; } = [];
}

/// <summary>
/// One MCP text content block in a <c>tools/call</c> result.
/// </summary>
public sealed record McpToolContentTextWire
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// MCP <c>tools/call</c> <c>result</c> body (stub).
/// </summary>
public sealed record McpToolsCallResultWire
{
    [JsonPropertyName("content")]
    public required McpToolContentTextWire[] Content { get; init; }

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>read_file_chunk</c> tool. Line numbers are 1-based and inclusive.
/// </summary>
public sealed record ReadFileChunkParams
{
    [JsonPropertyName("relativePath")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }

    [JsonPropertyName("endLine")]
    public required int EndLine { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>replace_text_block</c> tool. <see cref="ExactSearchText"/> must be a verbatim block from the file (whitespace and newlines preserved).
/// </summary>
public sealed record ReplaceTextBlockParams
{
    [JsonPropertyName("relativePath")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("exactSearchText")]
    public required string ExactSearchText { get; init; }

    [JsonPropertyName("replacementText")]
    public required string ReplacementText { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>write_file</c> tool.
/// </summary>
public sealed record WriteFileParams(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Arguments accepted by the in-process <c>list_directory</c> tool.
/// </summary>
public sealed record ListDirectoryParams
{
    [JsonPropertyName("relativePath")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>search_workspace</c> tool.
/// Search is exact and independently scoped to each logical line.
/// </summary>
public sealed record SearchWorkspaceParams
{
    [JsonPropertyName("pattern")]
    public required string Pattern { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("caseSensitive")]
    public required bool CaseSensitive { get; init; }

    [JsonPropertyName("root")]
    public string? Root { get; init; }

    [JsonPropertyName("globs")]
    public string[] Globs { get; init; } = [];

    [JsonPropertyName("extensions")]
    public string[] Extensions { get; init; } = [];
}

/// <summary>
/// Arguments accepted by the in-process <c>apply_patch</c> tool.
/// The patch is a canonical unified diff; <see cref="DryRun"/> performs the complete
/// containment/fingerprint/hunk plan without staging or mutation.
/// </summary>
public sealed record ApplyPatchParams(
    [property: JsonPropertyName("patch")] string Patch,
    [property: JsonPropertyName("dryRun")] bool DryRun = false);

/// <summary>
/// Arguments accepted by <c>workspace_check</c>. The model selects only a closed profile and
/// exact allowlisted option values; executable names and arbitrary argument surfaces are absent.
/// </summary>
public sealed record WorkspaceCheckParams
{

    [JsonPropertyName("profile")]
    public required string Profile { get; init; }

    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Arguments accepted by the in-process <c>execute_command</c> tool.
/// </summary>
/// <remarks>
/// Callers must populate <see cref="ArgumentList"/> with one entry per argument token (preferred,
/// no OS-level re-parsing), or fall back to a single <see cref="Arguments"/> string that the host
/// tokenizes (quoted substrings stay together; everything else splits on whitespace).
/// </remarks>
public sealed record ExecuteCommandParams
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    [JsonPropertyName("argumentList")]
    public string[]? ArgumentList { get; init; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>ask_human</c> tool.
/// </summary>
public sealed record AskHumanParams
{
    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("promptId")]
    public required string PromptId { get; init; }
}

public sealed record ScribeLexiconParams(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("facts")] string[] Facts);

public sealed record DeleteLexiconParams([property: JsonPropertyName("name")] string Name);

public sealed record SearchArchivesParams([property: JsonPropertyName("query")] string Query);

/// <summary>
/// Arguments accepted by the in-process <c>read_saga</c> tool (RAG Phase 4 — semantic search over
/// Saga long-term associative memory).
/// </summary>
public sealed record ReadSagaParams(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int? Limit = null);

/// <summary>
/// Arguments accepted by the in-process <c>attach_session_file</c> tool (re-attach a bound session
/// attachment into the next model turn). Session id is ambient — never passed as an argument.
/// </summary>
public sealed record AttachSessionFileParams(
    [property: JsonPropertyName("logicalName")] string LogicalName,
    [property: JsonPropertyName("version")] int? Version = null);

/// <summary>
/// Arguments accepted by the in-process <c>adjust_initiative</c> tool (Unseen Servant dynamic polling).
/// </summary>
public sealed record AdjustInitiativeArgs
{

    [JsonPropertyName("job_name")]
    public required string JobName { get; init; }

    [JsonPropertyName("interval_minutes")]
    public required int IntervalMinutes { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>send_commlink_alert</c> tool (Comm Link alerts).
/// Legacy tools/call alias <c>use_commlink</c> accepts the same shape.
/// </summary>
public sealed record UseCommlinkParams
{

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>petition_dungeon_master</c> tool (Divine Intervention).
/// </summary>
public sealed record PetitionDungeonMasterParams
{

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

/// <summary>
/// Structured result from <c>petition_dungeon_master</c>. Delivery is informational —
/// the tool never sets MCP <c>IsError</c> for Comm Link delivery outcomes.
/// <c>notificationStatus</c> is <c>delivered</c>, <c>suppressed</c>, or <c>failed</c>.
/// </summary>
public sealed record PetitionDungeonMasterResultWire
{

    [JsonPropertyName("escalationRequested")]
    public required bool EscalationRequested { get; init; }

    [JsonPropertyName("notificationStatus")]
    public required string NotificationStatus { get; init; }

}

/// <summary>
/// Arguments accepted by the in-process <c>cast_sending</c> tool (Conclave cross-Apprentice delegation).
/// </summary>
public sealed record CastSendingParams
{

    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

}

/// <summary>
/// Result payload returned by the in-process <c>cast_sending</c> tool: the id of the newly created child Apprentice.
/// </summary>
public sealed record CastSendingResultWire
{

    [JsonPropertyName("childApprenticeId")]
    public required Guid ChildApprenticeId { get; init; }

}

/// <summary>
/// Arguments accepted by the in-process <c>dispatch_sending</c> tool (Archmage Client: A2A delegation to an external agent).
/// </summary>
public sealed record DispatchSendingParams
{

    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    [JsonPropertyName("agent_url")]
    public required string AgentUrl { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

}

/// <summary>
/// Result payload returned by the in-process <c>dispatch_sending</c> tool. Always the JSON-RPC success shape
/// (<c>IsError = false</c>) once a dispatch was actually attempted, so both the calling Apprentice's tool loop
/// and <c>ApprenticeService</c>'s Chronicle interception can parse the same structured outcome — including a
/// remote-side failure, which is carried in <see cref="Error"/> rather than as an MCP-level tool error.
/// </summary>
public sealed record DispatchSendingResultWire
{

    [JsonPropertyName("agentUrl")]
    public required string AgentUrl { get; init; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    [JsonPropertyName("succeeded")]
    public required bool Succeeded { get; init; }

    [JsonPropertyName("response")]
    public string? Response { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

}

