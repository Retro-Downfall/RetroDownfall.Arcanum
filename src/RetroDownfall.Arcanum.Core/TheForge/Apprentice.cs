namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed class Apprentice
{

    public Guid Id { get; set; }

    public Guid? CampaignId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Goal { get; set; } = string.Empty;

    public string Plan { get; set; } = "[]";

    public int CurrentStep { get; set; }

    public string Status { get; set; } = ApprenticeStatus.Idle.ToString();

    public Guid? SessionId { get; set; }

    public string WorkspacePath { get; set; } = string.Empty;

    public string? CheckpointData { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

}
