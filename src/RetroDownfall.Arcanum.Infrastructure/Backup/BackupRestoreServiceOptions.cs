using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed class BackupRestoreServiceOptions
{

    /// <summary>Embedding width this installation is configured for; derived vectors of any other width are rebuilt.</summary>
    public int EmbeddingDimensions { get; init; } = 1536;

    /// <summary>Overrides the measured destination free space so capacity refusal can be exercised.</summary>
    internal long? AvailableBytesOverrideForTests { get; init; }

    /// <summary>Invoked as each phase begins; throwing simulates a fault at that exact boundary.</summary>
    internal Action<BackupRestorePhase>? BeforePhaseForTests { get; init; }

}
