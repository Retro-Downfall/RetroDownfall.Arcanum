using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class TestDirectoryCleanup
{
    /// <summary>
    /// Deletes <paramref name="path"/> recursively. A failure never fails the test that owned the
    /// directory, but it is reported to the xUnit diagnostic sink so a leaking fixture is visible
    /// instead of silently accumulating directories under the temp root.
    /// </summary>
    /// <returns><see langword="true"/> when the directory is gone afterwards.</returns>
    internal static bool TryDelete(
        string path,
        string owner,
        Action<string>? report = null,
        Action<string>? delete = null)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            (delete ?? DeleteTree)(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (report ?? TestDiagnostics.Report)(
                $"{owner} could not delete its temp directory '{path}' ({ex.GetType().Name}): {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// Deletes a directory tree, first restoring owner access when a test left part of it read-only. A
    /// run killed between making a directory read-only and restoring it (the failure-injection shape
    /// several suites use) would otherwise leave a tree no later run could ever delete.
    /// </summary>
    internal static void DeleteTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            if (OperatingSystem.IsWindows())
            {
                throw;
            }

            RestoreOwnerAccess(path);

            Directory.Delete(path, recursive: true);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestoreOwnerAccess(string path)
    {
        const UnixFileMode OwnerAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };

        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | OwnerAll);

        foreach (string directory in Directory.EnumerateDirectories(path, "*", options))
        {
            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | OwnerAll);
        }
    }
}
