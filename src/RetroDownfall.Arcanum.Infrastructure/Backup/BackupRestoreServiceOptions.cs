using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The protected-transfer capabilities a selective import runs under when Covenant is enabled.
/// </summary>
/// <remarks>
/// Grouped and optional rather than constructor parameters, because with the gate off there is no
/// operation gate to drain and no transfer store to route through — and a restore that required them
/// anyway would fail on installations that never enabled the feature.
/// </remarks>
internal sealed record CovenantSelectiveImportServices(
    ICovenantOperationGate Gate,
    IProtectedArtifactTransferStore TransferStore);

internal sealed class BackupRestoreServiceOptions
{

    /// <summary>Embedding width this installation is configured for; derived vectors of any other width are rebuilt.</summary>
    public int EmbeddingDimensions { get; init; } = 1536;

    /// <summary>
    /// The protected transfer path, present only while <c>Arcanum:Features:Covenant</c> is on.
    /// </summary>
    /// <remarks>
    /// Absent means the installation never enabled Covenant, and selective import behaves exactly as
    /// it did before this slice. Present means every selective import is routed through the transfer
    /// store under one atomic compound lease, and a Campaign-bound Session without an explicit
    /// destination mapping is refused rather than silently unbound.
    /// </remarks>
    internal CovenantSelectiveImportServices? SelectiveImport { get; init; }

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
