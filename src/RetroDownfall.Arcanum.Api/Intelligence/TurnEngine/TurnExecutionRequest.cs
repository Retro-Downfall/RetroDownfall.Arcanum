using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>Internal logical-run request. Does not carry leases or HTTP surface modes.</summary>
internal sealed record TurnExecutionRequest(
    PingRequest Request,
    TurnResponseMode ResponseMode,
    TurnPurpose Purpose,
    bool HumanInteractionAvailable,
    bool HasIdempotencyKey,
    TurnAccountingHandle? AccountingHandle);

/// <summary>Correlation metadata carried on every semantic turn event.</summary>
internal sealed record TurnEventCorrelation(
    Guid RunId,
    long Sequence,
    int ProviderAttempt,
    int ModelRound,
    string? ModelCallId,
    string? ToolCallId,
    DateTimeOffset Timestamp);
