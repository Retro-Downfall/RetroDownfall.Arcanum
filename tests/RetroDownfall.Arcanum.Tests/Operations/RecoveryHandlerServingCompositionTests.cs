using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// The serve host's recovery composition, built the way the host builds it and then resolved:
/// the reconciler must come out of a fresh scope with exactly one handler for each registry kind.
/// </summary>
public sealed class RecoveryHandlerServingCompositionTests
{
    /// <summary>
    /// The host's Infrastructure composition with every descriptor validated on build and scopes
    /// validated. The inference ports and the API-owned Long Rest ports sit outside this container, so
    /// they are supplied exactly as the Covenant full-host composition fixture supplies them.
    /// </summary>
    [Fact]
    public async Task Serving_infrastructure_composition_validates_on_build_and_resolves_one_handler_per_registry_kind()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IWeaveService>(static _ => null!);

        builder.Services.AddSingleton<IArcanumIntelligenceProvider>(static _ => null!);

        builder.Services.AddSingleton<IHumanPromptRegistry>(static _ => null!);

        builder.Services.AddSingleton<IModelTokenEstimator>(static _ => null!);

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        builder.Services.AddScoped<ILongRestMaintenance, UnreachedLongRestMaintenance>();

        builder.Services.AddSingleton(sp => new LongRestConfigurationEpoch(
            sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
            sp.GetRequiredService<CovenantProcessBootIdentity>().BootId));

        builder.Services.AddSingleton<ILongRestConfigurationProvider>(
            static sp => sp.GetRequiredService<LongRestConfigurationEpoch>());

        await AssertOneHandlerPerRegistryKindAsync(
            builder.Services,
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }

    /// <summary>
    /// The full serve composition, API layer included. Its singleton graph is not buildable outside a
    /// web host, so it is not validated on build here; scopes are validated, and the reconciler is
    /// resolved against the real API-owned Long Rest and inference registrations.
    /// </summary>
    [Fact]
    public async Task Serving_api_composition_resolves_one_handler_per_registry_kind_with_scopes_validated()
    {
        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        await AssertOneHandlerPerRegistryKindAsync(
            services,
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
    }

    private static async Task AssertOneHandlerPerRegistryKindAsync(
        IServiceCollection services,
        ServiceProviderOptions options)
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using ServiceProvider provider = services.BuildServiceProvider(options);

        Assert.IsType<GrimoireDbPassphraseSource>(provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase("recovery-registry-composition-validation");

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        // The factory builds the kind dictionary eagerly, so a duplicate kind throws right here.
        LongRunningOperationReconciler reconciler = scope.ServiceProvider
            .GetRequiredService<LongRunningOperationReconciler>();

        Assert.Empty(reconciler.MissingHandlerKinds);

        ILongRunningOperationRecoveryHandler[] handlers =
        [
            .. scope.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>(),
        ];

        string[] kinds =
        [
            .. handlers.Select(static handler => handler.Kind).Order(StringComparer.Ordinal),
        ];

        string[] registryKinds =
        [
            .. LongRunningOperationRecoveryRegistry.Descriptors.Keys.Order(StringComparer.Ordinal),
        ];

        Assert.Equal(registryKinds, kinds);

        // A fresh scope builds its own instances, which is what each recovered operation relies on.
        await using AsyncServiceScope other = provider.CreateAsyncScope();

        ILongRunningOperationRecoveryHandler[] otherHandlers =
        [
            .. other.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>(),
        ];

        string[] otherKinds =
        [
            .. otherHandlers.Select(static handler => handler.Kind).Order(StringComparer.Ordinal),
        ];

        Assert.Equal(registryKinds, otherKinds);

        Assert.DoesNotContain(
            otherHandlers,
            candidate => handlers.Any(handler => ReferenceEquals(handler, candidate)));

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler));
    }
}
