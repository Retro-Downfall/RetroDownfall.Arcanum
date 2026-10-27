using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal sealed partial class CovenantMemoryReviewService
{
    private static void PropagateNativeCancellation(Exception failure, CancellationToken cancellationToken)
    {
        // SQLITE_INTERRUPT on this cancelled request is cancellation, not an invitation to retry.
        // An interrupt without cancellation retains its existing storage-failure classification.
        if (failure is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xff) == 9)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool IsReadStorageFailure(Exception failure) => failure is
        SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or ReviewStateException;

    private Error MapReadFailure(Exception failure, ReviewOperation operation)
    {
        if (failure is ReviewStateException known)
        {
            return ReportIntegrity(known.Reason, operation, known.Stage, failure: failure);
        }

        if (failure is InvalidDataException)
        {
            return ReportIntegrity(ReviewReason.StoredStateInvalid, operation, ReviewStage.StorageRead, failure: failure);
        }

        SqliteStorageFailureKind kind = failure is SqliteException sqlite
            ? SqliteStorageFailure.Classify(sqlite.SqliteErrorCode) : SqliteStorageFailureKind.Unavailable;

        ReviewReason reason = kind switch
        {
            SqliteStorageFailureKind.Busy => ReviewReason.StorageBusy,
            SqliteStorageFailureKind.Unavailable => ReviewReason.StorageUnavailable,
            _ => ReviewReason.StorageFault,
        };

        LogReviewFailure(kind == SqliteStorageFailureKind.Fault ? LogLevel.Error : LogLevel.Warning,
            operation, ReviewStage.StorageRead, reason, failure);

        return kind == SqliteStorageFailureKind.Fault ? IntegrityFailure : new Error(
            ErrorCodes.Covenant.Unavailable,
            kind == SqliteStorageFailureKind.Busy
                ? "Covenant review data could not be read because storage is busy. Let conflicting work finish, then retry this review request."
                : "Covenant review data could not be read. Check storage availability, permissions and free space, then retry this review request.");
    }

    private Result<MemoryReviewBulkResultDto> MapApplyFailure(Exception failure, Guid? requestId)
    {
        if (failure is GrimoireBusyTimeoutException || SqliteBusyRetry.IsBusyOrLocked(failure))
        {
            LogReviewFailure(LogLevel.Warning, ReviewOperation.Apply, ReviewStage.StorageWrite,
                ReviewReason.StorageBusy, failure, requestId);

            return DatabaseBusy;
        }

        if (failure is ReviewStateException known)
        {
            return ReportIntegrity(known.Reason, ReviewOperation.Apply, known.Stage, requestId, failure);
        }

        if (failure is InvalidDataException)
        {
            return ReportIntegrity(ReviewReason.StoredStateInvalid, ReviewOperation.Apply,
                ReviewStage.StorageWrite, requestId, failure);
        }

        LogReviewFailure(LogLevel.Error, ReviewOperation.Apply, ReviewStage.StorageWrite,
            ReviewReason.StorageFault, failure, requestId);

        return WriteFailed;
    }

    private Error ReportIntegrity(ReviewReason reason, ReviewOperation operation, ReviewStage stage,
        Guid? requestId = null, Exception? failure = null)
    {
        LogReviewFailure(LogLevel.Error, operation, stage, reason, failure, requestId);

        return IntegrityFailure;
    }

    private void LogReviewFailure(LogLevel level, ReviewOperation operation, ReviewStage stage,
        ReviewReason reason, Exception? failure = null, Guid? requestId = null)
    {
        SqliteException? sqlite = failure as SqliteException ?? failure?.InnerException as SqliteException;

        GrimoireConnectionFailureKind kind = failure is null
            ? GrimoireConnectionFailureKind.Other : CovenantStorageFailure.Classify(failure);

        // Every value is a closed enum, a numeric driver code or a typed request GUID. Neither
        // exception objects nor arbitrary collaborator strings enter the diagnostic state.
        logger.Log(level,
            "Covenant review {Operation} diagnostic at {Stage}: {Reason}; failure {FailureKind}, SQLite {SqliteErrorCode}/{SqliteExtendedErrorCode}; request {RequestId}.",
            operation, stage, reason, kind, sqlite?.SqliteErrorCode, sqlite?.SqliteExtendedErrorCode, requestId);
    }

    private enum ReviewOperation
    {
        List,
        Prepare,
        Apply,
    }

    private enum ReviewStage
    {
        StorageRead,
        StorageWrite,
        CanonicalState,
        Marker,
        Provenance,
        Replay,
        Mutation,
        Republish,
    }

    private enum ReviewReason
    {
        StorageBusy,
        StorageUnavailable,
        StorageFault,
        StoredStateInvalid,
        CanonicalStateMissing,
        MarkerEstablishmentFailed,
        ProvenanceCountMismatch,
        MutationResultVersionMissing,
        MutationResultCardinality,
        ReplacementEventMissing,
        ReplayReceiptInvalid,
        ReplayGenerationMismatch,
        ReplayFrontierMismatch,
        ReplayDuplicateOrdinal,
        ReplayOriginalEventMissing,
        ReplayResponseDigestMismatch,
        ReplayResultVersionMissing,
        ReplayReplacementMismatch,
        ReplayReplacementEventMissing,
        ReplayReplacementDigestMismatch,
        ReplayReplacementReceiptMissing,
        AvailabilityPublicationFailed,
    }

    private sealed class ReviewStateException(ReviewReason reason, ReviewStage stage)
        : Exception("Covenant review state failed its integrity check.")
    {
        internal ReviewReason Reason { get; } = reason;

        internal ReviewStage Stage { get; } = stage;
    }
}
