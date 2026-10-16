using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// Which installation identity a Grimoire takes when a start brings it into being.
/// </summary>
/// <remarks>
/// The profile's pre-database installation identity is retained by every cleanup short of an attested
/// full reset, so a database created afterwards finds a profile that already names an installation.
/// These cases pin the three things that make taking it safe: it is read only by the start that
/// creates the database, only under the installation lock, and never written.
/// </remarks>
[Collection(HostedServiceLifetimeCollection.Name)]
public sealed class GrimoireDatabaseBootstrapperInstallationIdentityTests : IDisposable
{
    private readonly string _grimoire = Path.Combine(
        Path.GetTempPath(),
        "arcanum-tests",
        $"bootstrap-identity-{Guid.NewGuid():N}");

    private readonly string _database;

    private readonly InMemoryOsCredentialStore _store = new();

    private readonly SecretAccessRecordingCredentialStore _credentials;

    private readonly GrimoireDbPassphraseSource _passphrase = new();

    private readonly IdentitySecretStore _secrets = new();

    public GrimoireDatabaseBootstrapperInstallationIdentityTests()
    {
        SqliteNativeRuntime.Instance.Initialize();

        _database = Path.Combine(_grimoire, "grimoire.db");

        _credentials = new SecretAccessRecordingCredentialStore(_store);

        _secrets.SetApiKey("installation-identity-api-key");

        Directory.CreateDirectory(_grimoire);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_grimoire))
            {
                Directory.Delete(_grimoire, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of an isolated test root.
        }
    }

    [Fact]
    public async Task A_new_database_takes_the_installation_identity_the_profile_already_carries()
    {
        Guid carried = Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        Carry(carried);

        await BootstrapAsync();

        Assert.Equal(carried, await ReadDatabaseIdentityAsync());

        Assert.Equal(carried, ReadCarried());
    }

    [Fact]
    public async Task A_new_database_in_a_profile_that_carries_no_identity_mints_one_and_writes_none()
    {
        await BootstrapAsync();

        Assert.NotEqual(Guid.Empty, await ReadDatabaseIdentityAsync());

        // Seeding stays with the replacement restore and the authenticated reset publication.
        Assert.Null(ReadCarried());

        Assert.Equal(0, _credentials.SetCount(IdentityAccount()));
    }

    [Fact]
    public async Task A_new_database_mints_a_fresh_identity_once_the_binding_has_gone_with_the_old_one()
    {
        Guid carried = Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        Carry(carried);

        await BootstrapAsync();

        Guid adopted = await ReadDatabaseIdentityAsync();

        DeleteDatabase();

        // What the attested ending does: the binding goes with the database, so nothing is left to take.
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Delete(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);

        await BootstrapAsync();

        Assert.Equal(carried, adopted);

        Guid regenerated = await ReadDatabaseIdentityAsync();

        Assert.NotEqual(carried, regenerated);
    }

    [Fact]
    public async Task Starting_an_existing_database_never_reads_the_installation_identity()
    {
        await BootstrapAsync();

        Guid own = await ReadDatabaseIdentityAsync();

        // A different identity appears in the profile after the database exists. The row is the
        // database's own, so an ordinary start has nothing to take and nothing to read.
        Carry(Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB"));

        int readsBefore = _credentials.TryGetCount(IdentityAccount());

        await BootstrapAsync();

        Assert.Equal(readsBefore, _credentials.TryGetCount(IdentityAccount()));

        Assert.Equal(own, await ReadDatabaseIdentityAsync());
    }

    [Fact]
    public async Task A_start_with_no_installation_lock_never_reads_the_installation_identity()
    {
        Carry(Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB"));

        int readsBefore = _credentials.TryGetCount(IdentityAccount());

        await GrimoireDatabaseBootstrapper.EnsureInitializedWithoutInstallationLockForTestsAsync(
            _secrets,
            _passphrase,
            ScopeFactory(),
            _database,
            _grimoire,
            CancellationToken.None);

        Assert.Equal(readsBefore, _credentials.TryGetCount(IdentityAccount()));

        Assert.NotEqual(
            Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB"),
            await ReadDatabaseIdentityAsync());
    }

    [Fact]
    public async Task A_new_database_is_refused_when_the_carried_identity_is_not_one_canonical_identity()
    {
        // Lowercase is a different spelling of the same value, and one identity has one stored form.
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Set(
                ArcanumCredentialIdentity.Service,
                IdentityAccount(),
                "c0ffee00-1234-4abc-8def-0123456789ab").Status);

        GrimoireDatabaseUnavailableException refused =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(BootstrapAsync);

        Assert.Contains("installation identity", refused.Message, StringComparison.Ordinal);

        AssertNothingOfTheGrimoireExists();
    }

    [Fact]
    public void An_adopted_identity_is_spelled_as_the_row_spells_every_identity_and_is_never_empty()
    {
        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_grimoire));

        Guid adopted = Guid.Parse("c0ffee00-1234-4abc-8def-0123456789ab");

        GrimoireSchemaInitializationContext context = new CovenantAuthorityBootstrapper()
            .PrepareUnderInstallationLock(
                held,
                _grimoire,
                "installation-identity-api-key",
                DateTimeOffset.UtcNow,
                adopted);

        Assert.Equal("C0FFEE00-1234-4ABC-8DEF-0123456789AB", context.InstallationIdentity);

        _ = Assert.Throws<ArgumentException>(() => new CovenantAuthorityBootstrapper()
            .PrepareUnderInstallationLock(
                held,
                _grimoire,
                "installation-identity-api-key",
                DateTimeOffset.UtcNow,
                Guid.Empty));

        // With nothing to adopt the identity is minted, and each preparation mints its own.
        string first = new CovenantAuthorityBootstrapper()
            .PrepareUnderInstallationLock(held, _grimoire, "installation-identity-api-key", DateTimeOffset.UtcNow)
            .InstallationIdentity;

        string second = new CovenantAuthorityBootstrapper()
            .PrepareUnderInstallationLock(held, _grimoire, "installation-identity-api-key", DateTimeOffset.UtcNow)
            .InstallationIdentity;

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(OsCredentialStoreStatus.Unavailable)]
    [InlineData(OsCredentialStoreStatus.Failed)]
    public async Task A_new_database_is_refused_when_the_carried_identity_cannot_be_read(
        OsCredentialStoreStatus status)
    {
        // Only the identity account fails, so this is the new read and not the recovery passes that
        // run before it and have always stopped a start over an unreadable credential store.
        _credentials.FailAccount(IdentityAccount(), status);

        GrimoireDatabaseUnavailableException refused =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(BootstrapAsync);

        Assert.Contains("installation identity", refused.Message, StringComparison.Ordinal);

        AssertNothingOfTheGrimoireExists();
    }

    [Fact]
    public async Task A_start_refused_at_the_identity_read_leaves_the_next_start_able_to_take_the_identity()
    {
        Guid carried = Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        Carry(carried);

        _credentials.FailAccount(IdentityAccount(), OsCredentialStoreStatus.Unavailable);

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(BootstrapAsync);

        // Had the refused start written a salt or opened a connection, the next one would find a
        // database file or a sidecar and take the existing-Grimoire path, which mints no identity.
        AssertNothingOfTheGrimoireExists();

        _credentials.ClearFailure(IdentityAccount());

        await BootstrapAsync();

        Assert.Equal(carried, await ReadDatabaseIdentityAsync());
    }

    private async Task BootstrapAsync()
    {
        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_grimoire));

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secrets,
            _passphrase,
            ScopeFactory(),
            _database,
            _grimoire,
            held,
            expectedInstallationId: null,
            CancellationToken.None);
    }

    private IServiceScopeFactory ScopeFactory()
    {
        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(_credentials);

        _ = services.AddGrimoireSchemaInstallation();

        return services
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private void AssertNothingOfTheGrimoireExists()
    {
        Assert.False(File.Exists(_database));

        Assert.False(File.Exists(_database + ".kdf"));

        Assert.Null(_secrets.DedicatedSecret);
    }

    private async Task<Guid> ReadDatabaseIdentityAsync()
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Password = _passphrase.Passphrase,
        }.ToString());

        await connection.OpenAsync();

        Result<Guid> identity = await InstallationResetDatabaseIdentityReader.ReadOpenConnectionAsync(
            connection,
            CancellationToken.None);

        Assert.True(identity.IsSuccess, identity.IsFailure ? identity.Error.Message : string.Empty);

        return identity.Value;
    }

    private void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();

        foreach (string suffix in (string[])["", "-wal", "-shm", ".kdf"])
        {
            File.Delete(_database + suffix);
        }
    }

    private void Carry(Guid installationId) =>
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Set(
                ArcanumCredentialIdentity.Service,
                IdentityAccount(),
                installationId.ToString("D").ToUpperInvariant()).Status);

    private Guid? ReadCarried()
    {
        OsCredentialStoreResult result = _store.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount());

        return result.Status is OsCredentialStoreStatus.Ok
            ? Guid.Parse(result.Value!)
            : null;
    }

    private string IdentityAccount() =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_grimoire).Value.AccountSuffix);

    private sealed class IdentitySecretStore : ISecretStore
    {
        public string? ApiKey { get; private set; }

        public string? DedicatedSecret { get; private set; }

        public void SetApiKey(string apiKey) => ApiKey = apiKey;

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                ApiKey is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(ApiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {
            ApiKey = apiKey;

            return Task.CompletedTask;
        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult(DedicatedSecret);

        public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() =>
            Task.FromResult(
                DedicatedSecret is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(DedicatedSecret));

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {
            DedicatedSecret = encryptionSecret;

            return Task.CompletedTask;
        }
    }
}
