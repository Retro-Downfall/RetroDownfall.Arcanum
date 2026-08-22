using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IInstallationResetHostHandoffCoordinator
{

    Task<Result> BeginOrRecoverAsync(
        InstallationResetHostHandoff handoff,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

    Task<Result> RecordOnlineCompletionAsync(
        InstallationResetHostHandoff handoff,
        DataRetentionApplyResult result,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

    Task<Result> RetirePreEffectAsync(
        InstallationResetHostHandoff handoff,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

}

internal interface IInstallationResetDatabaseIdentityReader
{

    Task<Result<Guid>> ReadAsync(CancellationToken cancellationToken = default);

}

internal sealed class InstallationResetDatabaseIdentityReader(
    ICovenantConnectionSource connectionSource)
    : IInstallationResetDatabaseIdentityReader
{

    private readonly ICovenantConnectionSource _connectionSource =
        connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));

    public async Task<Result<Guid>> ReadAsync(
        CancellationToken cancellationToken = default)
    {

        try
        {

            SqliteConnection connection = await _connectionSource
                .GetOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ReadOpenConnectionAsync(
                connection,
                cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidCastException
                or InvalidOperationException
                or OverflowException)
        {

            return Unavailable<Guid>();

        }

    }

    internal static async Task<Result<Guid>> ReadOpenConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT StateKey, InstallationIdentity FROM covenant_authority_state ORDER BY StateKey LIMIT 2;";

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        long? stateKey = null;

        string? storedIdentity = null;

        int rowCount = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rowCount++;

            if (rowCount == 1)
            {

                stateKey = reader.IsDBNull(0) ? null : reader.GetInt64(0);

                storedIdentity = reader.IsDBNull(1) ? null : reader.GetString(1);

            }

        }

        Guid installationId = Guid.Empty;

        bool canonical = storedIdentity is not null
            && Guid.TryParseExact(storedIdentity, "D", out installationId)
            && installationId != Guid.Empty
            && string.Equals(
                installationId.ToString("D").ToUpperInvariant(),
                storedIdentity,
                StringComparison.Ordinal);

        return rowCount == 1 && stateKey == 1 && canonical
            ? installationId
            : Unavailable<Guid>();

    }

    private static Result<T> Unavailable<T>() =>
        new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The installation identity could not be authenticated from the Grimoire authority row.");

}

