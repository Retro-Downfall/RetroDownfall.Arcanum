using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>In-process repair facts, without labelling a schema fault as a connection-opening failure.</summary>
internal sealed class CovenantSchemaRepairResult<T>
{
    private readonly Result<T> _result;

    private CovenantSchemaRepairResult(Result<T> result, CovenantSchemaRepairStorageFault? fault)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));

        Fault = fault;
    }

    internal bool IsSuccess => _result.IsSuccess;

    internal bool IsFailure => _result.IsFailure;

    internal Error Error => _result.Error;

    internal T Value => _result.Value;

    internal CovenantSchemaRepairStorageFault? Fault { get; }

    internal static CovenantSchemaRepairResult<T> Failure(Error error, CovenantSchemaRepairStorageFault? fault = null) =>
        new(Result<T>.Failure(error), fault);

    public static implicit operator CovenantSchemaRepairResult<T>(Result<T> result) => new(result, null);

    public static implicit operator Result<T>(CovenantSchemaRepairResult<T> result) => result._result;

    public static implicit operator Result(CovenantSchemaRepairResult<T> result) => result._result;
}

/// <summary>Only a closed kind and native numerical codes, never an exception or its message.</summary>
internal readonly record struct CovenantSchemaRepairStorageFault(
    GrimoireConnectionFailureKind Kind,
    int? SqliteCode,
    int? SqliteExtendedCode)
{
    internal static CovenantSchemaRepairStorageFault From(Exception failure)
    {
        SqliteException? sqlite = failure as SqliteException ?? failure.InnerException as SqliteException;

        return new(CovenantStorageFailure.Classify(failure), sqlite?.SqliteErrorCode, sqlite?.SqliteExtendedErrorCode);
    }
}
