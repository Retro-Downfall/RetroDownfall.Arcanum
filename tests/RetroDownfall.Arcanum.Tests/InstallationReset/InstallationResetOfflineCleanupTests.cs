using System.Buffers.Binary;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetOfflineCleanupTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task Cleanup_deletes_selected_files_and_preserves_valid_backup_in_place()
    {

        string selected = _workspace.CreateSubdir("selected");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string nested = Path.Combine(selected, "backups");

        Directory.CreateDirectory(nested);

        string backup = Path.Combine(nested, "safe.arcbackup");

        File.WriteAllText(ordinary, "state");

        WriteValidBackup(backup);

        InstallationResetOfflineCleanup cleanup = new();

        InstallationResetPlan plan = CreatePlan(selected, backup);

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(File.Exists(ordinary));

        Assert.True(File.Exists(backup));

        Assert.Single(result.Value.PreservedBackups);

        Assert.True(result.Value.Verification.Succeeded);

    }

    [Fact]
    public async Task Backup_lookalike_is_an_ordinary_deletion_target()
    {

        string selected = _workspace.CreateSubdir("selected");

        string lookalike = Path.Combine(selected, "lookalike.arcbackup");

        File.WriteAllText(lookalike, "not-a-backup");

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(File.Exists(lookalike));

        Assert.Empty(result.Value.PreservedBackups);

    }

    [Fact]
    public async Task Symlinked_entry_fails_closed_without_deleting_its_target()
    {

        string selected = _workspace.CreateSubdir("selected");

        string outside = _workspace.WriteFile("outside.txt", "keep");

        File.CreateSymbolicLink(Path.Combine(selected, "linked"), outside);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(outside));

        Assert.Equal("keep", File.ReadAllText(outside));

    }

    [Fact]
    public async Task Symlinked_directory_fails_closed_without_deleting_external_contents()
    {

        string selected = _workspace.CreateSubdir("selected");

        string outside = _workspace.CreateSubdir("outside");

        string sentinel = Path.Combine(outside, "sentinel.txt");

        File.WriteAllText(sentinel, "keep");

        Directory.CreateSymbolicLink(
            Path.Combine(selected, "linked-directory"),
            outside);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(sentinel));

        Assert.Equal("keep", File.ReadAllText(sentinel));

    }

    [Fact]
    public async Task Selected_root_with_a_symlinked_ancestor_fails_closed()
    {

        string outside = _workspace.CreateSubdir("outside-ancestor");

        string outsideState = Path.Combine(outside, ".arcanum");

        Directory.CreateDirectory(outsideState);

        string sentinel = Path.Combine(outsideState, "sentinel.txt");

        File.WriteAllText(sentinel, "keep");

        string linkedParent = Path.Combine(_workspace.Root, "linked-parent");

        Directory.CreateSymbolicLink(linkedParent, outside);

        string selected = Path.Combine(linkedParent, ".arcanum");

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetFileSystemInventory> result = await cleanup.PlanAsync(
            [selected],
            [],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(sentinel));

        Assert.Equal("keep", File.ReadAllText(sentinel));

    }

    [Fact]
    public async Task Unplanned_valid_backup_fails_before_deleting_neighboring_file()
    {

        string selected = _workspace.CreateSubdir("selected");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string backup = Path.Combine(selected, "unexpected.arcbackup");

        File.WriteAllText(ordinary, "state");

        WriteValidBackup(backup);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.True(File.Exists(ordinary));

        Assert.True(File.Exists(backup));

    }

    [Fact]
    public async Task Excluded_nested_root_remains_untouched_while_selected_neighbor_is_deleted()
    {

        string selected = _workspace.CreateSubdir("selected");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string excluded = Path.Combine(selected, "nested-campaign");

        Directory.CreateDirectory(excluded);

        string sentinel = Path.Combine(excluded, "sentinel.txt");

        File.WriteAllText(ordinary, "delete");

        File.WriteAllText(sentinel, "keep");

        InstallationResetPlan plan = CreatePlan(selected);

        plan = plan with
        {

            AcceptedBinding = plan.AcceptedBinding with
            {

                ExcludedRoots = [Path.GetFullPath(excluded)],

            },

        };

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(File.Exists(ordinary));

        Assert.True(File.Exists(sentinel));

        Assert.Equal("keep", File.ReadAllText(sentinel));

    }

    [Fact]
    public async Task Planning_reports_exact_files_and_preserved_backups_without_writing()
    {

        string selected = _workspace.CreateSubdir("selected-plan");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string excluded = Path.Combine(selected, "nested-campaign");

        string excludedFile = Path.Combine(excluded, "keep.txt");

        string backup = Path.Combine(selected, "safe.arcbackup");

        Directory.CreateDirectory(excluded);

        File.WriteAllText(ordinary, "state");

        File.WriteAllText(excludedFile, "keep");

        WriteValidBackup(backup);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetFileSystemInventory> result = await cleanup.PlanAsync(
            [selected],
            [excluded],
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        InstallationResetTargetDescriptor target = Assert.Single(result.Value.Targets);

        Assert.Equal(Path.GetFullPath(ordinary), target.CanonicalPath);

        Assert.Equal(1, target.Files);

        Assert.Equal(new FileInfo(ordinary).Length, target.EstimatedBytes);

        Assert.Equal(
            Path.GetFullPath(backup),
            Assert.Single(result.Value.PreservedBackups).CanonicalPath);

        Assert.Equal(
            Path.GetFullPath(excluded),
            Assert.Single(result.Value.Exclusions).ResourceId);

        Assert.True(File.Exists(ordinary));

        Assert.True(File.Exists(backup));

        Assert.True(File.Exists(excludedFile));

    }

    [Fact]
    public async Task Accepted_backup_replacement_fails_before_deleting_neighboring_file()
    {

        string selected = _workspace.CreateSubdir("selected");

        string outside = _workspace.CreateSubdir("outside");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string backup = Path.Combine(selected, "accepted.arcbackup");

        File.WriteAllText(ordinary, "state");

        WriteValidBackup(backup);

        InstallationResetPlan plan = CreatePlan(selected, backup);

        File.Move(backup, Path.Combine(outside, "original.arcbackup"));

        WriteValidBackup(backup);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.True(File.Exists(ordinary));

        Assert.True(File.Exists(backup));

    }

    [Fact]
    public async Task Accepted_backup_swap_after_capture_fails_before_deleting_neighboring_file()
    {

        string selected = _workspace.CreateSubdir("selected-capture-race");

        string outside = _workspace.CreateSubdir("outside-capture-race");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string backup = Path.Combine(selected, "accepted.arcbackup");

        File.WriteAllText(ordinary, "state");

        WriteValidBackup(backup);

        InstallationResetPlan plan = CreatePlan(selected, backup);

        InstallationResetOfflineCleanup cleanup = new(() =>
        {

            File.Move(backup, Path.Combine(outside, "original.arcbackup"));

            WriteValidBackup(backup);

        });

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        Assert.True(File.Exists(ordinary));

        Assert.True(File.Exists(backup));

    }

    [Fact]
    public async Task Missing_accepted_backup_fails_before_deleting_neighboring_file()
    {

        string selected = _workspace.CreateSubdir("selected");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string backup = Path.Combine(selected, "accepted.arcbackup");

        File.WriteAllText(ordinary, "state");

        WriteValidBackup(backup);

        InstallationResetPlan plan = CreatePlan(selected, backup);

        File.Delete(backup);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.True(File.Exists(ordinary));

    }

    [Fact]
    public async Task Cleanup_rerun_preserves_accepted_backup_and_is_idempotent()
    {

        string selected = _workspace.CreateSubdir("selected");

        string ordinary = Path.Combine(selected, "arcanum.json");

        string backup = Path.Combine(selected, "accepted.arcbackup");

        File.WriteAllText(ordinary, "state");

        WriteValidBackup(backup);

        InstallationResetPlan plan = CreatePlan(selected, backup);

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> first = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Result<InstallationResetOfflineCleanupResult> second = await cleanup.ExecuteAsync(
            plan,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(1, first.Value.FilesDeleted);

        Assert.Equal(0, second.Value.FilesDeleted);

        Assert.True(File.Exists(backup));

        Assert.Single(second.Value.PreservedBackups);

    }

    [Fact]
    public async Task Failure_after_one_file_deletion_returns_progress_for_checkpointing()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string firstRoot = _workspace.CreateSubdir("a-deletable");

        string lockedRoot = _workspace.CreateSubdir("z-locked");

        string first = Path.Combine(firstRoot, "first.json");

        string locked = Path.Combine(lockedRoot, "locked.json");

        File.WriteAllText(first, "first");

        File.WriteAllText(locked, "locked");

        InstallationResetOfflineCleanup cleanup = new();

        InstallationResetPlan plan = CreatePlan(
            [firstRoot, lockedRoot],
            acceptedBackups: []);

        long expectedBytes = new FileInfo(first).Length;

        UnixFileMode originalMode = File.GetUnixFileMode(lockedRoot);

        try
        {

            File.SetUnixFileMode(
                lockedRoot,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);

            Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
                plan,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(1, result.Value.FilesDeleted);

            Assert.Equal(expectedBytes, result.Value.EstimatedBytesDeleted);

            Assert.False(result.Value.Verification.Succeeded);

            Assert.Equal(
                ErrorCodes.Data.RecoveryRequired,
                Assert.Single(result.Value.Verification.RemainingIssues).Code);

            Assert.False(File.Exists(first));

            Assert.True(File.Exists(locked));

        }
        finally
        {

            File.SetUnixFileMode(lockedRoot, originalMode);

        }

    }

    [Fact]
    public async Task File_created_after_capture_is_reported_as_unverified_with_prior_progress()
    {

        string selected = _workspace.CreateSubdir("selected-late-file");

        string initial = Path.Combine(selected, "initial.json");

        string late = Path.Combine(selected, "late.json");

        File.WriteAllText(initial, "initial");

        long expectedBytes = new FileInfo(initial).Length;

        InstallationResetOfflineCleanup cleanup = new(
            () => File.WriteAllText(late, "late"));

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, result.Value.FilesDeleted);

        Assert.Equal(expectedBytes, result.Value.EstimatedBytesDeleted);

        Assert.False(result.Value.Verification.Succeeded);

        InstallationResetIssueSummary issue = Assert.Single(
            result.Value.Verification.RemainingIssues);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, issue.Code);

        Assert.Equal(Path.GetFullPath(late), issue.ResourceId);

        Assert.False(File.Exists(initial));

        Assert.True(File.Exists(late));

    }

    [Fact]
    public async Task Cancellation_after_one_file_deletion_returns_exact_resumable_progress()
    {

        string selected = _workspace.CreateSubdir("selected-cancelled");

        string first = Path.Combine(selected, "a-first.json");

        string second = Path.Combine(selected, "z-second.json");

        File.WriteAllText(first, "first");

        File.WriteAllText(second, "second");

        long expectedBytes = new FileInfo(first).Length;

        using CancellationTokenSource cancellation = new();

        InstallationResetOfflineCleanup cleanup = new(
            afterInitialCapture: null,
            afterFileDeleted: deletedPath =>
            {

                if (string.Equals(
                    deletedPath,
                    Path.GetFullPath(first),
                    StringComparison.Ordinal))
                {

                    cancellation.Cancel();

                }

            });

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            cancellation.Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, result.Value.FilesDeleted);

        Assert.Equal(expectedBytes, result.Value.EstimatedBytesDeleted);

        Assert.False(result.Value.Verification.Succeeded);

        Assert.Equal(
            ErrorCodes.Data.RecoveryRequired,
            Assert.Single(result.Value.Verification.RemainingIssues).Code);

        Assert.False(File.Exists(first));

        Assert.True(File.Exists(second));

    }

    /// <summary>
    /// W5-7: a resumed pass whose file pass already finished deletes only now-empty directories.
    /// Directory deletions were never counted, so the cancel-compensation predicate read a
    /// directory-only mutation as "nothing was touched" and rethrew a bare OperationCanceledException
    /// instead of reporting the resumable Incomplete/RecoveryRequired shape every other cancellation
    /// path here produces.
    /// </summary>
    [Fact]
    public async Task Cancelling_after_a_directory_only_delete_is_reported_incomplete()
    {

        string selected = _workspace.CreateSubdir("selected-directory-only");

        _ = Directory.CreateDirectory(Path.Combine(selected, "nested"));

        using CancellationTokenSource cancellation = new();

        InstallationResetOfflineCleanup cleanup = new(
            afterInitialCapture: null,
            afterFileDeleted: null,
            afterDirectoryDeleted: _ => cancellation.Cancel());

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            cancellation.Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, result.Value.FilesDeleted);

        Assert.False(result.Value.Verification.Succeeded);

        Assert.Equal(
            ErrorCodes.Data.RecoveryRequired,
            Assert.Single(result.Value.Verification.RemainingIssues).Code);

        Assert.False(Directory.Exists(Path.Combine(selected, "nested")));

    }

    /// <summary>
    /// W5-7 (residual, review round 1): FailureOrIncomplete's own discriminator was
    /// <c>filesDeleted == 0</c>, so a directory-only mutation followed by a *later* directory-loop
    /// failure (not cancellation - the case above already covers that) reported a clean hard Failure,
    /// discarding the fact that a directory really was deleted. Same misread as the cancellation
    /// predicate, in the non-cancellation path.
    /// </summary>
    [Fact]
    public async Task Directory_only_mutation_then_a_later_directory_failure_is_reported_incomplete()
    {

        string selected = _workspace.CreateSubdir("selected-directory-only-then-identity-changes");

        _ = Directory.CreateDirectory(Path.Combine(selected, "nested"));

        InstallationResetOfflineCleanup cleanup = new(
            afterInitialCapture: null,
            afterFileDeleted: null,
            afterDirectoryDeleted: _ =>
            {

                // "nested" (the deeper path, processed first) is now gone. Delete and recreate the
                // root itself so its identity (volume + file id) no longer matches what inventory
                // captured, forcing the *next* directory in the loop - "selected" - into the
                // "changed identity" failure instead of a clean delete.
                Directory.Delete(selected);

                Directory.CreateDirectory(selected);

            });

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, result.Value.FilesDeleted);

        Assert.False(result.Value.Verification.Succeeded);

        InstallationResetIssueSummary issue = Assert.Single(result.Value.Verification.RemainingIssues);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, issue.Code);

        Assert.Contains("changed identity", issue.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Hard_linked_file_fails_closed_without_deleting_either_link()
    {

        string selected = _workspace.CreateSubdir("selected");

        string outside = _workspace.CreateSubdir("outside");

        string selectedLink = Path.Combine(selected, "state.db");

        string outsideLink = Path.Combine(outside, "state.db");

        File.WriteAllText(selectedLink, "keep");

        Assert.True(HardLinkTestSupport.TryCreate(outsideLink, selectedLink));

        InstallationResetOfflineCleanup cleanup = new();

        Result<InstallationResetOfflineCleanupResult> result = await cleanup.ExecuteAsync(
            CreatePlan(selected),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(selectedLink));

        Assert.True(File.Exists(outsideLink));

        Assert.Equal("keep", File.ReadAllText(outsideLink));

    }

    private static InstallationResetPlan CreatePlan(
        string selectedRoot,
        params string[] acceptedBackupPaths)
    {

        InstallationResetPreservedBackup[] acceptedBackups =
        [
            .. acceptedBackupPaths.Select(CreateAcceptedBackup),
        ];

        return CreatePlan([selectedRoot], acceptedBackups);

    }

    private static InstallationResetPlan CreatePlan(
        string[] selectedRoots,
        InstallationResetPreservedBackup[] acceptedBackups)
    {

        InstallationResetAcceptedBinding binding = new(
            "binding",
            selectedRoots,
            [],
            acceptedBackups,
            [],
            ["data"]);

        return new InstallationResetPlan(
            "plan",
            InstallationResetScope.Global,
            Workspace: null,
            DateTimeOffset.UtcNow,
            DataInventoryAvailable: true,
            CredentialInventoryAvailable: true,
            Targets: [],
            Credentials: [],
            PreservedBackups: acceptedBackups,
            Exclusions: [],
            Blockers: [],
            Rows: 0,
            Files: 1,
            EstimatedBytes: 5,
            binding);

    }

    private static InstallationResetPreservedBackup CreateAcceptedBackup(string path)
    {

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            path,
            out FileHandleMetadata metadata));

        return new InstallationResetPreservedBackup(
            Path.GetFullPath(path),
            new InstallationResetFileIdentity(
                $"{metadata.Identity.VolumeId:X16}:{metadata.Identity.FileId:X16}",
                new FileInfo(path).Length,
                metadata.HardLinkCount));

    }

    private static void WriteValidBackup(string path)
    {

        byte[] header = new byte[68];

        "ARCABACK"u8.CopyTo(header);

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 1);

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), 68);

        header[16] = 1;

        header[17] = 1;

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), 1);

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), 16);

        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(28), 0);

        BinaryPrimitives.WriteInt64BigEndian(
            header.AsSpan(36),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        File.WriteAllBytes(path, header);

    }

}
