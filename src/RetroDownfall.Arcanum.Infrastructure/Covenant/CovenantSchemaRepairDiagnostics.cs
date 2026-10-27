using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal static class CovenantSchemaRepairDiagnostics
{
    internal static void Warn(CovenantSchemaRepairStage stage, CovenantSchemaRepairFailure reason,
        CovenantSchemaRepairIntent? intent = null, string? code = null, Exception? cause = null,
        CovenantSchemaRepairExecution? execution = null, Error? error = null,
        CovenantSchemaRepairStorageFault? fault = null)
    {
        SqliteException? sqlite = cause as SqliteException ?? cause?.InnerException as SqliteException;

        GrimoireConnectionFailureKind kind = execution?.FailureKind ?? fault?.Kind
            ?? (cause is null ? GrimoireConnectionFailureKind.Other : CovenantStorageFailure.Classify(cause));

        int? primary = execution?.SqliteCode ?? sqlite?.SqliteErrorCode ?? fault?.SqliteCode;

        int? extended = execution?.SqliteExtendedCode ?? sqlite?.SqliteExtendedErrorCode ?? fault?.SqliteExtendedCode;

        string guidance = DescribeRecovery(stage, reason, intent?.Phase, kind, primary, execution?.Effects,
            fault is not null || cause is not null);

        Log.Warning(
            "A Covenant schema repair did not finish at {Stage}: {Reason}; operation {OperationId}, action {Action}, phase {Phase}, revision {Revision}; code {ErrorCode}, failure {FailureKind}, SQLite {SqliteErrorCode}/{SqliteExtendedErrorCode}. {RecoveryGuidance}",
            stage, reason, intent?.OperationId, intent?.Action, intent?.Phase, intent?.Revision,
            SafeCode(code ?? execution?.ErrorCode ?? error?.Code), kind,
            primary, extended, guidance);
    }

    private static string DescribeRecovery(CovenantSchemaRepairStage stage, CovenantSchemaRepairFailure reason,
        CovenantSchemaRepairPhase? phase, GrimoireConnectionFailureKind kind, int? sqliteCode,
        CovenantSchemaRepairEffects? effects, bool hasStorageFault)
    {
        if (stage == CovenantSchemaRepairStage.Complete && phase == CovenantSchemaRepairPhase.ReopenPending)
        {
            return "Correct the reported storage or finalization condition, then restart to resume the recorded finalizer; do not clear the journal.";
        }

        if (stage == CovenantSchemaRepairStage.JournalPrepare)
        {
            return "Correct the reported journal/storage condition, then restart to inspect preparation. The repair action was not dispatched; keep the profile and do not clear an active journal.";
        }

        if (phase is null && stage is CovenantSchemaRepairStage.Acquisition or CovenantSchemaRepairStage.Inspect)
        {
            return "Correct the reported inspection/storage condition and retry inspection. The repair action was not dispatched.";
        }

        if (effects == CovenantSchemaRepairEffects.NoMutationProven)
        {
            return reason == CovenantSchemaRepairFailure.IndexDefinitionMissing
                ? "The ordinary index has no permitted repair definition. No schema repair mutation occurred; inspect the requested action and current catalog before choosing another repair."
                : "The requested action does not apply to this catalog. No schema repair mutation occurred; inspect the action and current family presence before choosing another repair.";
        }

        if (reason == CovenantSchemaRepairFailure.EffectsUnproven || effects == CovenantSchemaRepairEffects.Unknown)
        {
            return "Keep the profile intact and seek manual recovery. Restart alone cannot prove unknown Prepared repair effects safe; do not replay repairs or clear the journal blindly.";
        }

        bool storageCondition = kind is GrimoireConnectionFailureKind.IO or GrimoireConnectionFailureKind.UnauthorizedAccess
            || sqliteCode is { } native && SqliteStorageFailure.Classify(native) is SqliteStorageFailureKind.Busy or SqliteStorageFailureKind.Unavailable;

        if (reason == CovenantSchemaRepairFailure.JournalUnreadable && storageCondition)
        {
            return "Correct storage contention, permissions or free space, then restart. Keep the profile intact.";
        }

        if (hasStorageFault
            && phase is CovenantSchemaRepairPhase.CatalogCommitted or CovenantSchemaRepairPhase.HealthVerified or CovenantSchemaRepairPhase.ReopenPending
            && stage is CovenantSchemaRepairStage.Inspect or CovenantSchemaRepairStage.Reinspect
                or CovenantSchemaRepairStage.AdvanceAfterHealth or CovenantSchemaRepairStage.AdvanceToReopen)
        {
            return "Correct the reported storage condition, then restart to resume only the recorded health/finalization suffix. The repair action will not be rerun; keep the profile and journal intact.";
        }

        return "Keep the profile intact and seek manual recovery for the reported journal, catalog or ownership condition. Do not replay repairs or clear the journal blindly.";
    }

    internal static string SafeCode(string? code) => code switch
    {
        ErrorCodes.Covenant.Unavailable => ErrorCodes.Covenant.Unavailable,
        ErrorCodes.Covenant.ManualRecoveryRequired => ErrorCodes.Covenant.ManualRecoveryRequired,
        ErrorCodes.Covenant.LifecycleConflict => ErrorCodes.Covenant.LifecycleConflict,
        ErrorCodes.Covenant.RevisionConflict => ErrorCodes.Covenant.RevisionConflict,
        ErrorCodes.Covenant.ForbiddenAuthority => ErrorCodes.Covenant.ForbiddenAuthority,
        ErrorCodes.Covenant.InvalidScope => ErrorCodes.Covenant.InvalidScope,
        ErrorCodes.Covenant.StaleSnapshot => ErrorCodes.Covenant.StaleSnapshot,
        ErrorCodes.Covenant.IntegrityFailure => ErrorCodes.Covenant.IntegrityFailure,
        _ => ErrorCodes.Covenant.MaintenanceFailed,
    };
}

internal enum CovenantSchemaRepairStage : byte
{
    JournalRead,

    JournalPrepare,

    PhaseRead,

    OwnerAdoption,

    Resume,

    Inspect,

    CatalogMoved,

    PreparedEffectsUnknown,

    Execute,

    Reinspect,

    AdvanceAfterRepair,

    CanonicalInvalid,

    AdvanceAfterHealth,

    AdvanceToReopen,

    Complete,

    EvidenceSelectedKeepClosed,

    Acquisition,
}
