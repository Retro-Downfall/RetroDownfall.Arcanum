using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Core.TheForge;

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

    public IntelligenceEvent? WizardEvent { get; init; }

}
