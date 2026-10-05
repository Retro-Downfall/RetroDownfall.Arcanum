using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("WorkspacePathPolicy")]
public sealed class InstallationResetHostHandoffCoordinatorTests : IAsyncLifetime
{
    private static readonly Guid InstallationId =
        Guid.Parse("61111111-2222-4333-8444-555555555555");

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Theory]
    [InlineData("widened-root")]
    [InlineData("retained-account")]
    [InlineData("binding-id")]
    public async Task Legacy_record_with_widened_roots_or_retained_accounts_is_refused_before_the_host_handoff_migrates_it(
        string tampering)
    {
        // The startup projection builds the host handoff from the legacy record's own accepted binding,
        // so the coordinator's own "does this record match the handoff" comparison is a comparison of
        // the record with itself and proves nothing about the record. Migrating seals the file, after
        // which every later arm trusts what it names; the check against this installation has to
        // happen here too, not only in the service's locked branch.
        string guardedRoot = _workspace.CreateSubdir("host-handoff-legacy-refused");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(guardedRoot, new InMemoryOsCredentialStore());

        InstallationResetHostHandoff handoff = CreateHandoff(tampering);

        InstallationResetActiveRecord legacy = CreateLegacyRecord(handoff);

        Assert.True((await store.WriteLegacyV1ForTestsAsync(legacy, CancellationToken.None)).IsSuccess);

        string legacyText = await File.ReadAllTextAsync(store.ActivePath);

        InstallationResetHostHandoffCoordinator coordinator = CreateCoordinator(store);

        Result result = await coordinator.BeginOrRecoverAsync(
            handoff,
            heldLock,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.Error.Code);

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(
            CancellationToken.None);

        Assert.True(inspected.IsSuccess, inspected.IsFailure ? inspected.Error.Message : null);

        Assert.Equal(InstallationResetActiveRecoveryOutcome.LegacyV1, inspected.Value.Outcome);

        Assert.Equal(legacyText, await File.ReadAllTextAsync(store.ActivePath));
    }

    [Fact]
    public async Task Legacy_record_that_names_only_what_this_installation_plans_is_migrated_by_the_host_handoff()
    {
        string guardedRoot = _workspace.CreateSubdir("host-handoff-legacy-migrated");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(guardedRoot, new InMemoryOsCredentialStore());

        InstallationResetHostHandoff handoff = CreateHandoff("none");

        Assert.True((await store.WriteLegacyV1ForTestsAsync(
            CreateLegacyRecord(handoff),
            CancellationToken.None)).IsSuccess);

        InstallationResetHostHandoffCoordinator coordinator = CreateCoordinator(store);

        Result result = await coordinator.BeginOrRecoverAsync(
            handoff,
            heldLock,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(
            CancellationToken.None);

        Assert.True(inspected.IsSuccess, inspected.IsFailure ? inspected.Error.Message : null);

        Assert.Equal(InstallationResetActiveRecoveryOutcome.AuthenticatedV2, inspected.Value.Outcome);
    }

    private static InstallationResetHostHandoffCoordinator CreateCoordinator(
        InstallationResetActiveStore store) =>
        new(store, new FixedIdentityReader(), new FixedStateRoots());

    private static InstallationResetHostHandoff CreateHandoff(string tampering)
    {
        string retainedAccount = ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(
            new string('a', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength));

        string[] roots = tampering is "widened-root"
            ? ["/state", "/home/someone/documents"]
            : ["/state"];

        string[] accounts = tampering is "retained-account"
            ? ["accepted-account", retainedAccount]
            : ["accepted-account"];

        // The binding id is computed over the contents the record really carries, so the widened and
        // retained shapes are exactly what a service that planned them would have written, and only
        // the comparison with this installation can refuse them.
        InstallationResetAcceptedBinding binding = new(
            BindingId: string.Empty,
            roots,
            ExcludedRoots: [],
            PreservedBackups: [],
            accounts,
            DataPlanIds: ["data-plan"]);

        binding = binding with
        {
            BindingId = tampering is "binding-id"
                ? new string('0', 64)
                : InstallationResetService.ComputeBindingId(
                    InstallationResetScope.Global,
                    binding),
        };

        return new InstallationResetHostHandoff(
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.Global,
            Workspace: null,
            binding);
    }

    private static InstallationResetActiveRecord CreateLegacyRecord(
        InstallationResetHostHandoff handoff) =>
        new(
            InstallationResetActiveStore.CurrentVersion,
            handoff.RequestedOperationId,
            handoff.InstallationPlanId,
            handoff.Scope,
            handoff.Workspace,
            handoff.AcceptedBinding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure);

    private sealed class FixedStateRoots : IInstallationResetStateRoots
    {
        public string[] Resolve(
            InstallationResetScope scope,
            DataRetentionWorkspaceBinding? workspace) => ["/state"];
    }

    private sealed class FixedIdentityReader : IInstallationResetDatabaseIdentityReader
    {
        public Task<Result<Guid>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Guid>.Success(InstallationId));
    }
}
