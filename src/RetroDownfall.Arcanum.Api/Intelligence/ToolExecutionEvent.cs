namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Observable tool-pipeline lifecycle hooks for TurnEngine compatibility/HITL projection.</summary>
public abstract record ToolExecutionEvent;

/// <summary>
/// Compatibility event for a legacy <c>WardAsync</c> waiter. The current server tool pipeline does
/// not raise it.
/// </summary>
public sealed record ToolApprovalRequestedEvent(
    string WardId,
    string ToolName,
    string ArgumentsJson) : ToolExecutionEvent;

/// <summary>Fired before <c>ask_human</c> waits for a human response.</summary>
public sealed record ToolHumanInputRequestedEvent(
    string CallId,
    string Prompt) : ToolExecutionEvent;
