namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// Points the process at a temp root that does not exist, so the "Arcanum could not create a private
/// directory" path can be exercised for real rather than mocked behind a seam.
/// </summary>
/// <remarks>
/// <see cref="Path.GetTempPath"/> reads these variables on every call, so overriding them is the
/// only way to make <see cref="Directory.CreateTempSubdirectory"/> fail without inventing a test-only
/// abstraction over the one call that gives the directory its owner-only mode. It is process-wide,
/// which is why the tests that use it live in the non-parallel <c>ChildProcess</c> collection.
/// </remarks>
internal sealed class TempRootScope : IDisposable
{

    private static readonly string[] Names =
        OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];

    private readonly List<(string Name, string? Original)> _saved = [];

    public TempRootScope()
    {

        Root = Path.Combine(Path.GetTempPath(), "arcanum-absent-" + Guid.NewGuid().ToString("N"))
            + Path.DirectorySeparatorChar;

        foreach (string name in Names)
        {

            _saved.Add((name, System.Environment.GetEnvironmentVariable(name)));

            System.Environment.SetEnvironmentVariable(name, Root);

        }

    }

    /// <summary>The non-existent directory the process now believes is its temp root.</summary>
    public string Root { get; }

    public void Dispose()
    {

        foreach ((string name, string? original) in _saved)
        {
            System.Environment.SetEnvironmentVariable(name, original);
        }

    }

}
