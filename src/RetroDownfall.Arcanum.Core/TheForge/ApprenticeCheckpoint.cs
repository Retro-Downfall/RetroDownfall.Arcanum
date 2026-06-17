namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record ApprenticeCheckpoint
{

    public int CurrentStep { get; init; }

    public string? ConversationSummary { get; init; }

    public List<string> CompletedToolCallIds { get; init; } = new();

    public DateTimeOffset Timestamp { get; init; }

}
