using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed partial class BackupRestoreCovenantCoordinator
{
    private sealed record ClosedDestinationMarkerCandidate(
        BackupRestoreCovenantSession Session,
        ArcanumMaintenanceLock InstallationLock,
        string GuardedDirectory,
        BackupRestoreJournalPublication Publication,
        BackupRestoreJournalPayloadV2 Payload,
        IdentityOwnedFileSystemArtifact Live,
        IdentityOwnedFileSystemArtifact? Database);

    /// <summary>
    /// Reads the destination's marker registry through an owned pre-swap handle. Only retained root
    /// capabilities leave this method; the database handle is closed before staging or displacement.
    /// </summary>
    private async Task<Result<CampaignPathRestoreCleanupInventory>> InventoryDestinationMarkersAsync(
        BackupRestoreCovenantSession session,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            heldInstallationLock.AssertHeldFor(guardedDirectory);

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                guardedDirectory, FileSystemObjectKind.Directory, out IdentityOwnedFileSystemArtifact live))
            {
                return DestinationMarkerInventoryUnavailable();
            }

            string databasePath = Path.Combine(guardedDirectory, "arcanum.db");

            IdentityOwnedFileSystemArtifact? database = null;

            // Enumerate the exact leaf to distinguish proved absence from an unreadable or linked
            // node. A malformed existing node is never permission to treat its registry as empty.
            if (Directory.EnumerateFileSystemEntries(guardedDirectory, "arcanum.db").Any())
            {
                if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    databasePath, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact captured))
                {
                    return DestinationMarkerInventoryUnavailable();
                }

                database = captured;
            }

            ClosedDestinationMarkerCandidate candidate = new(
                session, heldInstallationLock, guardedDirectory, session.Publication, session.Payload, live, database);

            if (ValidateClosedDestinationMarkerCandidate(candidate).IsFailure)
            {
                return DestinationMarkerInventoryUnavailable();
            }

            if (database is not { } destination)
            {
                return new CampaignPathRestoreCleanupInventory([]);
            }

            SecretStoreReadResult secret = await _secretStore
                .GetGrimoireEncryptionSecretReadResultAsync().ConfigureAwait(false);

            if (secret.Status != SecretStoreReadStatus.Ok || string.IsNullOrEmpty(secret.Value)
                || ValidateClosedDestinationMarkerCandidate(candidate).IsFailure)
            {
                return DestinationMarkerInventoryUnavailable();
            }

            CampaignPathRestoreCleanupInventory? ownedInventory = null;

            try
            {
                Result<CampaignPathRestoreCleanupInventory> inventory;

                await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
                    destination.Path, secret.Value, readOnly: true, cancellationToken).ConfigureAwait(false))
                {
                    if (ValidateClosedDestinationMarkerCandidate(candidate).IsFailure)
                    {
                        return DestinationMarkerInventoryUnavailable();
                    }

                    inventory = await _services.Markers
                        .InventoryRestoreCleanupAsync(session.Owner, connection, cancellationToken).ConfigureAwait(false);

                    if (inventory.IsSuccess)
                    {
                        ownedInventory = inventory.Value;
                    }

                    if (ValidateClosedDestinationMarkerCandidate(candidate).IsFailure)
                    {
                        return DestinationMarkerInventoryUnavailable();
                    }
                }

                // Transfer the root capabilities only after the read handle closed successfully.
                ownedInventory = null;

                return inventory;
            }
            finally
            {
                if (ownedInventory is not null)
                {
                    await ownedInventory.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException
            or System.Text.Json.JsonException or NotSupportedException or FormatException)
        {
            return DestinationMarkerInventoryUnavailable();
        }
    }

    private Result ValidateClosedDestinationMarkerCandidate(ClosedDestinationMarkerCandidate candidate) =>
        candidate.Session.Lease.ExecuteWhileHeld(() =>
        {
            candidate.InstallationLock.AssertHeldFor(candidate.GuardedDirectory);

            if (_services.Anchors.RequireCurrentPublication(candidate.InstallationLock, candidate.GuardedDirectory,
                    candidate.Session.Profile, candidate.Publication).IsFailure
                || candidate.Session.Dispositioned || candidate.Session.Publication != candidate.Publication
                || candidate.Session.Payload != candidate.Payload
                || candidate.Payload.Phase >= BackupRestorePhase.Commit
                || candidate.Payload.ConflictMode != BackupRestoreConflictMode.ReplaceInstallation
                || candidate.Payload.RecoveryOwner() is not { IsSuccess: true } owner
                || owner.Value != candidate.Session.Owner
                || candidate.Session.Lease.Snapshot.RecoveryOwner != candidate.Session.Owner
                || candidate.Session.Lease.Snapshot.Kind != CovenantLeaseKind.Exclusive
                || candidate.Session.Lease.Snapshot.Coverage != CovenantLeaseCoverage.Installation
                || BackupRestorePhysicalTopology.Reconcile(candidate.Payload, candidate.GuardedDirectory, allowEffects: false)
                    is not { IsSuccess: true, Value: BackupRestoreTopologyState.PreSwap }
                || !IdentityOwnedFileSystemCleanup.TryCapturePath(candidate.Live.Path, FileSystemObjectKind.Directory, out var live)
                || live.Metadata.Identity != candidate.Live.Metadata.Identity)
            {
                return DestinationMarkerInventoryUnavailable();
            }

            bool sameDatabase = candidate.Database is { } expected
                ? IdentityOwnedFileSystemCleanup.TryCapturePath(expected.Path, FileSystemObjectKind.RegularFile, out var database)
                    && database.Metadata == expected.Metadata
                : !Directory.EnumerateFileSystemEntries(candidate.GuardedDirectory, "arcanum.db").Any();

            return sameDatabase ? Result.Success() : DestinationMarkerInventoryUnavailable();
        });

    private static Error DestinationMarkerInventoryUnavailable() =>
        new(ErrorCodes.Covenant.Unavailable,
            "The restore could not prove and read this destination's Campaign marker registry under its exact exclusive owner; "
            + "it stopped before replacing the installation.");
}
