using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetMaintenanceLockAccessorTests : IAsyncLifetime
{

    private const string HostedServiceOwner =
        "src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs";

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
    public void Only_the_hosted_service_attaches_detaches_and_disposes_the_accessor_lock()
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

        Assert.All(
            sources.Where(static source => source.Names(".AttachHostLock(")),
            source => Assert.True(source.IsExactOwner(HostedServiceOwner), source.RelativePath));

        Assert.All(
            sources.Where(static source => source.Names(".DetachHostLock(")),
            source => Assert.True(source.IsExactOwner(HostedServiceOwner), source.RelativePath));

    }

}
