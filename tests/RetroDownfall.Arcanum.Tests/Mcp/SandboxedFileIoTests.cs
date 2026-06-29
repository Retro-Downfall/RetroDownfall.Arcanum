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

}
