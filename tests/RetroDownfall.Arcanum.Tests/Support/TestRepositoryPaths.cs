using System.Runtime.CompilerServices;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Resolves authored repository paths without assuming test binaries live inside the checkout.
/// </summary>
internal static class TestRepositoryPaths
{
    internal static string RepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        string? sourceDirectory = Path.GetDirectoryName(sourceFilePath);

        string[] startingDirectories =
        [
            sourceDirectory ?? string.Empty,
            global::System.Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        ];

        HashSet<string> visited = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        foreach (string startingDirectory in startingDirectories)
        {
            if (string.IsNullOrWhiteSpace(startingDirectory))
            {
                continue;
            }

            DirectoryInfo? directory = new(startingDirectory);

            for (int depth = 0; directory is not null && depth <= 32; depth++)
            {
                if (visited.Add(directory.FullName)
                    && IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        string currentDirectory = global::System.Environment.CurrentDirectory;

        throw new InvalidOperationException(
            $"Could not locate the repository root from source '{sourceFilePath}', "
            + $"working directory '{currentDirectory}', "
            + $"or output '{AppContext.BaseDirectory}'.");
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "RetroDownfall.Arcanum.slnx"))
        && Directory.Exists(Path.Combine(path, "src"))
        && Directory.Exists(Path.Combine(path, "tests"));
}
