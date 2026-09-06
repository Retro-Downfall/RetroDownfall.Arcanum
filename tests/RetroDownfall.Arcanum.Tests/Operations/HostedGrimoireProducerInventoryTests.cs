using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Infrastructure.Data;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class HostedGrimoireProducerInventoryTests(ITestOutputHelper output)
{
    [Fact]
    public void SensitiveReadPropertySetterCannotBeClassifiedAsRead()
    {
        Assert.Contains(Discover(FixtureSource("new System.IO.FileInfo(\"path\").LastWriteTimeUtc = DateTime.UtcNow;")).Diagnostics, static d => d.Code == "HOSTED_SITE_UNCLASSIFIED" && d.Detail.Contains("LastWriteTimeUtc", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongNamespaceWrapperIsRejected()
    {
        string source = RegistrationSource("RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInstallationResetRecoveryAwareHostedService<Worker>(services);") + """
            namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection
            {
                public static class ServiceCollectionExtensions
                {
                    public static void AddInstallationResetRecoveryAwareHostedService<TService>(IServiceCollection services) where TService : class, IHostedService
                    {
                        services.AddHostedService(sp => new InstallationResetRecoveryAwareHostedService<TService>());
                    }
                }
                public class InstallationResetRecoveryAwareHostedService<T> : IHostedService
                {
                    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
                    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
                }
            }
            """;

        Assert.Contains(HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(source)]).Diagnostics, static d => d.Code == "HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED");
    }

    [Theory]
    [InlineData("System.IO.File.Delete(\"path\"); if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group;", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using (group) { } System.IO.File.Delete(\"path\");", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; held.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; System.IO.File.Delete(\"path\");", true)]
    public void OrdinaryEffectsMustBeInsideTheRetainedGroup(string body, bool protectedEffect)
    {
        string source = FixtureSource("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = null!; " + body, "namespace RetroDownfall.Arcanum.Infrastructure.Data { public interface IGrimoireWorkLease { bool TryBeginExternalEffectGroup(out System.IDisposable group); } }");

        CSharpCompilation compilation = Compile(source);

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [operation])], []);

        Assert.Equal(protectedEffect, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void NameofIsNotAnUnresolvedCallTarget()
    {
        Assert.Empty(Discover(FixtureSource("_ = nameof(Worker);")).Diagnostics);
    }

    [Fact]
    public void EndpointThroughSingletonInterfaceHasAnExternalRoot()
    {
        string source = FixtureSource("")
            .Replace("public class Worker : IHostedService", "public class Worker : IHostedService, IQueue", StringComparison.Ordinal)
            .Replace("public Task StartAsync", "public void QueueIndexNow() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IQueue>(sp => sp.GetRequiredService<Worker>());", StringComparison.Ordinal)
            + "interface IQueue { void QueueIndexNow(); } class Endpoint { void Invoke(IQueue worker) { worker.QueueIndexNow(); } }";

        Assert.Contains(Discover(source).Diagnostics, static item => item.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");
    }

    [Fact]
    public void DeclaredExternalRootIsResolvedAndNotReportedAsUncatalogued()
    {
        string source = FixtureSource("")
            .Replace("public Task StartAsync", "public void QueueIndexNow() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Invoke(Worker worker) { worker.QueueIndexNow(); } }";

        CSharpCompilation compilation = Compile(source);

        HostedProducerOperationEntry root = new("Worker.QueueIndexNow", "src/Fixture.cs", "Worker", "QueueIndexNow", HostedProducerAuthorityKind.FiniteRequest, null, "Worker.QueueIndexNow: admitted finite request", []);

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [root])], []);

        Assert.DoesNotContain(discovery.Diagnostics, static d => d.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED" || d.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.Contains(discovery.Items, static site => site.Member == "QueueIndexNow");
    }

    [Fact]
    public void EveryApplicationHostedServiceHasExactlyOneEntry()
    {
        HostedProducerInventoryValidation validation =
            HostedGrimoireProducerInventory.ValidateProductionTree();

        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code.StartsWith("HOSTED_SERVICE_", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDiscoveredProducerSiteIsCataloguedExactlyOnce()
    {
        HostedProducerInventoryValidation validation =
            HostedGrimoireProducerInventory.ValidateProductionTree();

        foreach (IGrouping<string, HostedProducerInventoryDiagnostic> group in validation.Diagnostics.GroupBy(static diagnostic => diagnostic.Code))
        {
            output.WriteLine($"{group.Key}: {group.Count()}");

            foreach (HostedProducerInventoryDiagnostic diagnostic in group.Take(30))
            {
                output.WriteLine($"{diagnostic.Identity}: {diagnostic.Detail}");
            }
        }

        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code.StartsWith("HOSTED_SITE_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryScopeRequiresItsDeclaredWorkFrontier(bool frontier)
    {
        HostedProducerSite scope = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.ScopeCreation, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope");

        HostedProducerSite lease = scope with { Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease", OperationId = "Worker.StartAsync/workKind=WorkspaceIndexing" };

        HostedProducerSite[] sites = frontier ? [scope, lease] : [scope];

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, sites);

        HostedProducerInventoryValidation result = HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new(sites, []));

        Assert.Equal(frontier, result.IsValid);

        if (!frontier)
        {
            Assert.Contains(result.Diagnostics, static d => d.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
        }
    }

    [Theory]
    [InlineData("lease.TryBeginExternalEffectGroup(out var group);", false)]
    [InlineData("if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask;", true)]
    public void OnlyGuardedBoundEffectAdmissionIsFrontier(string admission, bool expected)
    {
        string source = FixtureSource("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = null!; " + admission, "namespace RetroDownfall.Arcanum.Infrastructure.Data { public interface IGrimoireWorkLease { bool TryBeginExternalEffectGroup(out object group); } }");

        Assert.Equal(expected, Discover(source).Items.Any(static site => site.Kind == HostedProducerSiteKind.EffectFrontier));
    }

    [Fact]
    public void OverloadedStartIsExternalOperationRatherThanHostLifecycle()
    {
        string source = FixtureSource("").Replace("public Task StartAsync", "public void StartAsync(int job) { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Run(Worker worker) { worker.StartAsync(3); } }";

        Assert.Contains(Discover(source).Diagnostics, static d => d.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");
    }

    [Fact]
    public void TwoBranchesCallingSameHelperRetainIndependentContexts()
    {
        string source = FixtureSource("if (DateTime.Now.Ticks > 0) Helper.Read(); else Helper.Read();", "static class Helper { public static void Read() { System.IO.File.Exists(\"path\"); } }");

        Assert.Equal(2, Discover(source).Items.Count(static site => site.Callee == "System.IO.File.Exists"));
    }

    private static readonly string[] ExpectedHostedServices =
    [
        "GrimoireDatabaseHostedService",
        "CovenantFeatureConfigurationPublisher",
        "PidFileService",
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
        "FileEncryptionKeyBootstrapHostedService",
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
    public void ProductionRegistrationsMatchRuntimeDescriptorsAndClosedVocabulary()
    {
        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(HostedGrimoireProducerInventory.ProductionCompilations);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(ExpectedHostedServices.Order(StringComparer.Ordinal), result.Items.Order(StringComparer.Ordinal));

        ServiceCollection services = [];

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        ServiceDescriptor[] hosted = services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();

        // AddDataProtection contributes this one framework-owned descriptor. No application descriptor,
        // factory descriptor, or unknown framework descriptor is excluded from the count.
        Assert.Single(hosted, static descriptor => descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService" && descriptor.ImplementationType.Assembly.GetName().Name == "Microsoft.AspNetCore.DataProtection");

        Assert.Equal(result.Items.Count + 1, hosted.Length);
    }

    [Fact]
    public void ProductionIncludesApiAndCliRoots()
    {
        Assert.Equal(["RetroDownfall.Arcanum.Infrastructure", "RetroDownfall.Arcanum.Api", "RetroDownfall.Arcanum.Cli"], HostedGrimoireProducerInventory.ProductionCompilations.Select(static compilation => compilation.AssemblyName));

        Assert.Contains(HostedGrimoireProducerInventory.NonHostedCatalog, static chain => chain.EnclosingType == "RetroDownfall.Arcanum.Cli.Commands.BackupCommands" && chain.Member == "Create");
    }

    [Fact]
    public void TwoCallsInOneMemberHaveDistinctSiteIdentities()
    {
        HostedProducerSite[] sites = Discover(FixtureSource("System.IO.File.Exists(\"a\"); System.IO.File.Exists(\"b\");")).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Equal(2, sites.Length);
    }

    [Fact]
    public void PublicationIncludesWriterCallsAndImplicitDisposal()
    {
        string source = FixtureSource("using var writer = new System.IO.StreamWriter(System.IO.Stream.Null); writer.Write(\"body\"); writer.Flush();");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.StreamWriter.Write" && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.StreamWriter.Flush" && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.StreamWriter.Dispose" && site.Kind == HostedProducerSiteKind.FileSystemEffect);
    }

    [Fact]
    public void ConcreteProviderCallNormalizesToItsInterfaceSlot()
    {
        string source = FixtureSource("new RetroDownfall.Arcanum.Core.Weave.Weave().EmbedAsync();", "namespace RetroDownfall.Arcanum.Core.Weave { interface IWeaveService { void EmbedAsync(); } class Weave : IWeaveService { public void EmbedAsync() {} } }");

        Assert.Contains(Discover(source).Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedAsync");
    }

    [Fact]
    public void MarkedConnectionRouteIsJoinedThroughItsInterfaceBinding()
    {
        string source = FixtureSource("IRoute route = null!; route.Open();", "class GrimoireConnectionAcquisitionRouteAttribute : System.Attribute {} interface IRoute { void Open(); } class Route : IRoute { [GrimoireConnectionAcquisitionRoute] public void Open() {} }")
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IRoute, Route>();", StringComparison.Ordinal);

        Assert.Contains(Discover(source).Items, static site => site.Kind == HostedProducerSiteKind.OrdinaryConnectionRoute);
    }

    [Fact]
    public void IncompleteBlobPublicationIsRejected()
    {
        HostedProducerSite site = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemEffect, "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync");

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.BatchProcessing, null, [site]);

        HostedProducerInventoryValidation result = HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new([site], []));

        Assert.Contains(result.Diagnostics, static item => item.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Theory]
    [InlineData("Microsoft.Extensions.AI.IChatClient.GetResponseAsync", 3)]
    [InlineData("Microsoft.Extensions.AI.IChatClient.GetStreamingResponseAsync", 3)]
    [InlineData("Microsoft.Extensions.AI.IEmbeddingGenerator`2.GenerateAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteBufferedAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteStreamingAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.ExecutePromptAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.StreamPromptAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedBatchAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Weave.Tapestry.ITapestrySummarizer.SummarizeAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Resilience.IProviderHealthProbe.ProbeAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner.RunScheduledAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonJob.RunAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Weave.TapestryWeaver.WeaveAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StartAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Api.OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.A2A.IA2AClientService.CancelRemoteTaskAsync", 3)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.OpenReadAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.InspectAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.HasEnvelope", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.EncryptedBlobStoreCompatibilityExtensions.OpenCompatibleReadAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.OpenReadAsync", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCapturePath", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCaptureOpenFile", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetPathIdentity", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetHandleIdentity", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.RunStartupPermissionSelfCheck", 4)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcherFactory.Create", 4)]
    [InlineData("System.IO.Directory.Exists", 4)]
    [InlineData("System.IO.Directory.EnumerateFileSystemEntries", 4)]
    [InlineData("System.IO.Directory.ResolveLinkTarget", 4)]
    [InlineData("System.IO.File.Exists", 4)]
    [InlineData("System.IO.File.GetAttributes", 4)]
    [InlineData("System.IO.File.ReadAllText", 4)]
    [InlineData("System.IO.FileInfo.Length", 4)]
    [InlineData("System.IO.FileInfo.LastWriteTimeUtc", 4)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.WriteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.ReconcileAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyProvider.GetForWriteAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RotateAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RetireAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDelete", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryQuarantine", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryRestoreQuarantined", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.MigrateAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.ReencryptAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.TryDelete", 5)]
    [InlineData("RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.DataLifecycle.IDataRetentionService.ApplyAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.ApplyOrResumeHostedPruneAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverMutationAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverFactoryResetAsync", 5)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage.IUploadedFileRepository.CreateForOwnedFileAsync", 5)]
    [InlineData("System.IO.Directory.CreateDirectory", 5)]
    [InlineData("System.IO.File.WriteAllText", 5)]
    [InlineData("System.IO.File.Delete", 5)]
    [InlineData("System.IO.File.Move", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.EnsureOwnerOnlyDirectoryExists", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyFile", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyDirectory", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryApplyOwnerOnlyFileStrict", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyTempFile", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyToSensitivePaths", 5)]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryApplyUnixFileMode", 5)]
    public void EveryClosedVocabularyMemberIsDiscovered(string symbol, int kind)
    {
        int memberSeparator = symbol.LastIndexOf('.');

        string type = symbol[..memberSeparator];

        string member = symbol[(memberSeparator + 1)..];

        int typeSeparator = type.LastIndexOf('.');

        string space = type[..typeSeparator];

        string name = type[(typeSeparator + 1)..];

        string declaration = name.Replace("`2", "<T, U>", StringComparison.Ordinal);

        string constructed = type.Replace("`2", "<object, object>", StringComparison.Ordinal);

        string source = FixtureSource($"new {constructed}().{member}();", $"namespace {space} {{ public class {declaration} {{ public void {member}() {{ }} }} }}");

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Items, site => site.Callee == symbol && (int)site.Kind == kind);
    }

    [Theory]
    [InlineData("System.IO.File.Exists(\"path\");", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Exists")]
    [InlineData("System.IO.File.Delete(\"path\");", HostedProducerSiteKind.FileSystemEffect, "System.IO.File.Delete")]
    [InlineData("_ = new System.IO.FileInfo(\"path\").Length;", HostedProducerSiteKind.FileSystemRead, "System.IO.FileInfo.Length")]
    [InlineData("_ = new System.IO.FileStream(\"path\", System.IO.FileMode.Open, System.IO.FileAccess.Read);", HostedProducerSiteKind.FileSystemRead, "System.IO.FileStream..ctor")]
    [InlineData("_ = new System.IO.FileStream(\"path\", System.IO.FileMode.OpenOrCreate);", HostedProducerSiteKind.FileSystemEffect, "System.IO.FileStream..ctor")]
    [InlineData("_ = System.IO.File.Open(\"path\", System.IO.FileMode.Open, System.IO.FileAccess.Read);", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Open")]
    [InlineData("_ = System.IO.File.Open(\"path\", System.IO.FileMode.Create);", HostedProducerSiteKind.FileSystemEffect, "System.IO.File.Open")]
    [InlineData("using var scope = new ServiceCollection().BuildServiceProvider().CreateScope();", HostedProducerSiteKind.ScopeCreation, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope")]
    public void TraversalClassifiesBoundSites(string body, object kind, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(body));

        Assert.Contains(result.Items, site => site.Kind == (HostedProducerSiteKind)kind && site.Callee == callee && site.RootType == "Worker" && site.Member == "StartAsync");
    }

    [Fact]
    public void SharedHelperKeepsTwoAuthorityRootIdentities()
    {
        string source = FixtureSource("Helper.Read();", "static class Helper { public static void Read() { System.IO.File.Exists(\"path\"); } }")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "public Task StopAsync(CancellationToken token) { Helper.Read(); return Task.CompletedTask; }", StringComparison.Ordinal);

        HostedProducerSite[] sites = Discover(source).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Equal(2, sites.Length);

        Assert.Equal(2, sites.Select(static site => site.OperationId).Distinct().Count());
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllBytes(\"path\");", "", "HOSTED_SITE_UNCLASSIFIED")]
    [InlineData("dynamic x = null!; x.Run();", "", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("IHelper x = null!; x.Run();", "interface IHelper { void Run(); }", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager x = null!; x.InitializeAsync();", "namespace RetroDownfall.Arcanum.Core.Mcp { public interface IMcpConnectionManager { void InitializeAsync(); } }", "HOSTED_AGGREGATE_PROOF_MISSING")]
    public void TraversalFailsClosed(string body, string extra, string diagnostic)
    {
        Assert.Contains(Discover(FixtureSource(body, extra)).Diagnostics, item => item.Code == diagnostic);
    }

    [Fact]
    public void InterfaceCallFollowsExactSingletonBinding()
    {
        string source = FixtureSource("IHelper helper = null!; helper.Run();", "interface IHelper { void Run(); } class Helper : IHelper { public void Run() { System.IO.File.Exists(\"path\"); } }")
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddSingleton<IHelper, Helper>();", StringComparison.Ordinal);

        Assert.Contains(Discover(source).Items, static site => site.EnclosingType == "Helper" && site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    public void EndpointInvokedSensitiveOperationNeedsItsOwnRoot()
    {
        string source = FixtureSource("").Replace("public class Worker : IHostedService", "public class Worker : IHostedService", StringComparison.Ordinal)
            .Replace("public Task StartAsync", "public void QueueIndexNow() { System.IO.File.Exists(\"path\"); } public Task StartAsync", StringComparison.Ordinal)
            + "class Endpoint { void Invoke(Worker worker) { worker.QueueIndexNow(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Contains(result.Diagnostics, static item => item.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED");

        Assert.Contains(result.Items, static site => site.Member == "QueueIndexNow");
    }

    [Fact]
    public void DeclaredMissingRootFailsClosed()
    {
        CSharpCompilation compilation = Compile(FixtureSource(""));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [], [new("Missing.Run", "src/Fixture.cs", "Missing", "Run", HostedProducerAuthorityKind.StoppedHost, "Missing.Run: exact stopped host owner", [])]);

        Assert.Contains(result.Diagnostics, static item => item.Code == "HOSTED_ROOT_UNRESOLVED");
    }

    private static HostedProducerDiscovery<HostedProducerSite> Discover(string source)
    {
        CSharpCompilation compilation = Compile(source);

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [], []);
    }

    private static string FixtureSource(string body, string extra = "") => RegistrationSource("services.AddHostedService<Worker>();")
        .Replace("public Task StartAsync(CancellationToken token) => Task.CompletedTask;", "public Task StartAsync(CancellationToken token) { " + body + " return Task.CompletedTask; }", StringComparison.Ordinal) + extra;

    [Theory]
    [InlineData("new-service", "HOSTED_SERVICE_UNCATALOGUED")]
    [InlineData("stale-service", "HOSTED_SERVICE_STALE")]
    [InlineData("duplicate-service", "HOSTED_SERVICE_DUPLICATE")]
    [InlineData("new-site", "HOSTED_SITE_UNCATALOGUED")]
    [InlineData("stale-site", "HOSTED_SITE_STALE")]
    [InlineData("duplicate-site", "HOSTED_SITE_DUPLICATE")]
    [InlineData("broad", "HOSTED_IDENTITY_INVALID")]
    [InlineData("missing-proof", "HOSTED_PROOF_MISSING")]
    [InlineData("shared-proof", "HOSTED_PROOF_SHARED")]
    [InlineData("wrong-kind", "HOSTED_WORK_KIND_INVALID")]
    [InlineData("missing-kind", "HOSTED_WORK_KIND_MISSING")]
    [InlineData("external-effect", "HOSTED_SITE_EFFECT_FRONTIER_MISSING")]
    public void ValidatorRejectsIndependentMutation(string mutation, string code)
    {
        HostedProducerSite site = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Exists");

        HostedProducerOperationEntry operation = new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "Worker.StartAsync: Generic Host awaits completion", [site]);

        List<HostedProducerServiceEntry> catalog = [new("Worker", [operation])];

        List<string> registrations = ["Worker"];

        List<HostedProducerSite> sites = [site];

        switch (mutation)
        {
            case "new-service": registrations.Add("NewWorker"); break;
            case "stale-service": registrations.Clear(); break;
            case "duplicate-service": catalog.Add(catalog[0]); break;
            case "new-site": sites.Add(site with { Callee = "System.IO.File.ReadAllText" }); break;
            case "stale-site": sites.Clear(); break;
            case "duplicate-site": operation = operation with { Sites = [site, site] }; break;
            case "broad": operation = operation with { SourcePath = "src/**" }; break;
            case "missing-proof": operation = operation with { Proof = null }; break;
            case "shared-proof": catalog.Add(new("AnotherWorker", [operation with { OperationId = "AnotherWorker.StartAsync" }])); registrations.Add("AnotherWorker"); break;
            case "wrong-kind": operation = operation with { WorkKind = GrimoireWorkKind.WorkspaceIndexing }; break;
            case "missing-kind": operation = operation with { Authority = HostedProducerAuthorityKind.OrdinaryHostedWork, Proof = null }; break;
            case "external-effect":
                site = site with { Kind = HostedProducerSiteKind.FileSystemEffect, Callee = "System.IO.File.Delete" };

                operation = operation with { Authority = HostedProducerAuthorityKind.OrdinaryHostedWork, WorkKind = GrimoireWorkKind.WorkspaceIndexing, Proof = null, Sites = [site] };

                sites = [site];

                break;
        }

        catalog[0] = catalog[0] with { Operations = [operation] };

        HostedProducerInventoryValidation result = HostedGrimoireProducerInventory.Validate(catalog, [], new(registrations, []), new(sites, []));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAwareHelperIsUnwrappedOnceAndItsBodyIsValidated(bool mutate)
    {
        string source = RegistrationSource("RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInstallationResetRecoveryAwareHostedService<Worker>(services);") + $$"""
            namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection
            {
                public static class ServiceCollectionExtensions
                {
                    public static void AddInstallationResetRecoveryAwareHostedService<TService>(this IServiceCollection services) where TService : class, IHostedService
                    {
                        services.AddHostedService({{(mutate ? "sp => new Worker()" : "sp => new RetroDownfall.Arcanum.Infrastructure.Hosting.InstallationResetRecoveryAwareHostedService<TService>()")}});
                    }
                }
            }
            namespace RetroDownfall.Arcanum.Infrastructure.Hosting
            {
                public class InstallationResetRecoveryAwareHostedService<T> : IHostedService
                {
                    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
                    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
                }
            }
            """;

        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(source)]);

        Assert.Equal(["Worker"], result.Items);

        if (mutate)
        {
            Assert.Contains(result.Diagnostics, static d => d.Code == "HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED");
        }
        else
        {
            Assert.Empty(result.Diagnostics);
        }
    }

    [Theory]
    [InlineData("services.AddHostedService<Missing>();")]
    [InlineData("services.AddHostedService<IHostedService>();")]
    [InlineData("services.AddHostedService<AbstractWorker>();")]
    [InlineData("services.AddHostedService<T>();")]
    [InlineData("Lookalike.AddHostedService<Worker>(services);")]
    public void UnsupportedRegistrationFailsClosed(string registration)
    {
        string source = RegistrationSource(registration) + """
            public abstract class AbstractWorker : Microsoft.Extensions.Hosting.IHostedService
            {
                public abstract System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken t);
                public abstract System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken t);
            }
            public static class Lookalike { public static void AddHostedService<T>(object services) {} }
            """;

        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(source)]);

        Assert.Contains(result.Diagnostics, static d => d.Code == "HOSTED_REGISTRATION_UNSUPPORTED_SHAPE");
    }

    [Theory]
    [InlineData("services.AddHostedService<Worker>();")]
    [InlineData("services.AddHostedService<Worker>(sp => sp.GetRequiredService<Worker>());")]
    [InlineData("services.AddHostedService(sp => sp.GetRequiredService<Worker>());")]
    [InlineData("services.AddHostedService(sp => new Worker());")]
    [InlineData("services.AddHostedService(Factory);")]
    [InlineData("Func<IServiceProvider, Worker> factory = Factory; services.AddHostedService(factory);")]
    [InlineData("ServiceCollectionHostedServiceExtensions.AddHostedService<Worker>(services);")]
    public void RegistrationUsesBoundImplementationType(string registration)
    {
        HostedProducerDiscovery<string> result = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([Compile(RegistrationSource(registration))]);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(["Worker"], result.Items);
    }

    private static string RegistrationSource(string registration) => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        public class Worker : IHostedService
        {
            public Task StartAsync(CancellationToken token) => Task.CompletedTask;
            public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        }
        public static class Composition
        {
            static Worker Factory(IServiceProvider provider) => new Worker();
            public static void Configure(IServiceCollection services) { {{registration}} }
        }
        """;

    private static CSharpCompilation Compile(string source)
    {
        string[] assemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

        return CSharpCompilation.Create("InventoryFixture", [CSharpSyntaxTree.ParseText(source, path: "src/Fixture.cs")], assemblies.Select(static path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
