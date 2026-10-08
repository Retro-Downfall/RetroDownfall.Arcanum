using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Recognises the one SQLite failure a "claim by inserting" repository may treat as "someone else already holds
/// it": a unique or primary-key violation. Every other constraint failure (a trigger, a NOT NULL, a CHECK, a
/// foreign key) is a defect or an environment fault that must propagate, not be read back as a lost race.
/// </summary>
internal static class SqliteUniqueConstraintViolation
{
    private const int SqliteConstraint = 19;

    private const int SqliteConstraintPrimaryKey = 1555;

    private const int SqliteConstraintUnique = 2067;

    internal static bool Is(Exception exception) =>
        exception is SqliteException
        {
            SqliteErrorCode: SqliteConstraint,
            SqliteExtendedErrorCode: SqliteConstraintPrimaryKey or SqliteConstraintUnique,
        };
}
