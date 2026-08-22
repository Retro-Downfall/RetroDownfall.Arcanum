using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #89 — turning the feature off has to be visible to the next gate read, with no I/O.
/// </summary>
/// <remarks>
/// The publisher is the only thing that connects <c>Arcanum:Features:Covenant</c> to the in-memory
/// availability snapshot every hot-path gate reads. Two properties matter and neither is obvious.
///
/// <para>First, no storage. A disable that had to reach SQLCipher to take effect would be unavailable
/// exactly when an operator most wants it — a degraded or locked database — and "turn it off" would
/// fail closed in the wrong direction.</para>
///
/// <para>Second, serialized callbacks. <c>IOptionsMonitor</c> makes no ordering promise, so two rapid
/// edits can deliver out of order; a publisher that just wrote what its callback saw could leave the
/// installation enabled after the operator's last action was to disable it.</para>
/// </remarks>
public sealed class CovenantFeatureConfigurationPublisherTests
{

    [Fact]
    public void Startup_publishes_the_configured_value_and_advances_the_generation()
    {

        CovenantAvailability availability = new();

        long before = availability.Current.Generation;

        TestOptionsMonitor monitor = new(Settings(covenant: true));

        using CovenantFeatureConfigurationPublisher publisher = new(availability, monitor);

        publisher.Start();

        Assert.True(availability.Current.FeatureEnabled);

        Assert.True(availability.Current.Generation > before);

        Assert.Equal(CovenantHealthTransition.FeatureConfiguration, availability.Current.LastHealthTransition);

    }

    [Fact]
    public void Startup_after_schema_publication_preserves_the_installed_schema_snapshot()
    {

        CovenantAvailability availability = new();

        _ = availability.PublishSchema(
            new GrimoireSchemaInstallResult(
                HealthyTier(GrimoireSchemaTransactionTier.Core, "core-installed"),
                HealthyTier(GrimoireSchemaTransactionTier.CovenantCanonical, "canonical-installed"),
                HealthyTier(GrimoireSchemaTransactionTier.CovenantAccelerator, "accelerator-installed")),
            CovenantHealthTransition.Bootstrap);

        TestOptionsMonitor monitor = new(Settings(covenant: true));

        using CovenantFeatureConfigurationPublisher publisher = new(availability, monitor);

        publisher.Start();

        CovenantAvailabilitySnapshot snapshot = availability.Current;

        Assert.True(snapshot.FeatureEnabled);

        Assert.Equal(CovenantCapabilityState.Healthy, snapshot.Canonical);

        Assert.Equal(7, snapshot.CanonicalSchemaVersion);

        Assert.Equal("canonical-installed", snapshot.CanonicalInstalledFingerprint);

        Assert.Equal(CovenantCapabilityState.Healthy, snapshot.Accelerator);

        Assert.Equal(7, snapshot.AcceleratorSchemaVersion);

        Assert.Equal("accelerator-installed", snapshot.AcceleratorInstalledFingerprint);

        Assert.Equal(CovenantHealthTransition.FeatureConfiguration, snapshot.LastHealthTransition);

    }

    [Fact]
    public void Configuration_change_publishes_feature_state_and_advances_availability_generation()
    {

        CovenantAvailability availability = new();

        TestOptionsMonitor monitor = new(Settings(covenant: false));

        using CovenantFeatureConfigurationPublisher publisher = new(availability, monitor);

        publisher.Start();

        Assert.False(availability.Current.FeatureEnabled);

        long afterStart = availability.Current.Generation;

        monitor.Change(Settings(covenant: true));

        Assert.True(availability.Current.FeatureEnabled);

        Assert.True(availability.Current.Generation > afterStart);

        monitor.Change(Settings(covenant: false));

        Assert.False(availability.Current.FeatureEnabled);

    }

    [Fact]
    public void Disable_between_provider_attempts_is_visible_without_database_or_secret_store_io()
    {

        CovenantAvailability availability = new();

        TestOptionsMonitor monitor = new(Settings(covenant: true));

        using CovenantFeatureConfigurationPublisher publisher = new(availability, monitor);

        publisher.Start();

        Assert.True(availability.Current.FeatureEnabled);

        // No connection source, no secret store, no configuration file. The publisher's whole
        // dependency set is the two constructor arguments, so there is nothing here that could
        // block on storage during a disable.
        monitor.Change(Settings(covenant: false));

        Assert.False(availability.Current.FeatureEnabled);

    }

    [Fact]
    public void A_stale_callback_cannot_overwrite_a_later_value()
    {

        CovenantAvailability availability = new();

        TestOptionsMonitor monitor = new(Settings(covenant: false));

        using CovenantFeatureConfigurationPublisher publisher = new(availability, monitor);

        publisher.Start();

        // The monitor delivers the older value last. The publisher reads the monitor's current value
        // inside its own lock rather than trusting the value the callback carried, so the last
        // observed configuration wins regardless of callback order.
        monitor.ChangeWithStaleDelivery(latest: Settings(covenant: true), delivered: Settings(covenant: false));

        Assert.True(availability.Current.FeatureEnabled);

    }

    [Fact]
    public void Disposal_stops_publishing()
    {

        CovenantAvailability availability = new();

        TestOptionsMonitor monitor = new(Settings(covenant: false));

        CovenantFeatureConfigurationPublisher publisher = new(availability, monitor);

        publisher.Start();

        publisher.Dispose();

        long afterDispose = availability.Current.Generation;

        monitor.Change(Settings(covenant: true));

        Assert.False(availability.Current.FeatureEnabled);

        Assert.Equal(afterDispose, availability.Current.Generation);

    }

    private static ArcanumSettings Settings(bool covenant) =>
        new() { Features = new FeatureSettings { Covenant = covenant } };

    private static GrimoireSchemaTierInstallResult HealthyTier(
        GrimoireSchemaTransactionTier tier,
        string installedFingerprint) =>
        new(
            tier,
            SchemaVersion: 7,
            GrimoireSchemaTierHealth.Healthy,
            SourceDefinitionFingerprint: "source-definition",
            InstalledCatalogFingerprint: installedFingerprint,
            DiagnosticCode: null);

    private sealed class TestOptionsMonitor(ArcanumSettings initial) : IOptionsMonitor<ArcanumSettings>
    {

        private readonly List<Action<ArcanumSettings, string?>> _listeners = [];

        public ArcanumSettings CurrentValue { get; private set; } = initial;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ArcanumSettings, string?> listener)
        {

            _listeners.Add(listener);

            return new Subscription(() => _listeners.Remove(listener));

        }

        public void Change(ArcanumSettings updated)
        {

            CurrentValue = updated;

            foreach (Action<ArcanumSettings, string?> listener in _listeners.ToArray())
            {

                listener(updated, null);

            }

        }

        public void ChangeWithStaleDelivery(ArcanumSettings latest, ArcanumSettings delivered)
        {

            CurrentValue = latest;

            foreach (Action<ArcanumSettings, string?> listener in _listeners.ToArray())
            {

                listener(delivered, null);

            }

        }

        private sealed class Subscription(Action dispose) : IDisposable
        {

            public void Dispose() => dispose();

        }

    }

}
