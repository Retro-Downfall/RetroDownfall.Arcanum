namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

internal enum TurnResponseMode
{
    Buffered = 0,
    Streaming = 1,
}

/// <summary>
/// Closed logical-run reasons. Unclassified pipeline adapters select PipelineFailure; the existing
/// ProviderFailure value and prior numeric reservations are retained for semantic compatibility.
/// </summary>
internal enum TurnTerminationReason
{
    Completed = 0,
    ProviderFailure = 3,
    Cancelled = 10,
    PipelineFailure = 11,
}

internal enum ToolCallDisposition
{
    ServerExecution = 0,
    ClientForwarded = 1,
    DeniedByPolicy = 2,
}
