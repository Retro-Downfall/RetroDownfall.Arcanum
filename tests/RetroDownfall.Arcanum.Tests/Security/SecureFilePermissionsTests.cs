using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class SecureFilePermissionsTests : IAsyncLifetime
{

    private TempWorkspace _temp = null!;

    public async Task InitializeAsync()
    {

        _temp = new TempWorkspace();

        await _temp.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _temp.DisposeAsync();

    }

    [Fact]
    public void EnsureOwnerOnlyDirectoryExists_creates_restricted_directory()
    {

        string path = Path.Combine(_temp.Root, "secure-dir");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        Assert.True(Directory.Exists(path));

        if (!OperatingSystem.IsWindows())
        {

            UnixFileMode mode = File.GetUnixFileMode(path);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);

        }

    }

    [Fact]
    public void ApplyOwnerOnlyFile_restricts_new_file()
    {

        string path = Path.Combine(_temp.Root, "secret.txt");

        File.WriteAllText(path, "secret");

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        if (!OperatingSystem.IsWindows())
        {

            UnixFileMode mode = File.GetUnixFileMode(path);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        }

    }

    [Fact]
    public void RunStartupPermissionSelfCheck_does_not_throw_for_missing_paths()
    {

        SecureFilePermissions.RunStartupPermissionSelfCheck(NullLogger.Instance);

    }

    [Fact]
    public void RunStartupPermissionSelfCheck_warns_for_world_readable_file()
    {

        string path = Path.Combine(_temp.Root, "world-readable.txt");

        File.WriteAllText(path, "data");

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        }

        SecureFilePermissions.RunStartupPermissionSelfCheck(NullLogger.Instance);

    }

    [Fact]
    public void EnsureOwnerOnlyDirectoryExists_is_idempotent()
    {

        string path = Path.Combine(_temp.Root, "existing-dir");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        Assert.True(Directory.Exists(path));

    }

}
