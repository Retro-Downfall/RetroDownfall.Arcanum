using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Core.Conclave;

/// <summary>
/// In-process Chronicle event. <see cref="WizardEvent"/> is a pass-through carrier only;
/// SSE serialization spreads its fields flat (see ChronicleSseWriter).
/// </summary>
public sealed record ApprenticeEvent
{

    public ApprenticeEventType Type { get; init; }

    public Guid ApprenticeId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public string? Name { get; init; }

    public string? Goal { get; init; }

    public IReadOnlyList<PlanStep>? Plan { get; init; }

    public int? StepIndex { get; init; }

    public string? Description { get; init; }

    public string? Result { get; init; }

    public long? DurationMs { get; init; }

    public string? Error { get; init; }

    public string? Summary { get; init; }

    public long? TotalDurationMs { get; init; }

    public int? AtStep { get; init; }

    public int? FromStep { get; init; }

    public int? Attempt { get; init; }

    public long? BackoffMs { get; init; }

    /// <summary>
    /// Remote A2A task state on a Sending frame, in its protocol spelling (<c>working</c>,
    /// <c>input-required</c>, …). Only set on <c>sendingDispatched</c>/<c>sendingProgress</c> and the
    /// terminal Sending frames (issue #61).
    /// </summary>
    public string? SendingState { get; init; }

    /// <summary>Which side of the Conclave door a Sending frame describes: <c>outbound</c> or <c>inbound</c>.</summary>
    public string? SendingDirection { get; init; }

    /// <summary>
    /// Whether the peer reported what a delegated Sending cost. <c>false</c> means unknown, never free
    /// (issue #60).
    /// </summary>
    public bool? RemoteCostKnown { get; init; }

    /// <summary>Peer-reported total tokens for a delegated Sending, when known.</summary>
    public long? RemoteTotalTokens { get; init; }

    /// <summary>Peer-reported cost in USD for a delegated Sending, when known.</summary>
    public decimal? RemoteCostUsd { get; init; }

    public IntelligenceEvent? WizardEvent { get; init; }

}
