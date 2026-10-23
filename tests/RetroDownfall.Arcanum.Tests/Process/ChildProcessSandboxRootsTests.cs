using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The Windows broker grants the per-run AppContainer SID every declared root and throws on one that
/// does not exist, so a POSIX path that resolves to <c>C:\opt\homebrew</c> on Windows fails every
/// sandboxed command with exit 73 and no output. The root set is therefore built per platform through
/// an explicit OS argument, which lets any host pin the Windows answer.
/// </summary>
public sealed class ChildProcessSandboxRootsTests : IDisposable
{
    private readonly string _workspace;

    public ChildProcessSandboxRootsTests()
    {
        _workspace = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-roots-ws-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_workspace, nameof(ChildProcessSandboxRootsTests));
    }

    [Fact]
    public void SystemRuntimeRoots_for_windows_contains_no_posix_paths()
    {
        List<string> roots = ChildProcessSandboxRoots.SystemRuntimeRoots(OSPlatform.Windows);

        // AppContainer already has default read on C:\Windows and C:\Program Files, so Windows needs
        // no extra runtime roots — and must never receive the Homebrew-style ones.
        Assert.Empty(roots);
    }

    [Fact]
    public void SystemRuntimeRoots_for_linux_carries_no_homebrew_roots()
    {
        List<string> roots = ChildProcessSandboxRoots.SystemRuntimeRoots(OSPlatform.Linux);

        Assert.DoesNotContain(roots, static root => root.Contains("homebrew", StringComparison.Ordinal));
    }

    [Fact]
    public void ForExecuteCommand_returns_only_existing_roots()
    {
        string missing = Path.Combine(_workspace, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            [missing],
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: true);

        IEnumerable<string> every = request.ReadWriteRoots
            .Concat(request.ReadOnlyRoots)
            .Concat(request.ReadExecuteRoots);

        Assert.All(
            every,
            static root => Assert.True(
                Directory.Exists(root) || File.Exists(root),
                $"Sandbox root does not exist: {root}"));

        Assert.Contains(
            request.ReadWriteRoots,
            root => string.Equals(
                Path.GetFileName(root),
                Path.GetFileName(_workspace),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Model_directed_tool_requests_keep_the_network()
    {
        // Network denial is opt-in for Arcanum-authored workers. Turning it on for a model-directed tool
        // would break every command that legitimately fetches (git, package restore), so each existing
        // builder must leave it off.
        string package = Directory.CreateDirectory(Path.Combine(_workspace, "packages")).FullName;

        string writable = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-roots-rw-" + Guid.NewGuid().ToString("N"))).FullName;

        try
        {
            ChildProcessSandboxRequest command = ChildProcessSandboxRoots.ForExecuteCommand(
                _workspace,
                null,
                allowUnsandboxed: false,
                windowsPathBoundaryRequired: true);

            ChildProcessSandboxRequest spell = ChildProcessSandboxRoots.ForSpellScript(
                [_workspace],
                _workspace,
                null,
                allowUnsandboxed: false,
                windowsPathBoundaryRequired: true);

            ChildProcessSandboxRequest check = ChildProcessSandboxRoots.ForWorkspaceCheck(
                _workspace,
                [package],
                [writable]);

            Assert.False(command.DenyNetwork);

            Assert.False(spell.DenyNetwork);

            Assert.False(check.DenyNetwork);

            Assert.False(new ChildProcessSandboxRequest { ReadWriteRoots = [], ReadExecuteRoots = [] }.DenyNetwork);
        }
        finally
        {
            _ = TestDirectoryCleanup.TryDelete(writable, nameof(ChildProcessSandboxRootsTests));
        }
    }

    [Fact]
    public void Model_directed_tool_requests_keep_their_file_writes()
    {
        // Write denial is opt-in for the document worker. A model-directed tool writes its workspace and its temp
        // directory, so each existing builder must leave it off.
        string package = Directory.CreateDirectory(Path.Combine(_workspace, "packages")).FullName;

        string writable = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-roots-rw-" + Guid.NewGuid().ToString("N"))).FullName;

        try
        {
            Assert.False(ChildProcessSandboxRoots.ForExecuteCommand(_workspace, null, allowUnsandboxed: false, windowsPathBoundaryRequired: true).DenyFileWrites);

            Assert.False(ChildProcessSandboxRoots.ForSpellScript([_workspace], _workspace, null, allowUnsandboxed: false, windowsPathBoundaryRequired: true).DenyFileWrites);

            Assert.False(ChildProcessSandboxRoots.ForWorkspaceCheck(_workspace, [package], [writable]).DenyFileWrites);

            Assert.False(new ChildProcessSandboxRequest { ReadWriteRoots = [], ReadExecuteRoots = [] }.DenyFileWrites);
        }
        finally
        {
            _ = TestDirectoryCleanup.TryDelete(writable, nameof(ChildProcessSandboxRootsTests));
        }
    }
}
