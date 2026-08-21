using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

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

        Assert.Equal(1, credentials.ReadCount);

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

        Assert.Equal(1, credentials.ReadCount);

        Assert.Equal(0, credentials.WriteCount);

        Assert.Equal(0, credentials.DeleteCount);

    }

    [Fact]
    public async Task Active_reset_probe_reads_the_bounded_record_without_mutation()
    {

        string root = _workspace.CreateSubdir("arcanum");

        InstallationResetActiveStore activeStore = new(root);

        InstallationResetActiveRecord record = CreateActiveRecord();

        Assert.True((await activeStore.WriteAsync(
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

    private sealed class RecordingCredentialStore(
        OsCredentialStoreResult readResult) : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            ReadCount++;

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            Assert.Equal(ArcanumCredentialIdentity.MasterApiKeyAccount, account);

            return readResult;

        }

        public OsCredentialStoreResult Set(
            string service,
            string account,
            string secret)
        {

            WriteCount++;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            DeleteCount++;

            return OsCredentialStoreResult.Ok(string.Empty);

        }

    }

}
