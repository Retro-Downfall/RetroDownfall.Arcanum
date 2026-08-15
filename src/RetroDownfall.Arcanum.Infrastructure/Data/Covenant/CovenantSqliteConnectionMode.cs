namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// How a SQLCipher connection will be used, which decides the policy applied when it opens.
/// </summary>
/// <remarks>
/// Closed on purpose. Journal and locking policy differ per mode, and a caller that could invent its
/// own combination would be able to, for example, attempt a journal-mode change on a read-only
/// handle or hold an exclusive lock on an ordinary repository connection.
/// </remarks>
internal enum CovenantSqliteConnectionMode
{

    /// <summary>Ordinary repository, bootstrap, and EF connections.</summary>
    ReadWrite = 0,

    /// <summary>Diagnostics and snapshot reads. Never attempts a journal-mode change.</summary>
    ReadOnly = 1,

    /// <summary>Reset, restore, and factory-erase handles that require exclusive locking.</summary>
    ExclusiveMaintenance = 2,

}
