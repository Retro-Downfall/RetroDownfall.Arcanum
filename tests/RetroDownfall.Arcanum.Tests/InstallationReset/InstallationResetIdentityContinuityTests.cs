using System.Security.Cryptography;

using System.Text;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Backup;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// What an ordinary factory reset leaves behind for the Grimoire that is born after it.
/// </summary>
/// <remarks>
/// A global reset sweeps the Grimoire directory, so the database and the installation identity its
/// authority row carried are gone — while the profile's pre-database installation identity, an OS
/// credential seeded when the reset published its record, is retained by design. The next start
/// creates a brand-new Grimoire. Every operation that binds a journal to the installation (a second
/// reset, a replacement restore, a Covenant reset) seeds-or-compares the retained identity with the
/// database row and refuses on disagreement, so the two must agree for any of them to run again.
///
/// <para>These cases run the real active store, credential catalog, offline cleanup, identity
/// provider, stopped-host data service, protected erasure and Grimoire bootstrap over a real SQLCipher
/// database. Nothing in the data phase is a double: the stopped-host composition measures the
/// Covenant family through the real gate and runs the protected erasure over its own composition, so
/// the sweep that deletes the database follows a real erasure.</para>
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class InstallationResetIdentityContinuityTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private readonly ArcanumTestHomeScope _home = new("arcanum-reset-identity");

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly List<ServiceProvider> _providers = [];

    public InstallationResetIdentityContinuityTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Asynchronously: a provider that ran the protected erasure holds a singleton that only
        // implements IAsyncDisposable.
        foreach (ServiceProvider provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _home.Dispose();
    }

    [SkippableFact]
    public async Task The_Grimoire_born_after_an_ordinary_reset_carries_the_identity_the_reset_retained()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Rebirth rebirth = await ResetAndRebirthAsync();

        Assert.Equal(rebirth.Retained, rebirth.Fresh);

        Assert.Equal(rebirth.Prior, rebirth.Fresh);
    }

    [SkippableFact]
    public async Task A_second_factory_reset_is_admitted_on_the_Grimoire_born_after_a_reset()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Rebirth rebirth = await ResetAndRebirthAsync();

        // A new provider: a reset is the whole life of one process, and the Covenant runtime the first
        // one established describes a Grimoire that no longer exists. The secret store is carried over
        // from the first, because the reset deleted the key ring it protected its mirrors with and the
        // store still holds the one it was running on; a new process would meet a fresh ring.
        ServiceProvider secondProcess = CreateProvider(
            rebirth.Provider.GetRequiredService<DataProtectionSecretStore>());

        _providers.Add(secondProcess);

        await SeedMasterKeyAsync(secondProcess);

        Result<InstallationResetResult> second = await FactoryResetAsync(secondProcess);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : string.Empty);

        Assert.Equal(InstallationResetPhase.Completed, second.Value.Phase);

        Assert.False(second.Value.ResumeRequired);
    }

    [SkippableFact]
    public async Task A_Covenant_reset_transition_is_admitted_on_the_Grimoire_born_after_a_reset()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Rebirth rebirth = await ResetAndRebirthAsync();

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        GrimoireOfflineTransitionJournalLocation location = new GrimoireOfflineTransitionJournalFileStore()
            .ResolveLocation(ArcanumPaths.GrimoireDirectory)
            .Value;

        // The two calls GrimoireOfflineTransitionPhaseAuthority makes, in its order, before a Covenant
        // reset's journal can open: seed-or-compare the profile identity with the database row, then
        // begin the journal bound to that same row.
        Result<Guid> seeded = new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(
                held,
                ArcanumPaths.GrimoireDirectory,
                location.ProfileNamespace,
                rebirth.Fresh);

        Assert.True(seeded.IsSuccess, seeded.IsFailure ? seeded.Error.Message : string.Empty);

        Result<GrimoireOfflineTransitionJournalPublication> begun = await new GrimoireOfflineTransitionJournalStore(
                _credentials)
            .BeginAsync(
                held,
                ArcanumPaths.GrimoireDirectory,
                rebirth.Fresh,
                Guid.NewGuid(),
                GrimoireOfflineTransitionKind.CovenantReset,
                payloadVersion: 1,
                Encoding.UTF8.GetBytes("covenant-reset"),
                CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : string.Empty);
    }

    [SkippableFact]
    public async Task A_replacement_restore_of_a_backup_of_the_Grimoire_born_after_a_reset_is_admitted()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Rebirth rebirth = await ResetAndRebirthAsync();

        DataProtectionSecretStore secrets = rebirth.Provider.GetRequiredService<DataProtectionSecretStore>();

        string archives = Path.Combine(_home.Root, "archives");

        Directory.CreateDirectory(archives);

        string archive = Path.Combine(archives, "after-reset.arcbackup");

        BackupStatePaths paths = BackupStatePaths.Default;

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {
            KdfIterations = 10_000,

            ChunkSize = 64 * 1024,
        });

        BackupService backups = new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            codec,
            new RebirthSecretReader(secrets),
            TimeProvider.System);

        BackupCreateResult created = await backups.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, SessionId: null, [], Exclude: []),
                archive,
                Overwrite: true),
            BackupRestoreServiceTests.Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        BackupRestoreServiceOptions options = new()
        {
            RestoreStaging = new CovenantRestoreStagingServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(),
                new CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle(),
                new BackupRestoreJournalAnchorStore(
                    _credentials,
                    new BackupRestoreJournalKeyProvider(_credentials),
                    new BackupRestoreJournalInstallationIdentityProvider(_credentials)),
                new BackupRestoreJournalInstallationIdentityProvider(_credentials),
                new BackupRestoreJournalKeyProvider(_credentials),
                new BackupRestoreEffectDigestCalculator()),
        };

        BackupRestoreService restore = new(
            paths,
            codec,
            secrets,
            safetyBackupFactory: null,
            TimeProvider.System,
            GrimoireSchemaTestInstaller.Create(),
            new MemoryErasureKeyring(new InMemoryOsCredentialStore()),
            options);

        BackupRestoreResult restored = await restore.RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            BackupRestoreServiceTests.Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.True(
            restored.Status is BackupRestoreStatus.Completed,
            restored.Status + ": " + string.Join("; ", restored.Issues.Select(static issue => issue.Code + " " + issue.Message)));
    }

    private async Task<Rebirth> ResetAndRebirthAsync()
    {
        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        await InstallPriorGrimoireAsync(provider);

        Guid prior = await ReadDatabaseIdentityAsync(provider);

        Result<InstallationResetResult> reset = await FactoryResetAsync(provider);

        Assert.True(reset.IsSuccess, reset.IsFailure ? reset.Error.Message : string.Empty);

        Assert.Equal(InstallationResetPhase.Completed, reset.Value.Phase);

        // The premise, not the property under test: the sweep takes the database with it, and the
        // identity the reset seeded from that database's authority row is retained.
        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        Guid retained = Guid.Parse(RetainedIdentity()!);

        Assert.Equal(prior, retained);

        await BootstrapGrimoireAsync(provider);

        return new Rebirth(provider, prior, retained, await ReadDatabaseIdentityAsync(provider));
    }

    private ServiceProvider CreateProvider(DataProtectionSecretStore? carriedSecrets = null)
    {
        ServiceCollection services = new();

        services.AddLogging();

        services.AddSingleton<IOsCredentialStore>(_credentials);

        services.AddArcanumCliClientStack();

        if (carriedSecrets is not null)
        {
            services.AddSingleton(carriedSecrets);
        }

        services.AddSingleton<IInstallationResetPreDataMutation>(
            NoopInstallationResetPreDataMutation.Instance);

        services.AddArcanumInstallationReset(new ArcanumSettings());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async Task InstallPriorGrimoireAsync(ServiceProvider provider)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string fixtureCopy = _fixture.CopyDatabase();

        File.Copy(fixtureCopy, ArcanumPaths.GrimoireDatabaseFile, overwrite: true);

        File.Copy(
            fixtureCopy + ".kdf",
            GrimoireKdfSidecarFile.GetSidecarPath(ArcanumPaths.GrimoireDatabaseFile),
            overwrite: true);

        await provider
            .GetRequiredService<DataProtectionSecretStore>()
            .SaveGrimoireEncryptionSecretAsync(GrimoireFixture.TestGrimoireSecret);

        await SeedMasterKeyAsync(provider);
    }

    /// <summary>The measured Covenant inventory is taken under envelope keys derived from the master key.</summary>
    private static Task SeedMasterKeyAsync(ServiceProvider provider) =>
        provider
            .GetRequiredService<ISecretStore>()
            .SaveApiKeyAsync("identity-continuity-master-api-key");

    private static async Task<Guid> ReadDatabaseIdentityAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostDataService data = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostDataService>();

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        StoppedHostGrimoireAuthorityIssuer issuer = new(
            held,
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.GrimoireDatabaseFile);

        Result<Guid> identity = await data.ReadIdentityUnderStoppedHostAuthorityAsync(
            issuer,
            CancellationToken.None);

        Assert.True(identity.IsSuccess, identity.IsFailure ? identity.Error.Message : string.Empty);

        return identity.Value;
    }

    private async Task<Result<InstallationResetResult>> FactoryResetAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostPlanner planner = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostPlanner>();

        IInstallationResetLockedService locked = scope.ServiceProvider
            .GetRequiredService<IInstallationResetLockedService>();

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        StoppedHostGrimoireAuthorityIssuer issuer = new(
            held,
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.GrimoireDatabaseFile);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            _home.Root);

        Result<StoppedHostInstallationResetPlan> plan = await planner.PlanUnderStoppedHostLockAsync(
            request,
            issuer,
            CancellationToken.None);

        if (plan.IsFailure)
        {
            return Result<InstallationResetResult>.Failure(plan.Error);
        }

        return await locked.ApplyFreshUnderMaintenanceLockAsync(
            request,
            plan.Value,
            held,
            CancellationToken.None);
    }

    /// <summary>
    /// The production start of a Grimoire that does not exist yet: the locked bootstrap the host and the
    /// exclusive CLI both run, over the real secret store.
    /// </summary>
    private async Task BootstrapGrimoireAsync(ServiceProvider provider)
    {
        DataProtectionSecretStore secrets = provider.GetRequiredService<DataProtectionSecretStore>();

        await secrets.SaveApiKeyAsync("rebirth-bootstrap-api-key");

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(_credentials);

        _ = services.AddGrimoireSchemaInstallation();

        await using ServiceProvider scopes = services.BuildServiceProvider();

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

    private string? RetainedIdentity()
    {
        BackupRestoreProfileNamespace profile = BackupRestoreJournalAuthenticator
            .ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory)
            .Value;

        OsCredentialStoreResult result = _credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profile.AccountSuffix));

        return result.Status is OsCredentialStoreStatus.Ok ? result.Value : null;
    }

    private sealed record Rebirth(
        ServiceProvider Provider,
        Guid Prior,
        Guid Retained,
        Guid Fresh);

    private sealed class RebirthSecretReader(DataProtectionSecretStore secrets) : IBackupSecretSnapshotReader
    {
        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
            secrets.GetGrimoireEncryptionSecretReadResultAsync();

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
            Task.FromResult(
                SecretStoreReadResult.Ok(
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("rebirth-bootstrap-api-key"));
    }
}
