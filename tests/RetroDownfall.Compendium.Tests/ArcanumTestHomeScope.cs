using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Compendium.Ux.Tests;

/// <summary>
/// Redirects every <see cref="ArcanumPaths"/> lookup into a uniquely owned temporary root for the
/// lifetime of one test class, then restores the process environment exactly as it was found.
/// </summary>
/// <remarks>
/// Setting only <c>HOME</c>/<c>USERPROFILE</c> is not enough. On Windows
/// <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c> resolves through
/// SHGetKnownFolderPath and ignores a post-start <c>USERPROFILE</c> assignment, so a suite that
/// redirects only those two variables writes <c>arcanum.json</c> and certificates into the
/// developer's real <c>~/.config/arcanum</c>. <see cref="ArcanumPaths"/> honours an explicit
/// override only when a host environment reads <c>Testing</c> <em>and</em>
/// <c>ARCANUM_TEST_HOME</c> is set, which is the contract this scope establishes before the first
/// path access.
/// </remarks>
internal sealed class ArcanumTestHomeScope : IDisposable
{

    private const string Testing = "Testing";

    /// <summary>Mirrors the private constant <see cref="ArcanumPaths"/> reads for its override.</summary>
    private const string TestHomeVariable = "ARCANUM_TEST_HOME";

    private static readonly string[] RedirectedVariables =
    [
        "HOME",
        "USERPROFILE",
        "DOTNET_ENVIRONMENT",
        "ASPNETCORE_ENVIRONMENT",
        TestHomeVariable,
    ];

    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

    private bool _disposed;

    public ArcanumTestHomeScope(
        string prefix)
    {

        foreach (string name in RedirectedVariables)
        {

            _originalValues[name] = global::System.Environment.GetEnvironmentVariable(name);

        }

        Root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(Root);

        global::System.Environment.SetEnvironmentVariable("HOME", Root);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", Root);

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", Testing);

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Testing);

        global::System.Environment.SetEnvironmentVariable(
            TestHomeVariable,
            Root);

    }

    /// <summary>Temporary profile root owned exclusively by the current test class.</summary>
    public string Root { get; }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        foreach ((string name, string? value) in _originalValues)
        {

            global::System.Environment.SetEnvironmentVariable(name, value);

        }

        try

        {

            if (Directory.Exists(Root))
            {

                Directory.Delete(Root, recursive: true);

            }

        }
        catch (IOException)

        {

            // Best-effort cleanup.

        }
        catch (UnauthorizedAccessException)

        {

            // Best-effort cleanup.

        }

    }

}
