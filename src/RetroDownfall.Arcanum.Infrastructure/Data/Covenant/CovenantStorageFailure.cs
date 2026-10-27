using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Contextual, content-free refusals; callers supply only their fixed operation name.</summary>
internal static class CovenantStorageFailure
{
    internal static Error Create(Exception failure, string operation)
    {
        SqliteStorageFailureKind kind = failure is SqliteException sqlite
            ? SqliteStorageFailure.Classify(sqlite.SqliteErrorCode)
            : failure is IOException or UnauthorizedAccessException
                ? SqliteStorageFailureKind.Unavailable : SqliteStorageFailureKind.Fault;

        string guidance = kind switch
        {
            SqliteStorageFailureKind.Busy => "Storage is busy. Let conflicting work release the database before retrying.",
            SqliteStorageFailureKind.Unavailable => "Check storage availability, permissions and free space before retrying.",
            _ => "Preserve the installation and inspect its storage and schema diagnostics before retrying.",
        };

        return new Error(kind == SqliteStorageFailureKind.Fault
                ? ErrorCodes.Covenant.IntegrityFailure : ErrorCodes.Covenant.Unavailable,
            $"{operation} refused. {(failure is SqliteException native ? Describe(native) : $"Failure={Classify(failure)}.")} {guidance}");
    }

    internal static string Describe(SqliteException failure) =>
        FormattableString.Invariant($"SQLite={failure.SqliteErrorCode}, extended={failure.SqliteExtendedErrorCode}.");

    internal static GrimoireConnectionFailureKind Classify(Exception failure) => failure switch
    {
        SqliteException => GrimoireConnectionFailureKind.Sqlite,
        SqliteNativeRuntimeUnavailableException => GrimoireConnectionFailureKind.NativeRuntime,
        UnauthorizedAccessException => GrimoireConnectionFailureKind.UnauthorizedAccess,
        IOException => GrimoireConnectionFailureKind.IO,
        ArgumentException => GrimoireConnectionFailureKind.Argument,
        InvalidOperationException => GrimoireConnectionFailureKind.InvalidOperation,
        _ => GrimoireConnectionFailureKind.Other,
    };
}
