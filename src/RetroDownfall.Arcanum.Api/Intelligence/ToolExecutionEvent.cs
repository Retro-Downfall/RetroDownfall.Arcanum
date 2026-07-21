namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Observable tool-pipeline lifecycle hooks for TurnEngine Ward/HITL projection.</summary>
public abstract record ToolExecutionEvent;

/// <summary>Fired before <c>WardAsync</c> waits for operator approval.</summary>
public sealed record ToolApprovalRequestedEvent(
    string WardId,
    string ToolName,
    string ArgumentsJson) : ToolExecutionEvent;

/// <summary>Fired before <c>ask_human</c> waits for a human response.</summary>
public sealed record ToolHumanInputRequestedEvent(
    string CallId,
    string Prompt) : ToolExecutionEvent;
