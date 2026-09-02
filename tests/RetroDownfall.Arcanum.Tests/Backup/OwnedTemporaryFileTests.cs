using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

[Collection("WorkspacePathPolicy")]
public sealed class OwnedTemporaryFileTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-owned-temp-file-" + Guid.NewGuid().ToString("N"));

    public OwnedTemporaryFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void Create_fails_closed_when_owner_only_permissions_cannot_be_verified()
    {

        string path = Path.Combine(_root, "permission-failure.tmp");

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, isDirectory) => isDirectory;

        IOException error = Assert.Throws<IOException>(
            () => OwnedTemporaryFile.Create(
                path,
                out FileStream _));

        Assert.Contains(
            "owner-only",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(path));

    }

    [Fact]
    public async Task Delete_succeeds_only_while_path_still_names_the_owned_file()
    {

        string path = Path.Combine(_root, "payload.tmp");

        OwnedTemporaryFile owned = OwnedTemporaryFile.Create(
            path,
            out FileStream stream);

        await using (stream)
        {

            await stream.WriteAsync("owned"u8.ToArray());

        }

        Assert.True(owned.TryDelete());

        Assert.False(File.Exists(path));

        OwnedTemporaryFile replaced = OwnedTemporaryFile.Create(
            path,
            out FileStream replacementStream);

        await using (replacementStream)
        {

            await replacementStream.WriteAsync("original"u8.ToArray());

        }

        string moved = Path.Combine(_root, "moved-owned");

        File.Move(path, moved);

        await File.WriteAllTextAsync(path, "replacement");

        Assert.False(replaced.TryDelete());

        Assert.Equal("replacement", await File.ReadAllTextAsync(path));

        Assert.True(File.Exists(moved));

    }

    [SkippableFact]
    public void Create_is_owner_only_on_Unix()
    {

        Skip.If(OperatingSystem.IsWindows(), "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string path = Path.Combine(_root, "owner-only.tmp");

        OwnedTemporaryFile owned = OwnedTemporaryFile.Create(
            path,
            out FileStream stream);

        using (stream)
        {

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));

        }

        Assert.True(owned.TryDelete());

    }

    [SkippableFact]
    public void Create_refuses_an_existing_symbolic_link_without_touching_its_target()
    {

        Skip.If(OperatingSystem.IsWindows(), "Symlink retarget refusal is exercised on Unix hosts.");

        string target = Path.Combine(_root, "target");

        File.WriteAllText(target, "target sentinel");

        string path = Path.Combine(_root, "payload-link.tmp");

        File.CreateSymbolicLink(path, target);

        Assert.Throws<IOException>(
            () => OwnedTemporaryFile.Create(
                path,
                out FileStream _));

        Assert.Equal("target sentinel", File.ReadAllText(target));

        Assert.Equal(
            target,
            File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName);

    }

}
