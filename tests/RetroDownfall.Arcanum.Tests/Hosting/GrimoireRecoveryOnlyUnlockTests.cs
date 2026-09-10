using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// The one opener a recovery pass is allowed to reach for before the database is bootstrapped.
/// </summary>
/// <remarks>
/// Every refusal here is a mutation the ordinary bootstrap would happily perform: it creates a
/// database when none is there, upgrades a legacy one, finishes an interrupted rekey, and installs
/// schema. A pass whose whole purpose is to read the evidence of an unfinished transformation must do
/// none of those, because each of them changes the thing the evidence describes.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class GrimoireRecoveryOnlyUnlockTests : IClassFixture<GrimoireFixture>, IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly TempWorkspace _workspace = new();

    public GrimoireRecoveryOnlyUnlockTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync()
    {

        SqliteConnection.ClearAllPools();

        return _workspace.DisposeAsync();

    }

    [SkippableFact]
    public async Task An_existing_keyed_catalog_opens_and_publishes_its_passphrase()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using Harness harness = Create("opens", withDatabase: true);

        Result<GrimoireRecoveryUnlockedCatalog> unlocked = await harness.Unlock
            .OpenExistingAsync(harness.Lock, harness.Root, harness.DatabasePath, Token);

        Assert.True(unlocked.IsSuccess, unlocked.IsFailure ? unlocked.Error.Message : null);

        await using GrimoireRecoveryUnlockedCatalog catalog = unlocked.Value;

        Assert.Equal(System.Data.ConnectionState.Open, catalog.Connection.State);

        Assert.False(string.IsNullOrEmpty(harness.Passphrase.Passphrase));

    }

    [SkippableFact]
    public async Task An_absent_database_refuses_rather_than_being_created()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using Harness harness = Create("absent", withDatabase: false);

        Result<GrimoireRecoveryUnlockedCatalog> unlocked = await harness.Unlock
            .OpenExistingAsync(harness.Lock, harness.Root, harness.DatabasePath, Token);

        Assert.True(unlocked.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, unlocked.Error.Code);

        Assert.False(File.Exists(harness.DatabasePath));

    }

    [SkippableFact]
    public async Task A_database_with_no_sidecar_refuses_rather_than_being_upgraded()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using Harness harness = Create("legacy", withDatabase: true);

        File.Delete(GrimoireKdfSidecarFile.GetSidecarPath(harness.DatabasePath));

        Result<GrimoireRecoveryUnlockedCatalog> unlocked = await harness.Unlock
            .OpenExistingAsync(harness.Lock, harness.Root, harness.DatabasePath, Token);

        Assert.True(unlocked.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, unlocked.Error.Code);

        Assert.False(GrimoireKdfSidecarFile.Exists(harness.DatabasePath));

    }

    [SkippableFact]
    public async Task A_pending_key_derivation_upgrade_refuses_rather_than_being_finished()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using Harness harness = Create("pending", withDatabase: true);

        GrimoireKdfSidecarFile.WritePending(
            harness.DatabasePath,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        Result<GrimoireRecoveryUnlockedCatalog> unlocked = await harness.Unlock
            .OpenExistingAsync(harness.Lock, harness.Root, harness.DatabasePath, Token);

        Assert.True(unlocked.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, unlocked.Error.Code);

        Assert.True(GrimoireKdfSidecarFile.PendingExists(harness.DatabasePath));

    }

    [SkippableFact]
    public async Task A_key_that_does_not_open_the_catalog_refuses()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using Harness harness = Create("wrong-key", withDatabase: true);

        // A sidecar with a different salt derives a different passphrase from the same secret, which
        // is exactly the shape of a database restored beside somebody else's key material.
        GrimoireKdfSidecarFile.Write(
            harness.DatabasePath,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        Result<GrimoireRecoveryUnlockedCatalog> unlocked = await harness.Unlock
            .OpenExistingAsync(harness.Lock, harness.Root, harness.DatabasePath, Token);

        Assert.True(unlocked.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, unlocked.Error.Code);

    }

    [SkippableFact]
    public async Task A_lock_held_for_another_root_refuses()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using Harness harness = Create("foreign-lock", withDatabase: true);

        string elsewhere = _workspace.CreateSubdir("recovery-unlock-elsewhere");

        using ArcanumMaintenanceLock foreign = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(elsewhere));

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await harness.Unlock.OpenExistingAsync(
                foreign,
                harness.Root,
                harness.DatabasePath,
                Token));

    }

    private Harness Create(string name, bool withDatabase)
    {

        string root = _workspace.CreateSubdir("recovery-unlock-" + name);

        ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(root));

        string databasePath = Path.Combine(root, "arcanum.db");

        if (withDatabase)
        {

            string source = _fixture.CopyDatabase();

            File.Copy(source, databasePath, overwrite: true);

            File.Copy(source + ".kdf", databasePath + ".kdf", overwrite: true);

        }

        GrimoireDbPassphraseSource passphrase = new();

        return new Harness(
            held,
            root,
            databasePath,
            passphrase,
            new GrimoireRecoveryOnlyUnlock(
                new TestApiKeySecretStore(GrimoireFixture.TestApiKey),
                passphrase));

    }

    private sealed record Harness(
        ArcanumMaintenanceLock Lock,
        string Root,
        string DatabasePath,
        GrimoireDbPassphraseSource Passphrase,
        GrimoireRecoveryOnlyUnlock Unlock) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

}
