using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

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
    IGrimoireMaintenanceConnectionFactory factory,
    IGrimoireMaintenancePathAuthority paths,
    IGrimoireDbPassphraseSource passphrase)
{

    private readonly IGrimoireExclusiveClosedLease _closed =
        closed ?? throw new ArgumentNullException(nameof(closed));

    private readonly IGrimoireMaintenancePathAuthority _paths =
        paths ?? throw new ArgumentNullException(nameof(paths));

    private readonly IGrimoireDbPassphraseSource _passphrase =
        passphrase ?? throw new ArgumentNullException(nameof(passphrase));

    /// <summary>The compaction lease this closed period last opened, and the only one that may attach.</summary>
    private IGrimoireMaintenanceConnectionLease? _compaction;

    private readonly IGrimoireMaintenanceIoLane _lane =
        lane ?? throw new ArgumentNullException(nameof(lane));

    private readonly IGrimoireMaintenanceConnectionFactory _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Opens the one transaction that empties the Covenant family.</summary>
    [GrimoireConnectionAcquisitionRoute]
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenCanonicalErasureAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.CanonicalErasure,
            _factory.OpenJournalCanonicalErasureAsync,
            cancellationToken);

    /// <summary>Opens the connection a checked write-ahead-log truncation runs on.</summary>
    [GrimoireConnectionAcquisitionRoute]
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenWalTruncationAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.WalTruncation,
            _factory.OpenJournalWalTruncationAsync,
            cancellationToken);

    /// <summary>Opens the connection vacuuming, exporting and journal restoration run on.</summary>
    /// <remarks>
    /// The lease is remembered because it is the only one an export attach may name. Remembering the
    /// exact instance rather than a flag means a later compaction open replaces it, so a lease that
    /// has already been disposed cannot be used to reach the staging file afterwards.
    /// </remarks>
    [GrimoireConnectionAcquisitionRoute]
    internal async Task<Result<IGrimoireMaintenanceConnectionLease>> OpenCompactionAsync(
        CancellationToken cancellationToken)
    {

        Result<IGrimoireMaintenanceConnectionLease> opened = await OpenAsync(
            CovenantMaintenanceConnectionPurpose.Compaction,
            _factory.OpenJournalCompactionAsync,
            cancellationToken).ConfigureAwait(false);

        if (opened.IsSuccess)
        {

            _compaction = opened.Value;

        }

        return opened;

    }

    /// <summary>Opens the exported candidate, read-only, before the destination is touched.</summary>
    [GrimoireConnectionAcquisitionRoute]
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenExportVerificationAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            _factory.OpenJournalExportVerificationAsync,
            cancellationToken);

    /// <summary>Opens the connection the empty search accelerator is prepared on.</summary>
    [GrimoireConnectionAcquisitionRoute]
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenAcceleratorInitializationAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.AcceleratorInitialization,
            _factory.OpenJournalAcceleratorInitializationAsync,
            cancellationToken);

    /// <summary>Opens the immutable read that verifies the candidate without writing a sidecar.</summary>
    [GrimoireConnectionAcquisitionRoute]
    internal Task<Result<IGrimoireMaintenanceConnectionLease>> OpenCandidateReopenAsync(
        CancellationToken cancellationToken) =>
        OpenAsync(
            CovenantMaintenanceConnectionPurpose.ReopenVerification,
            _factory.OpenJournalCandidateReopenAsync,
            cancellationToken);

    /// <summary>Opens the bounded read-only snapshot an inventory page is proved from.</summary>
    [GrimoireConnectionAcquisitionRoute]
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
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(exportLease);

        // The refusal is the point, not the null check. An attach names a second database file on an
        // already-open connection, so a lease opened for anything but the compaction it belongs to
        // would be reaching past the one purpose its capability was issued for - which is the whole
        // guarantee the purpose-bound capability exists to make, and the one place it could
        // otherwise be walked around.
        if (!ReferenceEquals(exportLease, _compaction))
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    "Only this closed period's compaction lease may attach the export staging database."));

        }

        await using SqliteCommand command = exportLease.Connection.CreateCommand();

        command.CommandText = $"ATTACH DATABASE $path AS {ExportAlias} KEY $key;";

        _ = command.Parameters.AddWithValue("$path", _paths.ExportStagingDatabasePath);

        _ = command.Parameters.AddWithValue("$key", _passphrase.Passphrase);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    /// <summary>The schema name an export writes its candidate through.</summary>
    internal const string ExportAlias = "covenant_erasure_export";

    [GrimoireConnectionAcquisitionRoute]
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

        // Disposed only when the open did not take. A capability that was consumed has handed its
        // authority to a live tracked handle, and disposing it while that handle is open revokes the
        // handle underneath the connection the caller is about to use - which presents as a lease
        // whose connection is already closed, and is indistinguishable from a provider that refused.
        Result<IGrimoireMaintenanceConnectionLease> opened =
            await open(capability.Value, _lane, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {

            await capability.Value.DisposeAsync().ConfigureAwait(false);

        }

        return opened;

    }

}
