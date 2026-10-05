using System.Runtime.InteropServices;
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

    /// <summary>
    /// The name offsets read are those of the 64-bit-inode macOS dirent and the 64-bit Linux dirent. The
    /// plain <c>readdir</c> symbol on macOS x64 returns the legacy 32-bit-inode struct, whose name length
    /// and name sit elsewhere, so reading it through the arm64 offsets would invent names from the wrong
    /// bytes; macOS x64 is not a shipping RID and fails closed, like its <c>stat</c> layout.
    /// </summary>
    [Fact]
    public void Macos_x64_dirent_layout_is_rejected()
    {
        Assert.False(
            SecureDirectoryNameEnumerator.TryGetUnixDirentLayout(
                isMacOS: true,
                Architecture.X64,
                out _,
                out _));
    }

    [Theory]
    [InlineData(true, Architecture.Arm64, 21, 18)]
    [InlineData(false, Architecture.X64, 19, -1)]
    [InlineData(false, Architecture.Arm64, 19, -1)]
    public void The_verified_dirent_layouts_are_read(
        bool isMacOS,
        Architecture architecture,
        int expectedNameOffset,
        int expectedNameLengthOffset)
    {
        Assert.True(
            SecureDirectoryNameEnumerator.TryGetUnixDirentLayout(
                isMacOS,
                architecture,
                out int nameOffset,
                out int nameLengthOffset));

        Assert.Equal(expectedNameOffset, nameOffset);

        Assert.Equal(expectedNameLengthOffset, nameLengthOffset);
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
