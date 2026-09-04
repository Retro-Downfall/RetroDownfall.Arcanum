using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one authority every database open of a closed period is performed under.
/// </summary>
/// <remarks>
/// A Covenant erasure closes ordinary admission and then still has to read and write the database it
/// is closing. Everything it does in that window goes through here: the closed lease that issues each
/// purpose-bound capability, the single process-wide maintenance lane those capabilities are spent
/// on, and the factory that turns them into connections. There is no second route, which is the whole
/// point — a second one is indistinguishable from no closed period at all.
///
/// <para>It carries no path and no mode. Both are decided by the gate from the purpose, so a caller
/// that could open the wrong file would have to name a purpose it does not have a capability for, and
/// the gate refuses that on the way back in.</para>
///
/// <para>The lane is held for the whole closed period rather than taken per open. The gate admits one
/// lane at a time process-wide, so acquiring and releasing around each phase would let an unreleased
/// lane from a failed phase wedge every phase after it; and the closed generation is what every
/// capability is bound to, so a second closure part-way through would invalidate the authority the
/// phases before it already ran under.</para>
/// </remarks>
internal sealed class CovenantClosedPeriodAuthority(
    IGrimoireExclusiveClosedLease closed,
    IGrimoireMaintenanceIoLane lane,
    IGrimoireMaintenanceConnectionFactory factory)
{

    private readonly IGrimoireExclusiveClosedLease _closed =
        closed ?? throw new ArgumentNullException(nameof(closed));

    private readonly IGrimoireMaintenanceIoLane _lane =
        lane ?? throw new ArgumentNullException(nameof(lane));

    private readonly IGrimoireMaintenanceConnectionFactory _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Opens the one transaction that empties the Covenant family.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenCanonicalErasureAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.CanonicalErasure,
            _factory.OpenJournalCanonicalErasureAsync,
            cancellationToken);

    /// <summary>Opens the connection a checked write-ahead-log truncation runs on.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenWalTruncationAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.WalTruncation,
            _factory.OpenJournalWalTruncationAsync,
            cancellationToken);

    /// <summary>Opens the connection vacuuming, exporting and journal restoration run on.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenCompactionAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.Compaction,
            _factory.OpenJournalCompactionAsync,
            cancellationToken);

    /// <summary>Opens the exported candidate, read-only, before the destination is touched.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenExportVerificationAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            _factory.OpenJournalExportVerificationAsync,
            cancellationToken);

    /// <summary>Opens the connection the empty search accelerator is prepared on.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenAcceleratorInitializationAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.AcceleratorInitialization,
            _factory.OpenJournalAcceleratorInitializationAsync,
            cancellationToken);

    /// <summary>Opens the immutable read that verifies the candidate without writing a sidecar.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenCandidateReopenAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.ReopenVerification,
            _factory.OpenJournalCandidateReopenAsync,
            cancellationToken);

    /// <summary>Opens the bounded read-only snapshot an inventory page is proved from.</summary>
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenInventorySnapshotAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.InventorySnapshot,
            _factory.OpenJournalInventorySnapshotAsync,
            cancellationToken);

    /// <summary>
    /// Attaches the export staging database to an open compaction connection.
    /// </summary>
    /// <remarks>
    /// An attach is not an open, so it carries no capability and no acquisition marker. What it does
    /// carry is the same refusal shape as everything else here: a lease that is not a compaction one
    /// has no business naming the staging file, and the path comes from the gate rather than the
    /// caller for the same reason every other path does.
    /// </remarks>
    internal async Task<Result> AttachExportStagingAsync(
        IGrimoireMaintenanceConnectionLease exportLease,
        string stagingPath,
        string passphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(exportLease);

        await using SqliteCommand command = exportLease.Connection.CreateCommand();

        command.CommandText = $"ATTACH DATABASE $path AS {ExportAlias} KEY $key;";

        _ = command.Parameters.AddWithValue("$path", stagingPath);

        _ = command.Parameters.AddWithValue("$key", passphrase);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    /// <summary>The schema name an export writes its candidate through.</summary>
    internal const string ExportAlias = "covenant_erasure_export";

    private async Task<Result<IGrimoireMaintenanceConnectionLease>> OpenAsync(
        CovenantMaintenanceConnectionPurpose purpose,
        Func<
            IGrimoireMaintenanceConnectionCapability,
            IGrimoireMaintenanceIoLane,
            CancellationToken,
            Task<Result<IGrimoireMaintenanceConnectionLease>>> open,
        CancellationToken cancellationToken)
    {

        Result<IGrimoireMaintenanceConnectionCapability> capability =
            _closed.IssueMaintenanceConnectionCapability(purpose, _lane);

        if (capability.IsFailure)
        {

            return Result<IGrimoireMaintenanceConnectionLease>.Failure(capability.Error);

        }

        await using IGrimoireMaintenanceConnectionCapability issued = capability.Value;

        return await open(issued, _lane, cancellationToken).ConfigureAwait(false);

    }

}
