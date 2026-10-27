using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The process-wide serialized owner of the warm Covenant disclosure connection.
/// </summary>
/// <remarks>
/// A disclosure acknowledgement commits before its external effect, so this writer cannot borrow a
/// scoped connection that an erasure has no way to name or stop. It owns one unpooled initialized
/// ordinary lease and admits one transaction at a time through the same serializer lifecycle
/// transitions use (§10.20.4).
/// </remarks>
internal sealed class CovenantDisclosureWriter :
    ICovenantDisclosureJournal,
    ICovenantDisclosureWriterLifecycle,
    IAsyncDisposable
{
    private readonly IGrimoireOrdinaryConnectionFactory _connections;

    private readonly ICovenantAvailability _availability;

    private readonly ICovenantDisclosureTransactionWriter _transactions;

    private readonly Action _observeReopen;

    private readonly Lock _state = new();

    private readonly SemaphoreSlim _serializer = new(1, 1);

    private IGrimoireOrdinaryConnectionLease? _connectionLease;

    private SqliteConnection? CoreConnection => _connectionLease?.Connection;

    private PreparationFailure? _fault;

    private PreparationFailure _preparationFailure = new(PreparationStage.Availability);

    private enum PreparationStage
    {
        Availability,
        Open,
        DatasetVerification,
        PublishedAuthority,
        CandidateRelease,
        ConnectionRelease,
    }

    private readonly record struct PreparationFailure(
        PreparationStage Stage,
        GrimoireConnectionFailureKind Kind = GrimoireConnectionFailureKind.Other,
        int? SqliteErrorCode = null,
        int? SqliteExtendedErrorCode = null,
        string CauseCode = "Other");

    private sealed class CandidateReleaseException() : Exception("The Covenant candidate release was not proved.");

    private readonly ILogger<CovenantDisclosureWriter> _logger;

    private bool _accepting = true;

    private bool _pendingClose;

    private bool _disposed;

    private long _closeRequestEpoch;

    private TaskCompletionSource<bool>? _disposal;

    internal CovenantDisclosureWriter(
        IGrimoireOrdinaryConnectionFactory connections,
        ICovenantAvailability availability,
        ICovenantDisclosureTransactionWriter transactions,
        ILogger<CovenantDisclosureWriter> logger,
        Action? observeReopen = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

        _availability = availability ?? throw new ArgumentNullException(nameof(availability));

        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));

        _observeReopen = observeReopen ?? NoObservation;
    }

    private static void NoObservation()
    {
    }

    public async ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category,
        ProviderCallSensitivity sensitivity,
        CancellationToken cancellationToken)
    {
        long admissionEpoch;

        lock (_state)
        {
            if (!CanAccept())
            {
                return Result<CovenantDisclosureReceipt>.Failure(AdmissionClosed());
            }

            admissionEpoch = _closeRequestEpoch;
        }

        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_state)
            {
                if (!CanAccept() || admissionEpoch != _closeRequestEpoch)
                {
                    return Result<CovenantDisclosureReceipt>.Failure(AdmissionClosed());
                }
            }

            if (CoreConnection is null)
            {
                Result opened;

                try
                {
                    opened = await OpenVerifiedAsync(admissionEpoch, cancellationToken).ConfigureAwait(false);
                }
                catch (CandidateReleaseException)
                {
                    FailClosed(admissionEpoch, _preparationFailure, ErrorCodes.Covenant.MaintenanceFailed);

                    return Result<CovenantDisclosureReceipt>.Failure(ReleaseFailure());
                }

                if (opened.IsFailure)
                {
                    // Prepared readiness may be exercised before maintenance releases ordinary
                    // admission. That refusal has no receipt or effect and must remain retryable.
                    if (opened.Error.Code != ErrorCodes.Grimoire.MaintenanceUnavailable)
                    {
                        FailClosed(admissionEpoch, _preparationFailure, opened.Error.Code);
                    }

                    return Result<CovenantDisclosureReceipt>.Failure(opened.Error);
                }
            }

            return await _transactions.AcknowledgeAsync(
                CoreConnection!,
                draft,
                category,
                sensitivity,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _serializer.Release();
        }
    }

    public async ValueTask<Result> QuiesceAsync(CancellationToken cancellationToken)
    {
        long requestEpoch;

        lock (_state)
        {
            if (_disposed)
            {
                return Result.Success();
            }

            _accepting = false;

            _pendingClose = true;

            _fault = null;

            requestEpoch = checked(++_closeRequestEpoch);
        }

        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Result closed = await CloseCurrentAsync().ConfigureAwait(false);

            lock (_state)
            {
                if (!_disposed && requestEpoch == _closeRequestEpoch)
                {
                    _pendingClose = false;
                }
            }

            return closed;
        }
        finally
        {
            _ = _serializer.Release();
        }
    }

    public async ValueTask<Result> ReopenAsync(CancellationToken cancellationToken)
    {
        _observeReopen();

        long reopenEpoch;

        lock (_state)
        {
            if (_disposed)
            {
                return Result.Failure(AdmissionClosed());
            }

            reopenEpoch = _closeRequestEpoch;
        }

        await _serializer.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_state)
            {
                if (_disposed || reopenEpoch != _closeRequestEpoch)
                {
                    return Result.Failure(AdmissionClosed());
                }
            }

            // A cancelled quiesce can leave the prior generation's handle behind while still
            // correctly closing admission. Never reuse it: replacement may have occurred before
            // this recovery attempt reached the serializer.
            Result normalized = await CloseCurrentAsync().ConfigureAwait(false);

            if (normalized.IsFailure)
            {
                FailClosed(reopenEpoch, _preparationFailure, normalized.Error.Code);

                return normalized;
            }

            _preparationFailure = new(PreparationStage.Availability);

            CovenantAvailabilitySnapshot published = _availability.Current;

            if (published.Canonical != CovenantCapabilityState.Healthy
                || published.DatasetGeneration is not { } dataset
                || dataset == Guid.Empty)
            {
                FailClosed(reopenEpoch, _preparationFailure, ErrorCodes.Covenant.Unavailable);

                return Result.Failure(new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "The Covenant disclosure writer has no healthy published dataset."));
            }

            lock (_state)
            {
                if (_disposed || reopenEpoch != _closeRequestEpoch)
                {
                    return Result.Failure(AdmissionClosed());
                }

                if (!ReferenceEquals(_availability.Current, published))
                {
                    return Result.Failure(new Error(
                        ErrorCodes.Covenant.StaleSnapshot,
                        "Covenant availability changed while the disclosure writer prepared."));
                }

                // No ordinary connection can be opened until both dispositions and authenticated
                // retirement finish. AcknowledgeAsync verifies its fresh handle before any receipt.
                _pendingClose = false;

                _accepting = true;

                _fault = null;
            }

            return Result.Success();
        }
        finally
        {
            _ = _serializer.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;

        bool startsDisposal = false;

        lock (_state)
        {
            if (_disposal is null)
            {
                _disposed = true;

                _accepting = false;

                _pendingClose = true;

                _ = checked(++_closeRequestEpoch);

                _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

                startsDisposal = true;
            }

            completion = _disposal;
        }

        if (startsDisposal)
        {
            _ = DisposeCoreAsync(completion);
        }

        return new ValueTask(completion.Task);
    }

    private bool CanAccept() => _accepting && !_pendingClose && !_disposed;

    private async Task<Result> OpenVerifiedAsync(
        long expectedCloseEpoch,
        CancellationToken cancellationToken)
    {
        _preparationFailure = new(PreparationStage.Availability);

        CovenantAvailabilitySnapshot published = _availability.Current;

        if (published.Canonical != CovenantCapabilityState.Healthy
            || published.DatasetGeneration is not { } expectedDataset
            || expectedDataset == Guid.Empty)
        {
            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "The Covenant disclosure writer has no healthy published dataset."));
        }

        IGrimoireOrdinaryConnectionLease? candidateLease = null;

        bool adopted = false;

        bool cancelled = false;

        try
        {
            _preparationFailure = new(PreparationStage.Open);

            Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                .OpenFreshAsync(
                    GrimoireOrdinaryFreshConnectionKind.ReadWrite,
                    cancellationToken)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                if (acquired.Error.ConnectionFailure is { } failure)
                {
                    _preparationFailure = new(PreparationStage.Open,
                        Enum.IsDefined(failure.Kind) ? failure.Kind : GrimoireConnectionFailureKind.Other,
                        failure.SqliteErrorCode, failure.SqliteExtendedErrorCode);
                }

                return Result.Failure(acquired.Error);
            }

            candidateLease = acquired.Value;

            SqliteConnection candidate = candidateLease.Connection;

            _preparationFailure = new(PreparationStage.DatasetVerification);

            Result<Guid> observed = await ReadDatasetGenerationAsync(candidate, cancellationToken)
                .ConfigureAwait(false);

            if (observed.IsFailure)
            {
                return Result.Failure(observed.Error);
            }

            if (observed.Value != expectedDataset)
            {
                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "The disclosure writer connection does not match the published Covenant dataset."));
            }

            lock (_state)
            {
                if (_disposed || expectedCloseEpoch != _closeRequestEpoch)
                {
                    return Result.Failure(AdmissionClosed());
                }

                // The handle was verified against one dataset on a healthy canonical tier, and that is
                // what has to still hold. Any other republication is routine, because every committed
                // write that moves the search position, and every maintenance batch, republishes
                // availability, and refusing it here would close the writer until the next reopen.
                _preparationFailure = new(PreparationStage.PublishedAuthority);

                CovenantAvailabilitySnapshot current = _availability.Current;

                if (current.Canonical != CovenantCapabilityState.Healthy
                    || current.DatasetGeneration != expectedDataset)
                {
                    return Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.StaleSnapshot,
                            "Covenant availability changed while the disclosure writer opened."));
                }

                if (!CanAccept())
                {
                    return Result.Failure(AdmissionClosed());
                }

                _connectionLease = candidateLease;

                adopted = true;
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;

            throw;
        }
        catch (Exception failure)
        {
            _preparationFailure = FromException(_preparationFailure.Stage, failure);

            return Result.Failure(MaintenanceFailure());
        }
        finally
        {
            if (!adopted)
            {
                Result released = await CleanupAsync(candidateLease).ConfigureAwait(false);

                if (released.IsFailure)
                {
                    // An unverified candidate is never usable for a receipt. Keep its lease solely
                    // so explicit lifecycle cleanup must prove release before admission can reopen.
                    _connectionLease = candidateLease;

                    _preparationFailure = _preparationFailure with { Stage = PreparationStage.CandidateRelease };

                    if (cancelled)
                    {
                        FailClosed(expectedCloseEpoch, _preparationFailure, ErrorCodes.Covenant.MaintenanceFailed);
                    }
                    else
                    {
                        throw new CandidateReleaseException();
                    }
                }
            }
        }
    }

    private async Task<Result> CloseCurrentAsync()
    {
        IGrimoireOrdinaryConnectionLease? lease = _connectionLease;

        Result released = await CleanupAsync(lease).ConfigureAwait(false);

        if (released.IsSuccess)
        {
            _connectionLease = null;
        }

        return released;
    }

    private async Task<Result> CleanupAsync(
        IGrimoireOrdinaryConnectionLease? lease)
    {
        bool failed = false;

        if (lease is not null)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _preparationFailure = FromException(PreparationStage.ConnectionRelease, failure);

                failed = true;
            }
        }

        return failed ? Result.Failure(ReleaseFailure()) : Result.Success();
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await _serializer.WaitAsync().ConfigureAwait(false);

            try
            {
                Result closed = await CloseCurrentAsync().ConfigureAwait(false);

                if (closed.IsFailure)
                {
                    throw new InvalidOperationException(
                        "The Covenant disclosure writer could not release its warm connection.");
                }

                lock (_state)
                {
                    _pendingClose = false;
                }
            }
            finally
            {
                _ = _serializer.Release();
            }

            _ = completion.TrySetResult(true);
        }
        catch (Exception failed)
        {
            _ = completion.TrySetException(failed);
        }
    }

    private void FailClosed(long expectedCloseEpoch, PreparationFailure reason, string causeCode)
    {
        PreparationFailure recorded = reason with { CauseCode = SafeCauseCode(causeCode) };

        lock (_state)
        {
            if (_disposed || expectedCloseEpoch != _closeRequestEpoch || _fault is not null)
            {
                return;
            }

            _accepting = false;

            _pendingClose = true;

            _ = checked(++_closeRequestEpoch);

            _fault = recorded;
        }

        _logger.LogWarning(
            "Covenant disclosure admission closed after a preparation failure. Stage={Stage} CauseCode={CauseCode} Failure={Failure} SQLite={SqliteErrorCode} Extended={SqliteExtendedErrorCode}. Inspect storage diagnostics before restoring the host.",
            recorded.Stage, recorded.CauseCode, recorded.Kind, recorded.SqliteErrorCode, recorded.SqliteExtendedErrorCode);
    }

    private static string SafeCauseCode(string code) => code switch
    {
        ErrorCodes.Covenant.Unavailable or ErrorCodes.Covenant.IntegrityFailure
            or ErrorCodes.Covenant.StaleSnapshot or ErrorCodes.Covenant.MaintenanceFailed
            or ErrorCodes.Covenant.LifecycleConflict => code,
        _ => "Other",
    };

    private static PreparationFailure FromException(PreparationStage stage, Exception failure) =>
        new(stage, CovenantStorageFailure.Classify(failure),
            (failure as SqliteException)?.SqliteErrorCode, (failure as SqliteException)?.SqliteExtendedErrorCode);

    private static Error ReleaseFailure() => new(ErrorCodes.Covenant.MaintenanceFailed,
        "The Covenant disclosure writer could not prove connection release. Preserve the installation and inspect storage diagnostics; admission must remain closed until explicit lifecycle cleanup succeeds.");

    private static async Task<Result<Guid>> ReadDatasetGenerationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is byte[] { Length: 16 } bytes
            ? Result<Guid>.Success(new Guid(bytes))
            : Result<Guid>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The Covenant disclosure writer could not read an exact dataset generation."));
    }

    private Error AdmissionClosed() =>
        !_disposed && _fault is { } fault
            ? new Error(ErrorCodes.Covenant.Unavailable,
                $"The Covenant disclosure writer is closed after an earlier preparation failure. Stage={fault.Stage} CauseCode={fault.CauseCode} Failure={fault.Kind}. "
                + "Inspect and resolve storage diagnostics, then restart the host when no active maintenance or recovery operation prohibits it.")
            : new Error(ErrorCodes.Covenant.LifecycleConflict,
                "The Covenant disclosure writer is closed for maintenance.");

    private static Error MaintenanceFailure() =>
        new(
            ErrorCodes.Covenant.MaintenanceFailed,
            "The Covenant disclosure writer could not prepare its warm connection safely.");
}
