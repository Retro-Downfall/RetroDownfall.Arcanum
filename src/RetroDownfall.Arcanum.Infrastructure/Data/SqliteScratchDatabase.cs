namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Owns one throwaway database file and the sidecars SQLite creates beside it.
/// </summary>
/// <remarks>
/// The validator writes a real encrypted database to prove the codec works, which means it creates
/// files. This type records only the paths it created itself and removes exactly those: it never
/// globs, never walks a parent directory, and never deletes a path it was handed. A failed
/// validation still cleans up, because the alternative is leaving decryptable scratch material on
/// disk after the one code path whose job is to prove encryption works.
/// </remarks>
internal sealed class SqliteScratchDatabase : IDisposable
{

    /// <summary>The sidecars SQLite may create next to a database file.</summary>
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    private readonly string _directory;

    private bool _disposed;

    internal SqliteScratchDatabase(string scratchDirectory, string fileName)
    {

        _directory = Path.Combine(scratchDirectory, Path.GetRandomFileName());

        _ = Directory.CreateDirectory(_directory);

        Path_ = Path.Combine(_directory, fileName);

    }

    /// <summary>Absolute path of the scratch database file.</summary>
    internal string Path_ { get; }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        TryDelete(Path_);

        foreach (string suffix in SidecarSuffixes)
        {

            TryDelete(Path_ + suffix);

        }

        try
        {

            // Only removes the directory this instance created, and only when the deletions above
            // emptied it. A leftover file means something unexpected wrote here, and that is worth
            // leaving for inspection rather than silently erasing.
            if (Directory.Exists(_directory) && Directory.GetFileSystemEntries(_directory).Length == 0)
            {

                Directory.Delete(_directory);

            }

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            // Cleanup is best effort; the caller's scratch root is disposable.

        }

    }

    private static void TryDelete(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            // Same rationale as directory removal: never let cleanup mask the real failure.

        }

    }

}
