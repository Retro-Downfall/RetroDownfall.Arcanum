using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal sealed partial class CovenantMutationService
{
    private static bool IsOperatorStorageFailure(Exception failure) => failure is
        OperatorAttemptFailure or SqliteException or GrimoireBusyTimeoutException
        or IOException or UnauthorizedAccessException;

    private static void PropagateOperatorCancellation(Exception failure, CancellationToken cancellationToken)
    {
        Exception cause = failure is OperatorAttemptFailure attempt ? attempt.Failure : failure;

        if (cause is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xff) == 9)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async ValueTask<SqliteConnection> OpenOperatorConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsOperatorStorageFailure(failure))
        {
            throw new OperatorAttemptFailure(failure, OperatorStage.Connection, retryableBusy: false);
        }
    }

    private async ValueTask<MemoryDelegationMutation?> BeginOperatorDelegationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await memoryDelegation.BeginOptionalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsOperatorStorageFailure(failure))
        {
            throw new OperatorAttemptFailure(failure, OperatorStage.Delegation, retryableBusy: false);
        }
    }

    private CovenantAgentErasureGate CaptureOperatorErasureGate()
    {
        try
        {
            return kernel.CaptureErasureGate();
        }
        catch (Exception failure) when (IsOperatorStorageFailure(failure))
        {
            throw new OperatorAttemptFailure(failure, OperatorStage.ErasureGate, retryableBusy: false);
        }
    }

    private static SqliteTransaction BeginOperatorTransaction(SqliteConnection connection)
    {
        try
        {
            return connection.BeginTransaction(deferred: false);
        }
        catch (Exception failure) when (IsOperatorStorageFailure(failure))
        {
            throw new OperatorAttemptFailure(failure, OperatorStage.Begin,
                retryableBusy: SqliteBusyRetry.IsBusyOrLocked(failure));
        }
    }

    private Result<T> MapOperatorFailure<T>(Exception failure, OperatorAction action, Guid mutationId)
    {
        OperatorAttemptFailure? attempt = failure as OperatorAttemptFailure;

        Exception cause = attempt?.Failure ?? failure;

        OperatorStage stage = attempt?.Stage ?? OperatorStage.Connection;

        bool uncertain = stage is OperatorStage.ResourceCleanup or OperatorStage.Rollback or OperatorStage.Commit or OperatorStage.Publication
            or OperatorStage.Delegation or OperatorStage.ErasureGate;

        bool busy = !uncertain && (cause is GrimoireBusyTimeoutException || SqliteBusyRetry.IsBusyOrLocked(cause));

        OperatorFailureReason reason = uncertain ? OperatorFailureReason.OutcomeUncertain
            : busy ? OperatorFailureReason.StorageBusy : OperatorFailureReason.StorageFault;

        SqliteException? sqlite = cause as SqliteException ?? cause.InnerException as SqliteException;

        logger.Log(busy ? LogLevel.Warning : LogLevel.Error,
            "Covenant operator {Operation} failed at {Stage}: {Reason}; failure {FailureKind}, SQLite {SqliteErrorCode}/{SqliteExtendedErrorCode}; mutation {MutationId}.",
            action, stage, reason, CovenantStorageFailure.Classify(cause), sqlite?.SqliteErrorCode,
            sqlite?.SqliteExtendedErrorCode, mutationId);

        return new Error(busy ? ErrorCodes.Covenant.Unavailable : ErrorCodes.Covenant.WriteFailed,
            busy
                ? "Covenant storage remained busy within the operation bound. Let conflicting work finish, then retry the same request with its original mutation identity."
                : uncertain
                    ? "The complete Covenant mutation outcome could not be verified. Preserve the original mutation identity and resolve the reported storage or recovery condition before repeating changes; do not create a new request identity."
                    : "The Covenant write could not be completed safely. Check storage availability, permissions and free space, then resolve the same request using its original mutation identity.");
    }

    private enum OperatorAction
    {
        Set,
        Retire,
        Correct,
        Curate,
    }

    private enum OperatorStage
    {
        Connection,
        Delegation,
        ErasureGate,
        Begin,
        Write,
        Rollback,
        Commit,
        ResourceCleanup,
        Publication,
    }

    private enum OperatorFailureReason
    {
        StorageBusy,
        StorageFault,
        OutcomeUncertain,
    }

    // Only this wrapper proves a busy arose inside the protected operation body. An exception from
    // disposing any owned resource replaces it, escapes as ResourceCleanup and is never retried.
    private sealed class OperatorAttemptFailure(Exception failure, OperatorStage stage, bool retryableBusy)
        : Exception("A Covenant operator storage attempt failed.", failure)
    {
        internal Exception Failure { get; } = failure;

        internal OperatorStage Stage { get; } = stage;

        internal bool RetryableBusy { get; } = retryableBusy;
    }
}
