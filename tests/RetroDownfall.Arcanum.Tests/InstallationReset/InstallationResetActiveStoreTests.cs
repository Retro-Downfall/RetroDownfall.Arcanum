using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("WorkspacePathPolicy")]
public sealed class InstallationResetActiveStoreTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public async Task DisposeAsync()
    {

        SecureFileReader.AfterOpenForTests = null;

        await _workspace.DisposeAsync();

    }

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
    public async Task Host_handoff_and_completion_proof_round_trip_without_changing_V1()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord prepared = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        Assert.True((await store.WriteAsync(prepared, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecord proven = prepared with
        {
            OnlineDataCompletion = new InstallationResetOnlineDataCompletion(
                ServerOperationId: Guid.NewGuid(),
                RequestedOperationId: prepared.OperationId,
                DataPlanId: "data-plan",
                RowsDeleted: 7,
                FilesDeleted: 3,
                EstimatedBytesDeleted: 19,
                DerivedRecordsDeleted: 2),
        };

        Assert.True((await store.WriteAsync(proven, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecord read = Assert.IsType<InstallationResetActiveRecord>(
            (await store.ReadAsync(CancellationToken.None)).Value);

        Assert.Equal(1, read.Version);

        Assert.Equivalent(proven, read, strict: true);

    }

    [Fact]
    public async Task Legacy_V1_record_without_handoff_fields_remains_readable()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        const string json = """
            {"version":1,"operationId":"11111111-1111-1111-1111-111111111111","planId":"composite-plan","scope":"Global","workspace":null,"acceptedBinding":{"bindingId":"binding","selectedRoots":["/selected"],"excludedRoots":[],"preservedBackups":[],"credentialAccounts":[],"dataPlanIds":["data-plan"]},"phase":"Prepared","pointOfNoReturn":false,"rowsDeleted":0,"filesDeleted":0,"estimatedBytesDeleted":0,"credentialResults":[],"lastErrorCode":null}
            """;

        await File.WriteAllTextAsync(store.ActivePath, json);

        Result<InstallationResetActiveRecord?> result = await store.ReadAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value!.DataHandoff);

        Assert.Null(result.Value.OnlineDataCompletion);

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
    public async Task Host_handoff_requires_one_nonempty_data_plan_identity()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord invalid = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            AcceptedBinding = CreateRecord(InstallationResetPhase.Prepared)
                .AcceptedBinding with
            {
                DataPlanIds = [" "],
            },
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
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
    public async Task Pre_effect_retirement_never_deletes_a_concurrently_published_proof()
    {

        InstallationResetActiveStore store = new(
            _workspace.CreateSubdir("arcanum"));

        InstallationResetActiveRecord prepared = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        Assert.True((await store.WriteAsync(prepared, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecord proven = prepared with
        {
            OnlineDataCompletion = new InstallationResetOnlineDataCompletion(
                ServerOperationId: Guid.Parse(
                    "45454545-4545-4545-8545-454545454545"),
                RequestedOperationId: prepared.OperationId,
                DataPlanId: "data-plan",
                RowsDeleted: 7,
                FilesDeleted: 3,
                EstimatedBytesDeleted: 19,
                DerivedRecordsDeleted: 2),
        };

        bool proofPublished = false;

        SecureFileReader.AfterOpenForTests = openedPath =>
        {

            if (!string.Equals(openedPath, store.ActivePath, StringComparison.Ordinal))
            {

                return;

            }

            SecureFileReader.AfterOpenForTests = null;

            Result published = store.WriteAsync(
                    proven,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.True(published.IsSuccess, published.Error.Message);

            proofPublished = true;

        };

        InstallationResetOnlineDataHandoff handoff = new(
            prepared.OperationId,
            prepared.PlanId,
            "data-plan",
            DataResetCompleted: false);

        Result retired = await store.RetirePreEffectAsync(
            handoff,
            CancellationToken.None);

        Assert.True(proofPublished);

        Assert.True(retired.IsFailure);

        Assert.True(File.Exists(store.ActivePath));

        InstallationResetActiveRecord read = Assert.IsType<InstallationResetActiveRecord>(
            (await store.ReadAsync(CancellationToken.None)).Value);

        Assert.Equivalent(proven.OnlineDataCompletion, read.OnlineDataCompletion, strict: true);

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
