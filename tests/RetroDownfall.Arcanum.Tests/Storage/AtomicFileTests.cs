using Microsoft.Win32.SafeHandles;

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

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

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
    public async Task ReplaceAsync_rejects_destination_directory_without_touching_it()
    {

        string destination = Path.Combine(_root, "existing-directory");

        Directory.CreateDirectory(destination);

        string tempPath = TempPathFor(destination);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "replacement", ct),
            CancellationToken.None);

        Assert.Equal(AtomicReplaceStatus.Aborted, status);

        Assert.True(Directory.Exists(destination));

        Assert.False(File.Exists(tempPath));

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
    public async Task ReplaceAsync_retains_external_temp_replacement_before_finally()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        string tempPath = TempPathFor(destination);

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            tempPath,
            (stream, ct) => WriteTextAsync(stream, "transaction", ct),
            CancellationToken.None,
            beforeReplace: () =>
            {
                File.Delete(tempPath);

                File.WriteAllText(tempPath, "external temp replacement");

                return false;
            });

        Assert.Equal(AtomicReplaceStatus.Aborted, status);

        Assert.Equal(
            "external temp replacement",
            await File.ReadAllTextAsync(tempPath));

    }

    [Fact]
    public async Task ReplaceAsync_retains_external_backup_replacement_before_finally()
    {

        string destination = Path.Combine(_root, "artifact.txt");

        await File.WriteAllTextAsync(destination, "original");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AtomicFile.ReplaceAsync(
                destination,
                TempPathFor(destination),
                (stream, ct) => WriteTextAsync(
                    stream,
                    "transaction",
                    ct),
                CancellationToken.None,
                beforeMove: () =>
                {
                    string backup = Assert.Single(
                        Directory.GetFiles(
                            _root,
                            ".arcanum-bak-*"));

                    File.Delete(backup);

                    File.WriteAllText(
                        backup,
                        "external backup replacement");

                    throw new InvalidOperationException(
                        "stop before move");
                }));

        string retainedBackup = Assert.Single(
            Directory.GetFiles(_root, ".arcanum-bak-*"));

        Assert.Equal(
            "external backup replacement",
            await File.ReadAllTextAsync(retainedBackup));

        Assert.Equal(
            "original",
            await File.ReadAllTextAsync(destination));

    }

    [Fact]
    public async Task ReplaceAsync_cleans_identity_owned_backup_when_path_capture_fails()
    {
        string destination = Path.Combine(
            _root,
            "capture-failure.txt");

        await File.WriteAllTextAsync(
            destination,
            "original");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                Assert.StartsWith(
                    ".arcanum-bak-",
                    Path.GetFileName(path),
                    StringComparison.Ordinal);

                FileHandleIdentityInterop
                    .TryGetPathMetadataNoFollowForTests = null;

                return null;
            };

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(
                stream,
                "replacement",
                ct),
            CancellationToken.None);

        Assert.Equal(
            AtomicReplaceStatus.Aborted,
            status);

        Assert.Equal(
            "original",
            await File.ReadAllTextAsync(destination));

        Assert.Empty(
            Directory.GetFiles(
                _root,
                ".arcanum-bak-*"));
    }

    [Fact]
    public async Task ReplaceAsync_retains_unproven_backup_when_handle_capture_fails()
    {
        string destination = Path.Combine(
            _root,
            "handle-capture-failure.txt");

        await File.WriteAllTextAsync(
            destination,
            "original");

        FileHandleIdentityInterop.TryGetHandleMetadataForTests =
            handle =>
            {
                if (Directory.GetFiles(
                        _root,
                        ".arcanum-bak-*").Length == 1)
                {
                    return null;
                }

                return ReadActualHandleMetadata(handle);
            };

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(
                stream,
                "replacement",
                ct),
            CancellationToken.None);

        Assert.Equal(
            AtomicReplaceStatus.Aborted,
            status);

        Assert.Equal(
            "original",
            await File.ReadAllTextAsync(destination));

        Assert.Single(
            Directory.GetFiles(
                _root,
                ".arcanum-bak-*"));
    }

    [Fact]
    public async Task ReplaceAsync_retains_backup_path_replacement_swapped_during_handle_capture()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string destination = Path.Combine(
            _root,
            "handle-capture-race.txt");

        await File.WriteAllTextAsync(
            destination,
            "original");

        FileHandleIdentityInterop.TryGetHandleMetadataForTests =
            handle =>
            {
                FileHandleMetadata? metadata =
                    ReadActualHandleMetadata(handle);

                string[] backups = Directory.GetFiles(
                    _root,
                    ".arcanum-bak-*");

                if (backups.Length == 1)
                {
                    string backup = backups[0];

                    File.Delete(backup);
                    File.WriteAllText(
                        backup,
                        "external replacement");
                }

                return metadata;
            };

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(
                stream,
                "replacement",
                ct),
            CancellationToken.None);

        Assert.Equal(
            AtomicReplaceStatus.Aborted,
            status);

        string retainedBackup = Assert.Single(
            Directory.GetFiles(
                _root,
                ".arcanum-bak-*"));

        Assert.Equal(
            "external replacement",
            await File.ReadAllTextAsync(
                retainedBackup));
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
    public async Task ReplaceAsync_never_restores_backup_swapped_immediately_before_restore()
    {
        string destination = Path.Combine(
            _root,
            "backup-swap-before-restore.txt");

        await File.WriteAllTextAsync(
            destination,
            "original");

        bool swapped = false;

        FileHandleIdentityInterop.TryGetPathMetadataForTests =
            path =>
            {
                FileHandleMetadata? metadata =
                    ReadActualPathMetadata(path);

                if (!swapped
                    && Path.GetFileName(path).StartsWith(
                        ".arcanum-bak-",
                        StringComparison.Ordinal)
                    && Directory.GetFiles(
                        _root,
                        ".arcanum-quarantine-*").Length == 1)
                {
                    File.Delete(path);
                    File.WriteAllText(
                        path,
                        "external backup replacement");
                    swapped = true;
                }

                return metadata;
            };

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(
                stream,
                "transaction",
                ct),
            CancellationToken.None,
            afterReplace: () => false);

        Assert.True(swapped);
        Assert.Equal(
            AtomicReplaceStatus.ReplacedButUnverified,
            status);
        Assert.False(File.Exists(destination));

        string backup = Assert.Single(
            Directory.GetFiles(
                _root,
                ".arcanum-bak-*"));

        Assert.Equal(
            "external backup replacement",
            await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task ReplaceAsync_verifies_restored_destination_after_backup_move()
    {
        string destination = Path.Combine(
            _root,
            "post-restore-swap.txt");

        await File.WriteAllTextAsync(
            destination,
            "original");

        bool sawBackup = false;
        bool swapped = false;

        FileHandleIdentityInterop.TryGetPathMetadataForTests =
            path =>
            {
                FileHandleMetadata? metadata =
                    ReadActualPathMetadata(path);

                string[] backups = Directory.GetFiles(
                    _root,
                    ".arcanum-bak-*");

                sawBackup |= backups.Length > 0;

                if (!swapped
                    && sawBackup
                    && backups.Length == 0
                    && string.Equals(
                        path,
                        destination,
                        StringComparison.Ordinal)
                    && File.Exists(destination)
                    && File.ReadAllText(destination)
                        == "original")
                {
                    File.Delete(destination);
                    File.WriteAllText(
                        destination,
                        "external post-restore replacement");
                    swapped = true;
                }

                return metadata;
            };

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(
                stream,
                "transaction",
                ct),
            CancellationToken.None,
            afterReplace: () => false);

        Assert.True(swapped);
        Assert.Equal(
            AtomicReplaceStatus.ReplacedButUnverified,
            status);
        Assert.Equal(
            "external post-restore replacement",
            await File.ReadAllTextAsync(destination));

        Assert.Single(
            Directory.GetFiles(
                _root,
                ".arcanum-quarantine-*"));
    }

    [Fact]
    public async Task ReplaceAsync_detects_content_change_after_successful_hook()
    {

        string destination = Path.Combine(_root, "changed-after-hook.txt");

        await File.WriteAllTextAsync(destination, "original");

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "transaction", ct),
            CancellationToken.None,
            afterReplace: () =>
            {
                File.WriteAllText(destination, "changed-by-hook");

                return true;
            });

        Assert.Equal(AtomicReplaceStatus.RolledBack, status);

        Assert.Equal("original", await File.ReadAllTextAsync(destination));

        string recovery = Assert.Single(
            Directory.GetFiles(_root, ".arcanum-quarantine-*"));

        Assert.Equal("changed-by-hook", await File.ReadAllTextAsync(recovery));

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
    public async Task ReplaceAsync_does_not_restore_over_external_preverification_replacement()
    {

        string destination = Path.Combine(_root, "external-before-verify.txt");

        await File.WriteAllTextAsync(destination, "original");

        AtomicReplaceStatus status = await AtomicFile.ReplaceAsync(
            destination,
            TempPathFor(destination),
            (stream, ct) => WriteTextAsync(stream, "transaction", ct),
            CancellationToken.None,
            afterMoveBeforeVerify: () =>
            {
                File.Delete(destination);

                File.WriteAllText(destination, "external");
            });

        Assert.Equal(AtomicReplaceStatus.ReplacedButUnverified, status);

        Assert.Equal("external", await File.ReadAllTextAsync(destination));

        string backup = Assert.Single(
            Directory.GetFiles(_root, ".arcanum-bak-*"));

        Assert.Equal("original", await File.ReadAllTextAsync(backup));

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

    private static FileHandleMetadata? ReadActualHandleMetadata(
        SafeFileHandle handle)
    {
        Func<SafeFileHandle, FileHandleMetadata?>? seam =
            FileHandleIdentityInterop
                .TryGetHandleMetadataForTests;

        FileHandleIdentityInterop
            .TryGetHandleMetadataForTests = null;

        try
        {
            return FileHandleIdentityInterop
                .TryGetHandleMetadata(
                    handle,
                    out FileHandleMetadata metadata)
                ? metadata
                : null;
        }
        finally
        {
            FileHandleIdentityInterop
                .TryGetHandleMetadataForTests = seam;
        }
    }

    private static FileHandleMetadata? ReadActualPathMetadata(
        string path)
    {
        Func<string, FileHandleMetadata?>? seam =
            FileHandleIdentityInterop
                .TryGetPathMetadataForTests;

        FileHandleIdentityInterop
            .TryGetPathMetadataForTests = null;

        try
        {
            return FileHandleIdentityInterop
                .TryGetPathMetadata(
                    path,
                    out FileHandleMetadata metadata)
                ? metadata
                : null;
        }
        finally
        {
            FileHandleIdentityInterop
                .TryGetPathMetadataForTests = seam;
        }
    }

    private static async Task WriteTextAsync(Stream stream, string text, CancellationToken cancellationToken)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        await stream.WriteAsync(bytes, cancellationToken);

    }

}
