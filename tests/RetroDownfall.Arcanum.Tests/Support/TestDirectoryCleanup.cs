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

            (delete ?? DeleteRecursively)(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (report ?? TestDiagnostics.Report)(
                $"{owner} could not delete its temp directory '{path}' ({ex.GetType().Name}): {ex.Message}");

            return false;
        }
    }

    private static void DeleteRecursively(string path) =>
        Directory.Delete(path, recursive: true);
}
