using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class MaintenanceLockTypedCallSiteTests
{

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Commands/InstallationResetApplyBoundary.cs")]
    public void Production_call_sites_consume_the_typed_lock_acquisition_outcome(
        string repositoryRelativePath)
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(repositoryRelativePath));

        Assert.True(
            source.Names("ArcanumMaintenanceLock.AcquireDetailed("),
            $"{repositoryRelativePath} still collapses maintenance-lock contention and unsafe evidence.");

        Assert.False(
            source.Names("ArcanumMaintenanceLock.TryAcquire("),
            $"{repositoryRelativePath} still uses the nullable compatibility wrapper.");

    }

}
