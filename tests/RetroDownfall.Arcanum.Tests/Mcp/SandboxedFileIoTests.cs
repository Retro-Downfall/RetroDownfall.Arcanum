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

        if (File.Exists(_outsideFile))
        {

            File.Delete(_outsideFile);

        }

        await _workspace.DisposeAsync();

    }

    [Fact]
    public void TryOpenForRead_rejects_symlink_to_outside_workspace()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

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
    public void TryOpenForRead_rejects_path_that_escapes_on_second_validation()
    {

        string target = Path.Combine(_workspace.Root, "changed-between-validations.txt");

        File.WriteAllText(target, "inside");

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

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_existing_hard_link()
    {

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

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
            out _);

        Assert.True(opened);

        Assert.NotNull(stream);

        using (stream)
        {

            Assert.Equal("inside", new StreamReader(stream).ReadToEnd());

        }

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

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => null;

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

            FileHandleIdentityInterop.TryGetHandleIdentityForTests = null;

        }

    }

    [Fact]
    public void TryOpenForRead_rejects_when_handle_identity_mismatches_preopen_identity()
    {

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => new FileHandleIdentity(1, 1);

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => new FileHandleIdentity(1, 2);

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

            FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

            FileHandleIdentityInterop.TryGetHandleIdentityForTests = null;

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

    [Fact]
    public void TryGetPathIdentity_MatchesHandleIdentity_OnUnix()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

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
