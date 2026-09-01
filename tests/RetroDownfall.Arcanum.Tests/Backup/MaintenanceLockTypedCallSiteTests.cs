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

    [Theory]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs",
        2)]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs",
        1)]
    public void Stopped_host_authority_issuers_are_derived_only_from_explicit_typed_locks(
        string repositoryRelativePath,
        int expectedIssuerConstructions)
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(repositoryRelativePath));

        Assert.Equal(
            expectedIssuerConstructions,
            source.Occurrences("StoppedHostGrimoireAuthorityIssuer issuer = new("));

        Assert.False(
            source.Names("InstallationResetMaintenanceLockAccessor"),
            $"{repositoryRelativePath} must receive the exact held lock explicitly.");

    }

}
