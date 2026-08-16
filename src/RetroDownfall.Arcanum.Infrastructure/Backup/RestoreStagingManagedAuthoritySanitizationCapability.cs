using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// What one sanitation run removed, in counts and one commitment.
/// </summary>
/// <remarks>
/// Content-free. It proves what was stripped without being convertible back into any of it, which is
/// the whole reason the tombstones exist rather than the rows themselves (§10.16).
/// </remarks>
internal sealed record BackupRestoreManagedAuthoritySanitizationReceipt(
    Guid RestoreOperationId,
    ulong ManagedWriteIntentCount,
    ulong LocalErasureWorkItemCount,
    ulong RemovedLabelCount,
    CovenantDigest TombstoneVectorDigest);

/// <summary>
/// The one capability entitled to rewrite managed-file authority out of restore staging.
/// </summary>
/// <remarks>
/// Restore never inherits managed-file authority. A backup taken on one machine describes files that
/// do not exist on this one, and a restored write intent or erasure work item would be a delete
/// capability for a path this installation never owned. Stripping them is therefore not cleanup, it
/// is the removal of authority — which is why it is the one operation the general
/// <see cref="ICovenantSqliteConnectionInitializer.Authorize"/> entry point refuses (§10.16).
///
/// <para>Sealed, nonserializable, single-take, and reachable only through its own nested factory. It
/// exports no connection, transaction, command, delegate, callback, or generic execution method: the
/// only caller-facing operation is <see cref="RunImmediateAsync"/>, and the only code it will invoke
/// is the compile-time-bound static sanitizer.</para>
/// </remarks>
internal sealed class RestoreStagingManagedAuthoritySanitizationCapability : IAsyncDisposable
{

    private readonly CovenantSqliteConnectionInitializer _initializer;

    private readonly SqliteConnection _candidate;

    private readonly CovenantExclusiveRecoveryOwner _owner;

    private readonly Guid _candidateDatasetGeneration;

    private readonly TimeProvider _timeProvider;

    private readonly Func<bool> _candidateStillUnpublished;

    private RunIdentity? _activeRun;

    private int _taken;

    private int _disposed;

    private RestoreStagingManagedAuthoritySanitizationCapability(
        CovenantSqliteConnectionInitializer initializer,
        SqliteConnection candidate,
        CovenantExclusiveRecoveryOwner owner,
        Guid candidateDatasetGeneration,
        TimeProvider timeProvider,
        Func<bool> candidateStillUnpublished)
    {

        _initializer = initializer;

        _candidate = candidate;

        _owner = owner;

        _candidateDatasetGeneration = candidateDatasetGeneration;

        _timeProvider = timeProvider;

        _candidateStillUnpublished = candidateStillUnpublished;

    }

