using RetroDownfall.Arcanum.Infrastructure.Guild;
using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceMutationFenceWriterTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();

    private IDisposable? _processHistory;

    public Task InitializeAsync()
    {
        _processHistory = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        return _workspace.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _workspace.DisposeAsync();

        _processHistory?.Dispose();
    }

    [Fact]
    public async Task Physical_conflict_precedes_missing_parent_and_temporary_bytes()
    {
        using IDisposable registration = WorkspaceMutationFence.Install(new RejectingFence(_workspace.Root));

        ArcanumSettings settings = new();

        settings.Workspaces.EnableFileWrite = true;

        PhysicalFileSystemWriter writer = new(new TestOptionsSnapshot<ArcanumSettings>(settings));

        WorkspaceInfo workspace = new("test", "test", _workspace.Root, WorkspaceType.Custom, DateTimeOffset.UtcNow);

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "missing/leaf.txt", "new", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, result.Error.Code);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Root));
    }

    [Fact]
    public async Task Sandboxed_conflict_precedes_missing_parent_and_temporary_bytes()
    {
        using IDisposable registration = WorkspaceMutationFence.Install(new RejectingFence(_workspace.Root));

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root, Path.Combine(_workspace.Root, "missing", "leaf.txt"), "new", CancellationToken.None);

        Assert.False(success);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Root));
    }

    [Fact]
    public async Task Multi_file_conflict_precedes_all_staging_and_missing_parent_creation()
    {
        using IDisposable registration = WorkspaceMutationFence.Install(new RejectingFence(_workspace.Root));

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(_workspace.Root).CommitAsync(
            [new("missing/a.txt", WorkspaceFileFingerprint.Missing, Encoding.UTF8.GetBytes("a")),
             new("b.txt", WorkspaceFileFingerprint.Missing, Encoding.UTF8.GetBytes("b"))], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Failed, result.Status);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reversible_receipt_retains_permit_through_rollback_or_cleanup(bool rollback)
    {
        RecordingLease lease = new();

        using IDisposable registration = WorkspaceMutationFence.Install(new AcceptingFence(_workspace.Root, lease));

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(_workspace.Root).CommitAsync(
            [new("a.txt", WorkspaceFileFingerprint.Missing, Encoding.UTF8.GetBytes("a"))], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, result.Status);

        Assert.False(lease.Disposed);

        Assert.NotNull(result.Transaction);

        if (rollback)
        {
            Assert.True((await result.Transaction.RollbackAsync(CancellationToken.None)).Complete);
        }
        else
        {
            Assert.True((await result.Transaction.MarkIrreversibleAsync(CancellationToken.None)).Complete);
        }

        Assert.True(lease.Disposed);

        Assert.Null(lease.RecoveryReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Codex_conflict_preserves_existing_bytes(bool delete)
    {
        string path = _workspace.WriteFile("CODEX.md", "old");

        using IDisposable registration = WorkspaceMutationFence.Install(new RejectingFence(_workspace.Root));

        IResult? result = delete
            ? CodexEndpoints.DeleteCodex(_workspace.Root, path, "trace")
            : await CodexEndpoints.WriteCodexAsync(_workspace.Root, path, "new", "trace", CancellationToken.None);

        Assert.NotNull(result);

        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Native_spell_writer_requires_bound_complete_plan_when_fence_is_installed()
    {
        string directoryPath = _workspace.CreateSubdir("spell");

        string path = _workspace.WriteFile("spell/SPELL.md", "old");

        using SpellDirectory directory = SpellDirectory.Open(_workspace.Root, directoryPath);

        using IDisposable registration = WorkspaceMutationFence.Install(new RejectingFence(_workspace.Root));

        await Assert.ThrowsAsync<WorkspaceMutationFenceException>(() => directory.WriteTextAsync("SPELL.md", "new", CancellationToken.None));

        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Incomplete_patch_cleanup_preserves_a_reconciliation_hold_before_disposal()
    {
        string path = _workspace.WriteFile("a.txt", "old");

        RecordingLease lease = new();

        using IDisposable registration = WorkspaceMutationFence.Install(new AcceptingFence(_workspace.Root, lease));

        WorkspaceFileFingerprint fingerprint = await WorkspaceFileFingerprintService.CaptureForMutationAsync(_workspace.Root, "a.txt", CancellationToken.None);

        MultiFileCommitCoordinator coordinator = new(_workspace.Root, new MultiFileCommitCoordinatorOptions
        {
            BeforeFileRemovalRename = relative =>
            {
                if (relative.EndsWith(".bak", StringComparison.Ordinal))
                {
                    File.WriteAllText(Path.Combine(_workspace.Root, relative), "external edit");
                }
            },
        });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [new("a.txt", fingerprint, Encoding.UTF8.GetBytes("new"))], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, result.Status);

        WorkspaceArtifactCleanupResult cleanup = await result.Transaction!.MarkIrreversibleAsync(CancellationToken.None);

        Assert.False(cleanup.Complete);

        Assert.Equal("cleanup_incomplete", lease.RecoveryReason);

        Assert.True(lease.Disposed);

        Assert.Equal("new", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Incomplete_patch_rollback_preserves_a_reconciliation_hold_before_disposal()
    {
        string path = _workspace.WriteFile("a.txt", "old");

        RecordingLease lease = new();

        using IDisposable registration = WorkspaceMutationFence.Install(new AcceptingFence(_workspace.Root, lease));

        WorkspaceFileFingerprint fingerprint = await WorkspaceFileFingerprintService.CaptureForMutationAsync(_workspace.Root, "a.txt", CancellationToken.None);

        MultiFileCommitCoordinator coordinator = new(_workspace.Root, new MultiFileCommitCoordinatorOptions
        {
            AfterCommitStepAsync = (_, _) =>
            {
                File.WriteAllText(path, "external edit");

                throw new IOException("Receipt failed.");
            },
        });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [new("a.txt", fingerprint, Encoding.UTF8.GetBytes("new"))], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal("rollback_incomplete", lease.RecoveryReason);

        Assert.True(lease.Disposed);

        Assert.Equal("external edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Disposed_unprotected_plan_cannot_outlive_later_protected_installation()
    {
        WorkspaceMutationPlan plan = new(_workspace.Root);

        string path = Path.Combine(_workspace.Root, "leaf.txt");

        plan.Add(path);

        Result<IWorkspaceMutationLease> admitted = await plan.AdmitAsync(CancellationToken.None);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task captured;

        using (plan.Enter(admitted.Value))
        {
            captured = Task.Run(async () =>
            {
                await release.Task;

                WorkspaceMutationPlan.Demand(path);
            });
        }

        await admitted.Value.DisposeAsync();

        using IDisposable registration = WorkspaceMutationFence.Install(new RejectingFence(_workspace.Root));

        release.SetResult();

        await Assert.ThrowsAsync<WorkspaceMutationFenceException>(() => captured);
    }

    private sealed class RejectingFence(string root) : IWorkspaceMutationFence
    {
        public ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<IWorkspaceMutationLease>>(workspaceRoot == root
                ? Result<IWorkspaceMutationLease>.Failure(new(ErrorCodes.Guild.LeaseConflict, "Held by another Quest."))
                : Result<IWorkspaceMutationLease>.Success(new RecordingLease()));
    }

    private sealed class AcceptingFence(string root, RecordingLease lease) : IWorkspaceMutationFence
    {
        public ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<IWorkspaceMutationLease>>(workspaceRoot == root ? lease : new RecordingLease());
    }

    private sealed class RecordingLease : IWorkspaceMutationLease
    {
        public bool Disposed { get; private set; }

        public string? RecoveryReason { get; private set; }

        public ValueTask<Result> RetainRecoveryAsync(string reasonCode, CancellationToken cancellationToken)
        {
            Assert.False(Disposed);

            RecoveryReason = reasonCode;

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseDirectoryCreation preparation, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AdoptCreatedDirectoryAsync(WorkspaceLeaseCreatedDirectory proof, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseDirectoryRemoval preparation, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AdoptRemovedDirectoryAsync(WorkspaceLeaseRemovedDirectory proof, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public IDisposable Enter() => new EmptyScope();

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public void Dispose() { }
    }
}
