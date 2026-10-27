using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>Positive execution evidence, without treating an error as proof that no write happened.</summary>
internal sealed class CovenantSchemaRepairExecution
{
    private CovenantSchemaRepairExecution(CovenantSchemaRepairEffects effects, CovenantSchemaRepairFailure failure,
        GrimoireConnectionFailureKind failureKind, int? sqliteCode, int? sqliteExtendedCode)
    {
        Effects = effects;

        Failure = failure;

        FailureKind = failureKind;

        SqliteCode = sqliteCode;

        SqliteExtendedCode = sqliteExtendedCode;
    }

    internal CovenantSchemaRepairEffects Effects { get; }

    internal CovenantSchemaRepairFailure Failure { get; }

    internal GrimoireConnectionFailureKind FailureKind { get; }

    internal int? SqliteCode { get; }

    internal int? SqliteExtendedCode { get; }

    internal bool IsSuccess => Failure == CovenantSchemaRepairFailure.None;

    internal string ErrorCode => Failure == CovenantSchemaRepairFailure.Storage
        ? ErrorCodes.Covenant.MaintenanceFailed : ErrorCodes.Covenant.ManualRecoveryRequired;

    internal static CovenantSchemaRepairExecution NoMutation(CovenantSchemaRepairFailure failure = CovenantSchemaRepairFailure.None) =>
        new(CovenantSchemaRepairEffects.NoMutationProven, failure, GrimoireConnectionFailureKind.Other, null, null);

    internal static CovenantSchemaRepairExecution MutationCompleted() =>
        new(CovenantSchemaRepairEffects.MutationCompleted, CovenantSchemaRepairFailure.None,
            GrimoireConnectionFailureKind.Other, null, null);

    internal static CovenantSchemaRepairExecution Unknown(CovenantSchemaRepairFailure failure, Exception? cause = null)
    {
        if (failure == CovenantSchemaRepairFailure.None)
        {
            throw new ArgumentException("Unknown repair effects require a closed failure reason.", nameof(failure));
        }

        SqliteException? sqlite = cause as SqliteException ?? cause?.InnerException as SqliteException;

        return new(CovenantSchemaRepairEffects.Unknown, failure,
            cause is null ? GrimoireConnectionFailureKind.Other : CovenantStorageFailure.Classify(cause),
            sqlite?.SqliteErrorCode, sqlite?.SqliteExtendedErrorCode);
    }

    // This is the same pure dispatch refusal used by the executor. Matching a catalog is necessary
    // but insufficient: recovery also proves this action could not enter a mutator in that catalog.
    internal static bool RefusesBeforeAnyMutator(CovenantSchemaRepairAction action, CovenantSchemaRepairInspection inspected) =>
        action == CovenantSchemaRepairAction.InstallAbsentCanonicalFamily && inspected.CanonicalObjectsPresent
        || action == CovenantSchemaRepairAction.RepairExistingFamily && !inspected.CanonicalObjectsPresent;
}

internal enum CovenantSchemaRepairEffects : byte
{
    NoMutationProven,

    MutationCompleted,

    Unknown,
}

internal enum CovenantSchemaRepairFailure : byte
{
    None,

    ActionNotApplicable,

    IndexDefinitionMissing,

    Storage,

    SchemaRefused,

    CanonicalUnhealthy,

    EffectsUnproven,

    JournalMalformed,

    MultipleActiveJournals,

    JournalUnreadable,

    OwnerAdoptionRefused,

    InvalidOwnerIdentifier,

    RecoveryRefused,
}
