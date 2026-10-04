namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

internal enum TurnResponseMode
{
    Buffered = 0,
    Streaming = 1,
}

/// <summary>
/// Why a logical run ended. Only the reasons the engine can select are declared; the numeric values
/// of the survivors are kept so a reason that is added back later cannot reuse one.
/// </summary>
internal enum TurnTerminationReason
{
    Completed = 0,
    ProviderFailure = 3,
    Cancelled = 10,
}

internal enum ToolCallDisposition
{
    ServerExecution = 0,
    ClientForwarded = 1,
    DeniedByPolicy = 2,
}
