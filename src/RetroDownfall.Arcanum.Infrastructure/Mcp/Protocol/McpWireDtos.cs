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

    /// <summary>
    /// Optional MCP <c>outputSchema</c>: the shape of this tool's <c>structuredContent</c>. Omitted
    /// for every tool that answers in text alone, so no existing descriptor gains a property.
    /// </summary>
    [JsonPropertyName("outputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? OutputSchema { get; init; }
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

    /// <summary>
    /// Optional MCP <c>structuredContent</c>: the typed result a tool with an <c>outputSchema</c>
    /// returns beside its compact text fallback. A client that ignores it still gets a usable answer.
    /// </summary>
    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StructuredContent { get; init; }
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

    [JsonPropertyName("continuation")]
    public string? Continuation { get; init; }
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

    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }
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
/// Arguments accepted by the in-process <c>read_command_output</c> continuation tool.
/// </summary>
public sealed record ReadCommandOutputParams
{

    [JsonPropertyName("handle")]
    public required string Handle { get; init; }

    [JsonPropertyName("stream")]
    public required string Stream { get; init; }

    [JsonPropertyName("offset")]
    public long Offset { get; init; }

    [JsonPropertyName("maxBytes")]
    public int? MaxBytes { get; init; }

}

public sealed record CommandOutputPageResultWire
{

    [JsonPropertyName("handle")]
    public required string Handle { get; init; }

    [JsonPropertyName("stream")]
    public required string Stream { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("offset")]
    public long Offset { get; init; }

    [JsonPropertyName("nextOffset")]
    public long? NextOffset { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

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
    [property: JsonPropertyName("facts")] string[] Facts,
    [property: JsonPropertyName("attachment_id")] string? AttachmentId = null);

public sealed record DeleteLexiconParams([property: JsonPropertyName("name")] string Name);

public sealed record SearchArchivesParams([property: JsonPropertyName("query")] string Query);

/// <summary>
/// Arguments accepted by the in-process <c>propose_covenant</c> tool.
/// </summary>
/// <remarks>
/// Deliberately two fields. Scope, Campaign id, lane, origin, lifecycle, revision, attachment ids,
/// and every authority field are server-derived, so the model cannot name a scope it may not write,
/// claim provenance it did not receive, or assert a revision it never observed (§10.14).
/// </remarks>
public sealed record ProposeCovenantParams(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Arguments accepted by the in-process <c>retire_covenant</c> tool.
/// </summary>
/// <remarks>
/// The lane is the only choice the model has: the exact target and its expected revision come from
/// the producing provider attempt's admission receipt, not from the call.
/// </remarks>
public sealed record RetireCovenantParams(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("lane")] string Lane);

/// <summary>
/// The typed <c>structuredContent</c> a staged Covenant mutation returns.
/// </summary>
/// <remarks>
/// Carries the compact redacted receipt and nothing else. No key, no authored content, no operator
/// authority field: the operator reads committed content back through the authenticated Covenant
/// API, and a tool result is not a place to republish profile text.
///
/// <para><c>status</c> says <c>staged</c>, never <c>published</c>. Publication happens only if the
/// assistant response itself commits.</para>
/// </remarks>
public sealed record CovenantMutationStagedResultWire(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("mutationId")] string MutationId,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision,
    [property: JsonPropertyName("renderedHash")] string? RenderedHash,
    [property: JsonPropertyName("byteCost")] int? ByteCost);

/// <summary>
/// The typed <c>structuredContent</c> an expected Covenant tool failure returns.
/// </summary>
/// <remarks>
/// An expected failure is still structured, because the model has to be able to tell "the operator
/// declined" from "this key already has a staged change" without parsing prose. The message is
/// code-owned and never echoes the key or the content it failed on.
/// </remarks>
public sealed record CovenantMutationFailureResultWire(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

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
/// Arguments for <c>refresh_session_file</c>. Exactly one selector is accepted; session and source
/// path are host-owned ambient state and never appear in this model-visible contract.
/// </summary>
public sealed record RefreshSessionFileParams(
    [property: JsonPropertyName("attachmentId")] Guid? AttachmentId = null,
    [property: JsonPropertyName("logicalKey")] string? LogicalKey = null);

public sealed record RefreshSessionFileResultWire(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("attachmentId")] Guid? AttachmentId,
    [property: JsonPropertyName("logicalKey")] string? LogicalKey,
    [property: JsonPropertyName("version")] int? Version,
    [property: JsonPropertyName("newVersionCreated")] bool NewVersionCreated,
    [property: JsonPropertyName("queuedForInjection")] bool QueuedForInjection,
    [property: JsonPropertyName("sourcePath")] string? SanitizedSourcePath,
    [property: JsonPropertyName("contentSha256")] string? ContentSha256,
    [property: JsonPropertyName("byteLength")] long? ByteLength,
    [property: JsonPropertyName("sourceFreshnessTimestamp")] DateTimeOffset? SourceFreshnessTimestamp,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("message")] string? Message = null);

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
/// </summary>
public sealed record SendCommLinkAlertParams
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

    /// <summary>
    /// When <c>true</c>, a remote agent that reaches <c>input-required</c>/<c>auth-required</c> returns a
    /// continuation task id instead of ending the Sending (issue #64). Default <c>false</c> keeps the
    /// historical blocking behavior.
    /// </summary>
    [JsonPropertyName("continuable")]
    public bool? Continuable { get; init; }

    /// <summary>
    /// When <c>true</c>, ask the peer to report back instead of holding a concurrency slot for the whole
    /// remote run. Falls back to the blocking wait when the peer cannot accept a callback (issue #67).
    /// </summary>
    [JsonPropertyName("callback")]
    public bool? Callback { get; init; }

    /// <summary>
    /// Optional Agent Card skill id to target. Validated against the peer's card before the remote task
    /// is created (issue #65).
    /// </summary>
    [JsonPropertyName("skill_id")]
    public string? SkillId { get; init; }

    /// <summary>
    /// Optional media types to accept back. Omitted means "whatever this instance can consume".
    /// </summary>
    [JsonPropertyName("accepted_output_modes")]
    public string[]? AcceptedOutputModes { get; init; }

}

