using System.Runtime.InteropServices;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Process;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Points the document worker at the same-build managed Cli project output, so the real worker mode runs out of process
/// through the production launch: the hidden mode in the Cli's <c>Main</c>, the applied jail with no network, the cleared
/// environment and the code-owned limits. A managed apphost needs what the Native AOT binary does not: its runtime's
/// location, read access to that runtime and the libraries it links against, and more file descriptors.
/// </summary>
internal static class ManagedApphostDocumentWorker
{
    private const string HomebrewPrefix = "/opt/homebrew/";

    /// <summary>The managed runtime keeps far more files open than the Native AOT binary the production limit is sized for.</summary>
    private const int FileDescriptors = 256;

    /// <summary>Skips the calling test where the real worker cannot run here; otherwise scopes the launch to the calling async flow.</summary>
    public static IDisposable Use()
    {
        Skip.IfNot(MacOsSandboxProbe.IsSandboxExecRunnable(), "Needs a macOS host where sandbox-exec can apply a profile.");

        string apphost = ResolveApphost(Path.Combine(AppContext.BaseDirectory, "document-worker.sha256"), AppContext.BaseDirectory);

        string dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        // A Homebrew runtime's compression library links Homebrew's brotli: without it every Flate stream decodes to nothing.
        List<string> runtimeRoots = [dotnetRoot];

        if (dotnetRoot.StartsWith(HomebrewPrefix, StringComparison.Ordinal))
        {
            runtimeRoots.Add(HomebrewPrefix);
        }

        return ProcessDocumentExtractionWorker.UseExecutableForTests(
            apphost,
            new Dictionary<string, string> { ["DOTNET_ROOT"] = dotnetRoot },
            runtimeRoots,
            FileDescriptors);
    }

    internal static string ResolveApphost(string manifestPath, string testOutputDirectory)
    {
        string forbiddenRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testOutputDirectory)) + Path.DirectorySeparatorChar;

        string? apphost = null;

        HashSet<string> files = new(StringComparer.Ordinal);

        foreach (string line in File.ReadAllLines(manifestPath))
        {
            if (line.Length <= 65 || line[64] != ' ')
            {
                throw new InvalidDataException("The managed document worker build manifest is malformed.");
            }

            string path = Path.GetFullPath(line[65..]);

            if (path.StartsWith(forbiddenRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The managed document worker must be outside the instrumented test output.");
            }

            if (!files.Add(path))
            {
                throw new InvalidDataException("The managed document worker build manifest repeats a file.");
            }

            using FileStream file = File.OpenRead(path);

            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(line[..64], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The managed document worker output changed after the test build.");
            }

            if (Path.GetFileName(path) == "RetroDownfall.Arcanum.Cli")
            {
                if (apphost is not null)
                {
                    throw new InvalidDataException("The managed document worker build manifest has multiple apphosts.");
                }

                apphost = path;
            }
        }

        if (apphost is null)
        {
            throw new InvalidDataException("The managed document worker build manifest has no apphost.");
        }

        string root = Path.GetDirectoryName(apphost)!;

        if (!files.SetEquals(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("The managed document worker file set changed after the test build.");
        }

        return apphost;
    }
}
