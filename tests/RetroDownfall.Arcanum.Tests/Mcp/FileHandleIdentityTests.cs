using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class FileHandleIdentityTests : IDisposable
{

    private readonly string _tempFile;

    private readonly Func<string, FileHandleIdentity?>? _previousPathTestHook;

    private readonly Func<SafeFileHandle, FileHandleIdentity?>? _previousHandleTestHook;

    public FileHandleIdentityTests()
    {

        _tempFile = Path.Combine(Path.GetTempPath(), $"arcanum-fhi-test-{Guid.NewGuid():N}.txt");

        File.WriteAllText(_tempFile, "test");

        _previousPathTestHook = FileHandleIdentityInterop.TryGetPathIdentityForTests;

        _previousHandleTestHook = FileHandleIdentityInterop.TryGetHandleIdentityForTests;

    }

    public void Dispose()
    {

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _previousPathTestHook;

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _previousHandleTestHook;

        try
        {

            File.Delete(_tempFile);

        }
        catch
        {

            // Best-effort cleanup.

        }

    }

    [Fact]
    public void TryGetPathIdentity_TestHookReturningNull_ReturnsFalse()
    {

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => null;

        bool result = FileHandleIdentityInterop.TryGetPathIdentity(_tempFile, out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);

    }

    [Fact]
    public void TryGetPathIdentity_TestHookReturningValue_ReturnsTrue()
    {

        FileHandleIdentity expected = new(1, 2);

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => expected;

        bool result = FileHandleIdentityInterop.TryGetPathIdentity(_tempFile, out FileHandleIdentity identity);

        Assert.True(result);

        Assert.Equal(expected, identity);

    }

    [Fact]
    public void TryGetHandleIdentity_TestHookReturningNull_ReturnsFalse()
    {

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => null;

        using SafeFileHandle handle = File.OpenHandle(_tempFile, FileMode.Open, FileAccess.Read);

        bool result = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);

    }

    [Fact]
    public void TryGetHandleIdentity_TestHookReturningValue_ReturnsTrue()
    {

        FileHandleIdentity expected = new(3, 4);

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => expected;

        using SafeFileHandle handle = File.OpenHandle(_tempFile, FileMode.Open, FileAccess.Read);

        bool result = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity identity);

        Assert.True(result);

        Assert.Equal(expected, identity);

    }

    [Fact]
    public void TryGetHandleIdentity_InvalidHandle_ReturnsFalse()
    {

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => null;

        using SafeFileHandle handle = new(new IntPtr(-1), false);

        bool result = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);

    }

    [Fact]
    public void IdentitiesMatch_SameIdentity_ReturnsTrue()
    {

        FileHandleIdentity a = new(1, 2);

        FileHandleIdentity b = new(1, 2);

        Assert.True(FileHandleIdentity.IdentitiesMatch(a, b));

    }

    [Fact]
    public void IdentitiesMatch_DifferentIdentity_ReturnsFalse()
    {

        FileHandleIdentity a = new(1, 2);

        FileHandleIdentity b = new(1, 3);

        Assert.False(FileHandleIdentity.IdentitiesMatch(a, b));

    }

    [Fact]
    public void TryGetPathIdentity_UnknownPlatform_ReturnsFalse()
    {

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => null;

        bool result = FileHandleIdentityInterop.TryGetPathIdentity(
            "/nonexistent-platform-path",
            out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);

    }

}
