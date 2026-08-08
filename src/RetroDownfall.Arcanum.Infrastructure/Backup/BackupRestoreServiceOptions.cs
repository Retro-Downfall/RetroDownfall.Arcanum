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

    /// <summary>
    /// Invoked with each machine-local entry name before it is moved across the swap; throwing
    /// simulates the commit failing partway through preserving, which no phase hook can reach.
    /// </summary>
    internal Action<string>? BeforePreservedEntryMoveForTests { get; init; }

    /// <summary>
    /// Invoked before a reversal's directory renames; throwing simulates a filesystem fault during
    /// rollback, when the displaced installation is the only surviving copy.
    /// </summary>
    internal Action? BeforeReversalRenameForTests { get; init; }

}
