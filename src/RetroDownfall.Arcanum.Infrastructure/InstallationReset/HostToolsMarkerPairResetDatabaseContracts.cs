using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal enum HostToolsDatabaseMarkerRecoveryObservation : byte
{

    OriginalTainted = 1,

    SameInstallationClean = 2,

}

internal interface IHostToolsMarkerPairResetDatabase
{

    Task<Result<HostToolsMarkerPairResetDatabaseSession>>
        OpenHostToolsMarkerPairResetDatabaseSessionAsync(
        IStoppedHostGrimoireConnectionAuthority authority,
        CancellationToken cancellationToken);

}

internal sealed class HostToolsDatabaseMarkerCompareDeleteCapability
{

    internal HostToolsDatabaseMarkerCompareDeleteCapability(
        HostToolsMarkerPairResetDatabaseSession issuer,
        object creationTicket)
    {

        ArgumentNullException.ThrowIfNull(issuer);

        ArgumentNullException.ThrowIfNull(creationTicket);

        if (!issuer.TryConsumeCapabilityCreationTicket(creationTicket))
        {

            throw new InvalidOperationException(
                "The host-tools database marker capability is unavailable.");

        }

    }

}

internal static class HostToolsDatabaseMarkerProjectionReader
{

    internal static async Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT StateKey,
                       InstallationIdentity,
                       HostToolsStateCode,
                       TransitionId,
                       TaintTimeMasterVersion,
                       TaintFingerprint
                FROM covenant_authority_state;
                """;

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.FieldCount != 6)
            {

                return Failure<HostProcessToolsDatabaseMarkerEvidence>();

            }

            object[] rawValues = new object[6];

            string[] storageClasses = new string[6];

            for (int index = 0; index < rawValues.Length; index++)
            {

                rawValues[index] = reader.GetValue(index) is byte[] bytes
                    ? bytes.ToArray()
                    : reader.GetValue(index);

                storageClasses[index] = reader.IsDBNull(index)
                    ? "null"
                    : reader.GetDataTypeName(index).ToLowerInvariant();

            }

            if (rawValues[0] is not long stateKey
                || stateKey != 1
                || storageClasses[0] != "integer"
                || rawValues[1] is not string installationIdentity
                || storageClasses[1] != "text"
                || rawValues[2] is not long stateCode
                || stateCode is not ((long)CovenantHostToolsState.Clean)
                    and not ((long)CovenantHostToolsState.PendingHostToolsTaint)
                    and not ((long)CovenantHostToolsState.HostToolsTainted)
                || storageClasses[2] != "integer")
            {

                return Failure<HostProcessToolsDatabaseMarkerEvidence>();

            }

            CovenantHostToolsState state = (CovenantHostToolsState)stateCode;

            Guid? transitionId = null;

            ulong? taintVersion = null;

            CovenantDigest? taintFingerprint = null;

            if (state is CovenantHostToolsState.Clean)
            {

                if (rawValues[3] is not DBNull
                    || rawValues[4] is not DBNull
                    || rawValues[5] is not DBNull)
                {

                    return Failure<HostProcessToolsDatabaseMarkerEvidence>();

                }

            }
            else
            {

                if (rawValues[3] is not string transitionText
                    || storageClasses[3] != "text"
                    || !Guid.TryParse(transitionText, out Guid transition)
                    || transition == Guid.Empty
                    || !HostProcessToolsTaintVersionStorage.TryDecode(
                        rawValues[4],
                        out taintVersion)
                    || taintVersion is null
                    || storageClasses[4] is not ("integer" or "blob")
                    || rawValues[5] is not byte[] { Length: 32 } fingerprint
                    || storageClasses[5] != "blob")
                {

                    return Failure<HostProcessToolsDatabaseMarkerEvidence>();

                }

                transitionId = transition;

                taintFingerprint = new CovenantDigest(fingerprint);

            }

            HostProcessToolsDatabaseMarkerEvidence evidence = new(
                installationIdentity,
                state,
                transitionId,
                taintVersion,
                taintFingerprint);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return Failure<HostProcessToolsDatabaseMarkerEvidence>();

            }

            return Result<HostProcessToolsDatabaseMarkerEvidence>.Success(evidence);

        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {

            return Failure<HostProcessToolsDatabaseMarkerEvidence>();

        }

    }

    private static Result<T> Failure<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The host-tools database marker evidence is unavailable."));

}

internal interface IHostToolsMarkerPairResetDatabaseTestSeam
{

    void BeforeRollback();

    ValueTask AfterMarkerClearAsync(CancellationToken callerCancellationToken);

    ValueTask BeforeCommitAsync(CancellationToken checkpointCancellationToken);

}

internal sealed class NoopHostToolsMarkerPairResetDatabaseTestSeam
    : IHostToolsMarkerPairResetDatabaseTestSeam
{

    internal static NoopHostToolsMarkerPairResetDatabaseTestSeam Instance { get; } = new();

    private NoopHostToolsMarkerPairResetDatabaseTestSeam()
    {
    }

    public void BeforeRollback()
    {
    }

    public ValueTask AfterMarkerClearAsync(CancellationToken callerCancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask BeforeCommitAsync(CancellationToken checkpointCancellationToken) =>
        ValueTask.CompletedTask;

}

internal sealed class HostToolsMarkerPairResetDatabaseSession : IAsyncDisposable
{

    private readonly SqliteConnection _connection;

    private readonly IStoppedHostGrimoireConnectionLease _lease;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IHostToolsMarkerPairResetDatabaseTestSeam _testSeam;

    private readonly TimeSpan _checkpointTimeout;

    private HostToolsDatabaseMarkerCompareDeleteCapability? _activeCapability;

    private AttemptProjection? _activeProjection;

    private bool _disposed;

    private bool _transactionActive;

    internal HostToolsMarkerPairResetDatabaseSession(
        IStoppedHostGrimoireConnectionLease lease,
        HostToolsMarkerPairResetDatabase owner,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam,
        TimeSpan checkpointTimeout,
        object creationTicket)
    {

        ArgumentNullException.ThrowIfNull(lease);

        SqliteConnection connection = lease.Connection;

        ArgumentNullException.ThrowIfNull(owner);

        ArgumentNullException.ThrowIfNull(testSeam);

        if (checkpointTimeout <= TimeSpan.Zero
            || checkpointTimeout > TimeSpan.FromSeconds(5))
        {

            throw new ArgumentOutOfRangeException(nameof(checkpointTimeout));

        }

        ArgumentNullException.ThrowIfNull(creationTicket);

        if (!owner.TryConsumeSessionCreationTicket(connection, creationTicket))
        {

            throw new InvalidOperationException(
                "The host-tools database marker session is unavailable.");

        }

        _lease = lease;

        _connection = connection;

        _testSeam = testSeam;

        _checkpointTimeout = checkpointTimeout;

    }

    internal SqliteConnection BorrowCoreConnection()
    {

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        return _connection;

    }

    internal Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadTaintedAsync(
        CancellationToken cancellationToken) =>
        WithGateAsync(
            () => ReadTaintedCoreAsync(cancellationToken),
            cancellationToken);

    internal Task<Result<HostToolsDatabaseMarkerRecoveryObservation>>
        ObserveExpectedOrCleanAsync(
            HostProcessToolsDatabaseMarkerEvidence expected,
            CancellationToken cancellationToken) =>
        WithGateAsync(
            () => ObserveExpectedOrCleanCoreAsync(expected, cancellationToken),
            cancellationToken);

    internal Task<Result<HostToolsDatabaseMarkerCompareDeleteCapability>>
        BeginImmediateAndCaptureAsync(
            HostProcessToolsDatabaseMarkerEvidence expected,
            CancellationToken cancellationToken) =>
        WithGateAsync(
            () => BeginImmediateAndCaptureCoreAsync(expected, cancellationToken),
            cancellationToken);

    internal Task<Result> CompareClearCommitAndProveDurableAsync(
        HostToolsDatabaseMarkerCompareDeleteCapability capability,
        CancellationToken cancellationToken) =>
        WithGateForCompareAsync(
            () => CompareClearCommitAndProveDurableCoreAsync(
                capability,
                cancellationToken));

    internal Task<Result> ProveSameInstallationCleanDurableAsync(
        string expectedInstallationIdentity,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            () => ProveSameInstallationCleanDurableCoreAsync(
                expectedInstallationIdentity,
                cancellationToken),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {

            if (_disposed)
            {

                return;

            }

            if (_transactionActive)
            {

                _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            }

        }
        finally
        {

            _disposed = true;

            InvalidateAttempt();

            try
            {

                await _lease.DisposeAsync().ConfigureAwait(false);

            }
            catch (Exception)
            {

                // A provider disposal failure cannot disclose provider diagnostics.

            }

            _gate.Release();

        }

    }

    private async Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadTaintedCoreAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        try
        {

            Result<HostProcessToolsDatabaseMarkerEvidence> evidence =
                await HostToolsDatabaseMarkerProjectionReader.ReadAsync(
                    _connection,
                    cancellationToken)
                    .ConfigureAwait(false);

            return evidence.IsFailure
                ? evidence
                : evidence.Value.State
                    is not CovenantHostToolsState.HostToolsTainted
                    ? Failure<HostProcessToolsDatabaseMarkerEvidence>()
                    : evidence;

        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {

            return Failure<HostProcessToolsDatabaseMarkerEvidence>();

        }

    }

    private async Task<Result<HostToolsDatabaseMarkerRecoveryObservation>>
        ObserveExpectedOrCleanCoreAsync(
            HostProcessToolsDatabaseMarkerEvidence expected,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(expected);

        cancellationToken.ThrowIfCancellationRequested();

        if (expected.State is not CovenantHostToolsState.HostToolsTainted)
        {

            return Failure<HostToolsDatabaseMarkerRecoveryObservation>();

        }

        try
        {

            Result<HostProcessToolsDatabaseMarkerEvidence> evidence =
                await HostToolsDatabaseMarkerProjectionReader.ReadAsync(
                    _connection,
                    cancellationToken)
                    .ConfigureAwait(false);

            if (evidence.IsFailure)
            {

                return Failure<HostToolsDatabaseMarkerRecoveryObservation>();

            }

            HostProcessToolsDatabaseMarkerEvidence observed = evidence.Value;

            if (EvidenceEquals(observed, expected))
            {

                return Result<HostToolsDatabaseMarkerRecoveryObservation>.Success(
                    HostToolsDatabaseMarkerRecoveryObservation.OriginalTainted);

            }

            return observed.State is CovenantHostToolsState.Clean
                && string.Equals(
                    observed.InstallationIdentity,
                    expected.InstallationIdentity,
                    StringComparison.Ordinal)
                ? Result<HostToolsDatabaseMarkerRecoveryObservation>.Success(
                    HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean)
                : Failure<HostToolsDatabaseMarkerRecoveryObservation>();

        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {

            return Failure<HostToolsDatabaseMarkerRecoveryObservation>();

        }

    }

    private async Task<Result<HostToolsDatabaseMarkerCompareDeleteCapability>>
        BeginImmediateAndCaptureCoreAsync(
            HostProcessToolsDatabaseMarkerEvidence expected,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(expected);

        cancellationToken.ThrowIfCancellationRequested();

        if (expected.State is not CovenantHostToolsState.HostToolsTainted)
        {

            return Failure<HostToolsDatabaseMarkerCompareDeleteCapability>();

        }

        if (_transactionActive)
        {

            return Failure<HostToolsDatabaseMarkerCompareDeleteCapability>();

        }

        try
        {

            await ExecuteAsync("BEGIN IMMEDIATE;", cancellationToken).ConfigureAwait(false);

            _transactionActive = true;

            Result<AttemptProjection> projection =
                await ReadProjectionForResetAsync(
                    _connection,
                    cancellationToken)
                    .ConfigureAwait(false);

            if (projection.IsFailure
                || !EvidenceEquals(projection.Value.Evidence, expected))
            {

                _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

                return Failure<HostToolsDatabaseMarkerCompareDeleteCapability>();

            }

            object creationTicket = new CapabilityCreationTicket();

            HostToolsDatabaseMarkerCompareDeleteCapability capability =
                new(this, creationTicket);

            _activeCapability = capability;

            _activeProjection = projection.Value;

            return Result<HostToolsDatabaseMarkerCompareDeleteCapability>.Success(
                capability);

        }
        catch (OperationCanceledException)
        {

            bool rolledBack =
                await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            if (!rolledBack)
            {

                return Failure<HostToolsDatabaseMarkerCompareDeleteCapability>();

            }

            throw;

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            return Failure<HostToolsDatabaseMarkerCompareDeleteCapability>();

        }

    }

    private async Task<Result> CompareClearCommitAndProveDurableCoreAsync(
        HostToolsDatabaseMarkerCompareDeleteCapability capability,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(capability);

        if (!_transactionActive
            || !ReferenceEquals(capability, _activeCapability)
            || _activeProjection is not { } projection)
        {

            _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            return Result.Failure(IntegrityError());

        }

        _activeCapability = null;

        _activeProjection = null;

        object[] rawValues = projection.RawValues;

        string[] storageClasses = projection.StorageClasses;

        if (cancellationToken.IsCancellationRequested)
        {

            bool rolledBack =
                await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            if (!rolledBack)
            {

                return Result.Failure(IntegrityError());

            }

            cancellationToken.ThrowIfCancellationRequested();

        }

        try
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = """
                UPDATE covenant_authority_state
                SET HostToolsStateCode = 1,
                    TransitionId = NULL,
                    TaintTimeMasterVersion = NULL,
                    TaintFingerprint = NULL
                WHERE StateKey IS $raw0 AND typeof(StateKey) = $type0
                  AND InstallationIdentity IS $raw1 AND typeof(InstallationIdentity) = $type1
                  AND HostToolsStateCode IS $raw2 AND typeof(HostToolsStateCode) = $type2
                  AND TransitionId IS $raw3 AND typeof(TransitionId) = $type3
                  AND TaintTimeMasterVersion IS $raw4 AND typeof(TaintTimeMasterVersion) = $type4
                  AND TaintFingerprint IS $raw5 AND typeof(TaintFingerprint) = $type5;
                """;

            for (int index = 0; index < rawValues.Length; index++)
            {

                _ = command.Parameters.AddWithValue("$raw" + index, rawValues[index]);

                _ = command.Parameters.AddWithValue("$type" + index, storageClasses[index]);

            }

            int affected = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (affected != 1)
            {

                _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

                return Result.Failure(IntegrityError());

            }

        }
        catch (OperationCanceledException)
        {

            bool rolledBack =
                await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            if (!rolledBack)
            {

                return Result.Failure(IntegrityError());

            }

            throw;

        }
        catch (Exception)
        {

            _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            return Result.Failure(IntegrityError());

        }

        try
        {

            await _testSeam.AfterMarkerClearAsync(cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception)
        {

            _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            return Result.Failure(IntegrityError());

        }

        using CancellationTokenSource checkpoint = new(_checkpointTimeout);

        try
        {

            await _testSeam.BeforeCommitAsync(checkpoint.Token).ConfigureAwait(false);

            await ExecuteAsync("COMMIT;", checkpoint.Token).ConfigureAwait(false);

            _transactionActive = false;

            InvalidateAttempt();

            Result truncated =
                await TruncateWalAsync(checkpoint.Token).ConfigureAwait(false);

            if (truncated.IsFailure)
            {

                return Result.Failure(IntegrityError());

            }

            return await ProveSameInstallationCleanAsync(
                (string)rawValues[1],
                checkpoint.Token).ConfigureAwait(false);

        }
        catch (Exception)
        {

            if (_transactionActive)
            {

                _ = await TryRollbackAndInvalidateAsync().ConfigureAwait(false);

            }

            return Result.Failure(IntegrityError());

        }

    }

    private async Task<Result> TruncateWalAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.FieldCount != 3)
        {

            return Result.Failure(IntegrityError());

        }

        CovenantWalCheckpointOutcome outcome =
            CovenantWalCheckpointOutcome.Project(reader);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result.Failure(IntegrityError());

        }

        Result truncated = outcome.RequireTruncated();

        return truncated.IsSuccess
            ? Result.Success()
            : Result.Failure(IntegrityError());

    }

    private async Task<Result> ProveSameInstallationCleanDurableCoreAsync(
        string expectedInstallationIdentity,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(expectedInstallationIdentity);

        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource checkpoint = new(_checkpointTimeout);

        try
        {

            Result truncated =
                await TruncateWalAsync(checkpoint.Token).ConfigureAwait(false);

            return truncated.IsFailure
                ? Result.Failure(IntegrityError())
                : await ProveSameInstallationCleanAsync(
                    expectedInstallationIdentity,
                    checkpoint.Token).ConfigureAwait(false);

        }
        catch (Exception)
        {

            return Result.Failure(IntegrityError());

        }

    }

    private async Task<Result> ProveSameInstallationCleanAsync(
        string expectedInstallationIdentity,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT StateKey,
                   InstallationIdentity,
                   HostToolsStateCode,
                   TransitionId,
                   TaintTimeMasterVersion,
                   TaintFingerprint
            FROM covenant_authority_state;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result.Failure(IntegrityError());

        }

        if (reader.FieldCount != 6
            || reader.GetValue(0) is not long stateKey
            || stateKey != 1
            || !string.Equals(
                reader.GetDataTypeName(0),
                "integer",
                StringComparison.OrdinalIgnoreCase)
            || reader.GetValue(1) is not string installationIdentity
            || !string.Equals(
                installationIdentity,
                expectedInstallationIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                reader.GetDataTypeName(1),
                "text",
                StringComparison.OrdinalIgnoreCase)
            || reader.GetValue(2) is not long stateCode
            || stateCode != (long)CovenantHostToolsState.Clean
            || !string.Equals(
                reader.GetDataTypeName(2),
                "integer",
                StringComparison.OrdinalIgnoreCase)
            || !reader.IsDBNull(3)
            || !reader.IsDBNull(4)
            || !reader.IsDBNull(5))
        {

            return Result.Failure(IntegrityError());

        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result.Failure(IntegrityError());

        }

        return Result.Success();

    }

    private static async Task<Result<AttemptProjection>> ReadProjectionForResetAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT StateKey,
                   InstallationIdentity,
                   HostToolsStateCode,
                   TransitionId,
                   TaintTimeMasterVersion,
                   TaintFingerprint
            FROM covenant_authority_state;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.FieldCount != 6)
        {

            return Failure<AttemptProjection>();

        }

        object[] rawValues = new object[6];

        string[] storageClasses = new string[6];

        for (int index = 0; index < rawValues.Length; index++)
        {

            rawValues[index] = reader.GetValue(index) is byte[] bytes
                ? bytes.ToArray()
                : reader.GetValue(index);

            storageClasses[index] = reader.IsDBNull(index)
                ? "null"
                : reader.GetDataTypeName(index).ToLowerInvariant();

        }

        if (rawValues[0] is not long stateKey
            || stateKey != 1
            || storageClasses[0] != "integer"
            || rawValues[1] is not string installationIdentity
            || storageClasses[1] != "text"
            || rawValues[2] is not long stateCode
            || stateCode != (long)CovenantHostToolsState.HostToolsTainted
            || storageClasses[2] != "integer"
            || rawValues[3] is not string transitionText
            || storageClasses[3] != "text"
            || !Guid.TryParse(transitionText, out Guid transitionId)
            || transitionId == Guid.Empty
            || !HostProcessToolsTaintVersionStorage.TryDecode(
                rawValues[4],
                out ulong? taintVersion)
            || taintVersion is null
            || storageClasses[4] is not ("integer" or "blob")
            || rawValues[5] is not byte[] { Length: 32 } fingerprint
            || storageClasses[5] != "blob")
        {

            return Failure<AttemptProjection>();

        }

        HostProcessToolsDatabaseMarkerEvidence evidence = new(
            installationIdentity,
            CovenantHostToolsState.HostToolsTainted,
            transitionId,
            taintVersion.Value,
            new CovenantDigest(fingerprint));

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Failure<AttemptProjection>();

        }

        return Result<AttemptProjection>.Success(new AttemptProjection(
            evidence,
            rawValues,
            storageClasses));

    }

    private async Task<Result<T>> WithGateAsync<T>(
        Func<Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            return _disposed
                ? Failure<T>()
                : await action().ConfigureAwait(false);

        }
        finally
        {

            _gate.Release();

        }

    }

    private async Task<Result> WithGateAsync(
        Func<Task<Result>> action,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            return _disposed
                ? Result.Failure(IntegrityError())
                : await action().ConfigureAwait(false);

        }
        finally
        {

            _gate.Release();

        }

    }

    private async Task<Result> WithGateForCompareAsync(
        Func<Task<Result>> action)
    {

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {

            return _disposed
                ? Result.Failure(IntegrityError())
                : await action().ConfigureAwait(false);

        }
        finally
        {

            _gate.Release();

        }

    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    internal bool TryConsumeCapabilityCreationTicket(object creationTicket)
    {

        return creationTicket is CapabilityCreationTicket ticket
            && Interlocked.CompareExchange(ref ticket.Consumed, 1, 0) == 0;

    }

    private async Task<bool> TryRollbackAndInvalidateAsync()
    {

        bool rollbackSucceeded = !_transactionActive;

        try
        {

            if (_transactionActive)
            {

                _testSeam.BeforeRollback();

                await ExecuteAsync("ROLLBACK;", CancellationToken.None)
                    .ConfigureAwait(false);

                rollbackSucceeded = true;

            }

        }
        catch (Exception)
        {

            rollbackSucceeded = false;

            _disposed = true;

            try
            {

                await _connection.DisposeAsync().ConfigureAwait(false);

            }
            catch (Exception)
            {

                // A provider disposal failure cannot disclose provider diagnostics.

            }

        }
        finally
        {

            _transactionActive = false;

            InvalidateAttempt();

        }

        return rollbackSucceeded;

    }

    private void InvalidateAttempt()
    {

        _activeCapability = null;

        _activeProjection = null;

    }

    private static bool EvidenceEquals(
        HostProcessToolsDatabaseMarkerEvidence left,
        HostProcessToolsDatabaseMarkerEvidence right) =>
        string.Equals(
            left.InstallationIdentity,
            right.InstallationIdentity,
            StringComparison.Ordinal)
        && left.State == right.State
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && left.TaintFingerprint == right.TaintFingerprint
        && left.TaintIdentityDigest == right.TaintIdentityDigest
        && left.DatabaseMarkerDigest == right.DatabaseMarkerDigest;

    private static Result<T> Failure<T>() =>
        Result<T>.Failure(IntegrityError());

    private static Error IntegrityError() =>
        new(
            ErrorCodes.Covenant.IntegrityFailure,
            "The host-tools database marker evidence is unavailable.");

    private sealed record AttemptProjection(
        HostProcessToolsDatabaseMarkerEvidence Evidence,
        object[] RawValues,
        string[] StorageClasses);

    private sealed class CapabilityCreationTicket
    {

        internal int Consumed;

    }

}
