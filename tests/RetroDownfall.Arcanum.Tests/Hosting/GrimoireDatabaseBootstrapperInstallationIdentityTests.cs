using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

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

using RetroDownfall.Arcanum.Tests.Security;

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

        // Seeding stays with the replacement restore, the authenticated reset publication, and the
        // offline transition's opening journal.
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
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

        Assert.Contains("installation identity", refused.Message, StringComparison.Ordinal);

        // The credential store answered, so telling the operator to unlock or repair it names a cause
        // that is not there; the value is what is wrong, and the message says so.
        Assert.Contains("not one canonical identity", refused.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("Unlock or repair", refused.Message, StringComparison.Ordinal);

        Assert.Contains("Local Grimoire reinstall", refused.Message, StringComparison.Ordinal);

        // The words are the ones the host's pair recovery uses for a profile that already has a
        // Grimoire, so they are true of both: neither created nor opened beside the item.
        Assert.Contains(
            "so Arcanum will not start over it. It creates no Grimoire beside it, which would replace the "
            + "binding it holds, and it opens no existing Grimoire whose installation that binding pins. "
            + "Nothing was created, and the item is left as it is.",
            refused.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain("will not create a Grimoire that would replace it", refused.Message, StringComparison.Ordinal);

        // The way out is a command, not a hand edit of the credential store: the refusal names it, says
        // what it does, and says when it will not.
        Assert.Contains(
            "Run 'arcanum data repair-installation-identity' to put the damaged item right, which it does only "
            + "when no restore, offline transition or installation reset is unfinished, and start again",
            refused.Message,
            StringComparison.Ordinal);

        AssertNothingOfTheGrimoireExists();
    }

    [Fact]
    public async Task A_refusal_over_a_damaged_identity_holds_for_every_start_and_changes_nothing()
    {
        const string damaged = "c0ffee00-1234-4abc-8def-0123456789ab";

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Set(ArcanumCredentialIdentity.Service, IdentityAccount(), damaged).Status);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

            AssertNothingOfTheGrimoireExists();
        }

        // Never repaired, replaced, or removed: the value is the evidence the refusal is about.
        OsCredentialStoreResult stored = _store.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount());

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        Assert.Equal(damaged, stored.Value);

        Assert.Equal(0, _credentials.SetCount(IdentityAccount()));
    }

    [Fact]
    public async Task Once_the_damaged_identity_is_removed_the_next_start_creates_a_Grimoire_that_mints_its_own()
    {
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Set(
                ArcanumCredentialIdentity.Service,
                IdentityAccount(),
                "c0ffee00-1234-4abc-8def-0123456789ab").Status);

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Delete(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);

        await BootstrapAsync();

        Assert.NotEqual(Guid.Empty, await ReadDatabaseIdentityAsync());

        Assert.Null(ReadCarried());
    }

    /// <summary>
    /// A first start that stopped after it opened the database and before it committed the authority
    /// row leaves a database file with no row, and the next start still takes the carried identity.
    /// </summary>
    /// <remarks>
    /// Opening the install connection creates the file, so any failure between that open and the
    /// commit of the core tier strands one. The identity the next start must take then cannot be read
    /// on the strength of the file's absence, and a start that decided by the file alone would mint a
    /// different identity from the binding and be refused by every later restore, reset, and offline
    /// transition, with nothing able to change either side.
    /// </remarks>
    [Theory]
    [InlineData(UnfinishedStart.HostToolsHardBlock)]
    [InlineData(UnfinishedStart.InstallFailure)]
    [InlineData(UnfinishedStart.Cancellation)]
    [InlineData(UnfinishedStart.SchemaRefusal)]
    public async Task A_database_left_without_an_authority_row_still_takes_the_carried_identity(
        UnfinishedStart cause)
    {
        Guid carried = Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        Carry(carried);

        await LeaveDatabaseWithoutAuthorityRowAsync(cause);

        await BootstrapAsync();

        Assert.Equal(carried, await ReadDatabaseIdentityAsync());

        Assert.Equal(carried, ReadCarried());

        // Read, never written.
        Assert.Equal(0, _credentials.SetCount(IdentityAccount()));
    }

    [Fact]
    public async Task A_database_left_without_an_authority_row_in_a_profile_with_no_identity_mints_and_writes_none()
    {
        await LeaveDatabaseWithoutAuthorityRowAsync(UnfinishedStart.InstallFailure);

        await BootstrapAsync();

        Assert.NotEqual(Guid.Empty, await ReadDatabaseIdentityAsync());

        Assert.Null(ReadCarried());

        Assert.Equal(0, _credentials.SetCount(IdentityAccount()));
    }

    [Fact]
    public async Task A_database_left_without_an_authority_row_is_refused_when_the_carried_identity_cannot_be_read()
    {
        Guid carried = Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        Carry(carried);

        await LeaveDatabaseWithoutAuthorityRowAsync(UnfinishedStart.InstallFailure);

        _credentials.FailAccount(IdentityAccount(), OsCredentialStoreStatus.Unavailable);

        GrimoireDatabaseUnavailableException refused =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

        Assert.Contains("installation identity", refused.Message, StringComparison.Ordinal);

        Assert.Contains("Unlock or repair", refused.Message, StringComparison.Ordinal);

        // Failing closed means no authority row was minted behind the unreadable binding.
        await AssertNoAuthorityRowAsync();

        _credentials.ClearFailure(IdentityAccount());

        await BootstrapAsync();

        Assert.Equal(carried, await ReadDatabaseIdentityAsync());
    }

    [Fact]
    public async Task A_database_left_without_an_authority_row_is_refused_when_the_carried_identity_is_damaged()
    {
        await LeaveDatabaseWithoutAuthorityRowAsync(UnfinishedStart.InstallFailure);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _store.Set(
                ArcanumCredentialIdentity.Service,
                IdentityAccount(),
                "c0ffee00-1234-4abc-8def-0123456789ab").Status);

        GrimoireDatabaseUnavailableException refused =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

        Assert.Contains("not one canonical identity", refused.Message, StringComparison.Ordinal);

        await AssertNoAuthorityRowAsync();
    }

    [Fact]
    public async Task A_database_that_already_has_an_authority_row_keeps_it_whatever_the_profile_now_carries()
    {
        await LeaveDatabaseWithoutAuthorityRowAsync(UnfinishedStart.InstallFailure);

        await BootstrapAsync();

        Guid own = await ReadDatabaseIdentityAsync();

        Carry(Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB"));

        int readsBefore = _credentials.TryGetCount(IdentityAccount());

        await BootstrapAsync();

        Assert.Equal(readsBefore, _credentials.TryGetCount(IdentityAccount()));

        Assert.Equal(own, await ReadDatabaseIdentityAsync());
    }

    /// <summary>
    /// An active installation reset names the installation whose database it resumes, so a database
    /// that is there but holds no authority row for it is as absent as a missing file.
    /// </summary>
    /// <remarks>
    /// The reset seeds the binding from the envelope's own identity, and adoption takes the binding. A
    /// start that adopted here would install a fresh schema carrying exactly the identity the reset is
    /// about to verify, the comparison would pass, and recovery would be published over a database that
    /// holds none of the installation's data. Both ways the file can be present without the row are
    /// covered: emptied to zero bytes, and replaced by a database that is not the Grimoire.
    /// </remarks>
    [Theory]
    [InlineData(RowlessDatabase.ZeroBytes)]
    [InlineData(RowlessDatabase.UnrelatedDatabase)]
    public async Task A_database_without_an_authority_row_is_refused_under_an_active_reset_and_is_left_as_found(
        RowlessDatabase shape)
    {
        await BootstrapAsync();

        Guid envelope = Guid.Parse("81515151-5151-4151-8151-515151515151");

        // What the reset's publication does: the binding names the envelope's installation.
        Carry(envelope);

        await ReplaceDatabaseAsync(shape);

        int readsBefore = _credentials.TryGetCount(IdentityAccount());

        GrimoireDatabaseUnavailableException refused = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(
            () => BootstrapAsync(expectedInstallationId: envelope));

        // An active reset never takes the retained identity: the adopting read belongs to a start that
        // has no expected identity, on either side of the open.
        Assert.Equal(readsBefore, _credentials.TryGetCount(IdentityAccount()));

        Assert.Contains("no authority row", refused.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(
            envelope.ToString("D"),
            refused.Message,
            StringComparison.OrdinalIgnoreCase);

        // Nothing was installed or written: not the row, and not the schema that would hold it.
        Assert.Equal(shape is RowlessDatabase.ZeroBytes ? 0L : 1L, await CountTablesAsync());

        await AssertNoAuthorityRowAsync();

        // The binding is read by the adopting start and never written by any start.
        Assert.Equal(envelope, ReadCarried());

        Assert.Equal(0, _credentials.SetCount(IdentityAccount()));

        // Refused for every start, because the file is still not the database the reset names.
        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(
            () => BootstrapAsync(expectedInstallationId: envelope));

        Assert.Equal(shape is RowlessDatabase.ZeroBytes ? 0L : 1L, await CountTablesAsync());
    }

    /// <summary>
    /// The refusal's message points at the log for the steps, so the log has to give them: put the
    /// original installation back and resume, not start fresh.
    /// </summary>
    [Theory]
    [InlineData(ActiveResetRefusal.DatabaseMissing)]
    [InlineData(ActiveResetRefusal.NoAuthorityRow)]
    public async Task The_refusal_under_an_active_reset_logs_the_recovery_steps_its_message_points_to(
        ActiveResetRefusal refusal)
    {
        if (refusal is ActiveResetRefusal.NoAuthorityRow)
        {
            await BootstrapAsync();

            await ReplaceDatabaseAsync(RowlessDatabase.ZeroBytes);
        }

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        try
        {
            GrimoireDatabaseUnavailableException refused =
                await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync(
                    expectedInstallationId: Guid.Parse("81515151-5151-4151-8151-515151515151")));

            Assert.Contains("See logs for recovery steps", refused.Message, StringComparison.Ordinal);

            // Other tests log through the same static logger while this one holds it, so the event is
            // found by what it says rather than by being the only one.
            string rendered = Assert.Single(
                sink.Messages,
                static message => message.Contains("installation reset is active", StringComparison.Ordinal));

            Assert.Contains("Recovery: stop every Arcanum host and daemon", rendered, StringComparison.Ordinal);

            Assert.Contains("put the whole binding set back", rendered, StringComparison.Ordinal);

            // The same set the start-fresh guidance and the reinstall table list, so a Grimoire put back
            // is never missing its salt, its pending salt, its blobs, the CLI state, or its dedicated secret.
            foreach (string item in (string[])
                     [
                         "arcanum.db",
                         "-wal",
                         "-shm",
                         ".kdf salt",
                         ".kdf.pending",
                         "attachments/",
                         "files/",
                         "cli-context.json",
                         "cli-session.txt",
                         "recent-resources.txt",
                         "grimoire-key.dat",
                         "secret-store directory",
                         "Local Grimoire reinstall",
                     ])
            {
                Assert.Contains(item, rendered, StringComparison.Ordinal);
            }

            Assert.Contains("start the host again so the reset resumes", rendered, StringComparison.Ordinal);

            Assert.Contains("Do not start fresh while the reset is unfinished", rendered, StringComparison.Ordinal);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }
    }

    [Fact]
    public async Task A_database_that_holds_the_authority_row_the_active_reset_names_is_accepted()
    {
        await BootstrapAsync();

        Guid own = await ReadDatabaseIdentityAsync();

        // The refusal is about a missing row, not about an active reset: the same database, with its row
        // intact, is exactly what the recovery host exists to verify.
        await BootstrapAsync(expectedInstallationId: own);

        Assert.Equal(own, await ReadDatabaseIdentityAsync());
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
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

        Assert.Contains("installation identity", refused.Message, StringComparison.Ordinal);

        AssertNothingOfTheGrimoireExists();
    }

    [Fact]
    public async Task A_start_refused_at_the_identity_read_leaves_the_next_start_able_to_take_the_identity()
    {
        Guid carried = Guid.Parse("C0FFEE00-1234-4ABC-8DEF-0123456789AB");

        Carry(carried);

        _credentials.FailAccount(IdentityAccount(), OsCredentialStoreStatus.Unavailable);

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

        // Had the refused start written a salt or opened a connection, the next one would find a
        // database file or a sidecar and take the existing-Grimoire path, which mints no identity.
        AssertNothingOfTheGrimoireExists();

        _credentials.ClearFailure(IdentityAccount());

        await BootstrapAsync();

        Assert.Equal(carried, await ReadDatabaseIdentityAsync());
    }

    /// <summary>How the database file came to be present without the Grimoire's authority row.</summary>
    public enum RowlessDatabase
    {
        ZeroBytes,

        UnrelatedDatabase,
    }

    /// <summary>Which of the two refusals an active reset makes over the database it must verify.</summary>
    public enum ActiveResetRefusal
    {
        DatabaseMissing,

        NoAuthorityRow,
    }

    /// <summary>
    /// Why a first start stopped after it opened the database and before the core tier committed.
    /// </summary>
    public enum UnfinishedStart
    {
        HostToolsHardBlock,

        InstallFailure,

        Cancellation,

        SchemaRefusal,
    }

    /// <summary>
    /// Runs a first start that fails for <paramref name="cause"/> after the database file exists, and
    /// proves it left a database with no authority row behind.
    /// </summary>
    private async Task LeaveDatabaseWithoutAuthorityRowAsync(UnfinishedStart cause)
    {
        switch (cause)
        {
            case UnfinishedStart.HostToolsHardBlock:
                _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync(
                    ScopeFactory(static services =>
                    {
                        HostProcessToolsRuntimePolicy policy = new();

                        services.AddSingleton(policy);

                        services.AddSingleton<IHostProcessToolsRuntimePolicy>(policy);

                        services.AddSingleton<IHostProcessToolsMarkerStore>(
                            new FakeHostProcessToolsMarkerStore
                            {
                                ReadStatusOverride = HostProcessToolsMarkerReadStatus.Malformed,
                            });

                        services.AddSingleton<IHostProcessToolsEnvironmentProbe>(
                            new FakeHostProcessToolsEnvironmentProbe
                            {
                                Edition = ArcanumEdition.Local,
                                EscapeHatchOptIn = false,
                            });
                    })));

                break;

            case UnfinishedStart.InstallFailure:
                _ = await Assert.ThrowsAsync<IOException>(() => BootstrapAsync(
                    ScopeFactory(static services =>
                        services.AddSingleton<GrimoireSchemaInstaller>(
                            static _ => throw new IOException("The disk failed before the core tier committed.")))));

                break;

            case UnfinishedStart.Cancellation:
                using (CancellationTokenSource cancellation = new())
                {
                    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BootstrapAsync(
                        ScopeFactory(services =>
                            services.AddSingleton(sp =>
                            {
                                // The host is stopping as the installer is about to run.
                                cancellation.Cancel();

                                return new GrimoireSchemaInstaller(
                                    sp.GetRequiredService<GrimoireSchemaManifestInspector>(),
                                    sp.GetRequiredService<GrimoireSchemaDataInitializers>(),
                                    sp.GetRequiredService<GrimoireSchemaVersionChainSet>(),
                                    sp.GetRequiredService<TimeProvider>());
                            })),
                        cancellation.Token));
                }

                break;

            case UnfinishedStart.SchemaRefusal:
                // The first start stops, so the database exists with the key it was created under and no
                // schema. An object of the wrong shape is then in the way of the core tier, which the
                // tier refuses to converge over.
                _ = await Assert.ThrowsAsync<IOException>(() => BootstrapAsync(
                    ScopeFactory(static services =>
                        services.AddSingleton<GrimoireSchemaInstaller>(
                            static _ => throw new IOException("The disk failed before the core tier committed.")))));

                await CreateIncompatibleCoreObjectAsync();

                _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() => BootstrapAsync());

                await RemoveIncompatibleCoreObjectAsync();

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, null);
        }

        // Not vacuous: the file the failed start opened is still there, and it holds no authority row.
        Assert.True(File.Exists(_database));

        await AssertNoAuthorityRowAsync();
    }

    private async Task BootstrapAsync(
        IServiceScopeFactory? scopes = null,
        CancellationToken cancellationToken = default,
        Guid? expectedInstallationId = null)
    {
        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_grimoire));

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secrets,
            _passphrase,
            scopes ?? ScopeFactory(),
            _database,
            _grimoire,
            held,
            expectedInstallationId,
            cancellationToken);
    }

    private IServiceScopeFactory ScopeFactory(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(_credentials);

        _ = services.AddGrimoireSchemaInstallation();

        configure?.Invoke(services);

        return services
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private async Task AssertNoAuthorityRowAsync()
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Password = _passphrase.Passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        await using SqliteCommand tables = connection.CreateCommand();

        tables.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'covenant_authority_state';";

        if (Convert.ToInt64(await tables.ExecuteScalarAsync()) == 0)
        {
            return;
        }

        await using SqliteCommand rows = connection.CreateCommand();

        rows.CommandText = "SELECT COUNT(*) FROM covenant_authority_state;";

        Assert.Equal(0L, Convert.ToInt64(await rows.ExecuteScalarAsync()));
    }

    private async Task CreateIncompatibleCoreObjectAsync()
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Password = _passphrase.Passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        await using SqliteCommand create = connection.CreateCommand();

        create.CommandText = "CREATE TABLE \"Entries\" (\"Unrelated\" TEXT);";

        _ = await create.ExecuteNonQueryAsync();
    }

    private async Task RemoveIncompatibleCoreObjectAsync()
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Password = _passphrase.Passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        await using SqliteCommand drop = connection.CreateCommand();

        drop.CommandText = "DROP TABLE IF EXISTS \"Entries\";";

        _ = await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Replaces the Grimoire at the host path with a file that is present and holds no authority row,
    /// keeping the salt sidecar and the dedicated secret that key whatever is created there.
    /// </summary>
    private async Task ReplaceDatabaseAsync(RowlessDatabase shape)
    {
        SqliteConnection.ClearAllPools();

        foreach (string suffix in (string[])["", "-wal", "-shm"])
        {
            File.Delete(_database + suffix);
        }

        if (shape is RowlessDatabase.ZeroBytes)
        {
            File.WriteAllBytes(_database, []);

            return;
        }

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Password = _passphrase.Passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        await using SqliteCommand create = connection.CreateCommand();

        create.CommandText = "CREATE TABLE \"Unrelated\" (\"Value\" TEXT);";

        _ = await create.ExecuteNonQueryAsync();
    }

    private async Task<long> CountTablesAsync()
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _database,
            Password = _passphrase.Passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        await using SqliteCommand tables = connection.CreateCommand();

        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";

        return Convert.ToInt64(await tables.ExecuteScalarAsync());
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

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            if (logEvent.Level != Serilog.Events.LogEventLevel.Fatal)
            {
                return;
            }

            lock (_messages)
            {
                _messages.Add(logEvent.RenderMessage());
            }
        }
    }

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
