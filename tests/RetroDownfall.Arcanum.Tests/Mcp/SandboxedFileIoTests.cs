using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class SandboxedFileIoTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string _outsideFile = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _outsideFile = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(_outsideFile, "outside secret");

    }

    public async Task DisposeAsync()
    {

        FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = null;

        FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        SecureFileReader.AfterOpenForTests = null;

        WorkspacePathPolicy.ResetTestSeams();

        if (File.Exists(_outsideFile))
        {

            File.Delete(_outsideFile);

        }

        await _workspace.DisposeAsync();

    }

    [SkippableFact]
    public void TryOpenForRead_rejects_symlink_to_outside_workspace()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string linkPath = Path.Combine(_workspace.Root, "escape-link.txt");

        if (File.Exists(linkPath))
        {

            File.Delete(linkPath);

        }

        File.CreateSymbolicLink(linkPath, _outsideFile);

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            linkPath,
            out _,
            out _);

        Assert.False(opened);

    }

    [Fact]
    public void TryOpenForRead_rejects_path_outside_workspace()
    {

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            _outsideFile,
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.False(opened);

        Assert.Null(stream);

        AssertSandboxError(error);

    }

    [Fact]
    public void TryOpenForRead_rejects_path_that_escapes_on_post_open_validation()
    {

        string target = Path.Combine(_workspace.Root, "changed-between-validations.txt");

        File.WriteAllText(target, "inside");

        int relativePathCalls = 0;

        WorkspacePathPolicy.SetRelativePathResolverForTests((root, candidate) =>
        {

            relativePathCalls++;

            return relativePathCalls <= 2
                ? Path.GetRelativePath(root, candidate)
                : "..";

        });

        try
        {

            bool opened = SandboxedFileIo.TryOpenForRead(
                _workspace.Root,
                target,
                out FileStream? stream,
                out McpToolsCallResultWire? error);

            Assert.False(opened);

            Assert.Null(stream);

            Assert.Equal(3, relativePathCalls);

            AssertSandboxError(error);

        }
        finally
        {

            WorkspacePathPolicy.ResetTestSeams();

        }

    }

    [Fact]
    public void TryOpenForRead_rejects_path_that_escapes_on_preopen_symlink_validation()
    {

        string target = Path.Combine(
            _workspace.Root,
            "changed-before-open.txt");

        File.WriteAllText(target, "inside");

        int relativePathCalls = 0;

        WorkspacePathPolicy.SetRelativePathResolverForTests(
            (root, candidate) =>
            {
                relativePathCalls++;

                return relativePathCalls == 1
                    ? Path.GetRelativePath(root, candidate)
                    : "..";
            });

        try
        {
            bool opened = SandboxedFileIo.TryOpenForRead(
                _workspace.Root,
                target,
                out FileStream? stream,
                out McpToolsCallResultWire? error);

            Assert.False(opened);

            Assert.Null(stream);

            Assert.Equal(2, relativePathCalls);

            AssertSandboxError(error);
        }
        finally
        {
            WorkspacePathPolicy.ResetTestSeams();
        }
    }

    [Fact]
    public void TryOpenForRead_rejects_missing_file_when_identity_cannot_be_resolved()
    {

        string missing = Path.Combine(_workspace.Root, "missing.txt");

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            missing,
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.False(opened);

        Assert.Null(stream);

        Assert.False(File.Exists(missing));

        AssertSandboxError(error);

    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_writes_inside_workspace()
    {

        string target = Path.Combine(_workspace.Root, "atomic.txt");

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "atomic content",
            CancellationToken.None);

        Assert.True(success);

        Assert.Equal("atomic content", await File.ReadAllTextAsync(target));

    }

    /// <summary>
    /// An overwrite carries the destination's UTF-8 BOM across, as the other write paths do.
    /// </summary>
    /// <remarks>
    /// <c>SecureFileReader.ReadUtf8TextAsync</c> strips the preamble before handing the model any
    /// text, so a read_file/write_file round-trip through MCP used to silently strip the BOM from
    /// every file that had one — an unrequested re-encoding the model never asked for.
    /// <c>PhysicalFileSystemWriter</c> and <c>WorkspaceTextFile</c> both preserve it; this was the
    /// last write path that did not.
    /// </remarks>
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_preserves_an_existing_utf8_bom()
    {

        string target = Path.Combine(_workspace.Root, "bom.txt");

        byte[] preamble = [0xEF, 0xBB, 0xBF];

        await File.WriteAllBytesAsync(target, [.. preamble, .. "original"u8.ToArray()]);

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "replacement",
            CancellationToken.None);

        Assert.True(success);

        byte[] expected = [.. preamble, .. "replacement"u8.ToArray()];

        Assert.Equal(expected, await File.ReadAllBytesAsync(target));

    }

    /// <summary>
    /// A destination without a BOM never gains one, and a caller who supplies the preamble himself
    /// does not get it twice.
    /// </summary>
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_never_introduces_or_doubles_a_preamble()
    {

        string plain = Path.Combine(_workspace.Root, "plain.txt");

        await File.WriteAllBytesAsync(plain, "original"u8.ToArray());

        (bool plainWritten, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            plain,
            "replacement",
            CancellationToken.None);

        Assert.True(plainWritten);

        Assert.Equal("replacement"u8.ToArray(), await File.ReadAllBytesAsync(plain));

        byte[] preamble = [0xEF, 0xBB, 0xBF];

        string bommed = Path.Combine(_workspace.Root, "double.txt");

        await File.WriteAllBytesAsync(bommed, [.. preamble, .. "original"u8.ToArray()]);

        (bool bommedWritten, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            bommed,
            "\uFEFFreplacement",
            CancellationToken.None);

        Assert.True(bommedWritten);

        byte[] expected = [.. preamble, .. "replacement"u8.ToArray()];

        Assert.Equal(expected, await File.ReadAllBytesAsync(bommed));

    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_does_not_treat_a_short_destination_as_a_preamble()
    {

        string target = Path.Combine(_workspace.Root, "short.txt");

        await File.WriteAllBytesAsync(target, [0xEF]);

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "replacement",
            CancellationToken.None);

        Assert.True(success);

        Assert.Equal("replacement"u8.ToArray(), await File.ReadAllBytesAsync(target));

    }

    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_existing_hard_link()
    {

        Skip.If(
            !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Unsupported operating system.");

        string target = Path.Combine(_workspace.Root, "linked-write.txt");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-sandbox-link-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(target, "original");

        try
        {

            Assert.True(HardLinkTestSupport.TryCreate(outsideAlias, target));

            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "replacement",
                    CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.Equal("original", await File.ReadAllTextAsync(target));

            Assert.Equal("original", await File.ReadAllTextAsync(outsideAlias));

        }
        finally
        {

            File.Delete(outsideAlias);

        }

    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_fails_closed_when_link_count_is_unavailable()
    {

        string target = Path.Combine(_workspace.Root, "unknown-link-count.txt");

        await File.WriteAllTextAsync(target, "original");

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ => null;

        try
        {

            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "replacement",
                    CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.Equal("original", await File.ReadAllTextAsync(target));

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        }

    }

    [Fact]
    public void TryOpenForRead_accepts_regular_file_inside_workspace()
    {

        string target = Path.Combine(_workspace.Root, "inside.txt");

        File.WriteAllText(target, "inside");

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            target,
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.True(
            opened,
            error?.Content?[0].Text);

        Assert.NotNull(stream);

        using (stream)
        {

            Assert.Equal("inside", new StreamReader(stream).ReadToEnd());

        }

    }

    [Fact]
    public void TryOpenForRead_rejects_hard_linked_regular_file()
    {

        string target = Path.Combine(_workspace.Root, "linked-read.txt");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-sandbox-read-link-{Guid.NewGuid():N}.txt");

        File.WriteAllText(target, "inside");

        try
        {

            Assert.True(HardLinkTestSupport.TryCreate(outsideAlias, target));

            bool opened = SandboxedFileIo.TryOpenForRead(
                _workspace.Root,
                target,
                out FileStream? stream,
                out McpToolsCallResultWire? error);

            Assert.False(opened);

            Assert.Null(stream);

            AssertSandboxError(error);

        }
        finally
        {

            File.Delete(outsideAlias);

        }

    }

    [Fact]
    public async Task TryReadAllTextAsync_rejects_malformed_utf8()
    {

        string target = Path.Combine(_workspace.Root, "malformed.txt");

        await File.WriteAllBytesAsync(target, [0x66, 0x80, 0x6f]);

        (string? content, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                target,
                maxBytes: 1024,
                CancellationToken.None);

        Assert.Null(content);

        Assert.NotNull(error);

        Assert.True(error!.IsError);

        string message = Assert.Single(error.Content!).Text!;

        Assert.DoesNotContain(target, message, StringComparison.Ordinal);

        Assert.DoesNotContain("f�o", message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task TryReadAllTextAsync_observes_cancellation_inside_read_loop()
    {

        string target = Path.Combine(_workspace.Root, "cancel-read.txt");

        await File.WriteAllTextAsync(target, new string('x', 8192));

        using CancellationTokenSource cancellation = new();

        SecureFileReader.AfterOpenForTests = _ =>
        {
            SecureFileReader.AfterOpenForTests = null;

            cancellation.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                target,
                maxBytes: 8192,
                cancellation.Token));

    }

    [Fact]
    public void TryRevalidateOpenedHandle_rejects_stream_with_blank_opened_path()
    {

        string target = Path.Combine(_workspace.Root, "blank-name.txt");

        File.WriteAllText(target, "inside");

        using FileStream stream = new BlankNameFileStream(target);

        bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
            _workspace.Root,
            stream,
            expectedIdentity: default,
            out McpToolsCallResultWire? error);

        Assert.False(valid);

        AssertSandboxError(error);

    }

    [Fact]
    public void TryRevalidateOpenedHandle_rejects_open_file_outside_workspace()
    {

        Assert.True(
            FileHandleIdentityInterop.TryGetPathIdentity(_outsideFile, out FileHandleIdentity expectedIdentity));

        using FileStream stream = new(
            _outsideFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
            _workspace.Root,
            stream,
            expectedIdentity,
            out McpToolsCallResultWire? error);

        Assert.False(valid);

        AssertSandboxError(error);

    }

    [Fact]
    public void TryRevalidateOpenedHandle_rejects_when_handle_identity_cannot_be_resolved()
    {

        string target = Path.Combine(_workspace.Root, "missing-handle-identity.txt");

        File.WriteAllText(target, "inside");

        Assert.True(
            FileHandleIdentityInterop.TryGetPathIdentity(target, out FileHandleIdentity expectedIdentity));

        using FileStream stream = new(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ => null;

        try
        {

            bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
                _workspace.Root,
                stream,
                expectedIdentity,
                out McpToolsCallResultWire? error);

            Assert.False(valid);

            AssertSandboxError(error);

        }
        finally
        {

            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        }

    }

    [Fact]
    public void TryOpenForRead_rejects_when_handle_identity_mismatches_preopen_identity()
    {

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(1, 1),
                HardLinkCount: 1);

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(1, 2),
                HardLinkCount: 1);

        try
        {

            string target = Path.Combine(_workspace.Root, "mismatch.txt");

            File.WriteAllText(target, "mismatch");

            bool opened = SandboxedFileIo.TryOpenForRead(
                _workspace.Root,
                target,
                out _,
                out McpToolsCallResultWire? error);

            Assert.False(opened);

            Assert.NotNull(error);

            Assert.Contains("sandbox", error!.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        }

    }

    // W3.4 Group C #7: the write path validates the target lexically then File.Move's the
    // temp file (TOCTOU between validation and the move). A post-move handle-identity check
    // mirrors the read path: the destination's opened handle identity must match the temp
    // file's pre-move identity (the move preserves the inode on the same filesystem). A
    // mismatch means the destination was swapped (e.g. to a symlink) between validation and
    // the move, and the write is rejected as a sandbox escape.
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_when_destination_handle_mismatches_temp_identity()
    {

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => new FileHandleIdentity(7, 7);

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => new FileHandleIdentity(7, 8);

        try
        {

            string target = Path.Combine(_workspace.Root, "write-mismatch.txt");

            File.WriteAllText(target, "original");

            (bool success, McpToolsCallResultWire? error) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                target,
                "new content",
                CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.Contains("sandbox", error!.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

            FileHandleIdentityInterop.TryGetHandleIdentityForTests = null;

        }

    }

    [SkippableFact]
    public void TryGetPathIdentity_MatchesHandleIdentity_OnUnix()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string target = Path.Combine(_workspace.Root, "identity.txt");

        File.WriteAllText(target, "identity");

        using SafeFileHandle handle = File.OpenHandle(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);

        bool pathOk = FileHandleIdentityInterop.TryGetPathIdentity(target, out FileHandleIdentity pathIdentity);

        bool handleOk = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity handleIdentity);

        Assert.True(pathOk);

        Assert.True(handleOk);

        Assert.True(FileHandleIdentity.IdentitiesMatch(pathIdentity, handleIdentity));

    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_target_outside_workspace()
    {

        (bool success, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                _outsideFile,
                "attacker content",
                CancellationToken.None);

        Assert.False(success);

        AssertSandboxError(error);

        Assert.Equal("outside secret", await File.ReadAllTextAsync(_outsideFile));

    }

    // The write path revalidates containment a second time after creating the parent directory,
    // because directory creation is an observable pause an attacker can use to swap the parent for
    // a symlink out of the workspace. That second revalidation must fail closed before any staging.
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_target_that_escapes_after_parent_directory_creation()
    {

        string target = Path.Combine(_workspace.Root, "escapes-after-mkdir.txt");

        int relativePathCalls = 0;

        WorkspacePathPolicy.SetRelativePathResolverForTests((root, candidate) =>
        {

            relativePathCalls++;

            return relativePathCalls == 1
                ? Path.GetRelativePath(root, candidate)
                : "..";

        });

        try
        {

            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "attacker content",
                    CancellationToken.None);

            Assert.False(success);

            Assert.Equal(2, relativePathCalls);

            AssertSandboxError(error);

            Assert.False(File.Exists(target));

        }
        finally
        {

            WorkspacePathPolicy.ResetTestSeams();

        }

    }

    // A path with no parent directory (a filesystem root) falls back to the workspace root when
    // choosing the staging directory. The write must still be rejected: the destination is a
    // directory, so nothing may be staged or replaced.
    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_root_path_that_has_no_parent_directory()
    {

        // A Windows drive root cannot be probed for a link target: the metadata query underneath
        // WorkspacePathPolicy's symlink check cannot name a root directory, so the check fails
        // closed and containment revalidation rejects the path before the staging fallback this
        // test exists to cover is reached. Failing closed at a drive root is the posture we want,
        // which makes the branch unreachable on Windows rather than broken.
        Skip.If(
            OperatingSystem.IsWindows(),
            "Containment revalidation fails closed at a Windows drive root, short of the staging fallback under test.");

        string filesystemRoot = Path.GetPathRoot(_workspace.Root)!;

        Assert.Null(Path.GetDirectoryName(filesystemRoot));

        WorkspacePathPolicy.SetRelativePathResolverForTests((_, _) => string.Empty);

        try
        {

            // Pins that containment revalidation passes, so the rejection below comes from the
            // staging/replace step rather than from the pre-write containment check.
            Assert.True(WorkspacePathPolicy.RevalidatePathBeforeIo(filesystemRoot, filesystemRoot));

            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    filesystemRoot,
                    filesystemRoot,
                    "attacker content",
                    CancellationToken.None);

            Assert.False(success);

            AssertSandboxError(error);

            Assert.Empty(Directory.EnumerateFiles(filesystemRoot, ".arcanum-*.tmp"));

        }
        finally
        {

            WorkspacePathPolicy.ResetTestSeams();

        }

    }

    // W3.4 Group C #7: when the post-move identity check fails AND the best-effort rollback cannot
    // confirm the quarantined destination, the caller must be told the destination is unverified
    // instead of receiving a success or a generic pre-move failure.
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_reports_unverified_destination_when_rollback_cannot_confirm_quarantine()
    {

        string target = Path.Combine(_workspace.Root, "unverified-write.txt");

        await File.WriteAllTextAsync(target, "original");

        // Forces the post-move handle-identity check to fail: the identity captured before the
        // move can never match the identity of the real moved file.
        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => new FileHandleIdentity(7, 7);

        // Everything else resolves normally except the quarantined copy, whose metadata cannot be
        // read back, so the rollback cannot confirm the unverified content was contained.
        FileHandleIdentityInterop.TryGetPathMetadataForTests = path =>
            path.Contains(".arcanum-quarantine-", StringComparison.Ordinal)
                ? null
                : ResolveRealPathMetadata(path);

        try
        {

            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "replacement",
                    CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.True(error!.IsError);

            Assert.Contains(
                "unverified",
                Assert.Single(error.Content!).Text!,
                StringComparison.OrdinalIgnoreCase);

            string? destinationContent = File.Exists(target)
                ? await File.ReadAllTextAsync(target)
                : null;

            Assert.NotEqual("replacement", destinationContent);

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        }

    }

    [Fact]
    public async Task TryReadAllTextAsync_returns_no_content_for_path_outside_workspace()
    {

        (string? content, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                _outsideFile,
                maxBytes: 1024,
                CancellationToken.None);

        Assert.Null(content);

        AssertSandboxError(error);

    }

    // The handle is revalidated again after the bytes are read, so content read through a handle
    // that no longer resolves inside the workspace is discarded rather than returned to the caller.
    [Fact]
    public async Task TryReadAllTextAsync_discards_content_when_post_read_revalidation_fails()
    {

        string target = Path.Combine(_workspace.Root, "escapes-after-read.txt");

        await File.WriteAllTextAsync(target, "secret payload");

        int relativePathCalls = 0;

        WorkspacePathPolicy.SetRelativePathResolverForTests((root, candidate) =>
        {

            relativePathCalls++;

            return relativePathCalls <= 3
                ? Path.GetRelativePath(root, candidate)
                : "..";

        });

        try
        {

            (string? content, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryReadAllTextAsync(
                    _workspace.Root,
                    target,
                    maxBytes: 1024,
                    CancellationToken.None);

            Assert.Null(content);

            Assert.Equal(4, relativePathCalls);

            AssertSandboxError(error);

        }
        finally
        {

            WorkspacePathPolicy.ResetTestSeams();

        }

    }

    // Real metadata for paths the seam is not simulating. Resolved through the no-follow probe,
    // which has its own seam, so this never has to unset (and race with) the seam it is called from.
    // Every path in these tests is a regular file, where the two probes agree.
    private static FileHandleMetadata? ResolveRealPathMetadata(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
            ? metadata
            : null;

    private static void AssertSandboxError(McpToolsCallResultWire? error)
    {

        Assert.NotNull(error);

        Assert.True(error!.IsError);

        McpToolContentTextWire content = Assert.IsType<McpToolContentTextWire>(Assert.Single(error.Content!));

        Assert.Contains("sandbox", content.Text!, StringComparison.OrdinalIgnoreCase);

    }

    private sealed class BlankNameFileStream : FileStream
    {

        public BlankNameFileStream(string path)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        {
        }

        public override string Name => " ";

    }

}
