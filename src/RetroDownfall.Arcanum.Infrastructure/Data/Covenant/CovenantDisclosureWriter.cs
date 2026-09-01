using Microsoft.Data.Sqlite;

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

    private readonly Lock _state = new();

    private readonly SemaphoreSlim _serializer = new(1, 1);

    private IGrimoireOrdinaryConnectionLease? _connectionLease;

    private SqliteConnection? CoreConnection => _connectionLease?.Connection;

    private bool _accepting = true;

    private bool _pendingClose;

    private bool _disposed;

    private long _closeRequestEpoch;

    private TaskCompletionSource<bool>? _disposal;

    internal CovenantDisclosureWriter(
        IGrimoireOrdinaryConnectionFactory connections,
        ICovenantAvailability availability,
        ICovenantDisclosureTransactionWriter transactions)
    {

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

        _availability = availability ?? throw new ArgumentNullException(nameof(availability));

        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));

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

                Result opened = await OpenVerifiedAsync(
                    admissionEpoch,
                    openAdmission: false,
                    cancellationToken).ConfigureAwait(false);

                if (opened.IsFailure)
                {

                    FailClosed(admissionEpoch);

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

        long reopenEpoch;

        lock (_state)
        {

            if (_disposed)
            {

                return Result.Failure(AdmissionClosed());

            }

            if (_accepting && !_pendingClose && CoreConnection is not null)
            {

                return Result.Success();

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

                if (_accepting && !_pendingClose && CoreConnection is not null)
                {

                    return Result.Success();

                }

            }

            // A cancelled quiesce can leave the prior generation's handle behind while still
            // correctly closing admission. Never reuse it: replacement may have occurred before
            // this recovery attempt reached the serializer.
            Result normalized = await CloseCurrentAsync().ConfigureAwait(false);

            if (normalized.IsFailure)
            {

                FailClosed(reopenEpoch);

                return normalized;

            }

            Result opened = await OpenVerifiedAsync(
                reopenEpoch,
                openAdmission: true,
                cancellationToken).ConfigureAwait(false);

            if (opened.IsFailure)
            {

                FailClosed(reopenEpoch);

            }

            return opened;

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
        bool openAdmission,
        CancellationToken cancellationToken)
    {

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

        try
        {

            Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                .OpenFreshAsync(
                    GrimoireOrdinaryFreshConnectionKind.ReadWrite,
                    cancellationToken)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {

                return Result.Failure(acquired.Error);

            }

            candidateLease = acquired.Value;

            SqliteConnection candidate = candidateLease.Connection;

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

                if (!ReferenceEquals(_availability.Current, published))
                {

                    return Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.StaleSnapshot,
                            "Covenant availability changed while the disclosure writer opened."));

                }

                if (!openAdmission && !CanAccept())
                {

                    return Result.Failure(AdmissionClosed());

                }

                _connectionLease = candidateLease;

                if (openAdmission)
                {

                    _pendingClose = false;

                    _accepting = true;

                }

                adopted = true;

            }

            return Result.Success();

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception)
        {

            return Result.Failure(MaintenanceFailure());

        }
        finally
        {

            if (!adopted)
            {

                _ = await CleanupAsync(candidateLease).ConfigureAwait(false);

            }

        }

    }

    private async Task<Result> CloseCurrentAsync()
    {

        IGrimoireOrdinaryConnectionLease? lease = _connectionLease;

        _connectionLease = null;

        return await CleanupAsync(lease).ConfigureAwait(false);

    }

    private static async Task<Result> CleanupAsync(
        IGrimoireOrdinaryConnectionLease? lease)
    {

        bool failed = false;

        if (lease is not null)
        {

            try
            {

                await lease.DisposeAsync().ConfigureAwait(false);

            }
            catch (Exception)
            {

                failed = true;

            }

        }

        return failed ? Result.Failure(MaintenanceFailure()) : Result.Success();

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

    private void FailClosed(long expectedCloseEpoch)
    {

        lock (_state)
        {

            if (_disposed || expectedCloseEpoch != _closeRequestEpoch)
            {

                return;

            }

            _accepting = false;

            _pendingClose = true;

            _ = checked(++_closeRequestEpoch);

        }

    }

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

    private static Error AdmissionClosed() =>
        new(
            ErrorCodes.Covenant.LifecycleConflict,
            "The Covenant disclosure writer is closed for maintenance.");

    private static Error MaintenanceFailure() =>
        new(
            ErrorCodes.Covenant.MaintenanceFailed,
            "The Covenant disclosure writer could not prepare its warm connection safely.");

}
