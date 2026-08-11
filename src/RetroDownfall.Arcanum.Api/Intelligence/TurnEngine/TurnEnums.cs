namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

internal enum TurnResponseMode
{
    Buffered = 0,
    Streaming = 1,
}

internal enum TurnPurpose
{
    Interactive = 0,
    Batch = 1,
    Background = 2,
    CampaignLogger = 3,
    Apprentice = 4,
}

internal enum TurnTerminationReason
{
    Completed = 0,
    ValidationFailed = 1,
    GuardrailsBlocked = 2,
    ProviderFailure = 3,
    Timeout = 4,
    ContextBudgetExceeded = 6,
    HostShutdown = 7,
    ClientDisconnected = 8,
    IdempotencyAbandoned = 9,
    Cancelled = 10,
}

internal enum ToolCallDisposition
{
    ServerExecution = 0,
    ClientForwarded = 1,
    DeniedByPolicy = 2,
}
