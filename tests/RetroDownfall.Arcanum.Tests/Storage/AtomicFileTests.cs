using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Storage;

[Collection("WorkspacePathPolicy")]
public sealed class AtomicFileTests : IDisposable
{

    private readonly string _root;

    public AtomicFileTests()
    {

        _root = Path.Combine(Path.GetTempPath(), $"arcanum-atomicfile-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_root);

    }

    public void Dispose()
    {

        FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task ReplaceAsync_writes_content_and_leaves_no_temp_residue()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        string tempPath = TempPathFor(destination);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "durable content", ct),
            CancellationToken.None);

        Assert.Equal(AtomicReplaceStatus.Succeeded, status);

        Assert.Equal("durable content", await File.ReadAllTextAsync(destination));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        Assert.False(File.Exists(tempPath));

    }

    [Fact]
    public async Task ReplaceAsync_atomically_overwrites_existing_destination()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "replacement", ct),
            CancellationToken.None);

        Assert.Equal(AtomicReplaceStatus.Succeeded, status);

        Assert.Equal("replacement", await File.ReadAllTextAsync(destination));

        Assert.Single(Directory.GetFiles(_root));

    }

    [Fact]
    public async Task ReplaceAsync_new_destination_does_not_overwrite_concurrent_create()
    {

        string destination = Path.Combine(_root, "concurrent.txt");

        string tempPath = TempPathFor(destination);

        await Assert.ThrowsAsync<IOException>(
            () => AtomicFile.ReplaceAsync(
                destination,
                tempPath,
                (stream, ct) => WriteTextAsync(stream, "transaction", ct),
                CancellationToken.None,
                beforeMove: () =>
                {

                    File.WriteAllText(destination, "external");

                }));

        Assert.Equal("external", await File.ReadAllTextAsync(destination));

        Assert.False(File.Exists(tempPath));

    }

    [Fact]
    public async Task ReplaceAsync_invokes_afterReplace_hook_after_the_move_completes()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        bool destinationExistedWhenHookRan = false;

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "hooked", ct),
            CancellationToken.None,
            afterReplace: () =>
            {

                destinationExistedWhenHookRan = File.Exists(destination);

                return true;

            });

        Assert.Equal(AtomicReplaceStatus.Succeeded, status);

        Assert.True(destinationExistedWhenHookRan);

    }

    [Fact]
    public async Task ReplaceAsync_when_beforeReplace_returns_false_aborts_and_cleans_temp()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        string tempPath = TempPathFor(destination);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "should not land", ct),
            CancellationToken.None,
            beforeReplace: () => false);

        Assert.Equal(AtomicReplaceStatus.Aborted, status);

        Assert.False(File.Exists(destination));

        Assert.False(File.Exists(tempPath));

        Assert.Empty(Directory.GetFiles(_root));

    }

    [Fact]
    public async Task ReplaceAsync_when_afterReplace_returns_false_restores_backup()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        string tempPath = TempPathFor(destination);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "post-move state", ct),
            CancellationToken.None,
            afterReplace: () => false);

        Assert.Equal(AtomicReplaceStatus.RolledBack, status);

        Assert.Equal("original", await File.ReadAllTextAsync(destination));

        Assert.False(File.Exists(tempPath));

        Assert.Empty(Directory.GetFiles(_root, ".arcanum-bak-*"));

        Assert.Empty(Directory.GetFiles(_root, ".arcanum-quarantine-*"));

    }

    [Fact]
    public async Task ReplaceAsync_does_not_restore_over_external_post_move_replacement()
    {

        string destination = Path.Combine(_root, "external-after-move.txt");

        await File.WriteAllTextAsync(destination, "original");

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "transaction", ct),
            CancellationToken.None,
            afterReplace: () =>
            {

                File.Delete(destination);

                File.WriteAllText(destination, "external");

                return false;

            });

        Assert.Equal(AtomicReplaceStatus.ReplacedButUnverified, status);

        Assert.Equal("external", await File.ReadAllTextAsync(destination));

        string backup = Assert.Single(
            Directory.GetFiles(_root, ".arcanum-bak-*"));

        Assert.Equal("original", await File.ReadAllTextAsync(backup));

    }

    [Fact]
    public async Task ReplaceAsync_detects_post_move_content_change_and_retains_recovery()
    {

        string destination = Path.Combine(_root, "content-race.txt");

        await File.WriteAllTextAsync(destination, "original");

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "transaction", ct),
            CancellationToken.None,
            afterMoveBeforeVerify: () =>
                File.WriteAllText(destination, "external-after-move"));

        Assert.Equal(AtomicReplaceStatus.RolledBack, status);

        Assert.Equal("original", await File.ReadAllTextAsync(destination));

        string recovery = Assert.Single(
            Directory.GetFiles(_root, ".arcanum-quarantine-*"));

        Assert.Equal(
            "external-after-move",
            await File.ReadAllTextAsync(recovery));

    }

    [Fact]
    public async Task ReplaceAsync_when_afterReplace_fails_without_prior_file_quarantines_destination()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        string tempPath = TempPathFor(destination);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "unverified", ct),
            CancellationToken.None,
            afterReplace: () => false);

        Assert.Equal(AtomicReplaceStatus.RolledBack, status);

        Assert.False(File.Exists(destination));

        Assert.False(File.Exists(tempPath));

        string[] quarantined = Directory.GetFiles(_root, ".arcanum-quarantine-*");

        Assert.Single(quarantined);

        Assert.Equal("unverified", await File.ReadAllTextAsync(quarantined[0]));

    }

    [Fact]
    public async Task ReplaceAsync_when_write_throws_cleans_temp_and_propagates_and_keeps_destination()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        string tempPath = TempPathFor(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AtomicFile.ReplaceAsync(
                destination,
                tempPath,
                (_, _) => throw new InvalidOperationException("write failed"),
                CancellationToken.None));

        Assert.False(File.Exists(tempPath));

        Assert.Equal("original", await File.ReadAllTextAsync(destination));

    }

    [Fact]
    public async Task ReplaceAsync_preserves_existing_unix_mode_and_changes_mtime()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string destination = Path.Combine(_root, "executable.sh");

        await File.WriteAllTextAsync(destination, "old");

        UnixFileMode mode =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead;

        File.SetUnixFileMode(destination, mode);

        DateTime oldMtime = DateTime.UtcNow.AddMinutes(-10);

        File.SetLastWriteTimeUtc(destination, oldMtime);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "new", ct),
            CancellationToken.None);

        Assert.Equal(AtomicReplaceStatus.Succeeded, status);

        Assert.Equal(mode, File.GetUnixFileMode(destination));

        Assert.NotEqual(oldMtime, File.GetLastWriteTimeUtc(destination));

    }

    [Fact]
    public async Task ReplaceAsync_rejects_existing_file_with_multiple_hard_links()
    {

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string destination = Path.Combine(_root, "linked.txt");

        string alias = Path.Combine(Path.GetTempPath(), $"arcanum-atomic-link-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(destination, "original");

        try
        {

            Assert.True(HardLinkTestSupport.TryCreate(alias, destination));

            AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
                destination,
                TempPathFor(destination),
                (stream, ct) => WriteTextAsync(stream, "new", ct),
                CancellationToken.None);

            Assert.Equal(AtomicReplaceStatus.Aborted, status);

            Assert.Equal("original", await File.ReadAllTextAsync(destination));

            Assert.Equal("original", await File.ReadAllTextAsync(alias));

        }
        finally
        {

            File.Delete(alias);

        }

    }

    [Fact]
    public async Task ReplaceAsync_fails_closed_when_existing_link_metadata_is_unavailable()
    {

        string destination = Path.Combine(_root, "unknown-links.txt");

        await File.WriteAllTextAsync(destination, "original");

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ => null;

        try
        {

            AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
                destination,
                TempPathFor(destination),
                (stream, ct) => WriteTextAsync(stream, "new", ct),
                CancellationToken.None);

            Assert.Equal(AtomicReplaceStatus.Aborted, status);

            Assert.Equal("original", await File.ReadAllTextAsync(destination));

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        }

    }

    private string TempPathFor(string destination) =>
        Path.Combine(_root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

    private static async Task WriteTextAsync(Stream stream, string text, CancellationToken cancellationToken)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        await stream.WriteAsync(bytes, cancellationToken);

    }

}
