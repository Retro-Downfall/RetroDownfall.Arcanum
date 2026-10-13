using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class HostedRuntimeRegistrationTests
{
    internal static readonly string[] ExpectedHostedServices =
    [
        "GrimoireDatabaseHostedService",
        "CovenantFeatureConfigurationPublisher",
        "PidFileService",
        "FileEncryptionKeyBootstrapHostedService",
        "LongRunningOperationStartupHostedService",
        "SessionAttachmentPendingGcHostedService",
        "CovenantMaintenanceHostedService",
        "GrimoireSchemaTransitionHostedService",
        "EntryWeavingService",
        "SessionAttachmentIndexingService",
        "WorkspaceIndexingService",
        "SagaExtractionService",
        "TapestryWeavingService",
        "ArcanumSettingsClampStartupLogger",
        "ArcanumSecurityStartupChecks",
        "DataRetentionSweepHostedService",
        "A2ASendingLeaseRenewer",
        "Loremaster",
        "LongRestSweepHostedService",
        "ApprenticeService",
        "McpServerBootstrapHostedService",
        "ProviderHealthProbeService",
        "UnseenServantService",
        "BatchProcessingService",
    ];

    [Fact]
    public void Runtime_descriptors_match_the_source_inventory_vocabulary()
    {
        ServiceCollection services = [];

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        ServiceDescriptor[] hosted = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToArray();

        // Count every application/factory descriptor and permit only this known framework entry.
        Assert.Single(hosted, static descriptor =>
            descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService"
            && descriptor.ImplementationType.Assembly.GetName().Name == "Microsoft.AspNetCore.DataProtection");

        Assert.Equal(ExpectedHostedServices.Length + 1, hosted.Length);
    }

    [Fact]
    public void The_Long_Rest_worker_is_registered_after_the_readiness_pass_and_the_host_starts_services_one_at_a_time()
    {
        ServiceCollection services = [];

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        string[] order = [.. services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(static descriptor => descriptor.ImplementationFactory?.GetType().GenericTypeArguments is [_, { IsGenericType: true } wrapper]
                ? wrapper.GenericTypeArguments[0].Name : descriptor.ImplementationFactory?.GetType().GenericTypeArguments.LastOrDefault()?.Name ?? string.Empty)];

        int readiness = Array.IndexOf(order, "LongRunningOperationStartupHostedService");

        int worker = Array.IndexOf(order, "LongRestSweepHostedService");

        Assert.True(readiness >= 0, "The readiness pass is not registered as a hosted service.");

        Assert.True(worker > readiness, $"The Long Rest worker ({worker}) must be registered after the readiness pass ({readiness}).");

        // Hosted services start in registration order and one at a time, so the readiness pass has finished (or has
        // continued in the background past its budget) before the worker can send. A concurrent start would break that.
        Assert.False(new HostOptions().ServicesStartConcurrently);

        Assert.DoesNotContain(ProductionSourceInventory.Sources(), static source => source.Names("ServicesStartConcurrently"));
    }
}
