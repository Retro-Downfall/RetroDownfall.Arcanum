namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>Storage conditions say whether a statement was unavailable or its evidence was faulty.</summary>
public enum SqliteStorageFailureKind
{
    Busy,
    Unavailable,
    Fault,
}

/// <summary>Classifies primary and extended SQLite codes without retaining engine text.</summary>
public static class SqliteStorageFailure
{
    public static SqliteStorageFailureKind Classify(int? code) => (code & 255) switch
    {
        5 or 6 => SqliteStorageFailureKind.Busy,
        7 or 8 or 9 or 10 or 13 or 14 => SqliteStorageFailureKind.Unavailable,
        _ => SqliteStorageFailureKind.Fault,
    };
}
