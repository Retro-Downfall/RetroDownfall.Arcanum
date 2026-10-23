using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Process;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Points the document worker at the managed Cli apphost in this test output, so the real worker mode runs out of process
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

        string apphost = Path.Combine(AppContext.BaseDirectory, "RetroDownfall.Arcanum.Cli");

        Skip.IfNot(File.Exists(apphost), "The Cli apphost is not in the test output.");

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
}
