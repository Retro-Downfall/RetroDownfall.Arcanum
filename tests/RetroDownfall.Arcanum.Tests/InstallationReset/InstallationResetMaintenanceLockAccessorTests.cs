using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetMaintenanceLockAccessorTests : IAsyncLifetime
{
    private const string HostedServiceOwner =
        "src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs";

    private const string LoanOwner =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/StoppedHostGrimoireAuthorityIssuer.cs";

    private const string AccessorOwner =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetMaintenanceLockAccessor.cs";

    private const string RecoveryOwner =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/IInstallationResetStartupRecovery.cs";

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void BorrowHeldLock_never_acquires_or_disposes_the_host_owned_lock()
    {
        InstallationResetMaintenanceLockAccessor accessor = new();

        string absentRoot = Path.Combine(_workspace.Root, "not-created");

        Result<ArcanumMaintenanceLock> absent = accessor.BorrowHeldLock(absentRoot);

        Assert.True(absent.IsFailure);

        Assert.False(Directory.Exists(absentRoot));

        Assert.False(File.Exists(ArcanumMaintenanceLock.LockPathFor(absentRoot)));

        string guardedRoot = _workspace.CreateSubdir("arcanum");

        ArcanumMaintenanceLock? hostOwned =
            ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(hostOwned);

        try
        {
            accessor.AttachHostLock(hostOwned, guardedRoot);

            Result<ArcanumMaintenanceLock> borrowed = accessor.BorrowHeldLock(guardedRoot);

            Assert.True(borrowed.IsSuccess);

            Assert.Same(hostOwned, borrowed.Value);

            using ArcanumMaintenanceLock? contenderWhileBorrowed =
                ArcanumMaintenanceLock.TryAcquire(guardedRoot);

            Assert.Null(contenderWhileBorrowed);

            accessor.DetachHostLock(hostOwned);

            using ArcanumMaintenanceLock? contenderWhileDetached =
                ArcanumMaintenanceLock.TryAcquire(guardedRoot);

            Assert.Null(contenderWhileDetached);
        }
        finally
        {
            accessor.DetachHostLock(hostOwned);

            hostOwned.Dispose();
        }

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(reacquired);
    }

    [Fact]
    public void BorrowHeldLock_rejects_a_different_or_disposed_host_lock()
    {
        string guardedRoot = _workspace.CreateSubdir("arcanum");

        string otherRoot = _workspace.CreateSubdir("other");

        InstallationResetMaintenanceLockAccessor accessor = new();

        ArcanumMaintenanceLock? hostOwned =
            ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(hostOwned);

        accessor.AttachHostLock(hostOwned, guardedRoot);

        Assert.True(accessor.BorrowHeldLock(otherRoot).IsFailure);

        accessor.DetachHostLock(hostOwned);

        hostOwned.Dispose();

        Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);
    }

    [Fact]
    public async Task The_issuer_minted_loan_makes_the_held_lock_borrowable_for_the_length_of_the_loan_only()
    {
        string guardedRoot = _workspace.CreateSubdir("arcanum");

        string databasePath = Path.Combine(guardedRoot, "arcanum.db");

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(held);

        StoppedHostGrimoireAuthorityIssuer issuer = new(held, guardedRoot, databasePath);

        InstallationResetMaintenanceLockAccessor accessor = new();

        Result<ArcanumMaintenanceLock> before = accessor.BorrowHeldLock(guardedRoot);

        Assert.True(before.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, before.Error.Code);

        Result<IStoppedHostGrimoireConnectionAuthority> issued = issuer.IssueStoppedHostFactoryErasureLockLoan();

        Assert.True(issued.IsSuccess);

        await using IStoppedHostGrimoireConnectionAuthority authority = issued.Value;

        Result<IAsyncDisposable> lent = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
            authority,
            accessor,
            databasePath);

        Assert.True(lent.IsSuccess);

        Result<ArcanumMaintenanceLock> during = accessor.BorrowHeldLock(guardedRoot);

        Assert.True(during.IsSuccess);

        Assert.Same(held, during.Value);

        await lent.Value.DisposeAsync();

        Result<ArcanumMaintenanceLock> after = accessor.BorrowHeldLock(guardedRoot);

        Assert.True(after.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, after.Error.Code);

        // The loan only borrowed. The lock is the issuer's, and is still held.
        using ArcanumMaintenanceLock? contender = ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.Null(contender);
    }

    [Fact]
    public async Task A_loan_is_refused_beside_an_attached_lock_and_changes_nothing()
    {
        string guardedRoot = _workspace.CreateSubdir("arcanum");

        string databasePath = Path.Combine(guardedRoot, "arcanum.db");

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(held);

        StoppedHostGrimoireAuthorityIssuer issuer = new(held, guardedRoot, databasePath);

        InstallationResetMaintenanceLockAccessor accessor = new();

        // The same lock already attached, as a serving host's own attachment would be: a nested loan
        // would detach what it did not attach.
        accessor.AttachHostLock(held, guardedRoot);

        Result<IStoppedHostGrimoireConnectionAuthority> issued = issuer.IssueStoppedHostFactoryErasureLockLoan();

        Assert.True(issued.IsSuccess);

        await using IStoppedHostGrimoireConnectionAuthority authority = issued.Value;

        Result<IAsyncDisposable> lent = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
            authority,
            accessor,
            databasePath);

        Assert.True(lent.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, lent.Error.Code);

        Result<ArcanumMaintenanceLock> stillAttached = accessor.BorrowHeldLock(guardedRoot);

        Assert.True(stillAttached.IsSuccess);

        Assert.Same(held, stillAttached.Value);

        accessor.DetachHostLock(held);
    }

    [Fact]
    public async Task A_loan_is_one_shot_bound_to_its_database_path_and_refused_over_a_released_lock()
    {
        string guardedRoot = _workspace.CreateSubdir("arcanum");

        string databasePath = Path.Combine(guardedRoot, "arcanum.db");

        ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(held);

        StoppedHostGrimoireAuthorityIssuer issuer = new(held, guardedRoot, databasePath);

        InstallationResetMaintenanceLockAccessor accessor = new();

        // An authority for another database is not an authority for this one.
        await using (IStoppedHostGrimoireConnectionAuthority wrongPath =
            issuer.IssueStoppedHostFactoryErasureLockLoan().Value)
        {
            Result<IAsyncDisposable> refused = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
                wrongPath,
                accessor,
                Path.Combine(guardedRoot, "other.db"));

            Assert.True(refused.IsFailure);

            Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);
        }

        // One authority is one loan.
        await using (IStoppedHostGrimoireConnectionAuthority once =
            issuer.IssueStoppedHostFactoryErasureLockLoan().Value)
        {
            Result<IAsyncDisposable> first = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
                once,
                accessor,
                databasePath);

            Assert.True(first.IsSuccess);

            await first.Value.DisposeAsync();

            Result<IAsyncDisposable> second = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
                once,
                accessor,
                databasePath);

            Assert.True(second.IsFailure);

            Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);
        }

        // An authority issued while the lock was held is refused once it is not.
        IStoppedHostGrimoireConnectionAuthority stale = issuer.IssueStoppedHostFactoryErasureLockLoan().Value;

        held.Dispose();

        Result<IAsyncDisposable> afterRelease = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
            stale,
            accessor,
            databasePath);

        Assert.True(afterRelease.IsFailure);

        Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);

        // And no loan can be issued at all over a lock that is no longer held.
        Assert.True(issuer.IssueStoppedHostFactoryErasureLockLoan().IsFailure);

        await stale.DisposeAsync();
    }

    [Fact]
    public void Only_the_hosted_service_and_the_issuer_minted_loan_attach_and_detach_the_accessor_lock()
    {
        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        ProductionSource accessor = Assert.Single(
            sources,
            source => source.IsExactOwner(AccessorOwner));

        ProductionSource recovery = Assert.Single(
            sources,
            source => source.IsExactOwner(RecoveryOwner));

        Assert.False(accessor.Names("ArcanumMaintenanceLock.TryAcquire"));

        Assert.False(accessor.Names(".Dispose("));

        Assert.False(recovery.Names("ArcanumMaintenanceLock.TryAcquire"));

        Assert.False(recovery.Names(".Dispose("));

        // Two owners, and no third. The serving host attaches the lock it holds for its lifetime. A
        // stopped-host process has no such host, and the one other thing that may attach is the loan its
        // own issuer mints from the lock the issuer was created over, for the length of one nested
        // erasure or one journal-first recovery (a nested type of the issuer, spent by a one-shot
        // authority). An ambient lookup of a held lock must never be enough to start either.
        Assert.All(
            sources.Where(static source => source.Names(".AttachHostLock(")),
            source => Assert.True(
                source.IsExactOwner(HostedServiceOwner) || source.IsExactOwner(LoanOwner),
                source.RelativePath));

        Assert.All(
            sources.Where(static source => source.Names(".DetachHostLock(")),
            source => Assert.True(
                source.IsExactOwner(HostedServiceOwner) || source.IsExactOwner(LoanOwner),
                source.RelativePath));

        ProductionSource loan = Assert.Single(
            sources,
            source => source.IsExactOwner(LoanOwner));

        Assert.True(loan.Names("FactoryErasureLockLoan"));

        Assert.True(loan.Names("InstallationResetFactoryErasure"));
    }
}
