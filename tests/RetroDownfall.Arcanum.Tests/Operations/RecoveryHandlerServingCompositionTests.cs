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
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// The serve host's recovery composition, built the way the host builds it and then resolved:
/// the reconciler must come out of a fresh scope with exactly one handler for each registry kind.
/// </summary>
/// <remarks>
/// Resolving every handler constructs <c>SessionAttachmentStore</c>, whose constructor creates the
/// attachments directory under <see cref="ArcanumPaths"/>. Each test therefore opens an
/// <see cref="ArcanumTestHomeScope"/> before the first path access so the composition never touches the
/// developer's real profile; the scope edits process-wide environment variables, hence the collection.
/// </remarks>
[Collection("ProcessEnvironment")]
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
        using ArcanumTestHomeScope home = new("arcanum-recovery-registry-composition");

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IWeaveService>(static _ => null!);

        builder.Services.AddSingleton<IArcanumIntelligenceProvider>(static _ => null!);

        builder.Services.AddSingleton<IHumanPromptRegistry>(static _ => null!);

        builder.Services.AddSingleton<IModelTokenEstimator>(static _ => null!);

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        builder.Services.AddScoped<UnreachedLongRestMaintenance>();

        builder.Services.AddScoped<ILongRestMaintenance>(static sp => sp.GetRequiredService<UnreachedLongRestMaintenance>());

        builder.Services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestAdmittedMaintenance>(
            static sp => sp.GetRequiredService<UnreachedLongRestMaintenance>());

        builder.Services.AddSingleton(sp => new LongRestConfigurationEpoch(
            sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
            sp.GetRequiredService<CovenantProcessBootIdentity>().BootId));

        builder.Services.AddSingleton<ILongRestConfigurationProvider>(
            static sp => sp.GetRequiredService<LongRestConfigurationEpoch>());

        await AssertOneHandlerPerRegistryKindAsync(
            home,
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
        using ArcanumTestHomeScope home = new("arcanum-recovery-registry-composition");

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        await AssertOneHandlerPerRegistryKindAsync(
            home,
            services,
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
    }

    /// <summary>
    /// The admitted ports are forwarding aliases, not second registrations: inside one scope the hosted worker's port
    /// and the public contract reach the same coordinator and the same maintenance, and a fresh scope builds its own.
    /// </summary>
    [Fact]
    public async Task Serving_api_composition_exposes_the_admitted_ports_as_scoped_aliases_of_one_coordinator_and_one_maintenance()
    {
        using ArcanumTestHomeScope home = new("arcanum-recovery-registry-composition");

        Assert.StartsWith(home.Root, ArcanumPaths.AttachmentsDirectory, StringComparison.Ordinal);

        SqliteNativeRuntime.Instance.Initialize();

        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.IsType<GrimoireDbPassphraseSource>(provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase("recovery-registry-composition-validation");

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        object coordinator = scope.ServiceProvider.GetRequiredService<LongRestSweepCoordinator>();

        Assert.Same(coordinator, scope.ServiceProvider.GetRequiredService<ILongRestSweepCoordinator>());

        Assert.Same(coordinator, scope.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>());

        object maintenance = scope.ServiceProvider.GetRequiredService<LongRestMaintenance>();

        Assert.Same(maintenance, scope.ServiceProvider.GetRequiredService<ILongRestMaintenance>());

        Assert.Same(maintenance, scope.ServiceProvider.GetRequiredService<ILongRestAdmittedMaintenance>());

        await using AsyncServiceScope other = provider.CreateAsyncScope();

        Assert.NotSame(coordinator, other.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>());

        Assert.NotSame(maintenance, other.ServiceProvider.GetRequiredService<ILongRestAdmittedMaintenance>());

        // The holder is gone from the composition: nothing can be asked to bind a lease.
        Assert.DoesNotContain(services, static descriptor => descriptor.ServiceType.Name == "LongRestWorkAdmission");
    }

    private static async Task AssertOneHandlerPerRegistryKindAsync(
        ArcanumTestHomeScope home,
        IServiceCollection services,
        ServiceProviderOptions options)
    {
        // Requiring the scope as a parameter keeps a future test from resolving handlers against the
        // developer's real profile; this pins that the redirect is the one in force.
        Assert.StartsWith(home.Root, ArcanumPaths.AttachmentsDirectory, StringComparison.Ordinal);

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
