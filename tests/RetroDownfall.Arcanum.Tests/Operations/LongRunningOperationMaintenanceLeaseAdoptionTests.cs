using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Taking one operation's lease from an owner the installation lock proves is gone.
/// </summary>
/// <remarks>
/// The ordinary acquisition waits for expiry because in the ordinary world an unexpired lease means
/// somebody may still be working. Pre-readiness recovery is not that world: the host holds the
/// installation maintenance lock <c>FileShare.None</c> for its whole lifetime and fails startup
/// without it, so an unexpired lease on this installation is provably a dead process's. What must not
/// change is anything else — a terminal row stays unadoptable, and the flagged states that may be
/// reclaimed are exactly the ones the ordinary path already admits.
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]
public sealed class LongRunningOperationMaintenanceLeaseAdoptionTests : IAsyncLifetime
{

    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly GrimoireFixture _fixture;

    private readonly TempWorkspace _workspace = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumMaintenanceLock? _lock;

    private string _root = string.Empty;

    private LongRunningOperationStore _store = null!;

    public LongRunningOperationMaintenanceLeaseAdoptionTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public async Task InitializeAsync()
    {

        await _workspace.InitializeAsync();

        _root = _workspace.CreateSubdir("lease-adoption");

        _lock = Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(_root));

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        await _db.Database.OpenConnectionAsync(Token);

        _store = new LongRunningOperationStore(_db, TestOrdinaryConnectionFactory.For(_db));

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        _lock?.Dispose();

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        await _workspace.DisposeAsync();

    }

    [SkippableFact]
    public async Task An_unexpired_lease_refuses_the_ordinary_acquisition_and_admits_the_adoption()
    {

        RequireSqlCipher();

        LongRunningOperation crashed = await SeedLeasedAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperationLeaseResult ordinary = await _store.TryAcquireLeaseAsync(
            crashed.Id,
            "recovery-owner",
            now,
            now.AddMinutes(2),
            Token);

        Assert.False(ordinary.Acquired);

        LongRunningOperationLeaseResult adopted = await Adoption().AdoptUnderInstallationLockAsync(
            _lock!,
            _root,
            crashed.Id,
            "recovery-owner",
            now,
            now.AddMinutes(2),
            Token);

        Assert.True(adopted.Acquired);

        Assert.Equal("recovery-owner", adopted.Operation.LeaseOwner);

        Assert.Equal(LongRunningOperationState.Running, adopted.Operation.State);

        // Adoption is one revision, exactly as the ordinary acquisition is. The journal binds itself
        // to a floor rather than an equality precisely so this write is ordinary rather than fatal.
        Assert.Equal(crashed.Revision + 1, adopted.Operation.Revision);

    }

    [SkippableFact]
    public async Task A_terminal_row_stays_unadoptable()
    {

        RequireSqlCipher();

        LongRunningOperation crashed = await SeedLeasedAsync();

        Assert.True(await _store.TryTransitionAsync(
            crashed.Id,
            crashed.Revision,
            "crashed-owner",
            LongRunningOperationState.Completed,
            DateTimeOffset.UtcNow,
            terminalErrorCode: null,
            Token));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperationLeaseResult adopted = await Adoption().AdoptUnderInstallationLockAsync(
            _lock!,
            _root,
            crashed.Id,
            "recovery-owner",
            now,
            now.AddMinutes(2),
            Token);

        Assert.False(adopted.Acquired);

        Assert.Equal(LongRunningOperationState.Completed, adopted.Operation.State);

    }

    [SkippableFact]
    public async Task A_lock_held_for_another_root_refuses()
    {

        RequireSqlCipher();

        LongRunningOperation crashed = await SeedLeasedAsync();

        string elsewhere = _workspace.CreateSubdir("lease-adoption-elsewhere");

        using ArcanumMaintenanceLock foreign = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(elsewhere));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await Adoption().AdoptUnderInstallationLockAsync(
                foreign,
                _root,
                crashed.Id,
                "recovery-owner",
                now,
                now.AddMinutes(2),
                Token));

    }

    private ILongRunningOperationMaintenanceLeaseAdoption Adoption() => _store;

    private async Task<LongRunningOperation> SeedLeasedAsync()
    {

        LongRunningOperation created = await _store.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted Covenant erasure.",
                DateTimeOffset.UtcNow));

        LongRunningOperationLeaseResult leased = await _store.TryAcquireLeaseAsync(
            created.Id,
            "crashed-owner",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(30),
            Token);

        Assert.True(leased.Acquired);

        return leased.Operation;

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

}
