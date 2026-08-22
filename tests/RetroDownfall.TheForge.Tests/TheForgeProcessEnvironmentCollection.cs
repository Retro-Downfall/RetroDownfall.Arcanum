using RetroDownfall.Arcanum.Core.Storage;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TheForgeProcessEnvironmentCollection
{

    public const string Name = "TheForgeProcessEnvironment";

}

internal sealed class TheForgeTestHomeScope : IDisposable
{

    private static readonly string[] Variables =
    [
        "HOME",
        "USERPROFILE",
        "DOTNET_ENVIRONMENT",
        "ASPNETCORE_ENVIRONMENT",
        "ARCANUM_TEST_HOME",
    ];

    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

    internal TheForgeTestHomeScope(string prefix)
    {

        foreach (string name in Variables)
        {

            _original[name] = global::System.Environment.GetEnvironmentVariable(name);

        }

        Root = Path.Combine(
            Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Root);

        global::System.Environment.SetEnvironmentVariable("HOME", Root);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", Root);

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", Root);

    }

    internal string Root { get; }

    public void Dispose()
    {

        foreach ((string name, string? value) in _original)
        {

            global::System.Environment.SetEnvironmentVariable(name, value);

        }

        if (Directory.Exists(Root))
        {

            Directory.Delete(Root, recursive: true);

        }

    }

    internal static string ClientMutationLockPath()
    {

        string guardedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(ArcanumPaths.GrimoireDirectory));

        return Path.Combine(
            Path.GetDirectoryName(guardedRoot)!,
            $".arcanum-client-mutation-{Path.GetFileName(guardedRoot)}.lock");

    }

    internal static FileStream HoldClientMutationLock()
    {

        string path = ClientMutationLockPath();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        FileStream held = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }

        return held;

    }

}
