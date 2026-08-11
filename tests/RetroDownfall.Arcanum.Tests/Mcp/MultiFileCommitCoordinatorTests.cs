using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class MultiFileCommitCoordinatorTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop
            .TryGetPathMetadataForTests = null;

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task Commit_rejects_destinations_that_are_canonical_filesystem_aliases()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {

            return;

        }

        string firstPath = OperatingSystem.IsWindows()
            ? "Folder/File"
            : "Résumé.txt";

        string secondPath = OperatingSystem.IsWindows()
            ? "FOLDER/file. "
            : "RE\u0301SUME\u0301.TXT";

        WorkspaceFileCommitOperation[] operations =
        [
            new(firstPath, WorkspaceFileFingerprint.Missing, Encoding.UTF8.GetBytes("first")),
            new(secondPath, WorkspaceFileFingerprint.Missing, Encoding.UTF8.GetBytes("second")),
        ];

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(_workspace.Root)
            .CommitAsync(operations, CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Failed, result.Status);

        Assert.Equal(WorkspaceCommitFailure.Validation, result.Failure);

        Assert.Null(result.Transaction);

    }

    [Fact]
    public async Task Commit_stages_all_inputs_then_commits_sequentially_with_observable_non_isolation()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("a.txt", "a-new"),
            await WriteOperationAsync("b.txt", "b-new"),
        ];

        bool observedIntermediateState = false;

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeFirstCommitAsync = (context, _) =>
                {
                    Assert.Equal(4, context.StagedArtifactPaths.Count);

                    Assert.All(context.StagedArtifactPaths, AssertRelativePath);

                    return ValueTask.CompletedTask;
                },
                AfterCommitStepAsync = async (context, _) =>
                {
                    if (context.Index == 0)
                    {
                        observedIntermediateState =
                            await ReadAsync("a.txt") == "a-new"
                            && await ReadAsync("b.txt") == "b-old";
                    }
                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, result.Status);

        Assert.True(observedIntermediateState);

        Assert.NotNull(result.Transaction);

        WorkspaceArtifactCleanupResult cleanup =
            await result.Transaction!.MarkIrreversibleAsync(CancellationToken.None);

        Assert.True(cleanup.Complete);

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Commit_preflights_staging_capacity_before_creating_directories()
    {

        WorkspaceFileCommitOperation operation =
            await WriteOperationAsync(
                "capacity/deep/new.txt",
                "too-large");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                MaxStagingBytesPerFile = 4,
                MaxTotalStagingBytes = 4,
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Failed, result.Status);

        Assert.Equal(WorkspaceCommitFailure.Validation, result.Failure);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "capacity")));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Commit_failure_rolls_back_committed_steps_in_reverse_order()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        _workspace.WriteFile("c.txt", "c-old");

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("a.txt", "a-new"),
            await WriteOperationAsync("b.txt", "b-new"),
            await WriteOperationAsync("c.txt", "c-new"),
        ];

        List<string> rollbackOrder = [];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 2
                        ? ValueTask.FromException(new IOException("injected commit failure"))
                        : ValueTask.CompletedTask,
                AfterRollbackStep = context => rollbackOrder.Add(context.RelativePath),
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Failed, result.Status);

        Assert.Equal(["b.txt", "a.txt"], rollbackOrder);

        Assert.Equal("a-old", await ReadAsync("a.txt"));

        Assert.Equal("b-old", await ReadAsync("b.txt"));

        Assert.Equal("c-old", await ReadAsync("c.txt"));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Commit_rechecks_each_fingerprint_and_does_not_overwrite_external_change()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("a.txt", "a-new"),
            await WriteOperationAsync("b.txt", "b-new"),
        ];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = async (context, _) =>
                {
                    if (context.Index == 1)
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(_workspace.Root, "b.txt"),
                            "external",
                            CancellationToken.None);
                    }
                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Failed, result.Status);

        Assert.Equal(WorkspaceCommitFailure.ConcurrentModification, result.Failure);

        Assert.Equal("a-old", await ReadAsync("a.txt"));

        Assert.Equal("external", await ReadAsync("b.txt"));

    }

    [Fact]
    public async Task Commit_preserves_bom_mixed_delimiters_and_unix_mode_but_changes_mtime()
    {

        string path = Path.Combine(_workspace.Root, "script.txt");

        byte[] original =
        [
            0xEF, 0xBB, 0xBF,
            .. Encoding.UTF8.GetBytes("one\r\ntwo\nthree"),
        ];

        await File.WriteAllBytesAsync(path, original, CancellationToken.None);

        DateTime oldMtime = DateTime.UtcNow.AddMinutes(-10);

        File.SetLastWriteTimeUtc(path, oldMtime);

        UnixFileMode? expectedMode = null;

        if (!OperatingSystem.IsWindows())
        {
            expectedMode =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead;

            File.SetUnixFileMode(path, expectedMode.Value);
        }

        WorkspaceTextFile document = WorkspaceTextFile.Decode(original);

        WorkspaceFileCommitOperation operation = new(
            "script.txt",
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspace.Root,
                "script.txt",
                CancellationToken.None),
            document.InsertLines(1, ["inserted"]).Encode());

        MultiFileCommitCoordinator coordinator = new(_workspace.Root);

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [operation],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, result.Status);

        Assert.Equal(
            [
                0xEF, 0xBB, 0xBF,
                .. Encoding.UTF8.GetBytes("one\r\ninserted\r\ntwo\nthree"),
            ],
            await File.ReadAllBytesAsync(path, CancellationToken.None));

        Assert.NotEqual(oldMtime, File.GetLastWriteTimeUtc(path));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(expectedMode!.Value, File.GetUnixFileMode(path));
        }

        _ = await result.Transaction!.MarkIrreversibleAsync(CancellationToken.None);

    }

    [Fact]
    public async Task Staged_output_is_never_group_or_world_readable_while_it_is_written()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string path = _workspace.WriteFile("secret.env", "TOKEN=old");

        UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        File.SetUnixFileMode(path, ownerOnly);

        UnixFileMode? stagedMode = null;

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterStagingArtifactCreated = relativePath =>
                {

                    if (OperatingSystem.IsWindows())
                    {

                        return;

                    }

                    stagedMode ??= File.GetUnixFileMode(
                        Path.Combine(_workspace.Root, relativePath));

                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [await WriteOperationAsync("secret.env", "TOKEN=new")],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, result.Status);

        Assert.NotNull(stagedMode);

        Assert.Equal(
            UnixFileMode.None,
            stagedMode!.Value
                & (UnixFileMode.GroupRead
                    | UnixFileMode.GroupWrite
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherWrite));

        Assert.Equal(ownerOnly, File.GetUnixFileMode(path));

        _ = await result.Transaction!.MarkIrreversibleAsync(CancellationToken.None);

    }

    [Fact]
    public async Task Rollback_incomplete_keeps_relative_recovery_artifacts_and_external_content()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("a.txt", "a-new"),
            await WriteOperationAsync("b.txt", "b-new"),
        ];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterCommitStepAsync = async (context, _) =>
                {
                    if (context.Index == 0)
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(_workspace.Root, "a.txt"),
                            "external-after-commit",
                            CancellationToken.None);
                    }
                },
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(new IOException("stop after external edit"))
                        : ValueTask.CompletedTask,
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal("external-after-commit", await ReadAsync("a.txt"));

        Assert.NotNull(result.Recovery);

        Assert.All(result.Recovery!.AffectedPaths, AssertRelativePath);

        Assert.All(result.Recovery.ArtifactPaths, AssertRelativePath);

        Assert.NotEmpty(result.Recovery.ArtifactPaths);

    }

    [Fact]
    public async Task Post_move_external_replacement_is_not_adopted_as_transaction_content()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterDestinationMutation = context =>
                {

                    if (context.Index == 0)
                    {

                        File.Delete(Path.Combine(_workspace.Root, "a.txt"));

                        File.WriteAllText(
                            Path.Combine(_workspace.Root, "a.txt"),
                            "external-after-move");

                    }

                },
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(
                            new IOException("trigger rollback"))
                        : ValueTask.CompletedTask,
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [
                await WriteOperationAsync("a.txt", "a-new"),
                await WriteOperationAsync("b.txt", "b-new"),
            ],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal("external-after-move", await ReadAsync("a.txt"));

        Assert.Contains("a.txt", result.Recovery!.AffectedPaths);

        Assert.NotEmpty(result.Recovery.ArtifactPaths);

    }

    [Fact]
    public async Task Missing_destination_parent_replacement_invalidates_fingerprint()
    {

        string originalParent = _workspace.CreateSubdir("parent");

        WorkspaceFileFingerprint expected =
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspace.Root,
                "parent/new.txt",
                CancellationToken.None);

        string displacedParent = Path.Combine(_workspace.Root, "parent-original");

        Directory.Move(originalParent, displacedParent);

        _ = _workspace.CreateSubdir("parent");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root)
            .CommitAsync(
                [
                    new WorkspaceFileCommitOperation(
                        "parent/new.txt",
                        expected,
                        Encoding.UTF8.GetBytes("transaction")),
                ],
                CancellationToken.None);

        Assert.NotEqual(WorkspaceCommitStatus.Committed, result.Status);

        Assert.False(
            File.Exists(Path.Combine(_workspace.Root, "parent", "new.txt")));

    }

    [Fact]
    public async Task Final_create_revalidation_rejects_parent_identity_replacement()
    {

        string parent = _workspace.CreateSubdir("final-parent");

        WorkspaceFileCommitOperation operation =
            await WriteOperationAsync("final-parent/new.txt", "transaction");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeDestinationMutation = _ =>
                {

                    string displaced = Path.Combine(
                        _workspace.Root,
                        "final-parent-original");

                    Directory.Move(parent, displaced);

                    Directory.CreateDirectory(parent);

                    string staged = Assert.Single(
                        Directory.GetFiles(displaced, "*.tmp"));

                    File.Move(
                        staged,
                        Path.Combine(parent, Path.GetFileName(staged)),
                        overwrite: false);

                },
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.NotEqual(WorkspaceCommitStatus.Committed, result.Status);

        Assert.False(File.Exists(Path.Combine(parent, "new.txt")));

    }

    [Fact]
    public async Task Final_create_revalidation_binds_transaction_created_parent_identity()
    {

        _ = _workspace.CreateSubdir("outer");

        string parent = Path.Combine(_workspace.Root, "outer", "generated");

        WorkspaceFileCommitOperation operation =
            await WriteOperationAsync(
                "outer/generated/new.txt",
                "transaction");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeDestinationMutation = _ =>
                {

                    string displaced = Path.Combine(
                        _workspace.Root,
                        "outer",
                        "generated-original");

                    Directory.Move(parent, displaced);

                    Directory.CreateDirectory(parent);

                    string staged = Assert.Single(
                        Directory.GetFiles(displaced, "*.tmp"));

                    File.Move(
                        staged,
                        Path.Combine(parent, Path.GetFileName(staged)),
                        overwrite: false);

                },
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.NotEqual(WorkspaceCommitStatus.Committed, result.Status);

        Assert.False(File.Exists(Path.Combine(parent, "new.txt")));

    }

    [Fact]
    public async Task Rollback_removes_identity_matching_empty_transaction_created_directories()
    {

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("new/deep/created.txt", "created"),
            await WriteOperationAsync("second.txt", "second"),
        ];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(new IOException("injected failure"))
                        : ValueTask.CompletedTask,
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Failed, result.Status);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "new")));

    }

    [Fact]
    public async Task Rollback_retains_transaction_created_directory_that_gained_external_content()
    {

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("new/deep/created.txt", "created"),
            await WriteOperationAsync("second.txt", "second"),
        ];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterCommitStepAsync = async (context, _) =>
                {
                    if (context.Index == 0)
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(_workspace.Root, "new", "external.txt"),
                            "external",
                            CancellationToken.None);
                    }
                },
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(new IOException("injected failure"))
                        : ValueTask.CompletedTask,
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.True(File.Exists(Path.Combine(_workspace.Root, "new", "external.txt")));

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "new", "deep", "created.txt")));

        Assert.Contains("new", result.Recovery!.ArtifactPaths);

    }

    [Fact]
    public async Task Delete_commit_renames_to_artifact_and_rollback_restores_original()
    {

        _workspace.WriteFile("delete.txt", "original");

        WorkspaceFileCommitOperation operation = new(
            "delete.txt",
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspace.Root,
                "delete.txt",
                CancellationToken.None),
            OutputBytes: null);

        Assert.True(operation.IsDelete);

        string[] filesAfterRemoval = [];

        WorkspaceCommitResult commit = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterFileRemovalRename = _ =>
                    filesAfterRemoval = Directory.GetFiles(_workspace.Root)
                        .Select(Path.GetFileName)
                        .OfType<string>()
                        .ToArray(),
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, commit.Status);

        Assert.DoesNotContain("delete.txt", filesAfterRemoval);

        Assert.Contains(filesAfterRemoval, path => path.EndsWith(".deleted", StringComparison.Ordinal));

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "delete.txt")));

        Assert.NotEmpty(commit.Recovery!.ArtifactPaths);

        Assert.All(commit.Recovery.ArtifactPaths, AssertRelativePath);

        WorkspaceRollbackResult rollback =
            await commit.Transaction!.RollbackAsync(CancellationToken.None);

        Assert.True(
            rollback.Complete,
            $"Recovery: {string.Join(", ", rollback.Recovery?.ArtifactPaths ?? [])}");

        Assert.Equal("original", await ReadAsync("delete.txt"));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Delete_rename_is_journaled_before_post_rename_failure()
    {

        _workspace.WriteFile("delete-journal.txt", "original");

        WorkspaceFileCommitOperation operation = new(
            "delete-journal.txt",
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspace.Root,
                "delete-journal.txt",
                CancellationToken.None),
            OutputBytes: null);

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterFileRemovalRename = _ =>
                    throw new IOException("fail after successful rename"),
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Contains("delete-journal.txt", result.Recovery!.AffectedPaths);

        Assert.NotEmpty(result.Recovery.ArtifactPaths);

    }

    [Fact]
    public async Task Concurrent_file_replacement_during_delete_is_restored_without_overwrite()
    {

        _workspace.WriteFile("delete.txt", "original");

        WorkspaceFileCommitOperation operation = new(
            "delete.txt",
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspace.Root,
                "delete.txt",
                CancellationToken.None),
            OutputBytes: null);

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterFileRemovalRename = context =>
                {
                    if (context.RelativePath == "delete.txt")
                    {

                        string artifactPath = Path.Combine(
                            _workspace.Root,
                            context.ArtifactRelativePath.Replace(
                                '/',
                                Path.DirectorySeparatorChar));

                        File.Delete(artifactPath);

                        File.WriteAllText(artifactPath, "external");

                    }
                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [operation],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal("external", await ReadAsync("delete.txt"));

        Assert.NotNull(result.Recovery);

        Assert.NotEmpty(result.Recovery!.ArtifactPaths);

        Assert.All(result.Recovery.ArtifactPaths, AssertRelativePath);

    }

    [Fact]
    public async Task Concurrent_directory_replacement_during_cleanup_is_retained()
    {

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("new/deep/created.txt", "created"),
            await WriteOperationAsync("second.txt", "second"),
        ];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(new IOException("trigger rollback"))
                        : ValueTask.CompletedTask,
                BeforeDirectoryRemovalRename = relativePath =>
                {
                    if (relativePath == "new")
                    {

                        string path = Path.Combine(_workspace.Root, "new");

                        Directory.Delete(path, recursive: false);

                        Directory.CreateDirectory(path);

                    }
                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, "new")));

        Assert.Contains("new", result.Recovery!.ArtifactPaths);

    }

    [Fact]
    public async Task External_content_added_after_directory_rename_is_restored_and_reported()
    {

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("new/created.txt", "created"),
            await WriteOperationAsync("second.txt", "second"),
        ];

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(new IOException("trigger rollback"))
                        : ValueTask.CompletedTask,
                AfterDirectoryRemovalRename = context =>
                {
                    if (context.RelativePath == "new")
                    {

                        File.WriteAllText(
                            Path.Combine(
                                _workspace.Root,
                                context.ArtifactRelativePath.Replace(
                                    '/',
                                    Path.DirectorySeparatorChar),
                                "external.txt"),
                            "external");

                    }
                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            operations,
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal(
            "external",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, "new", "external.txt")));

        Assert.Contains("new", result.Recovery!.ArtifactPaths);

    }

    [Fact]
    public async Task Rollback_journals_directory_rename_before_cancellation()
    {

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(
                            new IOException("trigger rollback"))
                        : ValueTask.CompletedTask,
                AfterDirectoryRemovalRename = _ =>
                    throw new OperationCanceledException(
                        "cancel after directory cleanup rename"),
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [
                await WriteOperationAsync(
                    "directory-journal/created.txt",
                    "created"),
                await WriteOperationAsync("second.txt", "second"),
            ],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        string retained = Assert.Single(
            result.Recovery!.ArtifactPaths,
            path => path.Contains(
                ".directory-cleanup",
                StringComparison.Ordinal));

        Assert.True(
            Directory.Exists(
                Path.Combine(
                    _workspace.Root,
                    retained.Replace('/', Path.DirectorySeparatorChar))));

    }

    [Fact]
    public async Task Caller_cancellation_completes_rollback_then_propagates()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        WorkspaceFileCommitOperation[] operations =
        [
            await WriteOperationAsync("a.txt", "a-new"),
            await WriteOperationAsync("b.txt", "b-new"),
        ];

        using CancellationTokenSource cancellation = new();

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterCommitStepAsync = (context, _) =>
                {
                    if (context.Index == 0)
                    {
                        cancellation.Cancel();

                        throw new OperationCanceledException(cancellation.Token);
                    }

                    return ValueTask.CompletedTask;
                },
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CommitAsync(operations, cancellation.Token));

        Assert.Equal("a-old", await ReadAsync("a.txt"));

        Assert.Equal("b-old", await ReadAsync("b.txt"));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Cancellation_immediately_after_destination_mutation_restores_original()
    {

        _workspace.WriteFile("a.txt", "a-old");

        WorkspaceFileCommitOperation operation = await WriteOperationAsync("a.txt", "a-new");

        using CancellationTokenSource cancellation = new();

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterDestinationMutation = _ => cancellation.Cancel(),
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CommitAsync([operation], cancellation.Token));

        Assert.Equal("a-old", await ReadAsync("a.txt"));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Cancellation_after_staging_artifact_creation_does_not_strand_temp()
    {

        WorkspaceFileCommitOperation operation = await WriteOperationAsync("new.txt", "new");

        using CancellationTokenSource cancellation = new();

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterStagingArtifactCreated = _ => cancellation.Cancel(),
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CommitAsync([operation], cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "new.txt")));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Concurrent_create_at_final_mutation_is_not_overwritten()
    {

        WorkspaceFileCommitOperation operation = await WriteOperationAsync("new.txt", "ours");

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeDestinationMutation = _ =>
                    File.WriteAllText(
                        Path.Combine(_workspace.Root, "new.txt"),
                        "external"),
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [operation],
            CancellationToken.None);

        Assert.NotEqual(WorkspaceCommitStatus.Committed, result.Status);

        Assert.Equal("external", await ReadAsync("new.txt"));

    }

    [Fact]
    public async Task Create_rename_failure_reports_affected_destination_without_claiming_artifact()
    {

        WorkspaceFileCommitOperation operation =
            await WriteOperationAsync("ambiguous-create.txt", "transaction");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterOutputRename = _ =>
                    throw new IOException("fail after create-only rename"),
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal(
            ["ambiguous-create.txt"],
            result.Recovery!.AffectedPaths);

        Assert.Empty(result.Recovery.ArtifactPaths);

    }

    [Fact]
    public async Task Concurrent_change_at_final_rollback_is_not_overwritten()
    {

        _workspace.WriteFile("a.txt", "a-old");

        _workspace.WriteFile("b.txt", "b-old");

        MultiFileCommitCoordinator coordinator = new(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeCommitStepAsync = (context, _) =>
                    context.Index == 1
                        ? ValueTask.FromException(new IOException("trigger rollback"))
                        : ValueTask.CompletedTask,
                BeforeRollbackMutation = context =>
                {
                    if (context.Index == 0)
                    {

                        File.WriteAllText(
                            Path.Combine(_workspace.Root, "a.txt"),
                            "external-during-rollback");

                    }
                },
            });

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [
                await WriteOperationAsync("a.txt", "a-new"),
                await WriteOperationAsync("b.txt", "b-new"),
            ],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.Equal("external-during-rollback", await ReadAsync("a.txt"));

        Assert.NotEmpty(result.Recovery!.ArtifactPaths);

    }

    [Fact]
    public async Task Created_directory_with_unavailable_identity_is_retained_and_reported()
    {

        WorkspaceFileCommitOperation operation =
            await WriteOperationAsync("unverified/created.txt", "new");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterRequiredDirectoryMove = _ =>
                    RetroDownfall.Arcanum.Infrastructure.Security
                        .FileHandleIdentityInterop
                        .TryGetPathMetadataForTests = _ => null,
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.RollbackIncomplete, result.Status);

        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, "unverified")));

        Assert.Contains("unverified", result.Recovery!.ArtifactPaths);

        Assert.All(result.Recovery.ArtifactPaths, AssertRelativePath);

    }

    [Fact]
    public async Task Concurrent_required_directory_create_is_not_transaction_owned()
    {

        WorkspaceFileCommitOperation operation =
            await WriteOperationAsync("concurrent/created.txt", "transaction");

        WorkspaceCommitResult result = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                BeforeRequiredDirectoryMove = relativePath =>
                {

                    Assert.Equal("concurrent", relativePath);

                    Directory.CreateDirectory(
                        Path.Combine(_workspace.Root, relativePath));

                },
            })
            .CommitAsync([operation], CancellationToken.None);

        Assert.NotEqual(WorkspaceCommitStatus.Committed, result.Status);

        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, "concurrent")));

        Assert.False(
            File.Exists(
                Path.Combine(_workspace.Root, "concurrent", "created.txt")));

    }

    [Fact]
    public async Task Irreversible_cleanup_repeats_the_same_incomplete_result()
    {

        _workspace.WriteFile("a.txt", "a-old");

        WorkspaceCommitResult commit = await new MultiFileCommitCoordinator(_workspace.Root)
            .CommitAsync(
                [await WriteOperationAsync("a.txt", "a-new")],
                CancellationToken.None);

        string backupRelative = Assert.Single(commit.Recovery!.ArtifactPaths);

        await File.WriteAllTextAsync(
            Path.Combine(
                _workspace.Root,
                backupRelative.Replace('/', Path.DirectorySeparatorChar)),
            "external-backup-change");

        WorkspaceArtifactCleanupResult first =
            await commit.Transaction!.MarkIrreversibleAsync(CancellationToken.None);

        WorkspaceArtifactCleanupResult second =
            await commit.Transaction.MarkIrreversibleAsync(CancellationToken.None);

        Assert.False(first.Complete);

        Assert.Equal(first, second);

    }

    [Fact]
    public async Task Irreversible_cleanup_journals_rename_before_cancellation()
    {

        _workspace.WriteFile("cleanup-journal.txt", "before");

        WorkspaceCommitResult commit = await new MultiFileCommitCoordinator(
            _workspace.Root,
            new MultiFileCommitCoordinatorOptions
            {
                AfterFileRemovalRename = _ =>
                    throw new OperationCanceledException(
                        "cancel after cleanup rename"),
            })
            .CommitAsync(
                [await WriteOperationAsync("cleanup-journal.txt", "after")],
                CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, commit.Status);

        WorkspaceArtifactCleanupResult cleanup =
            await commit.Transaction!.MarkIrreversibleAsync(
                CancellationToken.None);

        Assert.False(cleanup.Complete);

        string retained = Assert.Single(cleanup.RetainedArtifactPaths);

        Assert.Contains(".cleanup", retained, StringComparison.Ordinal);

        Assert.True(
            File.Exists(
                Path.Combine(
                    _workspace.Root,
                    retained.Replace('/', Path.DirectorySeparatorChar))));

    }

    [Fact]
    public async Task Successful_commit_remains_reversible_until_marked_irreversible()
    {

        _workspace.WriteFile("a.txt", "a-old");

        WorkspaceFileCommitOperation operation = await WriteOperationAsync("a.txt", "a-new");

        MultiFileCommitCoordinator coordinator = new(_workspace.Root);

        WorkspaceCommitResult result = await coordinator.CommitAsync(
            [operation],
            CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, result.Status);

        WorkspaceRollbackResult rollback =
            await result.Transaction!.RollbackAsync(CancellationToken.None);

        Assert.True(rollback.Complete);

        Assert.Equal("a-old", await ReadAsync("a.txt"));

        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Terminal_abandon_releases_directory_leases_without_deleting_committed_files()
    {
        WorkspaceCommitResult commit = await new MultiFileCommitCoordinator(_workspace.Root)
            .CommitAsync(
                [await WriteOperationAsync("new/deep/created.txt", "created")],
                CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, commit.Status);
        Assert.True(commit.Transaction!.HasOpenDirectoryLeasesForTests);

        await commit.Transaction.AbandonAsync();

        Assert.False(commit.Transaction.HasOpenDirectoryLeasesForTests);
        Assert.Equal("created", await ReadAsync("new/deep/created.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => commit.Transaction.RollbackAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => commit.Transaction.MarkIrreversibleAsync(CancellationToken.None));
    }

    private async Task<WorkspaceFileCommitOperation> WriteOperationAsync(
        string relativePath,
        string content)
    {

        WorkspaceFileFingerprint fingerprint =
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                _workspace.Root,
                relativePath,
                CancellationToken.None);

        return new WorkspaceFileCommitOperation(
            relativePath,
            fingerprint,
            Encoding.UTF8.GetBytes(content));

    }

    private Task<string> ReadAsync(string relativePath) =>
        File.ReadAllTextAsync(
            Path.Combine(_workspace.Root, relativePath),
            CancellationToken.None);

    private string[] ArcanumArtifacts() =>
        Directory.GetFiles(_workspace.Root, "*.arcanum-*", SearchOption.AllDirectories);

    private static void AssertRelativePath(string path)
    {

        Assert.False(Path.IsPathRooted(path));

        Assert.DoesNotContain("..", path, StringComparison.Ordinal);

        Assert.DoesNotContain('\\', path);

    }

}