/// <summary>
/// Arguments for the in-process <c>continue_sending</c> tool: answers a Sending a remote agent parked
/// at <c>input-required</c>/<c>auth-required</c> without re-running the goal (issue #64).
/// </summary>
public sealed record ContinueSendingParams
{

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("agent_url")]
    public required string AgentUrl { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("continuable")]
    public bool? Continuable { get; init; }

    /// <inheritdoc cref="DispatchSendingParams.SkillId"/>
    [JsonPropertyName("skill_id")]
    public string? SkillId { get; init; }

    /// <inheritdoc cref="DispatchSendingParams.AcceptedOutputModes"/>
    [JsonPropertyName("accepted_output_modes")]
    public string[]? AcceptedOutputModes { get; init; }

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

    /// <summary>
    /// Whether the peer reported what the delegated work cost. <c>false</c> means "unknown", never
    /// "free" — see <c>A2ARemoteCost</c> and issue #60.
    /// </summary>
    [JsonPropertyName("costKnown")]
    public bool CostKnown { get; init; }

    [JsonPropertyName("remoteTotalTokens")]
    public long? RemoteTotalTokens { get; init; }

    [JsonPropertyName("remoteCostUsd")]
    public decimal? RemoteCostUsd { get; init; }

    /// <summary>
    /// When the remote task was accepted. Distinct from <see cref="SettledAt"/> so remote wall-clock is
    /// derivable from the Chronicle rather than collapsing onto one timestamp (issue #60).
    /// </summary>
    [JsonPropertyName("dispatchedAt")]
    public DateTimeOffset? DispatchedAt { get; init; }

    [JsonPropertyName("settledAt")]
    public DateTimeOffset? SettledAt { get; init; }

    /// <summary>
    /// Set when the remote stopped at <c>input-required</c>/<c>auth-required</c> in continuable mode and
    /// is waiting to be answered (issue #64).
    /// </summary>
    [JsonPropertyName("continuationTaskId")]
    public string? ContinuationTaskId { get; init; }

    [JsonPropertyName("continuationNeed")]
    public string? ContinuationNeed { get; init; }

}
