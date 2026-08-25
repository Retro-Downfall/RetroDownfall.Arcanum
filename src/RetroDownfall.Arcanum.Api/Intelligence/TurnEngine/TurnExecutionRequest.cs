using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>Internal logical-run request. Does not carry leases or HTTP surface modes.</summary>
/// <remarks>
/// <see cref="InvocationContext"/> is the caller's authority classification, carried by reference from
/// the facade to the runner and on to commit. It is not nullable and has no default: a turn whose
/// authority was optional would eventually be a turn whose authority was forgotten (§10.12).
/// </remarks>
internal sealed record TurnExecutionRequest(
    PingRequest Request,
    ArcanumInvocationContext InvocationContext,
    TurnResponseMode ResponseMode,
    TurnPurpose Purpose,
    bool HumanInteractionAvailable,
    bool HasIdempotencyKey,
    TurnAccountingHandle? AccountingHandle);

/// <summary>Correlation metadata carried on every semantic turn event.</summary>
/// <remarks>
/// The run, the position within it, and when. A provider attempt, a model round, a model call id and
/// a tool call id used to sit here too, defaulted at the only place that mints a correlation; every
/// one of the eight production call sites took the default, and nothing anywhere read any of the
/// four back. Four fields that were always absent and never consulted are not correlation, they are a
/// shape a test could fill and production could not, so they are gone rather than left as a promise
/// this type does not keep.
/// </remarks>
internal sealed record TurnEventCorrelation(
    Guid RunId,
    long Sequence,
    DateTimeOffset Timestamp);
