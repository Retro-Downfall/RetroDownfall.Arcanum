using System.ComponentModel.DataAnnotations.Schema;

namespace RetroDownfall.Arcanum.Core.Conclave;

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

    /// <summary>
    /// Lineage to the Conclave Apprentice that cast this child via <c>cast_sending</c>, when any.
    /// Not a mapped column: the persisted source of truth is <see cref="ApprenticeCheckpoint.ParentApprenticeId"/>
    /// inside the <c>CheckpointData</c> JSON. Hydrated by the repository on load.
    /// </summary>
    [NotMapped]
    public Guid? ParentApprenticeId { get; set; }

}
