using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetActiveStoreTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task Missing_active_record_is_a_no_create_read()
    {

        string guardedRoot = Path.Combine(_workspace.Root, "arcanum");

        string[] before = Directory.GetFileSystemEntries(_workspace.Root);

        InstallationResetActiveStore store = new(guardedRoot);

        Result<InstallationResetActiveRecord?> result = await store.ReadAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value);

        Assert.Equal(before, Directory.GetFileSystemEntries(_workspace.Root));

    }

    [Fact]
    public async Task Active_record_round_trips_and_replaces_one_bounded_owner_file()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum");

        InstallationResetActiveStore store = new(guardedRoot);

        InstallationResetActiveRecord prepared = CreateRecord(
            InstallationResetPhase.Prepared);

        Result write = await store.WriteAsync(prepared, CancellationToken.None);

        Assert.True(write.IsSuccess);

        InstallationResetActiveRecord completed = prepared with
        {
            Phase = InstallationResetPhase.Completed,
            PointOfNoReturn = true,
            RowsDeleted = 7,
        };

        Result replace = await store.WriteAsync(completed, CancellationToken.None);

        Assert.True(replace.IsSuccess);

        Result<InstallationResetActiveRecord?> read = await store.ReadAsync(
            CancellationToken.None);

        Assert.True(read.IsSuccess);

        Assert.Equivalent(completed, read.Value, strict: true);

        Assert.True(new FileInfo(store.ActivePath).Length <= InstallationResetActiveStore.MaxBytes);

        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(_workspace.Root),
            path => Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));

        if (!OperatingSystem.IsWindows())
        {

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(store.ActivePath));

        }

    }

    [Fact]
    public async Task Oversized_record_is_rejected_before_publication()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum");

        InstallationResetActiveStore store = new(guardedRoot);

        InstallationResetActiveRecord oversized = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            AcceptedBinding = CreateRecord(InstallationResetPhase.Prepared)
                .AcceptedBinding with
            {
                SelectedRoots = [new string('x', InstallationResetActiveStore.MaxBytes)],
            },
        };

        Result result = await store.WriteAsync(oversized, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, result.Error.Code);

        Assert.False(File.Exists(store.ActivePath));

    }

    [Fact]
    public async Task Missing_composite_plan_id_is_rejected_before_publication()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord invalid = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            PlanId = string.Empty,
        };

        Result result = await store.WriteAsync(invalid, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, result.Error.Code);

        Assert.False(File.Exists(store.ActivePath));

    }

    [Fact]
    public async Task Corrupt_or_symlinked_active_record_fails_closed()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum");

        InstallationResetActiveStore store = new(guardedRoot);

        await File.WriteAllTextAsync(store.ActivePath, "{not-json");

        Result<InstallationResetActiveRecord?> corrupt = await store.ReadAsync(
            CancellationToken.None);

        Assert.True(corrupt.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, corrupt.Error.Code);

        File.Delete(store.ActivePath);

        string outside = _workspace.WriteFile("outside.json", "{}");

        File.CreateSymbolicLink(store.ActivePath, outside);

        Result<InstallationResetActiveRecord?> symlink = await store.ReadAsync(
            CancellationToken.None);

        Assert.True(symlink.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, symlink.Error.Code);

    }

    [Fact]
    public async Task Retirement_requires_the_exact_operation_binding()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Completed);

        Assert.True((await store.WriteAsync(record, CancellationToken.None)).IsSuccess);

        Result mismatch = await store.RetireAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(mismatch.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, mismatch.Error.Code);

        Assert.True(File.Exists(store.ActivePath));

        Result retired = await store.RetireAsync(
            record.OperationId,
            CancellationToken.None);

        Assert.True(retired.IsSuccess);

        Assert.False(File.Exists(store.ActivePath));

    }

    [Fact]
    public async Task Write_never_replaces_a_different_active_operation()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord first = CreateRecord(
            InstallationResetPhase.DataResetComplete) with
        {
            PointOfNoReturn = true,
        };

        Assert.True((await store.WriteAsync(first, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecord different = first with
        {
            OperationId = Guid.NewGuid(),
            PlanId = "different-plan",
        };

        Result replacement = await store.WriteAsync(
            different,
            CancellationToken.None);

        Assert.True(replacement.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, replacement.Error.Code);

        Result<InstallationResetActiveRecord?> read = await store.ReadAsync(
            CancellationToken.None);

        Assert.True(read.IsSuccess);

        Assert.Equivalent(first, read.Value, strict: true);

    }

    [Fact]
    public async Task Write_never_changes_the_immutable_binding_of_the_same_operation()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord first = CreateRecord(
            InstallationResetPhase.Prepared);

        Assert.True((await store.WriteAsync(first, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecord changed = first with
        {
            AcceptedBinding = first.AcceptedBinding with
            {
                BindingId = "changed-binding",
            },
        };

        Result replacement = await store.WriteAsync(
            changed,
            CancellationToken.None);

        Assert.True(replacement.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, replacement.Error.Code);

        Assert.Equivalent(
            first,
            (await store.ReadAsync(CancellationToken.None)).Value,
            strict: true);

    }

    private static InstallationResetActiveRecord CreateRecord(
        InstallationResetPhase phase)
    {

        InstallationResetAcceptedBinding binding = new(
            "binding",
            ["/selected"],
            ["/excluded"],
            [],
            ["master-api-key"],
            ["data-plan"]);

        return new InstallationResetActiveRecord(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.Global,
            Workspace: null,
            binding,
            phase,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null);

    }

}
