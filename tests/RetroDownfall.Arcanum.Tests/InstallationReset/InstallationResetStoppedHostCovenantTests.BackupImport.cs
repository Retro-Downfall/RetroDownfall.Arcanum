using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Backup;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed partial class InstallationResetStoppedHostCovenantTests
{
    [Fact]
    public async Task Standalone_backup_stack_resolves_enabled_import_without_installation_reset_composition()
    {
        ServiceCollection services = new();

        services.AddLogging();

        services.AddSingleton<IOsCredentialStore>(_credentials);

        ArcanumSettings settings = new();

        settings.Features.Covenant = true;

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings));

        services.AddArcanumCliClientStack();

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IBackupRestoreService>());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_stopped_host_backup_composition_imports_without_publishing_host_readiness(bool covenantFeature)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        using BackupRestoreServiceTests sourceOwner = new();

        BackupRestoreServiceTests.Fixture source = await sourceOwner.CreateFixtureAsync(refusableSessionTrio: true);

        string archive = await source.CreateBackupAsync("stopped-host-selective.arcbackup");

        ServiceCollection services = Compose(covenantFeature);

        if (!covenantFeature)
        {
            StoppedHostResetTestSupport.RefuseToConstruct<StoppedHostCovenantAuthority>(services);
        }

        ObserveImportSecrets(services, refuseMasterPeek: !covenantFeature);

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.Null(scope.ServiceProvider.GetRequiredService<CovenantRuntimeGenerationProvider>().Current.ActiveAuthority);

        Assert.False(scope.ServiceProvider.GetRequiredService<IHostProcessToolsRuntimePolicy>().IsPublished);

        int before = await CountRowsAsync("Sessions");

        BackupRestoreResult result = await scope.ServiceProvider.GetRequiredService<IBackupRestoreService>()
            .RestoreAsync(ImportRequest(archive), BackupRestoreServiceTests.Passphrase.AsMemory(), CancellationToken.None);

        Assert.True(result.Status == BackupRestoreStatus.Completed,
            string.Join("; ", result.Issues.Select(static issue => issue.Code + ": " + issue.Message)));

        Assert.Equal(before + 1, await CountRowsAsync("Sessions"));

        Assert.Equal(covenantFeature ? 1 : 0, await CountRowsAsync("protected_session_transfer_intents"));

        ImportObservedSecrets secrets = (ImportObservedSecrets)scope.ServiceProvider.GetRequiredService<ISecretStore>();

        Assert.Equal(covenantFeature ? 1 : 0, secrets.MasterPeeks);

        Assert.False(scope.ServiceProvider.GetRequiredService<IHostProcessToolsRuntimePolicy>().IsPublished);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopped_host_import_refuses_unproved_authority_before_secret_writes_or_transfer(bool missingMasterKey)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature: true, saveMasterKey: !missingMasterKey);

        if (!missingMasterKey)
        {
            _ = await StoppedHostResetTestSupport.ExecuteAsync("DROP INDEX idx_covenant_entries_campaign;");
        }

        using BackupRestoreServiceTests sourceOwner = new();

        BackupRestoreServiceTests.Fixture source = await sourceOwner.CreateFixtureAsync(refusableSessionTrio: true);

        string archive = await source.CreateBackupAsync("refused-stopped-host-selective.arcbackup");

        ServiceCollection services = Compose(covenantFeature: true);

        ObserveImportSecrets(services, refuseMasterPeek: false);

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        int sessions = await CountRowsAsync("Sessions");

        int entries = await CountRowsAsync("Entries");

        BackupRestoreResult result = await scope.ServiceProvider.GetRequiredService<IBackupRestoreService>()
            .RestoreAsync(ImportRequest(archive), BackupRestoreServiceTests.Passphrase.AsMemory(), CancellationToken.None);

        string expected = missingMasterKey
            ? "The master API key is not usable"
            : "The Covenant catalog could not be proved intact";

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(result.Issues, issue => issue.Code == "backup.restore_import_refused"
            && issue.Message.Contains(expected, StringComparison.Ordinal));

        Assert.Equal(sessions, await CountRowsAsync("Sessions"));

        Assert.Equal(entries, await CountRowsAsync("Entries"));

        Assert.Equal(0, await CountRowsAsync("protected_session_transfer_intents"));

        ImportObservedSecrets secrets = (ImportObservedSecrets)scope.ServiceProvider.GetRequiredService<ISecretStore>();

        Assert.Equal(0, secrets.Writes);

        Assert.Null(scope.ServiceProvider.GetRequiredService<CovenantRuntimeGenerationProvider>().Current.ActiveAuthority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enabled_selective_import_never_falls_back_to_plaintext_when_a_required_capability_is_missing(bool missingGate)
    {
        ServiceCollection services = Compose(covenantFeature: true);

        if (missingGate)
        {
            services.RemoveAll<ICovenantOperationGate>();
        }
        else
        {
            services.RemoveAll<StoppedHostCovenantAuthority>();
        }

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        _ = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IBackupRestoreService>());
    }

    private static BackupRestoreRequest ImportRequest(string archive) => new(
        archive,
        BackupRestoreConflictMode.ImportSelectedSessions,
        SessionIds: [BackupRestoreServiceTests.Fixture.CleanSessionA],
        Confirmed: true,
        CreateSafetyBackup: false);

    private static void ObserveImportSecrets(IServiceCollection services, bool refuseMasterPeek)
    {
        ServiceDescriptor original = services.Last(static descriptor => descriptor.ServiceType == typeof(ISecretStore));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(provider => new ImportObservedSecrets(
            (ISecretStore)original.ImplementationFactory!(provider), refuseMasterPeek));
    }

    private sealed class ImportObservedSecrets(ISecretStore inner, bool refuseMasterPeek) : ISecretStore, IDisposable
    {
        public int Writes { get; private set; }

        public int MasterPeeks { get; private set; }

        public Task<string?> GetApiKeyAsync() => inner.GetApiKeyAsync();

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => inner.GetApiKeyReadResultAsync();

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {
            MasterPeeks++;

            return refuseMasterPeek
                ? throw new InvalidOperationException("Covenant-off import must not run the authority verifier master-key peek.")
                : inner.PeekApiKeyReadResultAsync();
        }

        public Task SaveApiKeyAsync(string value)
        {
            Writes++;

            return inner.SaveApiKeyAsync(value);
        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() => inner.GetGrimoireEncryptionSecretAsync();

        public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() => inner.GetGrimoireEncryptionSecretReadResultAsync();

        public Task SaveGrimoireEncryptionSecretAsync(string value)
        {
            Writes++;

            return inner.SaveGrimoireEncryptionSecretAsync(value);
        }

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() => inner.GetFileEncryptionSecretReadResultAsync();

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync() => inner.PeekFileEncryptionSecretReadResultAsync();

        public Task SaveFileEncryptionSecretAsync(string value)
        {
            Writes++;

            return inner.SaveFileEncryptionSecretAsync(value);
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
