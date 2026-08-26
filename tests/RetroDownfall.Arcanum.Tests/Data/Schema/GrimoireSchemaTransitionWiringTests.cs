using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// An engine whose driver nothing runs is an engine that reports a transition forever.
/// </summary>
/// <remarks>
/// These are source-scan rules for the same reason the maintenance-sweep ones are: a suite that
/// composes a coordinator itself proves the algorithm and says nothing about whether anything under
/// <c>src</c> ever calls it. Each needle carries the first argument rather than the method name
/// alone, because a name that also appears on the type's own declaration - or on a wrapper named
/// after the method it drives - lets a severed call satisfy a bare-name search from the wrong side.
/// </remarks>
public sealed class GrimoireSchemaTransitionWiringTests
{

    [Fact]
    public void The_transition_coordinator_is_driven_by_production_code()
    {

        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => !source.Is("GrimoireSchemaTransitionCoordinator.cs"))
                .Where(static source => source.Names(".RunOnceAsync(stoppingToken"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            callers.Length > 0,
            "No production source drives GrimoireSchemaTransitionCoordinator.RunOnceAsync, so a pending "
            + "schema transition would never drain.");

    }

    [Fact]
    public void The_backfill_runner_is_driven_by_the_coordinator()
    {

        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => !source.Is("GrimoireSchemaBackfillRunner.cs"))
                .Where(static source => source.Names("_runner.AdvanceAsync("))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            callers.Length > 0,
            "No production source drives GrimoireSchemaBackfillRunner.AdvanceAsync.");

    }

    [Fact]
    public void The_transition_hosted_service_is_registered_on_the_long_running_host()
    {

        string[] registrations =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names(
                    "AddInstallationResetRecoveryAwareHostedService<GrimoireSchemaTransitionHostedService>()"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            registrations.Length > 0,
            "GrimoireSchemaTransitionHostedService is not registered, so nothing schedules a pass.");

    }

    /// <summary>
    /// The chain set is the installer's only source of what versions exist, and a container missing it
    /// fails to resolve the installer at bootstrap rather than at compile time.
    /// </summary>
    [Fact]
    public void The_shipped_chain_set_is_registered_in_every_composition_root()
    {

        int registrations = ProductionSourceInventory.Sources()
            .Where(static source => source.Is("ServiceCollectionExtensions.cs"))
            .Sum(static source => source.Occurrences("services.AddSingleton(static _ => GrimoireSchemaVersionChains.Default)"));

        Assert.True(
            registrations >= 2,
            $"The shipped schema chain set is registered {registrations} time(s); the host and the CLI "
            + "composition roots both resolve GrimoireSchemaInstaller and both need it.");

    }

}