    /// <summary>
    /// The only way to obtain one, and only from a proven restore owner over an unpublished candidate.
    /// </summary>
    internal static Result<RestoreStagingManagedAuthoritySanitizationCapability> Mint(
        CovenantSqliteConnectionInitializer initializer,
        SqliteConnection candidate,
        CovenantExclusiveRecoveryOwner owner,
        Guid candidateDatasetGeneration,
        TimeProvider timeProvider,
        Func<bool> candidateStillUnpublished)
    {

        if (initializer is null
            || candidate is null
            || timeProvider is null
            || candidateStillUnpublished is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A sanitation capability requires its initializer, candidate, and publication predicate.");

        }

        if (!owner.IsValid || owner.Operation != CovenantExclusiveOperation.BackupRestore)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "Only a complete BackupRestore owner may sanitize staged managed authority.");

        }

        if (candidateDatasetGeneration == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A sanitation capability requires the staged candidate's dataset generation.");

        }

        // A candidate that has already been published as live is ordinary live state, and this
        // authorization must never be borrowable on it.
        if (!candidateStillUnpublished())
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This candidate has already been published, so its authority is live state.");

        }

        return new RestoreStagingManagedAuthoritySanitizationCapability(
            initializer,
            candidate,
            owner,
            candidateDatasetGeneration,
            timeProvider,
            candidateStillUnpublished);

    }

    /// <summary>
    /// Takes the capability exactly once and runs the sanitizer inside one immediate transaction.
    /// </summary>
    /// <remarks>
    /// Everything about the run is decided here: the transaction is begun here, the session and run
    /// identity are minted here, the authorization scope is disposed before commit, and both the
    /// session and the identity are invalidated before control returns. A failure rolls back and
    /// changes no counter, so a rejected check can never be mistaken for a completed sanitation.
    /// </remarks>
    internal async Task<Result<BackupRestoreManagedAuthoritySanitizationReceipt>> RunImmediateAsync(
        CancellationToken cancellationToken)
    {

        if (Interlocked.CompareExchange(ref _taken, 1, 0) != 0)
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This sanitation capability has already been taken.");

        }

        Result current = AssertCurrent();

        if (current.IsFailure)
        {

            return current.Error;

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await _candidate.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        RunIdentity run = new();

        Volatile.Write(ref _activeRun, run);

        RestoreStagingManagedAuthoritySanitizationSession session = new(
            _candidate,
            transaction,
            run,
            _owner,
            _candidateDatasetGeneration,
            _timeProvider);

        try
        {

            CovenantSqliteAuthorizationScope scope =
                _initializer.AuthorizeRestoreStagingManagedAuthoritySanitization(this, run);

            Result<BackupRestoreManagedAuthoritySanitizationReceipt> receipt;

            using (scope)
            {

                receipt = await BackupRestoreManagedAuthoritySanitizer
                    .ExecuteInSessionAsync(session, run, cancellationToken)
                    .ConfigureAwait(false);

            }

            session.Invalidate();

            Volatile.Write(ref _activeRun, null);

            if (receipt.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return receipt;

            }

            Result before = AssertCurrent();

            if (before.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return before.Error;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return receipt;

        }
        catch
        {

            session.Invalidate();

            Volatile.Write(ref _activeRun, null);

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

    }

    /// <summary>
    /// The producer-only grant. Reachable from the initializer and from nothing else.
    /// </summary>
    /// <remarks>
    /// Reference equality on both the initializer and the active run identity. A caller that could
    /// present a different initializer, or an identity from a previous invocation, would be able to
    /// enable code 11 outside the one transaction it is meant to cover.
    /// </remarks>
    internal CovenantSqliteAuthorizationScope BorrowCode11Scope(
        CovenantSqliteConnectionInitializer caller,
        RunIdentity runIdentity)
    {

        if (!ReferenceEquals(caller, _initializer))
        {

            throw new InvalidOperationException(
                "Restore-staging sanitization is granted only by the initializer this capability retains.");

        }

        if (!ReferenceEquals(runIdentity, Volatile.Read(ref _activeRun)))
        {

            throw new InvalidOperationException(
                "Restore-staging sanitization is granted only to this capability's active run.");

        }

        Result current = AssertCurrent();

        if (current.IsFailure)
        {

            throw new InvalidOperationException(current.Error.Message);

        }

        return _initializer.AuthorizeCore(
            _candidate,
            CovenantSqliteAuthorizationKind.RestoreStagingManagedAuthoritySanitization);

    }

    /// <summary>
    /// Releases the borrowed staged connection exactly once, and never the caller's exclusive lease.
    /// </summary>
    public ValueTask DisposeAsync()
    {

        _ = Interlocked.Exchange(ref _disposed, 1);

        Volatile.Write(ref _activeRun, null);

        return ValueTask.CompletedTask;

    }

    private Result AssertCurrent()
    {

        if (Volatile.Read(ref _disposed) != 0)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This sanitation capability has been disposed."));

        }

        if (_candidate.State != System.Data.ConnectionState.Open)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The staged candidate connection is no longer open."));

        }

        return _candidateStillUnpublished()
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "The staged candidate was published while its authority was being sanitized."));

    }

    /// <summary>
    /// The identity of one taken invocation.
    /// </summary>
    /// <remarks>
    /// Minted only inside <see cref="RunImmediateAsync"/> and compared by reference. It is what stops
    /// a scope or a session from being carried into a second invocation, and it has no content at all
    /// because its whole value is that it cannot be reconstructed.
    /// </remarks>
    internal sealed class RunIdentity
    {

        internal RunIdentity()
        {
        }

    }

}
