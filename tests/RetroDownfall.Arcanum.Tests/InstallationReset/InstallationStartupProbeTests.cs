using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationStartupProbeTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void Backup_files_alone_leave_the_installation_fresh_without_writes()
    {

        string root = _workspace.CreateSubdir("arcanum");

        _ = _workspace.WriteFile("arcanum/backups/kept.arcbackup", "backup");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        string[] before = Directory.GetFileSystemEntries(
            _workspace.Root,
            "*",
            SearchOption.AllDirectories);

        Result<bool> result = probe.IsFreshInstallation();

        Assert.True(result.IsSuccess);

        Assert.True(result.Value);

        Assert.Contains(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            credentials.ReadAccounts);

        Assert.Equal(before, Directory.GetFileSystemEntries(
            _workspace.Root,
            "*",
            SearchOption.AllDirectories));

    }

    [Theory]
    [InlineData("arcanum.json")]
    [InlineData("arcanum.db")]
    [InlineData("arcanum.db.kdf")]
    [InlineData("arcanum.db.kdf.pending")]
    [InlineData("security.dat")]
    public void Authoritative_file_state_makes_the_installation_nonfresh(
        string relativePath)
    {

        string root = _workspace.CreateSubdir("arcanum");

        _ = _workspace.WriteFile("arcanum/" + relativePath, "state");

        InstallationStartupProbe probe = CreateProbe(
            root,
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        Result<bool> result = probe.IsFreshInstallation();

        Assert.True(result.IsSuccess);

        Assert.False(result.Value);

    }

    [Fact]
    public void Fixed_master_credential_makes_the_installation_nonfresh()
    {

        string root = _workspace.CreateSubdir("arcanum");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.Ok("secret-that-must-not-be-returned"));

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<bool> result = probe.IsFreshInstallation();

        Assert.True(result.IsSuccess);

        Assert.False(result.Value);

        Assert.Equal(
            1,
            credentials.ReadAccounts.Count(static account =>
                string.Equals(
                    account,
                    ArcanumCredentialIdentity.MasterApiKeyAccount,
                    StringComparison.Ordinal)));

        Assert.Equal(0, credentials.WriteCount);

        Assert.Equal(0, credentials.DeleteCount);

    }

    [Fact]
    public async Task Active_reset_probe_reads_the_bounded_record_without_mutation()
    {

        string root = _workspace.CreateSubdir("arcanum");

        InstallationResetActiveStore activeStore = new(root);

        InstallationResetActiveRecord record = CreateActiveRecord();

        Assert.True((await activeStore.WriteLegacyV1ForTestsAsync(
            record,
            CancellationToken.None)).IsSuccess);

        InstallationStartupProbe probe = CreateProbe(
            root,
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        Result<ActiveInstallationReset?> result = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        ActiveInstallationReset active = Assert.IsType<ActiveInstallationReset>(
            result.Value);

        Assert.Equal(InstallationResetScope.All, active.Scope);

        Assert.Equal("/selected/workspace", active.WorkspaceRoot);

        Assert.Equal(record.OperationId, active.OperationId);

        Assert.Equal(record.Phase, active.Phase);

        Assert.Equal(record.DataHandoff, active.DataHandoff);

        Assert.False(active.OnlineDataCompletionDurable);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

    }

    [Fact]
    public async Task Missing_active_record_is_a_no_create_probe()
    {

        string guardedRoot = Path.Combine(_workspace.Root, "missing-arcanum");

        InstallationStartupProbe probe = CreateProbe(
            guardedRoot,
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        string[] before = Directory.GetFileSystemEntries(_workspace.Root);

        Result<ActiveInstallationReset?> result = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value);

        Assert.Equal(before, Directory.GetFileSystemEntries(_workspace.Root));

    }

    [Fact]
    public async Task Missing_retained_parent_is_a_no_create_absence_not_an_authentication_error()
    {

        string missingParent = Path.Combine(_workspace.Root, "missing-parent");

        string guardedRoot = Path.Combine(missingParent, "arcanum");

        InstallationStartupProbe probe = CreateProbe(
            guardedRoot,
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        string[] before = Directory.GetFileSystemEntries(
            _workspace.Root,
            "*",
            SearchOption.AllDirectories);

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsSuccess, active.Error.Message);

        Assert.Null(active.Value);

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        Assert.True(fresh.Value);

        Assert.False(Directory.Exists(missingParent));

        Assert.Equal(before, Directory.GetFileSystemEntries(
            _workspace.Root,
            "*",
            SearchOption.AllDirectories));

    }

    [Fact]
    public async Task Ordinary_file_at_the_guarded_root_fails_closed_without_mutation()
    {

        string guardedRoot = _workspace.WriteFile(
            "guarded-root-file",
            "ordinary-file");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationStartupProbe probe = CreateProbe(guardedRoot, credentials);

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.Equal("ordinary-file", await File.ReadAllTextAsync(guardedRoot));

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            credentials.ReadAccounts);

    }

    [Fact]
    public async Task Dangling_symlink_at_the_guarded_root_fails_closed_without_mutation()
    {

        string guardedRoot = Path.Combine(_workspace.Root, "guarded-root-link");

        Directory.CreateSymbolicLink(
            guardedRoot,
            Path.Combine(_workspace.Root, "missing-target"));

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationStartupProbe probe = CreateProbe(guardedRoot, credentials);

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.NotNull(new DirectoryInfo(guardedRoot).LinkTarget);

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            credentials.ReadAccounts);

    }

    [Fact]
    public async Task Symlink_to_directory_at_the_guarded_root_fails_closed_without_mutation()
    {

        string target = _workspace.CreateSubdir("guarded-root-target");

        string guardedRoot = Path.Combine(_workspace.Root, "guarded-root-link");

        Directory.CreateSymbolicLink(guardedRoot, target);

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationStartupProbe probe = CreateProbe(guardedRoot, credentials);

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.NotNull(new DirectoryInfo(guardedRoot).LinkTarget);

        Assert.Empty(Directory.GetFileSystemEntries(target));

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            credentials.ReadAccounts);

    }

    [Fact]
    public async Task Inaccessible_ancestor_of_the_guarded_root_fails_closed_without_mutation()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string retainedParent = _workspace.CreateSubdir("inaccessible-parent");

        string guardedRoot = Path.Combine(retainedParent, "arcanum");

        Directory.CreateDirectory(guardedRoot);

        UnixFileMode originalMode = File.GetUnixFileMode(retainedParent);

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        try
        {

            File.SetUnixFileMode(retainedParent, UnixFileMode.None);

            InstallationStartupProbe probe = CreateProbe(guardedRoot, credentials);

            Result<ActiveInstallationReset?> active = await probe
                .ReadActiveResetAsync(CancellationToken.None);

            Result<bool> fresh = probe.IsFreshInstallation();

            Assert.True(active.IsFailure);

            Assert.True(fresh.IsFailure);

            Assert.DoesNotContain(
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                credentials.ReadAccounts);

        }
        finally
        {

            File.SetUnixFileMode(retainedParent, originalMode);

        }

        Assert.Empty(Directory.GetFileSystemEntries(guardedRoot));

    }

    [Fact]
    public async Task Existing_nondirectory_retained_parent_still_fails_closed()
    {

        string retainedParent = _workspace.WriteFile(
            "not-a-directory",
            "ordinary-file");

        string guardedRoot = Path.Combine(retainedParent, "arcanum");

        InstallationStartupProbe probe = CreateProbe(
            guardedRoot,
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.Equal("ordinary-file", await File.ReadAllTextAsync(retainedParent));

    }

    [Fact]
    public async Task Existing_dangling_symlink_retained_parent_still_fails_closed()
    {

        string retainedParent = Path.Combine(_workspace.Root, "linked-parent");

        Directory.CreateSymbolicLink(
            retainedParent,
            Path.Combine(_workspace.Root, "missing-target"));

        InstallationStartupProbe probe = CreateProbe(
            Path.Combine(retainedParent, "arcanum"),
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.NotNull(new DirectoryInfo(retainedParent).LinkTarget);

    }

    [Fact]
    public async Task Nondirectory_ancestor_of_the_retained_parent_fails_closed()
    {

        string obstructingAncestor = _workspace.WriteFile(
            "file-ancestor",
            "ordinary-file");

        string retainedParent = Path.Combine(obstructingAncestor, "missing-parent");

        InstallationStartupProbe probe = CreateProbe(
            Path.Combine(retainedParent, "arcanum"),
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.Equal("ordinary-file", await File.ReadAllTextAsync(obstructingAncestor));

    }

    [Fact]
    public async Task Dangling_symlink_ancestor_of_the_retained_parent_fails_closed()
    {

        string obstructingAncestor = Path.Combine(_workspace.Root, "linked-ancestor");

        Directory.CreateSymbolicLink(
            obstructingAncestor,
            Path.Combine(_workspace.Root, "missing-target"));

        string retainedParent = Path.Combine(obstructingAncestor, "missing-parent");

        InstallationStartupProbe probe = CreateProbe(
            Path.Combine(retainedParent, "arcanum"),
            new RecordingCredentialStore(OsCredentialStoreResult.NotFound()));

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.NotNull(new DirectoryInfo(obstructingAncestor).LinkTarget);

    }

    [Fact]
    public async Task Authenticated_v2_probe_projects_the_exact_record_without_mutation()
    {

        string root = _workspace.CreateSubdir("arcanum-v2");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationResetActiveStore activeStore = new(root, credentials);

        InstallationResetActiveRecord record = CreateActiveRecord();

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(root)))
        {

            _ = Value(await activeStore.BeginAsync(
                held,
                Guid.Parse("51515151-5151-4151-8151-515151515151"),
                record,
                CancellationToken.None));

        }

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        int writesBefore = credentials.WriteCount;

        int deletesBefore = credentials.DeleteCount;

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<ActiveInstallationReset?> result = await probe.ReadActiveResetAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        ActiveInstallationReset active = Assert.IsType<ActiveInstallationReset>(result.Value);

        Assert.Equal(record.OperationId, active.OperationId);

        Assert.Equal(record.PlanId, active.PlanId);

        Assert.Equal(record.Phase, active.Phase);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        Assert.False(fresh.Value);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

        Assert.Equal(writesBefore, credentials.WriteCount);

        Assert.Equal(deletesBefore, credentials.DeleteCount);

    }

    [Fact]
    public async Task Authenticated_v2_beside_an_absent_guarded_root_remains_active_and_nonfresh()
    {

        string root = _workspace.CreateSubdir("arcanum-v2-root-removed");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationResetActiveStore activeStore = new(root, credentials);

        InstallationResetActiveRecord record = CreateActiveRecord();

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(root)))
        {

            _ = Value(await activeStore.BeginAsync(
                held,
                Guid.Parse("81818181-8181-4181-8181-818181818181"),
                record,
                CancellationToken.None));

        }

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        int writesBefore = credentials.WriteCount;

        int deletesBefore = credentials.DeleteCount;

        Directory.Delete(root, recursive: true);

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsSuccess, active.Error.Message);

        Assert.Equal(record.OperationId, Assert.IsType<ActiveInstallationReset>(active.Value).OperationId);

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        Assert.False(fresh.Value);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

        Assert.Equal(writesBefore, credentials.WriteCount);

        Assert.Equal(deletesBefore, credentials.DeleteCount);

        Assert.False(Directory.Exists(root));

    }

    [Fact]
    public async Task Bounded_v1_beside_an_absent_guarded_root_remains_active_and_nonfresh()
    {

        string root = _workspace.CreateSubdir("arcanum-v1-root-removed");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationResetActiveStore activeStore = new(root, credentials);

        InstallationResetActiveRecord record = CreateActiveRecord();

        Assert.True((await activeStore.WriteLegacyV1ForTestsAsync(
            record,
            CancellationToken.None)).IsSuccess);

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        Directory.Delete(root, recursive: true);

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<ActiveInstallationReset?> active = await probe
            .ReadActiveResetAsync(CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsSuccess, active.Error.Message);

        Assert.Equal(record.OperationId, Assert.IsType<ActiveInstallationReset>(active.Value).OperationId);

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        Assert.False(fresh.Value);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

        Assert.Equal(0, credentials.WriteCount);

        Assert.Equal(0, credentials.DeleteCount);

        Assert.False(Directory.Exists(root));

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_v2_authentication_is_neither_null_nor_a_fresh_installation(
        bool removeAnchor)
    {

        string root = _workspace.CreateSubdir(
            removeAnchor ? "arcanum-missing-anchor" : "arcanum-missing-key");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationResetActiveStore activeStore = new(root, credentials);

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(root)))
        {

            _ = Value(await activeStore.BeginAsync(
                held,
                Guid.Parse("71717171-7171-4171-8171-717171717171"),
                CreateActiveRecord(),
                CancellationToken.None));

        }

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(root));

        string missingAccount = removeAnchor
            ? ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profile.AccountSuffix)
            : ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
                profile.AccountSuffix);

        credentials.RemoveStored(missingAccount);

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<ActiveInstallationReset?> active = await probe.ReadActiveResetAsync(
            CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

    }

    [Fact]
    public async Task Authenticated_probe_rejects_the_one_ahead_window_without_advancing_it()
    {

        string root = _workspace.CreateSubdir("arcanum-one-ahead");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationResetActiveStore activeStore = new(root, credentials);

        InstallationResetActivePublication publication;

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(root)))
        {

            publication = Value(await activeStore.BeginAsync(
                held,
                Guid.Parse("61616161-6161-4161-8161-616161616161"),
                CreateActiveRecord(),
                CancellationToken.None));

        }

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(root));

        using InstallationResetActiveRecordKeyLease key = Value(
            new InstallationResetActiveRecordKeyProvider(credentials)
                .OpenExisting(profile));

        InstallationResetActiveEnvelopeV2 ahead = Value(
            InstallationResetActiveRecordAuthenticator.Seal(
                key,
                publication.Location,
                publication.Envelope.InstallationId,
                publication.Envelope.Revision + 1,
                publication.EnvelopeDigest,
                publication.Payload));

        File.WriteAllBytes(
            activeStore.ActivePath,
            Value(InstallationResetActiveRecordAuthenticator.EncodeEnvelope(ahead)));

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        int writesBefore = credentials.WriteCount;

        int deletesBefore = credentials.DeleteCount;

        Result<ActiveInstallationReset?> result = await CreateProbe(root, credentials)
            .ReadActiveResetAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.Error.Code);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

        Assert.Equal(writesBefore, credentials.WriteCount);

        Assert.Equal(deletesBefore, credentials.DeleteCount);

    }

    [Fact]
    public async Task Corrupt_active_evidence_is_neither_null_nor_a_fresh_installation()
    {

        string root = _workspace.CreateSubdir("arcanum-corrupt");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound());

        InstallationResetActiveStore activeStore = new(root);

        await File.WriteAllTextAsync(activeStore.ActivePath, "{}");

        byte[] before = await File.ReadAllBytesAsync(activeStore.ActivePath);

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<ActiveInstallationReset?> active = await probe.ReadActiveResetAsync(
            CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.Equal(before, await File.ReadAllBytesAsync(activeStore.ActivePath));

        Assert.Equal(0, credentials.WriteCount);

        Assert.Equal(0, credentials.DeleteCount);

    }

    [Fact]
    public async Task Credential_probe_failure_is_neither_null_nor_a_fresh_installation()
    {

        string root = _workspace.CreateSubdir("arcanum-credential-error");

        RecordingCredentialStore credentials = new(
            OsCredentialStoreResult.Unavailable("injected"),
            failAllReads: true);

        InstallationStartupProbe probe = CreateProbe(root, credentials);

        Result<ActiveInstallationReset?> active = await probe.ReadActiveResetAsync(
            CancellationToken.None);

        Result<bool> fresh = probe.IsFreshInstallation();

        Assert.True(active.IsFailure);

        Assert.True(fresh.IsFailure);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "unexpected")));

        Assert.Equal(0, credentials.WriteCount);

        Assert.Equal(0, credentials.DeleteCount);

    }

    private static InstallationStartupProbe CreateProbe(
        string root,
        IOsCredentialStore credentials) =>
        new(
            root,
            Path.Combine(root, "arcanum.json"),
            Path.Combine(root, "arcanum.db"),
            Path.Combine(root, "security.dat"),
            credentials);

    private static InstallationResetActiveRecord CreateActiveRecord()
    {

        InstallationResetAcceptedBinding binding = new(
            "binding",
            ["/selected"],
            ["/excluded"],
            [],
            [ArcanumCredentialIdentity.MasterApiKeyAccount],
            ["data-plan"]);

        return new InstallationResetActiveRecord(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.All,
            new DataRetentionWorkspaceBinding(
                Guid.NewGuid(),
                "/selected/workspace"),
            binding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure);

    }

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private sealed class RecordingCredentialStore : IOsCredentialStore
    {

        private readonly OsCredentialStoreResult _masterReadResult;

        private readonly bool _failAllReads;

        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public RecordingCredentialStore(
            OsCredentialStoreResult masterReadResult,
            bool failAllReads = false)
        {

            _masterReadResult = masterReadResult;

            _failAllReads = failAllReads;

        }

        public bool IsAvailable => true;

        public int ReadCount { get; private set; }

        public List<string> ReadAccounts { get; } = [];

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public void RemoveStored(string account) => _ = _values.Remove(account);

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            ReadCount++;

            ReadAccounts.Add(account);

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (_failAllReads)
            {

                return _masterReadResult;

            }

            if (_values.TryGetValue(account, out string? value))
            {

                return OsCredentialStoreResult.Ok(value);

            }

            return string.Equals(
                account,
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                StringComparison.Ordinal)
                ? _masterReadResult
                : OsCredentialStoreResult.NotFound();

        }

        public OsCredentialStoreResult Set(
            string service,
            string account,
            string secret)
        {

            WriteCount++;

            _values[account] = secret;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            DeleteCount++;

            _ = _values.Remove(account);

            return OsCredentialStoreResult.Ok(string.Empty);

        }

    }

}