internal sealed class InstallationResetHostHandoffCoordinator(
    InstallationResetActiveStore activeStore,
    IInstallationResetDatabaseIdentityReader identityReader)
    : IInstallationResetHostHandoffCoordinator
{

    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(5);

    private readonly InstallationResetActiveStore _activeStore =
        activeStore ?? throw new ArgumentNullException(nameof(activeStore));

    private readonly IInstallationResetDatabaseIdentityReader _identityReader =
        identityReader ?? throw new ArgumentNullException(nameof(identityReader));

    public async Task<Result> BeginOrRecoverAsync(
        InstallationResetHostHandoff handoff,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(handoff);

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        Result<Guid> installation = await _identityReader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (installation.IsFailure)
        {

            return Result.Failure(installation.Error);

        }

        Result<InstallationResetActiveRecoveryState> recovered = await _activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result.Failure(recovered.Error);

        }

        if (recovered.Value.Outcome is InstallationResetActiveRecoveryOutcome.NoActiveRecord)
        {

            InstallationResetActiveRecord prepared = Prepared(handoff);

            Result<InstallationResetActivePublication> begun = await _activeStore
                .BeginAsync(
                    heldInstallationLock,
                    installation.Value,
                    prepared,
                    cancellationToken)
                .ConfigureAwait(false);

            return begun.IsSuccess
                ? Result.Success()
                : Result.Failure(begun.Error);

        }

        if (recovered.Value.Outcome is InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            && recovered.Value.Publication is { } publication)
        {

            return MatchesPrepared(publication.Payload.ToRecord(), handoff)
                ? Result.Success()
                : Mismatch();

        }

        if (recovered.Value.Outcome is InstallationResetActiveRecoveryOutcome.LegacyV1
            && recovered.Value.LegacyRecord is { } legacy
            && recovered.Value.LegacyFileIdentity is { } legacyIdentity
            && MatchesPrepared(legacy, handoff))
        {

            Result<InstallationResetActivePublication> migrated = await _activeStore
                .MigrateLegacyV1Async(
                    heldInstallationLock,
                    installation.Value,
                    legacy,
                    legacyIdentity,
                    cancellationToken)
                .ConfigureAwait(false);

            return migrated.IsSuccess
                && MatchesPrepared(migrated.Value.Payload.ToRecord(), handoff)
                    ? Result.Success()
                    : migrated.IsFailure
                        ? Result.Failure(migrated.Error)
                        : Mismatch();

        }

        return Mismatch();

    }

    public async Task<Result> RecordOnlineCompletionAsync(
        InstallationResetHostHandoff handoff,
        DataRetentionApplyResult result,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(handoff);

        ArgumentNullException.ThrowIfNull(result);

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        if (!TrustedCompletion(handoff, result))
        {

            return ReconciliationFailure();

        }

        using CancellationTokenSource checkpoint = CreateCheckpointToken();

        Result<InstallationResetActiveRecoveryState> recovered;

        try
        {

            recovered = await _activeStore
                .RecoverAsync(heldInstallationLock, checkpoint.Token)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return ReconciliationFailure();

        }

        if (recovered.IsFailure
            || recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } publication)
        {

            return recovered.IsFailure
                ? Result.Failure(recovered.Error)
                : Mismatch();

        }

        InstallationResetActiveRecord current = publication.Payload.ToRecord();

        if (!MatchesPrepared(current, handoff))
        {

            return Mismatch();

        }

        InstallationResetOnlineDataCompletion completion = new(
            result.OperationId,
            handoff.RequestedOperationId,
            result.PlanId,
            result.RowsDeleted,
            result.FilesDeleted,
            result.EstimatedBytesDeleted,
            result.DerivedRecordsDeleted);

        if (current.OnlineDataCompletion is { } existing)
        {

            bool sameIdentity = existing.ServerOperationId == completion.ServerOperationId
                && existing.RequestedOperationId == completion.RequestedOperationId
                && string.Equals(
                    existing.DataPlanId,
                    completion.DataPlanId,
                    StringComparison.Ordinal);

            bool replayCounts = result.RowsDeleted == 0
                && result.FilesDeleted == 0
                && result.EstimatedBytesDeleted == 0
                && result.DerivedRecordsDeleted == 0;

            return sameIdentity && (existing == completion || replayCounts)
                ? Result.Success()
                : ReconciliationFailure();

        }

        InstallationResetActiveRecord next = current with
        {
            OnlineDataCompletion = completion,
        };

        Result<InstallationResetActivePublication> advanced;

        try
        {

            advanced = await _activeStore
                .AdvanceAsync(
                    heldInstallationLock,
                    publication,
                    next,
                    checkpoint.Token)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return ReconciliationFailure();

        }

        return advanced.IsSuccess
            ? Result.Success()
            : Result.Failure(advanced.Error);

    }

    public async Task<Result> RetirePreEffectAsync(
        InstallationResetHostHandoff handoff,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(handoff);

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        using CancellationTokenSource checkpoint = CreateCheckpointToken();

        Result<InstallationResetActiveRecoveryState> recovered;

        try
        {

            recovered = await _activeStore
                .RecoverAsync(heldInstallationLock, checkpoint.Token)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return ReconciliationFailure();

        }

        if (recovered.IsFailure
            || recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } publication)
        {

            return recovered.IsFailure
                ? Result.Failure(recovered.Error)
                : Mismatch();

        }

        InstallationResetActiveRecord current = publication.Payload.ToRecord();

        if (!MatchesPrepared(current, handoff)
            || current.OnlineDataCompletion is not null
            || current.PointOfNoReturn
            || current.RowsDeleted != 0
            || current.FilesDeleted != 0
            || current.EstimatedBytesDeleted != 0
            || current.CredentialResults.Length != 0
            || current.LastErrorCode is not null)
        {

            return Mismatch();

        }

        try
        {

            return await _activeStore
                .RetireAsync(
                    heldInstallationLock,
                    handoff.RequestedOperationId,
                    checkpoint.Token)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return ReconciliationFailure();

        }

    }

    private static InstallationResetActiveRecord Prepared(
        InstallationResetHostHandoff handoff) =>
        new(
            InstallationResetActiveStore.CurrentVersion,
            handoff.RequestedOperationId,
            handoff.InstallationPlanId,
            handoff.Scope,
            handoff.Workspace,
            handoff.AcceptedBinding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure);

    private static bool MatchesPrepared(
        InstallationResetActiveRecord current,
        InstallationResetHostHandoff handoff) =>
        current.OperationId == handoff.RequestedOperationId
        && string.Equals(current.PlanId, handoff.InstallationPlanId, StringComparison.Ordinal)
        && current.Scope == handoff.Scope
        && current.Workspace == handoff.Workspace
        && SameBinding(current.AcceptedBinding, handoff.AcceptedBinding)
        && current.Phase is InstallationResetPhase.Prepared
        && !current.PointOfNoReturn
        && current.DataHandoff is InstallationResetDataHandoff.HostFactoryErasure;

    private static bool SameBinding(
        InstallationResetAcceptedBinding current,
        InstallationResetAcceptedBinding expected) =>
        string.Equals(current.BindingId, expected.BindingId, StringComparison.Ordinal)
        && current.SelectedRoots.SequenceEqual(expected.SelectedRoots, StringComparer.Ordinal)
        && current.ExcludedRoots.SequenceEqual(expected.ExcludedRoots, StringComparer.Ordinal)
        && current.PreservedBackups.SequenceEqual(expected.PreservedBackups)
        && current.CredentialAccounts.SequenceEqual(
            expected.CredentialAccounts,
            StringComparer.Ordinal)
        && current.DataPlanIds.SequenceEqual(expected.DataPlanIds, StringComparer.Ordinal);

    private static bool TrustedCompletion(
        InstallationResetHostHandoff handoff,
        DataRetentionApplyResult result) =>
        handoff.RequestedOperationId != Guid.Empty
        && result.OperationId != Guid.Empty
        && result.OperationId != handoff.RequestedOperationId
        && result.RequestedOperationId == handoff.RequestedOperationId
        && handoff.AcceptedBinding.DataPlanIds.Length == 1
        && string.Equals(
            result.PlanId,
            handoff.AcceptedBinding.DataPlanIds[0],
            StringComparison.Ordinal)
        && result.Reconciled
        && result.Blockers.Length == 0
        && result.Conflicts.Length == 0
        && result.RowsDeleted >= 0
        && result.FilesDeleted >= 0
        && result.EstimatedBytesDeleted >= 0
        && result.DerivedRecordsDeleted >= 0;

    private static CancellationTokenSource CreateCheckpointToken() =>
        new(CheckpointTimeout);

    private static Result Mismatch() =>
        Result.Failure(new Error(
            ErrorCodes.Data.ResetInProgress,
            "A different installation reset owns the authenticated active evidence."));

    private static Result ReconciliationFailure() =>
        Result.Failure(new Error(
            ErrorCodes.Data.ReconciliationFailed,
            "The authenticated host data reset completion proof did not reconcile."));

}
