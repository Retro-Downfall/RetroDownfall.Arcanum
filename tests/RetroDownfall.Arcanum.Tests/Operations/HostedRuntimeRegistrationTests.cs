using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;

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
}
