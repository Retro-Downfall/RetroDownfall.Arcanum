using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Incremental update from a streaming (or buffered) model call.</summary>
public abstract record ModelCallUpdate(ModelCallPurpose Purpose, string ModelCallId);

public sealed record ModelCallTextDelta(ModelCallPurpose Purpose, string ModelCallId, string Text)
    : ModelCallUpdate(Purpose, ModelCallId);

public sealed record ModelCallResponseUpdate(ModelCallPurpose Purpose, string ModelCallId, ChatResponseUpdate Update)
    : ModelCallUpdate(Purpose, ModelCallId);

public sealed record ModelCallUsageUpdate(ModelCallPurpose Purpose, string ModelCallId, UsageDetails? Usage)
    : ModelCallUpdate(Purpose, ModelCallId);

/// <summary>Final result of a buffered model call (or the combined streaming round).</summary>
public sealed record ModelCallResult(
    ModelCallPurpose Purpose,
    string ModelCallId,
    ChatResponse Response,
    UsageDetails? Usage);

/// <summary>Failure of a model call before a successful <see cref="ModelCallResult"/>.</summary>
public sealed record ModelCallFailure(
    ModelCallPurpose Purpose,
    string ModelCallId,
    Error Error,
    bool IsConnectivityFailure);
