using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Coordination;

[Collection("WorkspacePathPolicy")]
public sealed class ClientMutationEvidenceAdaptersTests : IDisposable
{

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly string _container;

    private readonly string _guardedRoot;

    public ClientMutationEvidenceAdaptersTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-client-evidence-adapter-" + Guid.NewGuid().ToString("N"));

        _guardedRoot = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_container);

    }

    public void Dispose()
    {

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_adapter_preserves_proven_absence_and_active_evidence(
        bool active)
    {

        ActiveInstallationReset? reset = active
            ? new ActiveInstallationReset(
                Scope: InstallationResetScope.All,
                WorkspaceRoot: null,
                PlanId: "accepted-plan",
                OperationId: Guid.NewGuid())
            : null;

        InstallationResetClientMutationEvidenceProbe probe = new(
            new FakeStartupProbe(Result<ActiveInstallationReset?>.Success(reset)));

        Result<ActiveInstallationReset?> result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(active, result.Value is not null);

    }

    [Fact]
    public async Task Restore_adapter_admits_a_genuinely_absent_root_with_no_evidence_without_creation()
    {

        BackupRestoreClientMutationEvidenceProbe probe = new(
            _guardedRoot,
            new AbsentCredentialStore());

        Result<ActiveReplacementRestore?> result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value);

        Assert.False(Directory.Exists(_guardedRoot));

        Assert.False(File.Exists(BackupRestoreStagingIndex.PathFor(_guardedRoot)));

    }

    [Fact]
    public async Task Reset_adapter_with_the_production_probe_rejects_a_guarded_root_symlink_without_following_it()
    {

        string target = Path.Combine(_container, "reset-symlink-target");

        Directory.CreateDirectory(target);

        Directory.CreateSymbolicLink(_guardedRoot, target);

        InstallationStartupProbe startup = new(
            _guardedRoot,
            Path.Combine(_guardedRoot, "config.json"),
            Path.Combine(_guardedRoot, "arcanum.db"),
            Path.Combine(_guardedRoot, "security.dat"),
            new AbsentCredentialStore());

        InstallationResetClientMutationEvidenceProbe probe = new(startup);

        Result<ActiveInstallationReset?> result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

    }

    [Fact]
    public async Task Restore_adapter_clears_an_orphan_empty_canonical_staging_root_without_mutating_it()
    {

        string staging = Path.Combine(
            _container,
            BackupRestoreJournal.StagingPrefix + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(staging);

        BackupRestoreClientMutationEvidenceProbe probe = new(
            _guardedRoot,
            new AbsentCredentialStore());

        Result<ActiveReplacementRestore?> result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value);

        Assert.True(Directory.Exists(staging));

    }

    [Fact]
    public async Task Restore_adapter_rejects_a_canonical_staging_symlink_without_following_it()
    {

        string target = Path.Combine(_container, "outside-target");

        Directory.CreateDirectory(target);

        string staging = Path.Combine(
            _container,
            BackupRestoreJournal.StagingPrefix + Guid.NewGuid().ToString("N"));

        Directory.CreateSymbolicLink(staging, target);

        BackupRestoreClientMutationEvidenceProbe probe = new(
            _guardedRoot,
            new AbsentCredentialStore());

        Result<ActiveReplacementRestore?> result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

    }

    [Fact]
    public async Task Closed_restore_tombstone_with_only_empty_staging_and_index_garbage_is_clear()
    {

        RestoreFixture fixture = PublishActiveRestore();

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot)))
        {

            Assert.True(fixture.Store.Close(
                held,
                _guardedRoot,
                fixture.Profile,
                fixture.Publication).IsSuccess);

        }

        BackupRestoreStagingIndex.Add(
            _guardedRoot,
            fixture.Publication.Location.StagingRoot);

        BackupRestoreClientMutationEvidenceProbe probe = new(
            _guardedRoot,
            _credentials);

        Result<ActiveReplacementRestore?> result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value);

    }

    [Fact]
    public async Task Authenticated_active_restore_journal_is_blocked()
    {

        RestoreFixture fixture = PublishActiveRestore();

        Result<ActiveReplacementRestore?> result = await new BackupRestoreClientMutationEvidenceProbe(
                _guardedRoot,
                _credentials)
            .InspectAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            fixture.Publication.Location.OperationId,
            Assert.IsType<ActiveReplacementRestore>(result.Value).OperationId);

    }

    [Fact]
    public async Task Semantically_invalid_legacy_restore_journal_is_unsafe()
    {

        string staging = WriteLegacyJournal(Guid.Empty);

        byte[] before = File.ReadAllBytes(
            Path.Combine(staging, BackupRestoreJournal.FileName));

        Result<ActiveReplacementRestore?> result =
            await new BackupRestoreClientMutationEvidenceProbe(
                    _guardedRoot,
                    _credentials)
                .InspectAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(
            before,
            File.ReadAllBytes(Path.Combine(staging, BackupRestoreJournal.FileName)));

    }

    [Fact]
    public async Task Two_distinct_legacy_journals_for_one_operation_are_ambiguous_and_unsafe()
    {

        Guid operationId = Guid.NewGuid();

        string first = WriteLegacyJournal(operationId);

        string second = WriteLegacyJournal(operationId);

        byte[] firstBefore = File.ReadAllBytes(
            Path.Combine(first, BackupRestoreJournal.FileName));

        byte[] secondBefore = File.ReadAllBytes(
            Path.Combine(second, BackupRestoreJournal.FileName));

        Result<ActiveReplacementRestore?> result =
            await new BackupRestoreClientMutationEvidenceProbe(
                    _guardedRoot,
                    _credentials)
                .InspectAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(
            firstBefore,
            File.ReadAllBytes(Path.Combine(first, BackupRestoreJournal.FileName)));

        Assert.Equal(
            secondBefore,
            File.ReadAllBytes(Path.Combine(second, BackupRestoreJournal.FileName)));

    }

    [Fact]
    public async Task Unanchored_restore_journal_is_unsafe_not_a_reusable_blocker()
    {

        RestoreFixture fixture = PublishActiveRestore();

        _credentials.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(
                fixture.Profile.AccountSuffix));

        Result<ActiveReplacementRestore?> result = await new BackupRestoreClientMutationEvidenceProbe(
                _guardedRoot,
                _credentials)
            .InspectAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

    }

    [Fact]
    public async Task Index_only_pre_effect_window_is_clear_and_read_only()
    {

        string absentStaging = Path.Combine(
            _container,
            BackupRestoreJournal.CreateStagingName());

        BackupRestoreStagingIndex.Add(_guardedRoot, absentStaging);

        byte[] before = File.ReadAllBytes(
            BackupRestoreStagingIndex.PathFor(_guardedRoot));

        Result<ActiveReplacementRestore?> result = await new BackupRestoreClientMutationEvidenceProbe(
                _guardedRoot,
                _credentials)
            .InspectAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value);

        Assert.Equal(
            before,
            File.ReadAllBytes(BackupRestoreStagingIndex.PathFor(_guardedRoot)));

    }

    [Fact]
    public async Task Malformed_staging_index_is_unsafe_and_unchanged()
    {

        string index = BackupRestoreStagingIndex.PathFor(_guardedRoot);

        File.WriteAllText(index, "{}");

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(
            index,
            logFailure: false));

        byte[] before = File.ReadAllBytes(index);

        Result<ActiveReplacementRestore?> result = await new BackupRestoreClientMutationEvidenceProbe(
                _guardedRoot,
                _credentials)
            .InspectAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(before, File.ReadAllBytes(index));

    }

    [Theory]
    [InlineData("relative")]
    [InlineData("aliased")]
    [InlineData("duplicate")]
    public async Task Noncanonical_staging_index_paths_are_unsafe_and_unchanged(
        string shape)
    {

        string canonical = Path.Combine(
            _container,
            BackupRestoreJournal.CreateStagingName());

        string[] roots = shape switch
        {
            "relative" => [Path.GetFileName(canonical)],
            "aliased" => [Path.Combine(_container, "missing", "..", Path.GetFileName(canonical))],
            "duplicate" => [canonical, canonical],
            _ => throw new InvalidOperationException("Unknown staging-index test shape."),
        };

        string index = BackupRestoreStagingIndex.PathFor(_guardedRoot);

        File.WriteAllBytes(
            index,
            JsonSerializer.SerializeToUtf8Bytes(
                new BackupRestoreStagingIndexRecord(
                    BackupRestoreStagingIndex.CurrentVersion,
                    roots),
                BackupJsonContext.Default.BackupRestoreStagingIndexRecord));

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(
            index,
            logFailure: false));

        byte[] before = File.ReadAllBytes(index);

        Result<ActiveReplacementRestore?> result = await new BackupRestoreClientMutationEvidenceProbe(
                _guardedRoot,
                _credentials)
            .InspectAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(before, File.ReadAllBytes(index));

        Assert.False(Directory.Exists(canonical));

    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stable_key_and_identity_partial_states_without_anchor_or_journal_are_clear(
        bool keyPresent,
        bool identityPresent)
    {

        Directory.CreateDirectory(_guardedRoot);

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot));

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guardedRoot));

        if (keyPresent)
        {

            Value(new BackupRestoreJournalKeyProvider(_credentials).CreateOrOpen(
                held,
                _guardedRoot,
                profile)).Dispose();

        }

        if (identityPresent)
        {

            _ = Value(new BackupRestoreJournalInstallationIdentityProvider(
                    _credentials)
                .SeedFromDatabase(
                    held,
                    _guardedRoot,
                    profile,
                    Guid.NewGuid()));

        }

        Result<ActiveReplacementRestore?> result = await new BackupRestoreClientMutationEvidenceProbe(
                _guardedRoot,
                _credentials)
            .InspectAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value);

    }

    [Fact]
    public void Narrow_coordination_registration_resolves_the_public_boundary()
    {

        ServiceCollection services = new();

        services.AddArcanumClientMutationCoordination();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<ArcanumClientMutationBoundary>(
            provider.GetRequiredService<IArcanumClientMutationBoundary>());

    }

    [Fact]
    public void Coordination_uses_one_registered_credential_store_for_both_evidence_authorities()
    {

        ServiceCollection services = new();

        AbsentCredentialStore expected = new();

        services.AddSingleton<IOsCredentialStore>(expected);

        services.AddArcanumClientMutationCoordination();

        using ServiceProvider provider = services.BuildServiceProvider();

        InstallationStartupProbe startup = Assert.IsType<InstallationStartupProbe>(
            provider.GetRequiredService<IInstallationStartupProbe>());

        Assert.Same(expected, startup.CredentialStore);

        BackupRestoreClientMutationEvidenceProbe restore = Assert.IsType<
            BackupRestoreClientMutationEvidenceProbe>(
            provider.GetRequiredService<IClientMutationRestoreEvidenceProbe>());

        Assert.Same(expected, restore.CredentialStore);

    }

    [Fact]
    public void Composed_cli_grimoire_and_backup_graph_uses_one_registered_credential_store_for_both_evidence_authorities()
    {

        ServiceCollection services = new();

        AbsentCredentialStore expected = new();

        services.AddSingleton<IOsCredentialStore>(expected);

        services.AddArcanumGrimoireForCli();

        services.AddArcanumBackup();

        using ServiceProvider provider = services.BuildServiceProvider();

        InstallationStartupProbe startup = Assert.IsType<InstallationStartupProbe>(
            provider.GetRequiredService<IInstallationStartupProbe>());

        Assert.Same(expected, startup.CredentialStore);

        BackupRestoreClientMutationEvidenceProbe restore = Assert.IsType<
            BackupRestoreClientMutationEvidenceProbe>(
            provider.GetRequiredService<IClientMutationRestoreEvidenceProbe>());

        Assert.Same(expected, restore.CredentialStore);

    }

    private sealed class FakeStartupProbe(
        Result<ActiveInstallationReset?> result) : IInstallationStartupProbe
    {

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(result);

        }

        public Result<bool> IsFreshInstallation() => Result<bool>.Success(false);

    }

    private sealed class AbsentCredentialStore : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.NotFound();

        public OsCredentialStoreResult Set(
            string service,
            string account,
            string secret) =>
            throw new InvalidOperationException("A read-only evidence probe must not write credentials.");

        public OsCredentialStoreResult Delete(string service, string account) =>
            throw new InvalidOperationException("A read-only evidence probe must not delete credentials.");

    }

    private RestoreFixture PublishActiveRestore()
    {

        Directory.CreateDirectory(_guardedRoot);

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot));

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guardedRoot));

        Guid installationId = Guid.NewGuid();

        _ = Value(new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(
                held,
                _guardedRoot,
                profile,
                installationId));

        Value(new BackupRestoreJournalKeyProvider(_credentials).CreateOrOpen(
            held,
            _guardedRoot,
            profile)).Dispose();

        string staging = Path.Combine(
            _container,
            BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(staging);

        BackupRestoreJournalAnchorStore store = new(
            _credentials,
            new BackupRestoreJournalKeyProvider(_credentials),
            new BackupRestoreJournalInstallationIdentityProvider(_credentials));

        BackupRestoreJournalPayloadV2 payload = Payload();

        BackupRestoreJournalLocation location = Value(store.ResolveLocation(
            profile,
            installationId,
            payload.OwnerOperationId,
            staging));

        BackupRestoreJournalPublication publication = Value(store.Begin(
            held,
            _guardedRoot,
            profile,
            location,
            payload));

        return new RestoreFixture(profile, store, publication);

    }

    private string WriteLegacyJournal(Guid operationId)
    {

        string staging = Path.Combine(
            _container,
            BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(staging);

        _ = BackupRestoreJournal.Write(
            staging,
            new BackupRestoreJournalRecord(
                BackupRestoreJournal.CurrentVersion,
                operationId,
                BackupRestoreConflictMode.ReplaceInstallation,
                BackupRestorePhase.Stage,
                _guardedRoot,
                Path.Combine(staging, BackupRestoreJournal.StagedDirectoryName),
                Path.Combine(staging, BackupRestoreJournal.DisplacedDirectoryName),
                SafetyBackupPath: null,
                Path.Combine(_container, "source.arcbackup"),
                StagingVolumeId: 1,
                StagingFileId: 1));

        return staging;

    }

    private BackupRestoreJournalPayloadV2 Payload()
    {

        BackupRestoreDurableNodeIdentityV1 live = new(
            _container,
            Digest(1),
            "arcanum",
            BackupRestoreNodeKind.Directory,
            BackupRestoreNodePresence.Present,
            Digest(2),
            ContentDigest: null);

        string stagingParent = Path.Combine(
            _container,
            ".arcanum-restore-0123456789abcdef0123456789abcdef");

        BackupRestoreDurableNodeIdentityV1 staged = new(
            stagingParent,
            Digest(3),
            "staged",
            BackupRestoreNodeKind.Directory,
            BackupRestoreNodePresence.Present,
            Digest(4),
            ContentDigest: null);

        BackupRestoreDurableNodeIdentityV1 displaced = new(
            stagingParent,
            Digest(3),
            "previous",
            BackupRestoreNodeKind.Directory,
            BackupRestoreNodePresence.Absent,
            NodePhysicalIdentityDigest: null,
            ContentDigest: null);

        BackupRestoreDurableNodeIdentityV1 archive = new(
            _container,
            Digest(5),
            "source.arcbackup",
            BackupRestoreNodeKind.RegularFile,
            BackupRestoreNodePresence.Present,
            Digest(6),
            Digest(7));

        return new BackupRestoreJournalPayloadV2(
            Guid.NewGuid(),
            CovenantExclusiveOperation.BackupRestore,
            Digest(8),
            BackupRestoreConflictMode.ReplaceInstallation,
            BackupRestorePhase.Stage,
            Digest(9),
            live,
            staged,
            displaced,
            archive,
            SafetyBackup: null,
            MarkerCleanup: null);

    }

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private sealed record RestoreFixture(
        BackupRestoreProfileNamespace Profile,
        BackupRestoreJournalAnchorStore Store,
        BackupRestoreJournalPublication Publication);

}
