using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The enumerator walks an already-open directory capability. Every enumeration of the same handle must
/// start at the beginning, or the second caller is told a populated directory is empty.
/// </summary>
[Collection("WorkspacePathPolicy")]
public sealed class SecureDirectoryNameEnumeratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-directory-enumerator-tests",
        Guid.NewGuid().ToString("N"));

    public SecureDirectoryNameEnumeratorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [SkippableFact]
    public void Enumerating_the_same_handle_twice_returns_the_same_names()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "The rewind defect is in the Unix readdir path.");

        string[] expected = ["alpha.bin", "beta.bin", "gamma.bin"];

        foreach (string name in expected)
        {
            File.WriteAllText(Path.Combine(_root, name), name);
        }

        using SafeFileHandle handle = OpenEnumerable(_root);

        Assert.True(SecureDirectoryNameEnumerator.TryEnumerate(handle, CancellationToken.None, out string[] first));

        Assert.True(SecureDirectoryNameEnumerator.TryEnumerate(handle, CancellationToken.None, out string[] second));

        Assert.True(SecureDirectoryNameEnumerator.TryEnumerate(handle, CancellationToken.None, out string[] third));

        Assert.Equal(expected, first);

        Assert.Equal(expected, second);

        Assert.Equal(expected, third);
    }

    [Fact]
    public void An_empty_directory_enumerates_as_empty_every_time()
    {
        using SafeFileHandle handle = OpenEnumerable(_root);

        Assert.True(SecureDirectoryNameEnumerator.TryEnumerate(handle, CancellationToken.None, out string[] first));

        Assert.True(SecureDirectoryNameEnumerator.TryEnumerate(handle, CancellationToken.None, out string[] second));

        Assert.Empty(first);

        Assert.Empty(second);
    }

    private static SafeFileHandle OpenEnumerable(string path)
    {
        Assert.True(
            FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                path,
                requestEnumeration: true,
                out SafeFileHandle handle,
                out FileHandleMetadata _));

        return handle;
    }
}
