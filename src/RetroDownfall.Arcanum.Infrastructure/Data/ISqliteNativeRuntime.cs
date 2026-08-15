namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Selects and freezes the SQLite provider for the process.
/// </summary>
/// <remarks>
/// Every path that opens a SQLCipher connection calls this first. Selection happens exactly once
/// and cannot be undone, so no later component can substitute a different SQLite engine underneath
/// a database Arcanum has already opened as encrypted.
/// </remarks>
internal interface ISqliteNativeRuntime
{

    /// <summary>
    /// Installs the hermetic SQLCipher provider if it is not installed yet. Safe to call from any
    /// thread and any number of times.
    /// </summary>
    /// <exception cref="SqliteNativeRuntimeUnavailableException">
    /// The native library for this runtime identifier could not be loaded.
    /// </exception>
    void Initialize();

}
