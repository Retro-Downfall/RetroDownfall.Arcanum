using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The repair's one question of the Grimoire, asked of a real SQLCipher database through the real
/// stopped-host data service: does it hold its authority row.
/// </summary>
/// <remarks>
/// The service cases fake that answer. These do not: the file is the fixture's own database, opened on
/// the read-only identity lane under the installation lock the repair takes, and the answers are the
/// ones the Grimoire's own start would give about the same file.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class InstallationIdentityRepairGrimoireTests : IAsyncLifetime
{
    private const string DamagedValue = "c0ffee00-1234-4abc-8def-0123456789ab";

    private readonly GrimoireFixture _fixture;

    private readonly ArcanumTestHomeScope _home = new("arcanum-repair-grimoire");

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly List<ServiceProvider> _providers = [];

    public InstallationIdentityRepairGrimoireTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _home.Dispose();
    }

    [SkippableFact]
    public async Task A_Grimoire_that_holds_its_authority_row_has_the_damaged_identity_seeded_again_from_that_row_and_is_left_exactly_as_it_was()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallGrimoireAsync();

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        SetIdentity(DamagedValue);

        string before = Digest(ArcanumPaths.GrimoireDatabaseFile);

        Guid row = await ReadRowIdentityAsync();

        InstallationIdentityRepair repair = Resolve();

        Result<InstallationIdentityRepairReport> planned = await repair.PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Damaged, planned.Value.State);

        Assert.Equal(InstallationIdentityRepairAction.Reseed, planned.Value.Action);

        AssertIdentityIs(DamagedValue);

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Reseeded, applied.Value.State);

        // The item now names the installation the Grimoire's own authority row carries, which is the
        // value every restore, reset and offline transition compares the row with.
        AssertIdentityIs(row.ToString("D").ToUpperInvariant());

        Assert.Equal(before, Digest(ArcanumPaths.GrimoireDatabaseFile));

        // Asking the question must not leave write-ahead-log bookkeeping beside a database that had none.
        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-wal"));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-shm"));
    }

    [SkippableFact]
    public async Task A_database_file_that_never_got_its_authority_row_does_not_block_the_repair()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallGrimoireAsync();

        // What an unfinished first start leaves: the file, the key it was created under, and no row.
        _ = await StoppedHostResetTestSupport.ExecuteAsync("DROP TABLE covenant_authority_state;");

        SetIdentity(DamagedValue);

        string before = Digest(ArcanumPaths.GrimoireDatabaseFile);

        InstallationIdentityRepair repair = Resolve();

        Result<InstallationIdentityRepairReport> planned = await repair.PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Damaged, planned.Value.State);

        Assert.Equal(before, Digest(ArcanumPaths.GrimoireDatabaseFile));

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Removed, applied.Value.State);

        Assert.Equal(before, Digest(ArcanumPaths.GrimoireDatabaseFile));

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);
    }

    [SkippableFact]
    public async Task A_database_that_cannot_be_opened_is_an_unproved_position_and_blocks_the_repair()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallGrimoireAsync(saveGrimoireSecret: false);

        SetIdentity(DamagedValue);

        Result<InstallationIdentityRepairReport> result = await Resolve().ApplyAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.InventoryUnavailable, result.Error.Code);

        Assert.Contains("authority row could not be proved", result.Error.Message, StringComparison.Ordinal);

        AssertIdentityIs(DamagedValue);
    }

    /// <summary>
    /// The whole path an operator takes: the start refuses, naming the command; the command removes the
    /// item; the next start creates a Grimoire that mints its own identity.
    /// </summary>
    [SkippableFact]
    public async Task A_refused_start_is_repaired_by_the_named_command_and_the_next_start_succeeds()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        SetIdentity(DamagedValue);

        GrimoireDatabaseUnavailableException refused =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(BootstrapGrimoireAsync);

        Assert.Contains("arcanum data repair-installation-identity", refused.Message, StringComparison.Ordinal);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        Result<InstallationIdentityRepairReport> applied = await Resolve().ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Removed, applied.Value.State);

        await BootstrapGrimoireAsync();

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        // The new Grimoire minted its own identity, and the repair left nothing in the store to seed.
        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);

        // And now that a Grimoire holds its row, the repair has nothing to say about a missing item.
        Result<InstallationIdentityRepairReport> again = await Resolve().ApplyAsync();

        Assert.True(again.IsSuccess);

        Assert.Equal(InstallationIdentityRepairState.Absent, again.Value.State);
    }

    /// <summary>
    /// The installation the Grimoire's own authority row carries, read the way the repair reads it: on
    /// the read-only identity lane under the installation lock.
    /// </summary>
    private async Task<Guid> ReadRowIdentityAsync()
    {
        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostDataService data = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostDataService>();

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        Result<Guid> identity = await data.ReadIdentityUnderStoppedHostAuthorityAsync(
            new StoppedHostGrimoireAuthorityIssuer(
                held,
                ArcanumPaths.GrimoireDirectory,
                ArcanumPaths.GrimoireDatabaseFile),
            CancellationToken.None);

        Assert.True(identity.IsSuccess, identity.IsFailure ? identity.Error.Message : string.Empty);

        return identity.Value;
    }

    private InstallationIdentityRepair Resolve()
    {
        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        return provider.CreateScope().ServiceProvider.GetRequiredService<InstallationIdentityRepair>();
    }

    private ServiceProvider CreateProvider()
    {
        ServiceCollection services = new();

        services.AddLogging();

        services.AddSingleton<IOsCredentialStore>(_credentials);

        services.AddArcanumCliClientStack();

        services.AddSingleton<IInstallationResetPreDataMutation>(
            NoopInstallationResetPreDataMutation.Instance);

        services.AddArcanumInstallationReset(new ArcanumSettings());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async Task InstallGrimoireAsync(bool saveGrimoireSecret = true)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string fixtureCopy = _fixture.CopyDatabase();

        File.Copy(fixtureCopy, ArcanumPaths.GrimoireDatabaseFile, overwrite: true);

        File.Copy(
            fixtureCopy + ".kdf",
            GrimoireKdfSidecarFile.GetSidecarPath(ArcanumPaths.GrimoireDatabaseFile),
            overwrite: true);

        if (!saveGrimoireSecret)
        {
            return;
        }

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        await provider
            .GetRequiredService<DataProtectionSecretStore>()
            .SaveGrimoireEncryptionSecretAsync(GrimoireFixture.TestGrimoireSecret);
    }

    /// <summary>
    /// The production start of a Grimoire that does not exist yet: the locked bootstrap the host and the
    /// exclusive CLI both run, over the real secret store.
    /// </summary>
    private async Task BootstrapGrimoireAsync()
    {
        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        DataProtectionSecretStore secrets = provider.GetRequiredService<DataProtectionSecretStore>();

        await secrets.SaveApiKeyAsync("repair-bootstrap-api-key");

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(_credentials);

        _ = services.AddGrimoireSchemaInstallation();

        await using ServiceProvider scopes = services.BuildServiceProvider();

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            secrets,
            new GrimoireDbPassphraseSource(),
            scopes.GetRequiredService<IServiceScopeFactory>(),
            ArcanumPaths.GrimoireDatabaseFile,
            ArcanumPaths.GrimoireDirectory,
            held,
            expectedInstallationId: null,
            CancellationToken.None);
    }

    private string ProfileSuffix() =>
        BackupRestoreJournalAuthenticator.ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory).Value.AccountSuffix;

    private string IdentityAccount() =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(ProfileSuffix());

    private void SetIdentity(string value)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(ArcanumCredentialIdentity.Service, IdentityAccount(), value).Status);
    }

    private void AssertIdentityIs(string expected)
    {
        OsCredentialStoreResult stored = _credentials.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount());

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        Assert.Equal(expected, stored.Value);
    }

    private static string Digest(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
