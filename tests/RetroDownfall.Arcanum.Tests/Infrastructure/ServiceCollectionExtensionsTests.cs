using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Infrastructure;

/// <summary>
/// Composition entry points share one container in different combinations: the host composes
/// <see cref="ServiceCollectionExtensions.AddArcanumInfrastructure(IServiceCollection, IConfiguration)"/>,
/// Compendium composes configuration presets and then the secret store, and the CLI composes its client
/// stack and then presets. A plain <c>AddSingleton</c> that runs after another entry point's
/// <c>TryAddSingleton</c> leaves two descriptors for one service, so the second silently wins and the
/// first becomes dead registration.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    /// <summary>
    /// Service types that are collections by design: each registration contributes one member that the
    /// consumer enumerates.
    /// </summary>
    private static readonly HashSet<Type> CollectionServiceTypes =
    [
        typeof(IWebResearchProvider),
        typeof(IGrimoireSchemaDataInitializer),
    ];

    [Fact]
    public void AddArcanumInfrastructure_registers_each_singleton_once()
    {
        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        AssertEachSingletonRegisteredOnce(services);
    }

    [Fact]
    public void Presets_then_secret_store_registers_each_singleton_once()
    {
        ServiceCollection services = [];

        services.AddArcanumConfigurationPresets();

        services.AddArcanumSecretStore();

        AssertEachSingletonRegisteredOnce(services);
    }

    [Fact]
    public void Presets_then_infrastructure_registers_each_singleton_once()
    {
        ServiceCollection services = [];

        services.AddArcanumConfigurationPresets();

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        AssertEachSingletonRegisteredOnce(services);
    }

    [Fact]
    public void Cli_client_stack_then_presets_registers_each_singleton_once()
    {
        ServiceCollection services = [];

        services.AddArcanumCliClientStack();

        services.AddArcanumConfigurationPresets();

        AssertEachSingletonRegisteredOnce(services);
    }

    /// <summary>
    /// The CLI container reaches the reconciler for one purpose: stopped-host bootstrap settles the one
    /// operation its authenticated journal names (<c>SettleExactlyAsync</c>). It is composed without the
    /// generic-discovery and classified-lease ports on purpose, so a generic pass attempted from the CLI
    /// fails closed instead of scanning an installation the CLI does not own. The registration is kept
    /// because a CLI path consumes it; this pins the posture so it is not mistaken for a half-wired
    /// reconciler that "can never reconcile".
    /// </summary>
    [Fact]
    public void CliStack_registers_the_reconciler_for_exact_settlement_without_generic_discovery()
    {
        ServiceCollection services = [];

        services.AddArcanumGrimoireForCli();

        ServiceDescriptor reconciler = Assert.Single(
            services,
            static descriptor => descriptor.ServiceType == typeof(LongRunningOperationReconciler));

        Assert.Equal(ServiceLifetime.Scoped, reconciler.Lifetime);

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(ILongRunningOperationGenericRecoveryDiscovery));

        Assert.DoesNotContain(
            services,
            static descriptor =>
                descriptor.ServiceType == typeof(ILongRunningOperationClassifiedRecoveryLeaseAcquisition));
    }

    private static void AssertEachSingletonRegisteredOnce(IServiceCollection services)
    {
        string[] duplicated =
        [
            .. services
                .Where(static descriptor =>
                    descriptor.Lifetime == ServiceLifetime.Singleton
                    && !descriptor.IsKeyedService
                    && descriptor.ServiceType.FullName?.StartsWith("RetroDownfall.", StringComparison.Ordinal) == true
                    && !CollectionServiceTypes.Contains(descriptor.ServiceType))
                .GroupBy(static descriptor => descriptor.ServiceType)
                .Where(static group => group.Count() > 1)
                .Select(static group => $"{group.Key.FullName} x{group.Count()}")
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(duplicated.Length == 0, string.Join('\n', duplicated));
    }
}
