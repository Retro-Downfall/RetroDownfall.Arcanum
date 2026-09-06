using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using Microsoft.CodeAnalysis.Diagnostics;

using System.Collections.Immutable;

using System.Diagnostics;

using System.Reflection;

using System.Text.Json;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal enum HostedProducerAuthorityKind : byte
{
    OrdinaryHostedWork = 1,

    FiniteRequest = 2,

    PreReadinessStartup = 3,

    OwnerBoundRecovery = 4,

    StoppedHost = 5,

    OwnerBoundMaintenance = 6,

    EffectFree = 7,
}

internal enum HostedProducerSiteKind : byte
{
    ScopeCreation = 1,

    OrdinaryConnectionRoute = 2,

    ProviderCall = 3,

    FileSystemRead = 4,

    FileSystemEffect = 5,

    EffectFrontier = 6,
}

internal sealed record HostedProducerSite(string RootType, string OperationId, string SourcePath, string EnclosingType, string Member, HostedProducerSiteKind Kind, string Callee);

internal sealed record HostedProducerOperationEntry(string OperationId, string SourcePath, string EnclosingType, string Member, HostedProducerAuthorityKind Authority, GrimoireWorkKind? WorkKind, string? Proof, IReadOnlyList<HostedProducerSite> Sites);

internal sealed record HostedProducerServiceEntry(string ServiceType, IReadOnlyList<HostedProducerOperationEntry> Operations);

internal sealed record NonHostedProducerChainEntry(string ChainId, string SourcePath, string EnclosingType, string Member, HostedProducerAuthorityKind Authority, string Proof, IReadOnlyList<HostedProducerSite> Sites);

internal sealed record HostedProducerInventoryValidation(IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics)
{
    internal bool IsValid => Diagnostics.Count == 0;
}

internal sealed record HostedProducerInventoryDiagnostic(string Code, string Identity, string Detail);

internal sealed record HostedProducerDiscovery<T>(IReadOnlyList<T> Items, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics);

internal static class HostedGrimoireProducerInventory
{
    private static readonly Lazy<IReadOnlyList<CSharpCompilation>> SourceCompilations = new(BuildProductionCompilations);

    internal static IReadOnlyList<CSharpCompilation> ProductionCompilations => SourceCompilations.Value;

    private static readonly Lazy<HostedProducerInventoryValidation> ProductionValidation = new(ValidateProductionSources);

    private static IReadOnlyList<CSharpCompilation> BuildProductionCompilations()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
        {
            directory = directory.Parent;
        }

        string repository = directory?.FullName ?? throw new InvalidOperationException("Could not locate the Arcanum source root.");

        List<CSharpCompilation> compilations = [];

        string[] projects = ["RetroDownfall.Arcanum.Infrastructure", "RetroDownfall.Arcanum.Api", "RetroDownfall.Arcanum.Cli"];

        foreach (string project in projects)
        {
            string projectDirectory = Path.Combine(repository, "src", project);

            ProcessStartInfo start = new("dotnet") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

            foreach (string argument in new[] { "msbuild", Path.Combine(projectDirectory, project + ".csproj"), "-t:ResolveReferences,GenerateGlobalUsings,GenerateAssemblyInfo,GenerateMSBuildEditorConfigFile", "-p:Configuration=Release", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "-getItem:Compile,ReferencePath,Analyzer,AdditionalFiles,EditorConfigFiles", "-getProperty:DefineConstants,LangVersion,InterceptorsNamespaces,OutputType" })
            {
                start.ArgumentList.Add(argument);
            }

            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not evaluate project compiler inputs.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();

            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(60000))
            {
                process.Kill(entireProcessTree: true);

                throw new InvalidOperationException("Project compiler input evaluation timed out: " + project);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Project compiler input evaluation failed: " + stdout.Result + stderr.Result);
            }

            using JsonDocument document = JsonDocument.Parse(stdout.Result);

            JsonElement properties = document.RootElement.GetProperty("Properties");

            string[] Inputs(string name) => document.RootElement.GetProperty("Items").GetProperty(name).EnumerateArray().Select(static item => item.GetProperty("FullPath").GetString()!).Distinct(StringComparer.Ordinal).ToArray();

            LanguageVersionFacts.TryParse(properties.GetProperty("LangVersion").GetString(), out LanguageVersion version);

            CSharpParseOptions parse = new(version, preprocessorSymbols: properties.GetProperty("DefineConstants").GetString()!.Split(';'));

            // Opt in only to the two namespaces emitted by the evaluated SDK generators.
            // Explicit project interceptor namespaces are appended below, never wildcarded.
            parse = parse.WithFeatures([new("InterceptorsNamespaces", "Microsoft.Extensions.Configuration.Binder.SourceGeneration;Microsoft.AspNetCore.Http.Generated")]);

            if (properties.GetProperty("InterceptorsNamespaces").GetString() is { Length: > 0 } interceptorNamespaces)
            {
                parse = parse.WithFeatures([new("InterceptorsNamespaces", "Microsoft.Extensions.Configuration.Binder.SourceGeneration;Microsoft.AspNetCore.Http.Generated;" + interceptorNamespaces)]);
            }

            SyntaxTree[] trees = Inputs("Compile").Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, Path.GetRelativePath(repository, file).Replace('\\', '/'))).ToArray();

            MetadataReference[] references = Inputs("ReferencePath").Select(static file => MetadataReference.CreateFromFile(file)).ToArray();

            CSharpCompilation compilation = CSharpCompilation.Create(project, trees, references, new CSharpCompilationOptions(properties.GetProperty("OutputType").GetString() == "Exe" ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

            GeneratorLoader loader = new();

            string[] analyzers = Inputs("Analyzer");

            foreach (string analyzer in analyzers)
            {
                loader.AddDependencyLocation(analyzer);
            }

            ISourceGenerator[] generators = analyzers.SelectMany(file => new AnalyzerFileReference(file, loader).GetGenerators(LanguageNames.CSharp)).ToArray();

            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, Inputs("AdditionalFiles").Select(static file => (AdditionalText)new GeneratorText(file)), parse, new GeneratorOptions(Inputs("EditorConfigFiles")));

            driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation complete, out ImmutableArray<Diagnostic> generatorDiagnostics);

            Diagnostic[] errors = generatorDiagnostics.Concat(complete.GetDiagnostics()).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

            if (errors.Length != 0)
            {
                throw new InvalidOperationException("Generator-complete compilation failed for " + project + ":\n" + string.Join("\n", errors.Select(static error => error.ToString())));
            }

            compilations.Add((CSharpCompilation)complete);
        }

        return compilations;
    }

    private sealed class GeneratorLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private sealed class GeneratorText(string path) : AdditionalText
    {
        public override string Path => path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(path));
    }

    private sealed class GeneratorOptions(IEnumerable<string> files) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions global = new Config(files.SelectMany(File.ReadAllLines).Where(static line => line.StartsWith("build_property.", StringComparison.Ordinal)).Select(static line => line.Split('=', 2)).Where(static pair => pair.Length == 2).GroupBy(static pair => pair[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase));

        public override AnalyzerConfigOptions GlobalOptions => global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => global;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => global;

        private sealed class Config(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }

    internal static HostedProducerInventoryValidation ValidateProductionTree() => ProductionValidation.Value;

    private static HostedProducerInventoryValidation ValidateProductionSources()
    {
        HostedProducerDiscovery<string> registrations = DiscoverApplicationHostedServices(ProductionCompilations);

        return Validate(Catalog, NonHostedCatalog, registrations, DiscoverProducerSites(ProductionCompilations, registrations));
    }

    private static readonly IReadOnlyDictionary<string, HostedProducerSiteKind> Vocabulary = new Dictionary<string, HostedProducerSiteKind>(StringComparer.Ordinal)
    {
        ["Microsoft.Extensions.AI.IChatClient.GetResponseAsync"] = (HostedProducerSiteKind)3,
        ["Microsoft.Extensions.AI.IChatClient.GetStreamingResponseAsync"] = (HostedProducerSiteKind)3,
        ["Microsoft.Extensions.AI.IEmbeddingGenerator`2.GenerateAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteBufferedAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Intelligence.IModelCallExecutor.ExecuteStreamingAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.ExecutePromptAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Intelligence.IArcanumIntelligenceProvider.StreamPromptAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Weave.IWeaveService.EmbedBatchAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Weave.Tapestry.ITapestrySummarizer.SummarizeAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Resilience.IProviderHealthProbe.ProbeAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner.RunScheduledAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonJob.RunAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.Weave.TapestryWeaver.WeaveAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StartAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Api.OpenAiV1Endpoints.ExecuteChatRequestForBatchAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.A2A.IA2AClientService.CancelRemoteTaskAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.OpenReadAsync"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.InspectAsync"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.HasEnvelope"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Core.Storage.EncryptedBlobStoreCompatibilityExtensions.OpenCompatibleReadAsync"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.OpenReadAsync"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCapturePath"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryCaptureOpenFile"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetPathIdentity"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.TryGetHandleIdentity"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.RunStartupPermissionSelfCheck"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcherFactory.Create"] = (HostedProducerSiteKind)4,
        ["System.IO.Directory.Exists"] = (HostedProducerSiteKind)4,
        ["System.IO.Directory.EnumerateFileSystemEntries"] = (HostedProducerSiteKind)4,
        ["System.IO.Directory.ResolveLinkTarget"] = (HostedProducerSiteKind)4,
        ["System.IO.File.Exists"] = (HostedProducerSiteKind)4,
        ["System.IO.File.GetAttributes"] = (HostedProducerSiteKind)4,
        ["System.IO.File.ReadAllText"] = (HostedProducerSiteKind)4,
        ["System.IO.FileInfo.Length"] = (HostedProducerSiteKind)4,
        ["System.IO.FileInfo.LastWriteTimeUtc"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.WriteAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.ReconcileAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyProvider.GetForWriteAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RotateAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.IFileEncryptionKeyRing.RetireAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDelete"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryQuarantine"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryDeleteQuarantined"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Security.IdentityOwnedFileSystemCleanup.TryRestoreQuarantined"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.MigrateAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.ReencryptAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.TryDelete"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.DataLifecycle.IDataRetentionService.ApplyAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.ApplyOrResumeHostedPruneAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverMutationAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverFactoryResetAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.IUploadedFileRepository.CreateForOwnedFileAsync"] = (HostedProducerSiteKind)5,
        ["System.IO.Directory.CreateDirectory"] = (HostedProducerSiteKind)5,
        ["System.IO.File.WriteAllText"] = (HostedProducerSiteKind)5,
        ["System.IO.File.Delete"] = (HostedProducerSiteKind)5,
        ["System.IO.File.Move"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.EnsureOwnerOnlyDirectoryExists"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyFile"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyDirectory"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryApplyOwnerOnlyFileStrict"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyTempFile"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.ApplyOwnerOnlyToSensitivePaths"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.TryApplyUnixFileMode"] = HostedProducerSiteKind.FileSystemEffect,
    };

    private static readonly HashSet<string> SensitiveBoundaryTypes = [.. Vocabulary.Keys.Select(static name => name[..name.LastIndexOf('.')]), "System.IO.File", "System.IO.Directory", "System.IO.FileStream", "System.IO.FileInfo", "System.IO.StreamWriter"];

    internal static readonly IReadOnlySet<string> AggregateBoundaries = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetStartupRecovery.RecoverBeforeBootstrapAsync",
        "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.IGrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync",
        "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseBootstrapper.EnsureInitializedAsync",
        "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler.ReconcileAsync",
        "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexProcessor.ProcessAsync",
        "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync",
    };

    private static readonly HashSet<string> ExpandedLifecycleTargets =
    [
        "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync",
        "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StartAsync",
        "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync",
        "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.ApplyOrResumeHostedPruneAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverMutationAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverFactoryResetAsync",
    ];

    // Task 14 fills independently reviewed site/frontier evidence. Producer tasks must not edit this catalog.
    internal static IReadOnlyList<HostedProducerServiceEntry> Catalog { get; } =
    [
        Lifecycle("Hosting", "GrimoireDatabaseHostedService", HostedProducerAuthorityKind.PreReadinessStartup, "installation maintenance lock and bootstrap awaited by Generic Host", HostedProducerAuthorityKind.StoppedHost, "host shutdown retains installation maintenance lock"),
        Lifecycle("Covenant", "CovenantFeatureConfigurationPublisher", HostedProducerAuthorityKind.EffectFree, "in-memory configuration subscription", HostedProducerAuthorityKind.EffectFree, "in-memory subscription disposal"),
        Lifecycle("Hosting", "PidFileService", HostedProducerAuthorityKind.PreReadinessStartup, "awaited process PID-file startup", HostedProducerAuthorityKind.StoppedHost, "process PID-file shutdown"),
        new("LongRunningOperationStartupHostedService",
        [
            AtCall(Operation("Operations", "LongRunningOperationStartupHostedService", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "awaited readiness reconciliation budget"), "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler.ReconcileAsync", 1879, 187),
            Operation("Operations", "LongRunningOperationStartupHostedService", "ContinueInBackgroundAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.LongRunningOperationRecovery),
            Operation("Operations", "LongRunningOperationStartupHostedService", "StopAsync", HostedProducerAuthorityKind.EffectFree, null, "cancels and joins the background task"),
        ]),
        Lifecycle("Hosting", "SessionAttachmentPendingGcHostedService", HostedProducerAuthorityKind.PreReadinessStartup, "pending-file and row reconciliation awaited by Generic Host", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Ordinary("Covenant", "CovenantMaintenanceHostedService", GrimoireWorkKind.CovenantMaintenance),
        Ordinary("Covenant", "GrimoireSchemaTransitionHostedService", GrimoireWorkKind.GrimoireSchemaTransition),
        Ordinary("Hosting", "EntryWeavingService", GrimoireWorkKind.EntryWeaving),
        Ordinary("Weave", "SessionAttachmentIndexingService", GrimoireWorkKind.SessionAttachmentIndexing),
        Ordinary("Hosting", "WorkspaceIndexingService", GrimoireWorkKind.WorkspaceIndexing, true),
        Ordinary("Hosting", "SagaExtractionService", GrimoireWorkKind.SagaExtraction),
        Ordinary("Hosting", "TapestryWeavingService", GrimoireWorkKind.TapestryWeaving),
        Lifecycle("Hosting", "ArcanumSettingsClampStartupLogger", HostedProducerAuthorityKind.EffectFree, "configuration warning logging only", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Lifecycle("Hosting", "ArcanumSecurityStartupChecks", HostedProducerAuthorityKind.PreReadinessStartup, "filesystem permission self-check awaited by Generic Host", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Lifecycle("Hosting", "FileEncryptionKeyBootstrapHostedService", HostedProducerAuthorityKind.PreReadinessStartup, "credential bootstrap awaited by Generic Host", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Ordinary("Data", "DataRetentionSweepHostedService", GrimoireWorkKind.DataRetentionSweep),
        Ordinary("A2A", "A2ASendingLeaseRenewer", GrimoireWorkKind.A2ASendingLeaseRenewal),
        Ordinary("Hosting", "Loremaster", GrimoireWorkKind.LoremasterSummarization),
        Ordinary("Hosting", "ApprenticeService", GrimoireWorkKind.ApprenticeExecution, true),
        new("McpServerBootstrapHostedService",
        [
            AtCall(Operation("Mcp", "McpServerBootstrapHostedService", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "blocking branch awaits global initialization"), "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync", 1527, 42, 1),
            AtCall(Operation("Mcp", "McpServerBootstrapHostedService", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.McpServerBootstrap), "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync", 1074, 53),
            Operation("Mcp", "McpServerBootstrapHostedService", "StopAsync", HostedProducerAuthorityKind.StoppedHost, null, "host shutdown joins manager-owned global initialization and stops connections"),
        ]),
        Ordinary("Resilience", "ProviderHealthProbeService", GrimoireWorkKind.ProviderHealthProbe),
        Ordinary("Hosting", "UnseenServantService", GrimoireWorkKind.UnseenServant, true),
        new("BatchProcessingService",
        [
            Operation("Intelligence", "BatchProcessingService", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "stranded-batch recovery awaited by Generic Host", "Api"),
            Operation("Intelligence", "BatchProcessingService", "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.BatchProcessing, project: "Api"),
            Operation("Intelligence", "BatchProcessingService", "StopAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.BatchProcessing, project: "Api"),
        ]),
    ];

    internal static IReadOnlyList<NonHostedProducerChainEntry> NonHostedCatalog { get; } =
    [
        Chain("Cli", "Commands", "BackupCommands", "Create", HostedProducerAuthorityKind.OwnerBoundMaintenance, "IGrimoireCliInitialization.RunExclusiveAsync holds installation maintenance lock and client mutation boundary; BackupService.CreateAsync owns durable backup operation and Covenant installation read lease"),
        Chain("Infrastructure", "Backup", "BackupRestoreService", "RestoreAsync", HostedProducerAuthorityKind.StoppedHost, "restore safety-backup under exact stopped-host restore ownership"),
        Chain("Infrastructure", "InstallationReset", "InstallationResetService", "ApplyFullUnderMaintenanceLockAsync", HostedProducerAuthorityKind.OwnerBoundMaintenance, "generation-bound installation reset maintenance lock and authenticated reset owner"),
        Chain("Infrastructure", "GrimoireTransitions", "GrimoireOfflineTransitionStartupRecovery", "RecoverBeforeBootstrapAsync", HostedProducerAuthorityKind.OwnerBoundRecovery, "authenticated offline-transition owner before Grimoire bootstrap"),
    ];

    private static NonHostedProducerChainEntry Chain(string project, string folder, string type, string member, HostedProducerAuthorityKind authority, string proof)
    {
        string enclosingType = $"RetroDownfall.Arcanum.{project}.{folder}.{type}";

        return new(enclosingType + "." + member, $"src/RetroDownfall.Arcanum.{project}/{folder}/{type}.cs", enclosingType, member, authority, enclosingType + "." + member + ": " + proof, []);
    }

    private static HostedProducerOperationEntry Operation(string folder, string type, string member, HostedProducerAuthorityKind authority, GrimoireWorkKind? kind, string? proof = null, string project = "Infrastructure")
    {
        string enclosingType = $"RetroDownfall.Arcanum.{project}.{folder}.{type}";

        return new(enclosingType + "." + member, $"src/RetroDownfall.Arcanum.{project}/{folder}/{type}.cs", enclosingType, member, authority, kind, proof is null ? null : enclosingType + "." + member + ": " + proof, []);
    }

    private static HostedProducerOperationEntry AtCall(HostedProducerOperationEntry operation, string callee, int start, int length, int occurrence = 0) => operation with { OperationId = operation.OperationId + "::call:" + callee + "#" + occurrence + "@" + start + ":" + length };

    private static HostedProducerServiceEntry Ordinary(string folder, string type, GrimoireWorkKind kind, bool stop = false) => new(type, stop ? [Operation(folder, type, "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, kind), Operation(folder, type, "StopAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, kind)] : [Operation(folder, type, "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, kind)]);

    private static HostedProducerServiceEntry Lifecycle(string folder, string type, HostedProducerAuthorityKind startAuthority, string startProof, HostedProducerAuthorityKind stopAuthority, string stopProof) => new(type, [Operation(folder, type, "StartAsync", startAuthority, null, startProof), Operation(folder, type, "StopAsync", stopAuthority, null, stopProof)]);

    internal static HostedProducerDiscovery<HostedProducerSite> DiscoverProducerSites(IReadOnlyList<CSharpCompilation> compilations, HostedProducerDiscovery<string> registrations) => DiscoverProducerSites(compilations, registrations, Catalog, NonHostedCatalog);

    internal static HostedProducerDiscovery<HostedProducerSite> DiscoverProducerSites(IReadOnlyList<CSharpCompilation> compilations, HostedProducerDiscovery<string> registrations, IReadOnlyList<HostedProducerServiceEntry> catalog, IReadOnlyList<NonHostedProducerChainEntry> nonHostedCatalog) => new ProducerGraph(compilations).Discover(registrations, catalog, nonHostedCatalog);

    private sealed record AuthoredMember(IMethodSymbol Symbol, SyntaxNode Syntax, SemanticModel Model, IReadOnlyDictionary<ISymbol, IReadOnlySet<string>>? AdmissionBindings = null);

    private sealed class ProducerGraph
    {
        private readonly record struct MethodIdentity(IAssemblySymbol Assembly, string Method);

        private sealed class MethodIdentityComparer : IEqualityComparer<MethodIdentity>
        {
            internal static MethodIdentityComparer Instance { get; } = new();

            public bool Equals(MethodIdentity x, MethodIdentity y) => ReferenceEquals(x.Assembly, y.Assembly) && x.Method == y.Method;

            public int GetHashCode(MethodIdentity value) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Assembly), StringComparer.Ordinal.GetHashCode(value.Method));
        }

        private readonly Dictionary<string, List<AuthoredMember>> members = new(StringComparer.Ordinal);

        private readonly Dictionary<string, HashSet<string>> bindings = new(StringComparer.Ordinal);

        private readonly List<(InvocationExpressionSyntax Call, SemanticModel Model)> invocations = [];

        private readonly List<HostedProducerInventoryDiagnostic> diagnostics = [];

        private readonly Dictionary<string, HostedProducerSite> sites = new(StringComparer.Ordinal);

        private readonly HashSet<string> lifecycleMembers = new(StringComparer.Ordinal);

        private HostedProducerOperationEntry[] declaredOperations = [];

        private readonly Dictionary<(int Compilation, int Tree, string Method, int SpanStart), InvocationExpressionSyntax[]> admissionCalls = [];

        private readonly Dictionary<(int Compilation, int Tree, string Method, int SpanStart), bool> admissionSeeds = [];

        private readonly Dictionary<(int Compilation, int Tree, string Method, int MemberStart, int ExpressionStart, int ExpressionLength, string Bindings), IReadOnlySet<string>> admissionOrigins = [];

        private readonly Dictionary<string, List<SyntaxNode>> selectedRoots = new(StringComparer.Ordinal);

        private readonly Dictionary<MethodIdentity, AuthoredMember?> resolvedMembers = new(MethodIdentityComparer.Instance);

        private readonly Dictionary<Compilation, int> compilationIdentities = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<SyntaxTree, int> treeIdentities = new(ReferenceEqualityComparer.Instance);

        private long emittedSiteEvidence;

        internal ProducerGraph(IReadOnlyList<CSharpCompilation> compilations)
        {
            foreach (CSharpCompilation compilation in compilations)
            {
                _ = CompilationIdentity(compilation);

                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    if (!tree.FilePath.StartsWith("src/", StringComparison.Ordinal) || tree.FilePath.Contains("/obj/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    SemanticModel model = compilation.GetSemanticModel(tree);

                    _ = TreeIdentity(tree);

                    foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                    {
                        if (node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax && model.GetDeclaredSymbol(node) is IMethodSymbol method)
                        {
                            AddMember(new(method, node, model));
                        }

                        if (node is PropertyDeclarationSyntax { ExpressionBody.Expression: { } expression } property && model.GetDeclaredSymbol(property)?.GetMethod is { } getter)
                        {
                            AddMember(new(getter, expression, model));
                        }

                        if (node is InvocationExpressionSyntax invocation)
                        {
                            invocations.Add((invocation, model));

                        }
                    }
                }
            }

            foreach ((InvocationExpressionSyntax call, SemanticModel model) in invocations)
            {
                ReadBinding(call, model);
            }
        }

        private static MethodIdentity Identity(IMethodSymbol method) => new(method.ContainingAssembly, MethodKey(method));

        private IEnumerable<AuthoredMember> AuthoredMembers => members.Values.SelectMany(static group => group);

        private void AddMember(AuthoredMember member)
        {
            string key = MethodKey(member.Symbol);

            if (!members.TryGetValue(key, out List<AuthoredMember>? candidates))
            {
                members.Add(key, candidates = []);
            }

            candidates.Add(member);
        }

        private int CompilationIdentity(Compilation compilation)
        {
            if (!compilationIdentities.TryGetValue(compilation, out int identity))
            {
                identity = compilationIdentities.Count;

                compilationIdentities.Add(compilation, identity);
            }

            return identity;
        }

        private int TreeIdentity(SyntaxTree tree)
        {
            if (!treeIdentities.TryGetValue(tree, out int identity))
            {
                identity = treeIdentities.Count;

                treeIdentities.Add(tree, identity);
            }

            return identity;
        }

        private (int Compilation, int Tree, string Method, int SpanStart) MemberIdentity(AuthoredMember member) => (CompilationIdentity(member.Model.Compilation), TreeIdentity(member.Syntax.SyntaxTree), MethodKey(member.Symbol), member.Syntax.SpanStart);

        private void ReadBinding(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol bound || !(bound.ReducedFrom ?? bound).ContainingNamespace.ToDisplayString().StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) || bound.Name is not ("AddSingleton" or "TryAddSingleton" or "AddScoped" or "TryAddScoped" or "AddTransient" or "TryAddTransient"))
            {
                return;
            }

            ITypeSymbol? contract = bound.TypeArguments.FirstOrDefault();

            if (contract is null)
            {
                return;
            }

            IEnumerable<ITypeSymbol?> implementations = bound.TypeArguments.Length == 2 ? [bound.TypeArguments[1]] : invocation.ArgumentList.Arguments.Count == 0 ? [contract] : ReturnedTypes(invocation.ArgumentList.Arguments.Last().Expression, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

            string key = TypeKey(contract);

            if (!bindings.TryGetValue(key, out HashSet<string>? targets))
            {
                targets = [];

                bindings[key] = targets;
            }

            foreach (ITypeSymbol? implementation in implementations.DefaultIfEmpty(null))
            {
                bool assignable = implementation is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } concrete && (SymbolEqualityComparer.Default.Equals(concrete, contract) || concrete.AllInterfaces.Any(slot => SymbolEqualityComparer.Default.Equals(slot, contract)) || IsBase(concrete, contract));

                targets.Add(assignable ? TypeKey(implementation!) : "<unsupported-factory-result>");
            }
        }

        private static bool IsBase(INamedTypeSymbol implementation, ITypeSymbol contract) => implementation.BaseType is { } parent && (SymbolEqualityComparer.Default.Equals(parent, contract) || IsBase(parent, contract));

        private IEnumerable<ITypeSymbol?> ReturnedTypes(SyntaxNode expression, SemanticModel model, HashSet<ISymbol> path)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parentheses:
                    return ReturnedTypes(parentheses.Expression, model, path);
                case CastExpressionSyntax cast:
                    return ReturnedTypes(cast.Expression, model, path);
                case AnonymousFunctionExpressionSyntax lambda:
                    return ReturnedTypes(lambda.Body, model, path);
                case ArrowExpressionClauseSyntax arrow:
                    return ReturnedTypes(arrow.Expression, model, path);
                case BlockSyntax block:
                    return block.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<ReturnStatementSyntax>().SelectMany(statement => statement.Expression is { } result ? ReturnedTypes(result, model, path) : [null]).DefaultIfEmpty(null).ToArray();
                case ConditionalExpressionSyntax conditional:
                    return ReturnedTypes(conditional.WhenTrue, model, path).Concat(ReturnedTypes(conditional.WhenFalse, model, path)).ToArray();
                case SwitchExpressionSyntax selection:
                    return selection.Arms.SelectMany(arm => ReturnedTypes(arm.Expression, model, path)).ToArray();
                case ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax:
                    return [model.GetTypeInfo(expression).Type];
            }

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is IMethodSymbol method && expression is InvocationExpressionSyntax && method.Name == "GetRequiredService" && (method.ReducedFrom ?? method).ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.DependencyInjection")
            {
                return [method.ReturnType];
            }

            if (symbol is null || !path.Add(symbol))
            {
                return [null];
            }

            IEnumerable<ITypeSymbol?> resultTypes = [null];

            if (symbol is IMethodSymbol factory && Resolve(factory) is { } authored)
            {
                SyntaxNode? body = authored.Syntax switch { MethodDeclarationSyntax declaration => (SyntaxNode?)declaration.ExpressionBody ?? declaration.Body, LocalFunctionStatementSyntax local => (SyntaxNode?)local.ExpressionBody ?? local.Body, _ => null };

                resultTypes = body is null ? [null] : ReturnedTypes(body, authored.Model, path).ToArray();
            }
            else if (symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is VariableDeclaratorSyntax variable)
            {
                List<SyntaxNode> values = variable.Initializer is { } initializer ? [initializer.Value] : [];

                values.AddRange(variable.Ancestors().OfType<BlockSyntax>().First().DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment => assignment.SpanStart < expression.SpanStart && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)).Select(static assignment => assignment.Right));

                resultTypes = values.SelectMany(value => ReturnedTypes(value, model, path)).DefaultIfEmpty(null).ToArray();
            }
            else if (symbol is IMethodSymbol { ReturnType: INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } returned })
            {
                resultTypes = [returned];
            }

            path.Remove(symbol);

            return resultTypes;
        }

        internal HostedProducerDiscovery<HostedProducerSite> Discover(HostedProducerDiscovery<string> registrations, IReadOnlyList<HostedProducerServiceEntry> catalog, IReadOnlyList<NonHostedProducerChainEntry> nonHostedCatalog)
        {
            HashSet<string> hosted = registrations.Items.ToHashSet(StringComparer.Ordinal);

            HostedProducerOperationEntry[] declared = [.. catalog.SelectMany(static service => service.Operations), .. nonHostedCatalog.Select(static chain => new HostedProducerOperationEntry(chain.ChainId, chain.SourcePath, chain.EnclosingType, chain.Member, chain.Authority, null, chain.Proof, chain.Sites))];

            declaredOperations = declared;

            List<(HostedProducerOperationEntry Operation, AuthoredMember Member, SyntaxNode Selection)> resolvedRoots = [];

            foreach (HostedProducerOperationEntry operation in declared)
            {
                AuthoredMember[] roots = AuthoredMembers.Where(member => member.Syntax.SyntaxTree.FilePath == operation.SourcePath && TypeKey(member.Symbol.ContainingType) == operation.EnclosingType && member.Symbol.Name == operation.Member).ToArray();

                SyntaxNode? selection = roots.Length == 1 ? SelectRoot(roots[0], operation.OperationId) : null;

                if (selection is null)
                {
                    diagnostics.Add(new("HOSTED_ROOT_UNRESOLVED", operation.OperationId, "The exact file/type/member and executable call selector must bind once."));

                    continue;
                }

                if (resolvedRoots.Any(existing => MethodKey(existing.Member.Symbol) == MethodKey(roots[0].Symbol) && existing.Selection.Span.OverlapsWith(selection.Span)))
                {
                    diagnostics.Add(new("HOSTED_ROOT_OVERLAP", operation.OperationId, "Declared authority roots must select disjoint source regions; whole-member mixed claims are forbidden."));
                }

                resolvedRoots.Add((operation, roots[0], selection));

                if (selection != roots[0].Syntax)
                {
                    string key = MethodKey(roots[0].Symbol);

                    if (!selectedRoots.TryGetValue(key, out List<SyntaxNode>? selections))
                    {
                        selectedRoots[key] = selections = [];
                    }

                    selections.Add(selection);
                }
            }

            foreach (AuthoredMember root in AuthoredMembers.Where(member => hosted.Contains(member.Symbol.ContainingType.Name) && IsLifecycle(member.Symbol)))
            {
                Traverse(root, root.Symbol.ContainingType.Name, TypeKey(root.Symbol.ContainingType) + "." + root.Symbol.Name, new HashSet<string>(StringComparer.Ordinal), true);
            }

            foreach ((HostedProducerOperationEntry operation, AuthoredMember root, SyntaxNode selection) in resolvedRoots)
            {
                string rootType = catalog.FirstOrDefault(service => service.Operations.Contains(operation))?.ServiceType ?? nonHostedCatalog.FirstOrDefault(chain => chain.ChainId == operation.OperationId)?.EnclosingType ?? operation.EnclosingType;

                Traverse(root, rootType, operation.OperationId, new HashSet<string>(StringComparer.Ordinal), false, selection: selection);
            }

            foreach ((InvocationExpressionSyntax call, SemanticModel model) in invocations)
            {
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol target)
                {
                    continue;
                }

                AuthoredMember? authored = Resolve(target);

                if (authored is null || !hosted.Contains(authored.Symbol.ContainingType.Name) || IsLifecycle(target))
                {
                    continue;
                }

                IMethodSymbol? caller = model.GetEnclosingSymbol(call.SpanStart) as IMethodSymbol;

                if (caller is not null && lifecycleMembers.Contains(MethodKey(caller)))
                {
                    continue;
                }

                bool catalogued = declared.Any(entry => entry.EnclosingType == TypeKey(authored.Symbol.ContainingType) && entry.Member == authored.Symbol.Name && entry.SourcePath == authored.Syntax.SyntaxTree.FilePath);

                int before = sites.Count;

                string operation = TypeKey(authored.Symbol.ContainingType) + "." + authored.Symbol.Name;

                Traverse(authored, authored.Symbol.ContainingType.Name, operation, new HashSet<string>(StringComparer.Ordinal), false);

                if (!catalogued && sites.Count > before)
                {
                    diagnostics.Add(new("HOSTED_EXTERNAL_OPERATION_UNCATALOGUED", Location(call), operation));
                }
            }

            return new(sites.Values.OrderBy(SiteIdentity, StringComparer.Ordinal).ToArray(), diagnostics.Distinct().ToArray());
        }

        private static SyntaxNode? SelectRoot(AuthoredMember member, string operationId)
        {
            string identity = TypeKey(member.Symbol.ContainingType) + "." + member.Symbol.Name;

            if (operationId == identity)
            {
                return member.Syntax;
            }

            string prefix = identity + "::call:";

            if (!operationId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            int separator = operationId.LastIndexOf('#');

            string[] anchor = operationId[(separator + 1)..].Split('@', ':');

            if (separator < prefix.Length || anchor.Length != 3 || !int.TryParse(anchor[0], out int occurrence) || occurrence < 0 || !int.TryParse(anchor[1], out int start) || !int.TryParse(anchor[2], out int length))
            {
                return null;
            }

            string callee = operationId[prefix.Length..separator];

            InvocationExpressionSyntax? selected = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol target && Normalize(target) == callee).ElementAtOrDefault(occurrence);

            return selected?.Span.Start == start && selected.Span.Length == length ? selected : null;
        }

        private AuthoredMember? Resolve(IMethodSymbol method)
        {
            MethodIdentity identity = Identity(method);

            if (resolvedMembers.TryGetValue(identity, out AuthoredMember? cached))
            {
                return cached;
            }

            AuthoredMember[] candidates = members.TryGetValue(MethodKey(method), out List<AuthoredMember>? group) ? group.Where(static candidate => candidate.Syntax is not MethodDeclarationSyntax { Body: null, ExpressionBody: null }).ToArray() : [];

            AuthoredMember? authored = candidates.FirstOrDefault(candidate => ReferenceEquals(candidate.Symbol.ContainingAssembly, method.ContainingAssembly) && SymbolEqualityComparer.Default.Equals(candidate.Symbol, method)) ?? (candidates.Length == 1 ? candidates[0] : null);

            if (authored is not null)
            {
                return resolvedMembers[identity] = authored;
            }

            if (method.ContainingType.TypeKind == TypeKind.Interface && bindings.TryGetValue(TypeKey(method.ContainingType), out HashSet<string>? targets) && targets.Count == 1)
            {
                return resolvedMembers[identity] = AuthoredMembers.FirstOrDefault(candidate => TypeKey(candidate.Symbol.ContainingType) == targets.Single() && candidate.Symbol.ContainingType.AllInterfaces.SelectMany(static contract => contract.GetMembers()).OfType<IMethodSymbol>().Any(slot => MethodKey(slot) == MethodKey(method) && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation && MethodKey(implementation) == MethodKey(candidate.Symbol)));
            }

            return resolvedMembers[identity] = null;
        }

        private void Traverse(AuthoredMember member, string rootType, string operationId, HashSet<string> visited, bool lifecycle, string? inheritedWork = null, string? inheritedEffect = null, SyntaxNode? selection = null, IReadOnlySet<int>? fieldPublications = null, bool completionOwned = false)
        {
            string visitKey = MethodKey(member.Symbol) + "@" + member.Syntax.SpanStart;

            if (!visited.Add(visitKey))
            {
                return;
            }

            try
            {

                if (lifecycle)
                {
                    lifecycleMembers.Add(MethodKey(member.Symbol));
                }

                HostedProducerOperationEntry? ordinary = declaredOperations.FirstOrDefault(entry => entry.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && (operationId == entry.OperationId || operationId.StartsWith(entry.OperationId + "/", StringComparison.Ordinal)));

                if (ordinary is not null && selection is not null && selection != member.Syntax)
                {
                    foreach (string admissionName in new[] { "TryAcquireWorkLease", "TryBeginExternalEffectGroup" })
                    {
                        string? retained = FindRetainedAdmission(member, selection, admissionName, admissionName == "TryAcquireWorkLease" ? ordinary.WorkKind : null);

                        InvocationExpressionSyntax? admission = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault(call => Location(call) == retained);

                        if (admission is not null && member.Model.GetSymbolInfo(admission).Symbol is IMethodSymbol gate)
                        {
                            AddSite(HostedProducerSiteKind.EffectFrontier, Normalize(gate), member, rootType, operationId, admission);
                        }
                    }
                }

                foreach (SyntaxNode node in (selection ?? member.Syntax).DescendantNodesAndSelf(node => node == (selection ?? member.Syntax) || node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax))
                {
                    if (selection is null && operationId == TypeKey(member.Symbol.ContainingType) + "." + member.Symbol.Name && selectedRoots.TryGetValue(MethodKey(member.Symbol), out List<SyntaxNode>? selections) && selections.Any(selected => selected.Span.Contains(node.Span)))
                    {
                        continue;
                    }

                    if (node is InvocationExpressionSyntax && member.Model.GetOperation(node) is INameOfOperation)
                    {
                        continue;
                    }

                    if (node is InvocationExpressionSyntax && member.Model.GetOperation(node) is IDynamicInvocationOperation)
                    {
                        diagnostics.Add(new("HOSTED_CALL_TARGET_UNRESOLVED", Location(node), "Dynamic invocation has no exact call target."));

                        continue;
                    }

                    ISymbol? symbol = node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or MemberAccessExpressionSyntax ? member.Model.GetSymbolInfo(node).Symbol : node is IdentifierNameSyntax && node.Parent is not MemberAccessExpressionSyntax ? member.Model.GetSymbolInfo(node).Symbol as IPropertySymbol : null;

                    string? workAdmitted = WorkAt(member, node, operationId, ordinary?.WorkKind, inheritedWork);

                    string? effectGroup = EffectAt(member, node, operationId, inheritedEffect);

                    if (symbol is IMethodSymbol method && node is not MemberAccessExpressionSyntax)
                    {
                        string callee = Normalize(method);

                        bool knownCallback = callee is "System.Threading.Tasks.Task.Run" or "System.Threading.Tasks.Parallel.ForEachAsync" || method.MethodKind == MethodKind.DelegateInvoke;

                        if (node is InvocationExpressionSyntax callbackCall && (knownCallback || Resolve(method) is null && callbackCall.ArgumentList.Arguments.Any(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) && IsDelegateExpression(member.Model, argument.Expression))))
                        {
                            if (TryHostHandoff(member, callbackCall) is { } handoff)
                            {
                                // A host-owned task transfers to an independently admitted root, never its caller's handles.
                                Traverse(handoff.Target, rootType, handoff.Root.OperationId, visited, lifecycle);

                                continue;
                            }

                            bool awaitableReturn = TypeKey(method.ReturnType) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1";

                            SyntaxNode? completion = awaitableReturn ? CompletionPoint(member, callbackCall, completionOwned) : callbackCall;

                            bool owned = knownCallback && completion is not null && WorkAt(member, completion, operationId, ordinary?.WorkKind, inheritedWork) == workAdmitted && EffectAt(member, completion, operationId, inheritedEffect) == effectGroup;

                            ExpressionSyntax[] callbacks = method.MethodKind == MethodKind.DelegateInvoke ? [callbackCall.Expression] : callbackCall.ArgumentList.Arguments.Where(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) && IsDelegateExpression(member.Model, argument.Expression)).Select(static argument => argument.Expression).ToArray();

                            foreach (ExpressionSyntax callback in callbacks)
                            {
                                AuthoredMember? body = ResolveCallable(callback, member.Model);

                                bool callbackOwned = owned && (body?.Symbol.IsAsync != true || awaitableReturn && !body.Symbol.ReturnsVoid);

                                if (!callbackOwned || body is null)
                                {
                                    diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", operationId + "/callback@" + Location(callbackCall), callee + "; Callback execution must bind once and complete before its inherited admission lifetime ends."));
                                }

                                if (body is not null)
                                {
                                    Traverse(body with { AdmissionBindings = member.AdmissionBindings }, rootType, operationId + "/callback@" + Location(callbackCall) + "/body@" + Location(callback), visited, lifecycle, callbackOwned ? workAdmitted : null, callbackOwned ? effectGroup : null, completionOwned: callbackOwned);
                                }
                            }

                            continue;
                        }

                        if (method.Name is "Dispose" or "DisposeAsync" && node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } && member.Model.GetTypeInfo(access.Expression).Type is { } receiver && TypeKey(receiver) is "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter" or "System.IO.StreamWriter")
                        {
                            callee = TypeKey(receiver) + "." + method.Name;
                        }

                        HostedProducerSiteKind? kind = Classify(method, callee, node, member.Model);

                        if (kind is not null)
                        {
                            AddSite(kind.Value, callee, member, rootType, operationId, node, effectGroup, workAdmitted);
                        }

                        bool aggregate = AggregateBoundaries.Contains(callee) || ExpandedLifecycleTargets.Contains(callee);

                        AuthoredMember? target = Resolve(method);

                        if (node is InvocationExpressionSyntax handleCall && (AdmissionArguments(member, handleCall).Any(argument => argument.Expression is not DeclarationExpressionSyntax && AdmissionOrigins(member, argument.Expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Count > 0 && (argument.RefKind is RefKind.Ref or RefKind.Out || target is null))
                            || AdmissionReceiver(member, handleCall) is { } receiverExpression && AdmissionOrigins(member, receiverExpression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Count > 0 && !PreservesAdmissionReceiver(method)))
                        {
                            diagnostics.Add(new("HOSTED_ADMISSION_HANDLE_ESCAPE", operationId + "/call@" + Location(node), callee + "; A retained handle passed by ref/out or to an opaque callee has no proven lifetime continuation."));
                        }

                        if (target?.Symbol.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "GrimoireConnectionAcquisitionRouteAttribute") == true && kind is null)
                        {
                            AddSite(HostedProducerSiteKind.OrdinaryConnectionRoute, Normalize(target.Symbol), member, rootType, operationId, node, effectGroup, workAdmitted);
                        }

                        if (kind is null || aggregate)
                        {
                            if (target is not null)
                            {
                                long before = emittedSiteEvidence;

                                IReadOnlySet<int>? carrier = node is InvocationExpressionSyntax call ? VerifyFieldPublication(member, call, target, operationId, inheritedEffect) : null;

                                bool joined = node is InvocationExpressionSyntax invocation && CompletionPoint(member, invocation, completionOwned) is not null;

                                bool detached = target.Symbol.IsAsync && !joined;

                                if (detached)
                                {
                                    diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", operationId + "/call@" + Location(node), callee + "; An async helper must complete within its caller's retained lifetime."));
                                }

                                Traverse(node is InvocationExpressionSyntax boundCall ? BindAdmissionArguments(member, boundCall, target) : target, rootType, operationId + "/call@" + Location(node), visited, lifecycle, detached ? null : workAdmitted, detached ? null : effectGroup, fieldPublications: carrier, completionOwned: joined);

                                if (AggregateBoundaries.Contains(callee) && emittedSiteEvidence == before)
                                {
                                    diagnostics.Add(new("HOSTED_AGGREGATE_PROOF_MISSING", operationId + "/call@" + Location(node), "The exact bound aggregate target " + MethodKey(target.Symbol) + " in " + target.Syntax.SyntaxTree.FilePath + " produced no executable site evidence; prose is not a proof."));
                                }
                            }
                            else if (aggregate)
                            {
                                diagnostics.Add(new("HOSTED_AGGREGATE_PROOF_MISSING", Location(node), callee));
                            }
                            else if (method.ContainingType.TypeKind == TypeKind.Interface && method.ContainingNamespace.ToDisplayString().StartsWith("RetroDownfall.", StringComparison.Ordinal) || method.ContainingType.TypeKind == TypeKind.Interface && method.ContainingType.Locations.Any(static location => location.IsInSource))
                            {
                                diagnostics.Add(new("HOSTED_CALL_TARGET_UNRESOLVED", Location(node), callee));
                            }
                        }
                    }
                    else if (symbol is IPropertySymbol property)
                    {
                        string callee = Normalize(property);

                        if (node is MemberAccessExpressionSyntax access && member.Model.GetTypeInfo(access.Expression).Type is { } receiver && TypeKey(receiver) == "System.IO.FileInfo")
                        {
                            callee = "System.IO.FileInfo." + property.Name;
                        }

                        if (SensitiveBoundaryTypes.Contains(callee[..callee.LastIndexOf('.')]) && (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node || node.Parent?.IsKind(SyntaxKind.PostIncrementExpression) == true || node.Parent?.IsKind(SyntaxKind.PreIncrementExpression) == true || node.Parent?.IsKind(SyntaxKind.PostDecrementExpression) == true || node.Parent?.IsKind(SyntaxKind.PreDecrementExpression) == true))
                        {
                            diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), callee + " setter"));

                            continue;
                        }

                        if (Vocabulary.TryGetValue(callee, out HostedProducerSiteKind kind))
                        {
                            AddSite(kind, callee, member, rootType, operationId, node, effectGroup, workAdmitted);
                        }
                        else if (SensitiveBoundaryTypes.Contains(TypeKey(property.ContainingType)))
                        {
                            diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), callee));
                        }

                        bool writes = node.Parent is AssignmentExpressionSyntax write && write.Left == node || node.Parent?.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PostIncrementExpression or SyntaxKind.PreDecrementExpression or SyntaxKind.PostDecrementExpression;

                        bool reads = node.Parent is not AssignmentExpressionSyntax simple || simple.Left != node || !simple.IsKind(SyntaxKind.SimpleAssignmentExpression);

                        foreach (IMethodSymbol accessor in new[] { reads ? property.GetMethod : null, writes ? property.SetMethod : null }.OfType<IMethodSymbol>())
                        {
                            if (Resolve(accessor) is { } target)
                            {
                                Traverse(target, rootType, operationId + (accessor.MethodKind == MethodKind.PropertyGet ? "/get@" : "/set@") + Location(node), visited, lifecycle, workAdmitted, effectGroup);
                            }
                        }
                    }
                    else if (node is InvocationExpressionSyntax && symbol is null)
                    {
                        diagnostics.Add(new("HOSTED_CALL_TARGET_UNRESOLVED", Location(node), node.ToString()));
                    }

                    if (node is UsingStatementSyntax { Expression: { } resource } expressionUsing)
                    {
                        string disposeName = expressionUsing.AwaitKeyword.RawKind != 0 ? "DisposeAsync" : "Dispose";

                        ITypeSymbol? resourceType = member.Model.GetTypeInfo(resource).Type;

                        IMethodSymbol? dispose = resourceType?.GetMembers(disposeName).OfType<IMethodSymbol>().SingleOrDefault(static candidate => candidate.Parameters.Length == 0);

                        int exit = expressionUsing.Statement.Span.End - 1;

                        string? retainedWork = WorkAt(member, resource, operationId, ordinary?.WorkKind, inheritedWork, exit, resource);

                        string? retainedEffect = EffectAt(member, resource, operationId, inheritedEffect, exit, resource);

                        if (resourceType?.ToDisplayString() is "System.IO.StreamWriter" or "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter")
                        {
                            AddSite(HostedProducerSiteKind.FileSystemEffect, resourceType.ToDisplayString() + "." + disposeName, member, rootType, operationId, resource, retainedEffect, retainedWork);
                        }
                        else if (dispose is not null && Resolve(dispose) is { } cleanup)
                        {
                            Traverse(cleanup, rootType, operationId + "/dispose@" + Location(resource), visited, lifecycle, retainedWork, retainedEffect);
                        }
                        else if (AdmissionOrigins(member, resource, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Count == 0)
                        {
                            diagnostics.Add(new("HOSTED_DISPOSAL_TARGET_UNRESOLVED", operationId + "/dispose@" + Location(resource), resourceType?.ToDisplayString() + "." + disposeName + "; Expression disposal must resolve to an authored cleanup or a known admission handle."));
                        }
                    }

                    if (node is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax))
                    {
                        ITypeSymbol? type = member.Model.GetTypeInfo(declaration.Type).Type;

                        if (type?.ToDisplayString() is "System.IO.StreamWriter" or "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter")
                        {
                            bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                            int disposeAt = DisposalPosition(declaration);

                            string? disposalGroup = EffectAt(member, declaration, operationId, inheritedEffect, disposeAt, declaration);

                            string? retainedWork = WorkAt(member, declaration, operationId, ordinary?.WorkKind, inheritedWork, disposeAt, declaration);

                            AddSite(HostedProducerSiteKind.FileSystemEffect, type.ToDisplayString() + "." + (asynchronous ? "DisposeAsync" : "Dispose"), member, rootType, operationId, node, disposalGroup, retainedWork);
                        }
                        else if (type is INamedTypeSymbol disposable)
                        {
                            bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                            IMethodSymbol? dispose = disposable.GetMembers(asynchronous ? "DisposeAsync" : "Dispose").OfType<IMethodSymbol>().SingleOrDefault(static method => method.Parameters.Length == 0);

                            if (dispose is not null && Resolve(dispose) is { } target)
                            {
                                int position = DisposalPosition(declaration);

                                string? retainedWork = WorkAt(member, declaration, operationId, ordinary?.WorkKind, inheritedWork, position, declaration);

                                Traverse(target, rootType, operationId + "/dispose@" + Location(declaration), visited, lifecycle, retainedWork, EffectAt(member, declaration, operationId, inheritedEffect, position, declaration));
                            }
                        }
                    }
                }

                ValidatePublicationRegions(member, selection ?? member.Syntax, operationId, inheritedEffect, fieldPublications);

            }
            finally
            {
                // Only the active recursion path is a cycle. A later sibling may carry different authority.
                visited.Remove(visitKey);
            }
        }

        private static int DisposalPosition(VariableDeclarationSyntax declaration) => declaration.Parent is UsingStatementSyntax statement ? statement.Statement.Span.End - 1 : declaration.Ancestors().OfType<BlockSyntax>().First().CloseBraceToken.SpanStart;

        private sealed record HostHandoffProof(AuthoredMember Target, HostedProducerOperationEntry Root, IFieldSymbol TaskField, string DispatchAnchor, string JoinAnchor);

        private HostHandoffProof? TryHostHandoff(AuthoredMember caller, InvocationExpressionSyntax dispatch)
        {
            if (caller.Symbol.Name != "StartAsync" || !IsLifecycle(caller.Symbol) || !caller.Symbol.ContainingType.AllInterfaces.Any(static type => TypeKey(type) == "Microsoft.Extensions.Hosting.IHostedService") || caller.Model.GetSymbolInfo(dispatch).Symbol is not IMethodSymbol taskRun || Normalize(taskRun) != "System.Threading.Tasks.Task.Run" || dispatch.Parent is not AssignmentExpressionSyntax assignment || assignment.Right != dispatch || caller.Model.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol field || TypeKey(field.Type) != "System.Threading.Tasks.Task" || !SymbolEqualityComparer.Default.Equals(field.ContainingType, caller.Symbol.ContainingType))
            {
                return null;
            }

            AuthoredMember[] owners = AuthoredMembers.Where(member => SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, caller.Symbol.ContainingType)).ToArray();

            bool RefersToField(AuthoredMember member, SyntaxNode syntax) => syntax.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, field));

            bool escapes = owners.Any(member => member.Syntax.DescendantNodes().Any(node => node is ArgumentSyntax argument && argument.RefKindKeyword.RawKind != 0 && RefersToField(member, argument.Expression) || node is RefExpressionSyntax reference && RefersToField(member, reference.Expression) || node is PrefixUnaryExpressionSyntax address && address.IsKind(SyntaxKind.AddressOfExpression) && RefersToField(member, address.Operand)));

            if (escapes || owners.SelectMany(member => member.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(write => RefersToField(member, write.Left))).Count() != 1 || dispatch.ArgumentList.Arguments.FirstOrDefault()?.Expression is not AnonymousFunctionExpressionSyntax { Body: InvocationExpressionSyntax targetCall } || caller.Model.GetSymbolInfo(targetCall).Symbol is not IMethodSymbol targetSymbol || Resolve(targetSymbol) is not { } target)
            {
                return null;
            }

            HostedProducerOperationEntry[] roots = declaredOperations.Where(entry => entry.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && entry.WorkKind is not null && entry.SourcePath == target.Syntax.SyntaxTree.FilePath && entry.EnclosingType == TypeKey(target.Symbol.ContainingType) && entry.Member == target.Symbol.Name && entry.OperationId == entry.EnclosingType + "." + entry.Member).ToArray();

            if (roots.Length != 1)
            {
                return null;
            }

            List<AwaitExpressionSyntax> joins = [];

            foreach (AuthoredMember stop in owners.Where(static member => member.Symbol.Name == "StopAsync" && IsLifecycle(member.Symbol)))
            {
                foreach (AwaitExpressionSyntax awaited in stop.Syntax.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<AwaitExpressionSyntax>())
                {
                    bool ExactNonNullGuard(IfStatementSyntax guard) => guard.Statement.Span.Contains(awaited.Span) && guard.Condition is IsPatternExpressionSyntax { Expression: { } tested, Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression } } } && SymbolEqualityComparer.Default.Equals(stop.Model.GetSymbolInfo(tested).Symbol, field);

                    if (awaited.Ancestors().Any(static node => node is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ConditionalExpressionSyntax or SwitchStatementSyntax) || awaited.Ancestors().OfType<IfStatementSyntax>().Any(guard => !ExactNonNullGuard(guard)))
                    {
                        continue;
                    }

                    ExpressionSyntax expression = awaited.Expression;

                    if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } configured })
                    {
                        expression = configured.Expression;
                    }

                    if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax wait } waitCall && stop.Model.GetSymbolInfo(waitCall).Symbol is IMethodSymbol waitMethod && Normalize(waitMethod) == "System.Threading.Tasks.Task.WaitAsync")
                    {
                        expression = wait.Expression;
                    }

                    if (SymbolEqualityComparer.Default.Equals(stop.Model.GetSymbolInfo(expression).Symbol, field))
                    {
                        joins.Add(awaited);
                    }
                }
            }

            return joins.Count == 1 ? new(target, roots[0], field, Location(dispatch), Location(joins[0])) : null;
        }

        private AuthoredMember? ResolveCallable(ExpressionSyntax expression, SemanticModel model)
        {
            if (expression is AnonymousFunctionExpressionSyntax lambda && model.GetOperation(lambda) is IAnonymousFunctionOperation operation)
            {
                return new(operation.Symbol, lambda.Body, model);
            }

            if (expression is not InvocationExpressionSyntax && model.GetSymbolInfo(expression).Symbol is IMethodSymbol method)
            {
                return Resolve(method);
            }

            if (model.GetSymbolInfo(expression).Symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer } variable && !variable.Ancestors().OfType<BlockSyntax>().First().DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)))
            {
                return initializer == expression ? null : ResolveCallable(initializer, model);
            }

            return null;
        }

        private static bool IsDelegateExpression(SemanticModel model, ExpressionSyntax expression)
        {
            Microsoft.CodeAnalysis.TypeInfo type = model.GetTypeInfo(expression);

            return type.Type?.TypeKind == TypeKind.Delegate || type.ConvertedType?.TypeKind == TypeKind.Delegate;
        }

        private IReadOnlySet<string> AdmissionOrigins(AuthoredMember member, ExpressionSyntax expression, HashSet<ISymbol> path)
        {
            bool cacheable = path.Count == 0;

            (int Compilation, int Tree, string Method, int MemberStart) memberIdentity = MemberIdentity(member);

            var key = (memberIdentity.Compilation, memberIdentity.Tree, memberIdentity.Method, memberIdentity.MemberStart, ExpressionStart: expression.SpanStart, ExpressionLength: expression.Span.Length, Bindings: AdmissionBindingIdentity(member));

            if (cacheable && admissionOrigins.TryGetValue(key, out IReadOnlySet<string>? cached))
            {
                return cached;
            }

            IReadOnlySet<string> resolved = ResolveAdmissionOrigins(member, expression, path);

            if (cacheable)
            {
                admissionOrigins[key] = resolved.ToImmutableHashSet(StringComparer.Ordinal);
            }

            return resolved;
        }

        private static string AdmissionBindingIdentity(AuthoredMember member)
        {
            if (member.AdmissionBindings is null)
            {
                return "unbound";
            }

            System.Text.StringBuilder identity = new();

            static void AppendPart(System.Text.StringBuilder target, string value) => target.Append(value.Length).Append(':').Append(value);

            foreach ((ISymbol symbol, IReadOnlySet<string> origins) in member.AdmissionBindings.OrderBy(static pair => pair.Key is IParameterSymbol parameter ? parameter.Ordinal : int.MaxValue).ThenBy(static pair => pair.Key.Kind).ThenBy(static pair => pair.Key.ToDisplayString(), StringComparer.Ordinal))
            {
                AppendPart(identity, symbol is IParameterSymbol parameter ? "parameter:" + parameter.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) : symbol.Kind + ":" + symbol.ToDisplayString());

                foreach (string origin in origins.Order(StringComparer.Ordinal))
                {
                    AppendPart(identity, origin);
                }

                identity.Append(';');
            }

            return identity.ToString();
        }

        private IReadOnlySet<string> ResolveAdmissionOrigins(AuthoredMember member, ExpressionSyntax expression, HashSet<ISymbol> path)
        {
            if (!HasAdmissionSeed(member))
            {
                return new HashSet<string>();
            }

            SemanticModel model = expression.SyntaxTree == member.Model.SyntaxTree ? member.Model : member.Model.Compilation.GetSemanticModel(expression.SyntaxTree);

            HashSet<string> origins = new(StringComparer.Ordinal);

            HashSet<ISymbol> references = new(SymbolEqualityComparer.Default);

            ISymbol? direct = model.GetSymbolInfo(expression).Symbol;

            if (direct is ILocalSymbol or IParameterSymbol)
            {
                references.Add(direct);
            }

            if (model.GetOperation(expression) is { } operation)
            {
                Stack<IOperation> pending = new();

                pending.Push(operation);

                while (pending.TryPop(out IOperation? current))
                {
                    if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
                    {
                        continue;
                    }

                    if (current is ILocalReferenceOperation local)
                    {
                        references.Add(local.Local);
                    }
                    else if (current is IParameterReferenceOperation parameter)
                    {
                        references.Add(parameter.Parameter);
                    }

                    foreach (IOperation child in current.ChildOperations)
                    {
                        pending.Push(child);
                    }
                }
            }

            foreach (ISymbol symbol in references)
            {
                origins.UnionWith(AdmissionOrigins(member, symbol, model, path));
            }

            return origins;
        }

        private bool HasAdmissionSeed(AuthoredMember member)
        {
            if (member.AdmissionBindings?.Values.Any(static origins => origins.Count > 0) == true)
            {
                return true;
            }

            (int Compilation, int Tree, string Method, int SpanStart) key = MemberIdentity(member);

            if (!admissionSeeds.TryGetValue(key, out bool seeded))
            {
                seeded = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) is "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

                if (!seeded)
                {
                    seeded = member.Syntax.DescendantNodesAndSelf(node => node == member.Syntax || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<IdentifierNameSyntax>().Select(identifier => member.Model.GetSymbolInfo(identifier).Symbol).OfType<ISymbol>().Where(static symbol => symbol is ILocalSymbol or IParameterSymbol).Distinct(SymbolEqualityComparer.Default).Any(symbol => HasAdmissionSeed(member, symbol, new HashSet<ISymbol>(SymbolEqualityComparer.Default)));
                }

                admissionSeeds[key] = seeded;
            }

            return seeded;
        }

        private static bool HasAdmissionSeed(AuthoredMember member, ISymbol symbol, HashSet<ISymbol> path)
        {
            if (!path.Add(symbol))
            {
                return false;
            }

            try
            {
                if (member.AdmissionBindings?.TryGetValue(symbol, out IReadOnlySet<string>? bound) == true && bound.Count > 0)
                {
                    return true;
                }

                SyntaxNode? declaration = symbol.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax();

                if (declaration is SingleVariableDesignationSyntax designation && designation.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is { } admission && member.Model.GetSymbolInfo(admission).Symbol is IMethodSymbol method && Normalize(method) is "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease")
                {
                    return true;
                }

                if (symbol is not ILocalSymbol || declaration is not VariableDeclaratorSyntax variable)
                {
                    return false;
                }

                IEnumerable<ExpressionSyntax> values = variable.Initializer is null ? [] : [variable.Initializer.Value];

                if (variable.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is { } block)
                {
                    values = values.Concat(block.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(assignment.Left).Symbol, symbol)).Select(static assignment => assignment.Right));
                }

                return values.SelectMany(static value => value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()).Select(identifier => member.Model.GetSymbolInfo(identifier).Symbol).OfType<ISymbol>().Where(static candidate => candidate is ILocalSymbol or IParameterSymbol).Distinct(SymbolEqualityComparer.Default).Any(candidate => HasAdmissionSeed(member, candidate, path));
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private IReadOnlySet<string> AdmissionOrigins(AuthoredMember member, ISymbol symbol, SemanticModel model, HashSet<ISymbol> path)
        {
            if (!path.Add(symbol))
            {
                return new HashSet<string>();
            }

            try
            {
                HashSet<string> origins = member.AdmissionBindings?.TryGetValue(symbol, out IReadOnlySet<string>? bound) == true ? new(bound, StringComparer.Ordinal) : new(StringComparer.Ordinal);

                SyntaxNode? declaration = symbol.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax();

                if (declaration is SingleVariableDesignationSyntax designation && designation.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is { } call && model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) is "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease")
                {
                    origins.Add(Location(call));
                }

                if (symbol is ILocalSymbol && declaration is VariableDeclaratorSyntax variable)
                {
                    if (variable.Initializer is not null)
                    {
                        origins.UnionWith(AdmissionOrigins(member, variable.Initializer.Value, path));
                    }

                    // May-alias joins deliberately retain prior origins across ambiguous assignments.
                    foreach (AssignmentExpressionSyntax assignment in variable.Ancestors().OfType<BlockSyntax>().First().DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, symbol)))
                    {
                        origins.UnionWith(AdmissionOrigins(member, assignment.Right, path));
                    }
                }

                return origins;
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private AuthoredMember BindAdmissionArguments(AuthoredMember caller, InvocationExpressionSyntax call, AuthoredMember target)
        {
            Dictionary<ISymbol, IReadOnlySet<string>> bindings = new(SymbolEqualityComparer.Default);

            foreach (var argument in AdmissionArguments(caller, call))
            {
                if (argument.Parameter is { } parameter && parameter.Ordinal < target.Symbol.Parameters.Length)
                {
                    bindings[target.Symbol.Parameters[parameter.Ordinal]] = AdmissionOrigins(caller, argument.Expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
                }
            }

            return target with { AdmissionBindings = bindings };
        }

        private static IEnumerable<(ExpressionSyntax Expression, IParameterSymbol? Parameter, RefKind RefKind)> AdmissionArguments(AuthoredMember caller, InvocationExpressionSyntax call)
        {
            if (caller.Model.GetOperation(call) is IInvocationOperation invocation)
            {
                // Roslyn includes the reduced extension receiver as an implicit argument to formal 0.
                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if ((argument.Syntax is ArgumentSyntax syntax ? syntax.Expression : argument.Value.Syntax) is ExpressionSyntax expression)
                    {
                        yield return (expression, argument.Parameter, argument.Parameter?.RefKind ?? RefKind.None);
                    }
                }
            }
            else
            {
                foreach (ArgumentSyntax argument in call.ArgumentList.Arguments)
                {
                    yield return (argument.Expression, null, argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ? RefKind.Out : argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ? RefKind.Ref : RefKind.None);
                }
            }
        }

        private static ExpressionSyntax? AdmissionReceiver(AuthoredMember member, InvocationExpressionSyntax call) => member.Model.GetOperation(call) is IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver } ? receiver : null;

        private static bool PreservesAdmissionReceiver(IMethodSymbol method) => Normalize(method) == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

        private static SyntaxNode? CompletionPoint(AuthoredMember member, InvocationExpressionSyntax call, bool inheritedCompletion)
        {
            SyntaxNode expression = CompletionPreservingExpression(call, member.Model);

            if (expression.Parent is AwaitExpressionSyntax awaited)
            {
                return awaited;
            }

            if (inheritedCompletion && (expression.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax || expression == member.Syntax && !member.Symbol.ReturnsVoid))
            {
                return call;
            }

            if (expression.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol local || variable.Parent?.Parent is not LocalDeclarationStatementSyntax { Parent: BlockSyntax block })
            {
                return null;
            }

            IdentifierNameSyntax[] uses = block.DescendantNodes().OfType<IdentifierNameSyntax>().Where(identifier => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(identifier).Symbol, local)).ToArray();

            if (uses.Length != 1 || CompletionPreservingExpression(uses[0], member.Model).Parent is not AwaitExpressionSyntax { Parent: ExpressionStatementSyntax statement } join || statement.Parent != block || statement.Expression != join || join.SpanStart < call.Span.End)
            {
                return null;
            }

            // An intervening call can throw before the join and unwind the retained handles.
            return block.Statements.Any(statement => statement.SpanStart > call.SpanStart && statement.Span.End < join.SpanStart) ? null : join;
        }

        private static SyntaxNode CompletionPreservingExpression(SyntaxNode expression, SemanticModel model)
        {
            while (true)
            {
                if (expression.Parent is ParenthesizedExpressionSyntax parentheses)
                {
                    expression = parentheses;
                }
                else if (expression.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait", Parent: InvocationExpressionSyntax configured } && model.GetSymbolInfo(configured).Symbol is IMethodSymbol method && Normalize(method) is "System.Threading.Tasks.Task.ConfigureAwait" or "System.Threading.Tasks.Task`1.ConfigureAwait" or "System.Threading.Tasks.ValueTask.ConfigureAwait" or "System.Threading.Tasks.ValueTask`1.ConfigureAwait")
                {
                    expression = configured;
                }
                else
                {
                    return expression;
                }
            }
        }

        private IReadOnlySet<int> VerifyFieldPublication(AuthoredMember caller, InvocationExpressionSyntax factoryCall, AuthoredMember factory, string operationId, string? inherited)
        {
            ITypeSymbol result = factory.Symbol.ReturnType;

            if (result is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                result = named.TypeArguments[0];
            }

            IFieldSymbol[] fields = result.GetMembers().OfType<IFieldSymbol>().Where(static field => field.Type.ToDisplayString() == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter").ToArray();

            if (fields.Length == 0 || factoryCall.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is not { } variable || caller.Model.GetDeclaredSymbol(variable) is not { } owner)
            {
                return new HashSet<int>();
            }

            string? group = EffectAt(caller, factoryCall, operationId, inherited);

            InvocationExpressionSyntax[] ownerCalls = caller.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(caller.Model.GetSymbolInfo(access.Expression).Symbol, owner) && IsUnconditionalExecution(caller, call, factoryCall.Span.End)).ToArray();

            bool Joined(InvocationExpressionSyntax call) => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && (TypeKey(method.ReturnType) is not ("System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1") || CompletionPoint(caller, call, false) is not null);

            AuthoredMember[] completionTargets = ownerCalls.Where(call => Joined(call) && caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "CompleteAsync" }).Select(call => Resolve((IMethodSymbol)caller.Model.GetSymbolInfo(call).Symbol!)).OfType<AuthoredMember>().ToArray();

            List<AuthoredMember> disposalTargets = ownerCalls.Where(call => Joined(call) && caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Dispose" or "DisposeAsync" }).Select(call => Resolve((IMethodSymbol)caller.Model.GetSymbolInfo(call).Symbol!)).OfType<AuthoredMember>().ToList();

            List<string?> disposalGroups = ownerCalls.Where(call => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Dispose" or "DisposeAsync" }).Select(call => EffectAt(caller, call, operationId, inherited)).ToList();

            if (variable.Parent is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax))
            {
                disposalGroups.Add(EffectAt(caller, declaration, operationId, inherited, DisposalPosition(declaration), declaration));

                bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                string methodName = asynchronous ? "DisposeAsync" : "Dispose";

                if (result is INamedTypeSymbol disposable && disposable.AllInterfaces.SingleOrDefault(type => TypeKey(type) == (asynchronous ? "System.IAsyncDisposable" : "System.IDisposable"))?.GetMembers(methodName).OfType<IMethodSymbol>().SingleOrDefault(static method => method.Parameters.Length == 0) is { } slot && disposable.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation && Resolve(implementation) is { } cleanup)
                {
                    disposalTargets.Add(cleanup);
                }
            }

            ObjectCreationExpressionSyntax[] returns = factory.Syntax.DescendantNodes().OfType<ReturnStatementSyntax>().Select(static statement => statement.Expression).OfType<ObjectCreationExpressionSyntax>().ToArray();

            HashSet<int> mappedCreations = [];

            List<(IFieldSymbol Field, AssignmentExpressionSyntax Assignment)> mappedFields = [];

            AuthoredMember? mappedConstructor = null;

            bool References(AuthoredMember member, SyntaxNode node, ISymbol symbol) => node.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, symbol));

            bool Written(AuthoredMember member, ISymbol symbol) => member.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => References(member, assignment.Left, symbol));

            ExpressionSyntax[] SymbolReferences(AuthoredMember member, ISymbol symbol) => member.Syntax.DescendantNodes().OfType<ExpressionSyntax>().Where(expression => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, symbol) && !expression.Ancestors().TakeWhile(ancestor => ancestor != member.Syntax).OfType<ExpressionSyntax>().Any(parent => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(parent).Symbol, symbol))).ToArray();

            bool HasOnlyReferences(AuthoredMember member, ISymbol symbol, params ExpressionSyntax[] allowed) => SymbolReferences(member, symbol) is { } references && references.Length == allowed.Length && references.All(reference => allowed.Contains(reference));

            bool mapped = returns.Length == 1 && factory.Model.GetOperation(returns[0]) is IObjectCreationOperation { Constructor: { } constructor } creation && Resolve(constructor) is { } constructorBody && SetMappedConstructor(constructorBody) && fields.All(field =>
            {
                AssignmentExpressionSyntax[] writes = constructorBody.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment => References(constructorBody, assignment.Left, field)).ToArray();

                if (!field.IsReadOnly || field.IsStatic || field.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null }) || writes.Length != 1 || !writes[0].IsKind(SyntaxKind.SimpleAssignmentExpression) || !IsUnconditionalExecution(constructorBody, writes[0], constructorBody.Syntax.SpanStart) || !SymbolEqualityComparer.Default.Equals(constructorBody.Model.GetSymbolInfo(writes[0].Left).Symbol, field))
                {
                    return false;
                }

                if (constructorBody.Model.GetSymbolInfo(writes[0].Right).Symbol is not IParameterSymbol parameter || Written(constructorBody, parameter) || !HasOnlyReferences(constructorBody, parameter, writes[0].Right) || creation.Arguments.SingleOrDefault(argument => argument.Parameter?.Ordinal == parameter.Ordinal)?.Value.Syntax is not ExpressionSyntax argument || factory.Model.GetSymbolInfo(argument).Symbol is not ILocalSymbol local || Written(factory, local) || !HasOnlyReferences(factory, local, argument) || local.DeclaringSyntaxReferences.Single().GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer })
                {
                    return false;
                }

                InvocationExpressionSyntax[] creations = initializer.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(call => factory.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync").ToArray();

                return creations.Length == 1 && IsUnconditionalExecution(factory, creations[0], factory.Syntax.SpanStart) && mappedCreations.Add(creations[0].SpanStart) && AddMappedField(field, writes[0]);
            });

            bool SetMappedConstructor(AuthoredMember constructor)
            {
                mappedConstructor = constructor;

                return true;
            }

            bool AddMappedField(IFieldSymbol field, AssignmentExpressionSyntax assignment)
            {
                mappedFields.Add((field, assignment));

                return true;
            }

            IEnumerable<(AuthoredMember Member, ExpressionSyntax Receiver)> FieldTerminals(IFieldSymbol field, string terminal, IEnumerable<AuthoredMember> targets) => targets.SelectMany(member => member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => IsUnconditionalExecution(member, call, member.Syntax.SpanStart) && call.Expression is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(access.Expression).Symbol, field) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && (TypeKey(method.ReturnType) is not ("System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1") || CompletionPoint(member, call, true) is not null) && (terminal == "Dispose" ? Normalize(method) is "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose" or "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync" or "System.IDisposable.Dispose" or "System.IAsyncDisposable.DisposeAsync" : Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync")).Select(call => (member, ((MemberAccessExpressionSyntax)call.Expression).Expression)));

            bool FieldHasTerminal(IFieldSymbol field, string terminal, IEnumerable<AuthoredMember> targets) => FieldTerminals(field, terminal, targets).Any();

            bool AllFieldsTerminate(string terminal, IEnumerable<AuthoredMember> targets) => targets.Any(target => target.Syntax.DescendantNodes().OfType<StatementSyntax>().All(static statement => statement is BlockSyntax or ExpressionStatementSyntax or ReturnStatementSyntax or LocalDeclarationStatementSyntax) && fields.All(field => FieldHasTerminal(field, terminal, [target])));

            bool FieldsHaveOnlyMappedTerminals() => mappedFields.All(mapping => AuthoredMembers.Where(member => SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, mapping.Field.ContainingType)).All(member =>
            {
                List<ExpressionSyntax> allowed = [];

                if (mappedConstructor is not null && SymbolEqualityComparer.Default.Equals(member.Symbol, mappedConstructor.Symbol))
                {
                    allowed.Add(mapping.Assignment.Left);
                }

                allowed.AddRange(FieldTerminals(mapping.Field, "CompleteAsync", [member]).Select(static terminal => terminal.Receiver));

                allowed.AddRange(FieldTerminals(mapping.Field, "Dispose", [member]).Select(static terminal => terminal.Receiver));

                return HasOnlyReferences(member, mapping.Field, [.. allowed]);
            }));

            bool valid = mapped && group is not null && disposalGroups.Count > 0 && disposalGroups.All(disposal => disposal == group) && ownerCalls.All(call => EffectAt(caller, call, operationId, inherited) == group) && AllFieldsTerminate("CompleteAsync", completionTargets) && AllFieldsTerminate("Dispose", disposalTargets) && FieldsHaveOnlyMappedTerminals();

            if (!valid)
            {
                diagnostics.Add(new("HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE", operationId + "/site@" + Location(factoryCall), "Field-backed publication requires exact constructor ownership and completion/disposal under its caller's one retained group."));
            }

            return valid ? mappedCreations : new HashSet<int>();
        }

        private static bool IsUnconditionalExecution(AuthoredMember member, SyntaxNode node, int after)
        {
            if (node.Ancestors().TakeWhile(ancestor => ancestor != member.Syntax).Any(static ancestor => ancestor is StatementSyntax and not (BlockSyntax or ExpressionStatementSyntax or ReturnStatementSyntax or LocalDeclarationStatementSyntax) || ancestor is ConditionalExpressionSyntax or SwitchExpressionSyntax or AnonymousFunctionExpressionSyntax))
            {
                return false;
            }

            return !member.Syntax.DescendantNodes(candidate => candidate == member.Syntax || candidate is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).Any(candidate => candidate.SpanStart >= after && candidate.Span.End < node.SpanStart && candidate is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax);
        }

        private string? EffectAt(AuthoredMember member, SyntaxNode site, string operationId, string? inherited, int? position = null, SyntaxNode? disposing = null)
        {
            string? local = FindRetainedAdmission(member, site, "TryBeginExternalEffectGroup", null, position, disposing);

            return local is null ? LiveInheritedAdmission(member, site, inherited, position) : operationId + "@effect:" + local;
        }

        private string? WorkAt(AuthoredMember member, SyntaxNode site, string operationId, GrimoireWorkKind? kind, string? inherited, int? position = null, SyntaxNode? disposing = null)
        {
            string? local = kind is null ? null : FindRetainedAdmission(member, site, "TryAcquireWorkLease", kind, position, disposing);

            return local is null ? LiveInheritedAdmission(member, site, inherited, position) : operationId + "@work:" + local;
        }

        private string? LiveInheritedAdmission(AuthoredMember member, SyntaxNode site, string? inherited, int? atPosition)
        {
            if (inherited is null)
            {
                return null;
            }

            string origin = inherited[(inherited.LastIndexOf("@", StringComparison.Ordinal) + 1)..];

            origin = origin[(origin.IndexOf(':') + 1)..];

            return AdmissionEndedBefore(member, origin, atPosition ?? site.SpanStart, new HashSet<string>(StringComparer.Ordinal)) ? null : inherited;
        }

        private bool AdmissionEndedBefore(AuthoredMember member, string origin, int position, HashSet<string> path)
        {
            string identity = MethodKey(member.Symbol) + "@" + member.Syntax.SpanStart;

            if (!path.Add(identity))
            {
                return true;
            }

            bool Matches(ExpressionSyntax expression) => AdmissionOrigins(member, expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Contains(origin);

            try
            {
                foreach (SyntaxNode node in member.Syntax.DescendantNodesAndSelf(node => node == member.Syntax || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
                {
                    // Returned or heap-stored handles leave the supported local/formal environment.
                    // Stop inheriting their authority instead of treating the escape as a new handle.
                    if (node.Span.End < position && (node is ReturnStatementSyntax { Expression: { } returned } && Matches(returned)
                        || node is ArrowExpressionClauseSyntax arrow && Matches(arrow.Expression)
                        || node is AssignmentExpressionSyntax assignment && member.Model.GetSymbolInfo(assignment.Left).Symbol is not ILocalSymbol and not IParameterSymbol && Matches(assignment.Right)))
                    {
                        return true;
                    }

                    if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" or "DisposeAsync" } access } disposal && disposal.Span.End < position && Matches(access.Expression)
                        || node is UsingStatementSyntax { Expression: { } resource } statement && statement.Span.End < position && Matches(resource)
                        || node is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax) && DisposalPosition(declaration) < position && declaration.Variables.Any(variable => variable.Initializer is not null && Matches(variable.Initializer.Value)))
                    {
                        return true;
                    }

                    if (node is InvocationExpressionSyntax call && call.Span.End < position && Location(call) != origin && AdmissionReceiver(member, call) is { } receiver && Matches(receiver) && (member.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol receiverMethod || !PreservesAdmissionReceiver(receiverMethod)))
                    {
                        return true;
                    }

                    if (node is InvocationExpressionSyntax argumentCall && argumentCall.Span.End < position && Location(argumentCall) != origin && AdmissionArguments(member, argumentCall).Any(argument => argument.Expression is not DeclarationExpressionSyntax && Matches(argument.Expression)))
                    {
                        if (AdmissionArguments(member, argumentCall).Any(argument => Matches(argument.Expression) && argument.RefKind is RefKind.Ref or RefKind.Out) || member.Model.GetSymbolInfo(argumentCall).Symbol is not IMethodSymbol method || Resolve(method) is not { } target || AdmissionEndedBefore(BindAdmissionArguments(member, argumentCall, target), origin, int.MaxValue, path))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            finally
            {
                path.Remove(identity);
            }
        }

        private void ValidatePublicationRegions(AuthoredMember member, SyntaxNode selection, string operationId, string? inherited, IReadOnlySet<int>? fieldPublications)
        {
            InvocationExpressionSyntax[] calls = member.Syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray();

            foreach (InvocationExpressionSyntax create in calls.Where(call => fieldPublications?.Contains(call.SpanStart) != true && selection.Span.Contains(call.Span) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync"))
            {
                VariableDeclaratorSyntax? variable = create.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();

                ISymbol? writer = variable is null ? null : member.Model.GetDeclaredSymbol(variable);

                string? creationGroup = EffectAt(member, create, operationId, inherited);

                bool ReceiverIsWriter(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(access.Expression).Symbol, writer);

                InvocationExpressionSyntax[] completions = calls.Where(call => writer is not null && ReceiverIsWriter(call) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "CompleteAsync" }).ToArray();

                InvocationExpressionSyntax[] disposals = calls.Where(call => writer is not null && ReceiverIsWriter(call) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Dispose" or "DisposeAsync" }).ToArray();

                List<string?> terminalGroups = disposals.Select(call => EffectAt(member, call, operationId, inherited)).ToList();

                if (variable?.Parent is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax))
                {
                    terminalGroups.Add(EffectAt(member, declaration, operationId, inherited, DisposalPosition(declaration), declaration));
                }

                bool incomplete = writer is null || creationGroup is null || completions.Length == 0 || terminalGroups.Count == 0 || completions.Any(call => EffectAt(member, call, operationId, inherited) != creationGroup) || terminalGroups.Any(group => group != creationGroup);

                if (incomplete)
                {
                    diagnostics.Add(new("HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE", operationId + "/site@" + Location(create), "Creation, completion, and every disposal must retain the same effect-group instance continuously."));
                }
            }
        }

        private string? FindRetainedAdmission(AuthoredMember member, SyntaxNode site, string admissionName, GrimoireWorkKind? kind, int? atPosition = null, SyntaxNode? disposing = null)
        {
            int position = atPosition ?? site.SpanStart;

            (int Compilation, int Tree, string Method, int SpanStart) memberKey = MemberIdentity(member);

            if (!admissionCalls.TryGetValue(memberKey, out InvocationExpressionSyntax[]? calls))
            {
                calls = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryAcquireWorkLease" or "TryBeginExternalEffectGroup" }).ToArray();

                admissionCalls[memberKey] = calls;
            }

            foreach (InvocationExpressionSyntax admission in calls.Where(call => call.SpanStart < position))
            {
                if (member.Model.GetSymbolInfo(admission).Symbol is not IMethodSymbol method || method.Name != admissionName || !IsGuardedAdmission(admission))
                {
                    continue;
                }

                string expected = admissionName == "TryAcquireWorkLease" ? "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease" : "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

                if (Normalize(method) != expected || kind is not null && member.Model.GetSymbolInfo(admission.ArgumentList.Arguments[0].Expression).Symbol?.Name != kind.ToString())
                {
                    continue;
                }

                IfStatementSyntax guard = admission.Ancestors().OfType<IfStatementSyntax>().First();

                bool negated = guard.Condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression };

                StatementSyntax? successful = negated ? guard.Else?.Statement : guard.Statement;

                BlockSyntax? successBranch = successful is BlockSyntax branch && branch.Span.Contains(position) ? branch : null;

                if (successBranch is null && (!negated || !FailureTerminates(guard)))
                {
                    continue;
                }

                BlockSyntax? block = successBranch ?? guard.Parent as BlockSyntax;

                int admittedAt = successBranch?.OpenBraceToken.Span.End ?? guard.Span.End;

                if (block is null || !site.Ancestors().Contains(block) || admittedAt > position || admission.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault() != site.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault())
                {
                    continue;
                }

                SingleVariableDesignationSyntax? designation = admission.ArgumentList.DescendantNodes().OfType<SingleVariableDesignationSyntax>().LastOrDefault();

                ISymbol? group = designation is null ? null : member.Model.GetDeclaredSymbol(designation);

                if (group is null)
                {
                    continue;
                }

                // A may-alias set can invalidate an instance, but cannot prove that a using owns it.
                bool ReferencesGroup(ExpressionSyntax expression) => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, group);

                bool retained = block.Statements.OfType<LocalDeclarationStatementSyntax>().Any(declaration => declaration.UsingKeyword.RawKind != 0 && declaration.SpanStart > admittedAt && declaration.Span.End < position && (disposing is null || disposing.Ancestors().OfType<BlockSyntax>().FirstOrDefault() != block || declaration.SpanStart < disposing.SpanStart) && declaration.Declaration.Variables.Any(variable => variable.Initializer is not null && ReferencesGroup(variable.Initializer.Value)))
                    || site.Ancestors().OfType<UsingStatementSyntax>().Any(statement => statement.Expression is not null && ReferencesGroup(statement.Expression));

                if (retained)
                {
                    bool disposed = AdmissionEndedBefore(member, Location(admission), position, new HashSet<string>(StringComparer.Ordinal));

                    if (disposed)
                    {
                        continue;
                    }

                    return Location(admission);
                }
            }

            return null;
        }

        private HostedProducerSiteKind? Classify(IMethodSymbol method, string callee, SyntaxNode node, SemanticModel model)
        {
            if (callee.StartsWith("<ambiguous:", StringComparison.Ordinal))
            {
                diagnostics.Add(new("HOSTED_CALL_TARGET_UNRESOLVED", Location(node), callee));

                return null;
            }

            if (Vocabulary.TryGetValue(callee, out HostedProducerSiteKind kind))
            {
                return kind;
            }

            if (callee is "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope" or "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory.CreateScope")
            {
                return HostedProducerSiteKind.ScopeCreation;
            }

            if (method.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "GrimoireConnectionAcquisitionRouteAttribute"))
            {
                return HostedProducerSiteKind.OrdinaryConnectionRoute;
            }

            if (callee is "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease" && IsGuardedAdmission(node))
            {
                return HostedProducerSiteKind.EffectFrontier;
            }

            if (TypeKey(method.ContainingType) == "System.IO.StreamWriter" && method.Name is ".ctor" or "Write" or "WriteAsync" or "WriteLine" or "WriteLineAsync" or "Flush" or "FlushAsync" or "Dispose" or "DisposeAsync" or "Close")
            {
                return HostedProducerSiteKind.FileSystemEffect;
            }

            if (callee is "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose" or "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync" or "System.IO.StreamWriter.Dispose" or "System.IO.StreamWriter.DisposeAsync")
            {
                return HostedProducerSiteKind.FileSystemEffect;
            }

            if (callee is "System.IO.FileStream..ctor" or "System.IO.File.Open")
            {
                IEnumerable<IArgumentOperation> arguments = model.GetOperation(node) switch
                {
                    IObjectCreationOperation creation => creation.Arguments,
                    IInvocationOperation invocation => invocation.Arguments,
                    _ => [],
                };

                bool open = arguments.Any(static argument => argument.Parameter?.Name == "mode" && argument.Value.ConstantValue is { HasValue: true, Value: int mode } && mode == (int)FileMode.Open);

                bool read = arguments.Any(static argument => argument.Parameter?.Name == "access" && argument.Value.ConstantValue is { HasValue: true, Value: int access } && access == (int)FileAccess.Read);

                return open && read ? HostedProducerSiteKind.FileSystemRead : HostedProducerSiteKind.FileSystemEffect;
            }

            if (SensitiveBoundaryTypes.Contains(TypeKey(method.ContainingType)))
            {
                diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), callee));
            }

            return null;
        }

        private void AddSite(HostedProducerSiteKind kind, string callee, AuthoredMember member, string rootType, string operationId, SyntaxNode node, string? effectGroup = null, string? workAdmitted = null)
        {
            emittedSiteEvidence++;

            HostedProducerOperationEntry? ordinary = declaredOperations.FirstOrDefault(entry => entry.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && (operationId == entry.OperationId || operationId.StartsWith(entry.OperationId + "/", StringComparison.Ordinal)));

            if (ordinary is not null && kind != HostedProducerSiteKind.EffectFrontier)
            {
                if (workAdmitted is null)
                {
                    diagnostics.Add(new("HOSTED_SITE_WORK_FRONTIER_MISSING", operationId + "/site@" + Location(node), callee));
                }

                if (RequiresExternalEffect(kind) && effectGroup is null)
                {
                    diagnostics.Add(new("HOSTED_SITE_EFFECT_FRONTIER_MISSING", operationId + "/site@" + Location(node), callee));
                }
            }

            if (kind == HostedProducerSiteKind.EffectFrontier && callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal))
            {
                workAdmitted = operationId + "@work:" + Location(node);
            }

            if (workAdmitted is not null)
            {
                operationId += "/work@" + Uri.EscapeDataString(workAdmitted);
            }

            if (kind == HostedProducerSiteKind.EffectFrontier && callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal))
            {
                effectGroup = operationId.Split("/work@", StringSplitOptions.None)[0] + "@effect:" + Location(node);
            }

            if (effectGroup is not null)
            {
                operationId += "/effect@" + Uri.EscapeDataString(effectGroup);
            }

            if (callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal) && node is InvocationExpressionSyntax invocation && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } expression && member.Model.GetSymbolInfo(expression).Symbol is IFieldSymbol workKind)
            {
                operationId += "/workKind=" + workKind.Name;
            }

            HostedProducerSite site = new(rootType, operationId + "/site@" + Location(node), member.Syntax.SyntaxTree.FilePath, TypeKey(member.Symbol.ContainingType), member.Symbol.Name, kind, callee);

            sites.TryAdd(SiteIdentity(site), site);
        }
    }

    private static bool IsLifecycle(IMethodSymbol method) => method.Name is "StartAsync" or "ExecuteAsync" or "StopAsync" && method.Parameters.Length == 1 && method.Parameters[0].Type.ToDisplayString() == "System.Threading.CancellationToken" && method.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task";

    private static bool RequiresExternalEffect(HostedProducerSiteKind kind) => kind is HostedProducerSiteKind.ProviderCall or HostedProducerSiteKind.FileSystemEffect;

    private static bool IsGuardedAdmission(SyntaxNode node)
    {
        IfStatementSyntax? guard = node.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();

        if (guard?.Condition == node)
        {
            return guard.Statement is BlockSyntax;
        }

        if (guard?.Condition is not PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } negation || !negation.Operand.Span.Contains(node.Span))
        {
            return false;
        }

        return guard.Else?.Statement is BlockSyntax || FailureTerminates(guard);
    }

    private static bool FailureTerminates(IfStatementSyntax guard)
    {
        StatementSyntax? failure = guard.Statement is BlockSyntax block ? block.Statements.LastOrDefault() : guard.Statement;

        return failure is ReturnStatementSyntax or ContinueStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax;
    }

    private static string TypeKey(ITypeSymbol type) => type is INamedTypeSymbol named ? (named.ContainingType is { } parent ? TypeKey(parent) + "." : named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace.ToDisplayString() + ".") + named.OriginalDefinition.MetadataName : type.ToDisplayString();

    private static string MethodKey(IMethodSymbol method) => (method.ReducedFrom ?? method).OriginalDefinition.GetDocumentationCommentId() ?? method.ToDisplayString();

    private static string Normalize(ISymbol symbol)
    {
        HashSet<string> candidates = new(StringComparer.Ordinal);

        if (symbol.ContainingType is { } type)
        {
            foreach (INamedTypeSymbol contract in type.AllInterfaces)
            {
                foreach (ISymbol slot in contract.GetMembers(symbol.Name))
                {
                    string key = TypeKey(contract) + "." + slot.Name;

                    if ((Vocabulary.ContainsKey(key) || key is "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease") && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(slot)?.OriginalDefinition, symbol.OriginalDefinition))
                    {
                        candidates.Add(key);
                    }
                }
            }
        }

        return candidates.Count switch { 0 => TypeKey(symbol.ContainingType!) + "." + symbol.Name, 1 => candidates.Single(), _ => "<ambiguous:" + string.Join(",", candidates.Order(StringComparer.Ordinal)) + ">" };
    }

    internal static HostedProducerInventoryValidation Validate(IReadOnlyList<HostedProducerServiceEntry> catalog, IReadOnlyList<NonHostedProducerChainEntry> nonHostedCatalog, HostedProducerDiscovery<string> registrations, HostedProducerDiscovery<HostedProducerSite> discoveredSites)
    {
        List<HostedProducerInventoryDiagnostic> diagnostics = [.. registrations.Diagnostics, .. discoveredSites.Diagnostics];

        Compare(catalog.Select(static entry => entry.ServiceType), registrations.Items, "HOSTED_SERVICE", diagnostics);

        HostedProducerOperationEntry[] operations = [.. catalog.SelectMany(static service => service.Operations), .. nonHostedCatalog.Select(static chain => new HostedProducerOperationEntry(chain.ChainId, chain.SourcePath, chain.EnclosingType, chain.Member, chain.Authority, null, chain.Proof, chain.Sites))];

        HostedProducerSite[] sites = [.. operations.SelectMany(static entry => entry.Sites)];

        Compare(sites.Select(SiteIdentity), discoveredSites.Items.Select(SiteIdentity), "HOSTED_SITE", diagnostics);

        foreach (string name in catalog.Select(static service => service.ServiceType))
        {
            CheckIdentity(name, diagnostics);
        }

        foreach (HostedProducerOperationEntry operation in operations)
        {
            foreach (string identity in new[] { operation.OperationId, operation.SourcePath, operation.EnclosingType, operation.Member })
            {
                CheckIdentity(identity, diagnostics);
            }

            if (!operation.SourcePath.EndsWith(".cs", StringComparison.Ordinal) || Path.IsPathRooted(operation.SourcePath))
            {
                diagnostics.Add(new("HOSTED_IDENTITY_INVALID", operation.OperationId, "SourcePath must name one source-relative .cs file."));
            }

            bool ordinary = operation.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork;

            if (ordinary && operation.WorkKind is null)
            {
                diagnostics.Add(new("HOSTED_WORK_KIND_MISSING", operation.OperationId, "Ordinary work must declare its work kind."));
            }

            if ((!ordinary && operation.WorkKind is not null) || operation.WorkKind is { } kind && !Enum.IsDefined(kind))
            {
                diagnostics.Add(new("HOSTED_WORK_KIND_INVALID", operation.OperationId, "A work kind belongs only to ordinary work and must be defined."));
            }

            if (!ordinary && string.IsNullOrWhiteSpace(operation.Proof))
            {
                diagnostics.Add(new("HOSTED_PROOF_MISSING", operation.OperationId, "Nonordinary authority needs an exact owning-member proof."));
            }

            if (operation.Proof is { Length: > 0 } proof && !proof.Contains(operation.EnclosingType + "." + operation.Member, StringComparison.Ordinal))
            {
                diagnostics.Add(new("HOSTED_PROOF_MEMBER_MISMATCH", operation.OperationId, "The proof must name this exact owning member."));
            }

            foreach (HostedProducerSite site in operation.Sites)
            {
                foreach (string identity in new[] { site.RootType, site.OperationId, site.SourcePath, site.EnclosingType, site.Member, site.Callee })
                {
                    CheckIdentity(identity, diagnostics);
                }

                if (!site.OperationId.StartsWith(operation.OperationId + "/", StringComparison.Ordinal) && site.OperationId != operation.OperationId || site.RootType != catalog.FirstOrDefault(service => service.Operations.Contains(operation))?.ServiceType && !nonHostedCatalog.Any(chain => chain.ChainId == operation.OperationId && chain.EnclosingType == site.RootType))
                {
                    diagnostics.Add(new("HOSTED_SITE_ROOT_MISMATCH", SiteIdentity(site), "The site must belong to this exact operation and owner root."));
                }

                if (operation.Authority == HostedProducerAuthorityKind.EffectFree && site.Kind != HostedProducerSiteKind.EffectFrontier)
                {
                    diagnostics.Add(new("HOSTED_SITE_AUTHORITY_INVALID", SiteIdentity(site), "Effect-free authority cannot contain a sensitive producer site."));
                }

                if (ordinary && site.Kind != HostedProducerSiteKind.EffectFrontier && !operation.Sites.Any(frontier => frontier.Kind == HostedProducerSiteKind.EffectFrontier && FrontierIdentity(site, "work") is { } lease && lease == FrontierIdentity(frontier, "work") && frontier.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal) && frontier.OperationId.Contains("/workKind=" + operation.WorkKind, StringComparison.Ordinal)))
                {
                    diagnostics.Add(new("HOSTED_SITE_WORK_FRONTIER_MISSING", SiteIdentity(site), "Ordinary work requires admission for its declared work kind before scopes and effects."));
                }

                if (ordinary && RequiresExternalEffect(site.Kind) && !operation.Sites.Any(frontier => frontier.Kind == HostedProducerSiteKind.EffectFrontier && EffectIdentity(site) is { } group && group == EffectIdentity(frontier) && frontier.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal)))
                {
                    diagnostics.Add(new("HOSTED_SITE_EFFECT_FRONTIER_MISSING", SiteIdentity(site), "External effects require TryBeginExternalEffectGroup in the same operation."));
                }
            }

            bool createsWriter = operation.Sites.Any(static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync");

            bool completesWriter = operation.Sites.Any(static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync");

            bool completeLifetime = operation.Sites.Where(static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync").All(create => EffectIdentity(create) is { } group && operation.Sites.Any(site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync" && EffectIdentity(site) == group) && operation.Sites.Any(site => site.Callee is "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose" or "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync" && EffectIdentity(site) == group));

            if (createsWriter != completesWriter || !completeLifetime)
            {
                diagnostics.Add(new("HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE", operation.OperationId, "A blob publication region must include both CreateWriterAsync and CompleteAsync, including writer disposal."));
            }
        }

        foreach (IGrouping<string, HostedProducerOperationEntry> group in operations.Where(static entry => !string.IsNullOrWhiteSpace(entry.Proof)).GroupBy(static entry => entry.Proof!, StringComparer.Ordinal).Where(static group => group.Count() > 1))
        {
            diagnostics.Add(new("HOSTED_PROOF_SHARED", group.Key, string.Join(", ", group.Select(static entry => entry.OperationId))));
        }

        return new(diagnostics);
    }

    internal static string SiteIdentity(HostedProducerSite site) => $"{site.RootType}|{site.OperationId}|{site.SourcePath}|{site.EnclosingType}|{site.Member}|{site.Kind}|{site.Callee}";

    private static string? EffectIdentity(HostedProducerSite site) => FrontierIdentity(site, "effect");

    private static string? FrontierIdentity(HostedProducerSite site, string kind)
    {
        string[] tags = site.OperationId.Split('/').Where(part => part.StartsWith(kind + "@", StringComparison.Ordinal)).ToArray();

        return tags.Length == 1 ? Uri.UnescapeDataString(tags[0][(kind.Length + 1)..]) : null;
    }

    private static void CheckIdentity(string value, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['*', '?']) >= 0 || value.EndsWith("/", StringComparison.Ordinal) || value.EndsWith(".", StringComparison.Ordinal))
        {
            diagnostics.Add(new("HOSTED_IDENTITY_INVALID", value, "An exact nonempty identity is required; namespace/directory wildcards are forbidden."));
        }
    }

    private static void Compare(IEnumerable<string> catalog, IEnumerable<string> discovery, string prefix, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        string[] expected = catalog.ToArray();

        string[] actual = discovery.ToArray();

        foreach (string missing in actual.Except(expected, StringComparer.Ordinal))
        {
            diagnostics.Add(new(prefix + "_UNCATALOGUED", missing, "Discovered in production, absent from the catalog."));
        }

        foreach (string stale in expected.Except(actual, StringComparer.Ordinal))
        {
            diagnostics.Add(new(prefix + "_STALE", stale, "Catalog identity no longer exists in production."));
        }

        foreach (string duplicate in expected.GroupBy(static value => value, StringComparer.Ordinal).Where(static group => group.Count() > 1).Select(static group => group.Key).Concat(actual.GroupBy(static value => value, StringComparer.Ordinal).Where(static group => group.Count() > 1).Select(static group => group.Key)).Distinct(StringComparer.Ordinal))
        {
            diagnostics.Add(new(prefix + "_DUPLICATE", duplicate, "Identity must occur exactly once on each side."));
        }
    }

    private const string HostedExtensions = "Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions";

    private const string ResetExtensions = "RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions";

    private const string ResetHelper = "AddInstallationResetRecoveryAwareHostedService";

    internal static HostedProducerDiscovery<string> DiscoverApplicationHostedServices(IReadOnlyList<CSharpCompilation> compilations)
    {
        List<string> services = [];

        List<HostedProducerInventoryDiagnostic> diagnostics = [];

        foreach (CSharpCompilation compilation in compilations)
        {
            INamedTypeSymbol? framework = compilation.GetTypeByMetadataName(HostedExtensions);

            IMethodSymbol[] overloads = framework?.GetMembers("AddHostedService").OfType<IMethodSymbol>().ToArray() ?? [];

            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                SemanticModel model = compilation.GetSemanticModel(tree);

                foreach (MethodDeclarationSyntax helper in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(static method => method.Identifier.ValueText == ResetHelper))
                {
                    if (model.GetDeclaredSymbol(helper)?.ContainingType.ToDisplayString() != ResetExtensions)
                    {
                        continue;
                    }

                    IMethodSymbol[] wrappers = helper.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(call => model.GetSymbolInfo(call).Symbol).OfType<IMethodSymbol>().Where(call => overloads.Any(method => SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, (call.ReducedFrom ?? call).OriginalDefinition))).ToArray();

                    if (wrappers.Length != 1 || wrappers[0].TypeArguments.SingleOrDefault() is not INamedTypeSymbol wrapper || TypeKey(wrapper) != "RetroDownfall.Arcanum.Infrastructure.Hosting.InstallationResetRecoveryAwareHostedService`1" || wrapper.TypeArguments.Length != 1 || wrapper.TypeArguments[0] is not ITypeParameterSymbol parameter || !SymbolEqualityComparer.Default.Equals(parameter, model.GetDeclaredSymbol(helper)?.TypeParameters.SingleOrDefault()))
                    {
                        diagnostics.Add(new("HOSTED_REGISTRATION_HELPER_SHAPE_CHANGED", Location(helper), "The reset-aware helper must register exactly one InstallationResetRecoveryAwareHostedService<TService>."));
                    }
                }

                foreach (InvocationExpressionSyntax invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string name = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                        SimpleNameSyntax simple => simple.Identifier.ValueText,
                        _ => "",
                    };

                    if (name is not "AddHostedService" and not ResetHelper)
                    {
                        continue;
                    }

                    IMethodSymbol? bound = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

                    IMethodSymbol? definition = (bound?.ReducedFrom ?? bound)?.OriginalDefinition;

                    MethodDeclarationSyntax? enclosingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                    if (enclosingMethod?.Identifier.ValueText == ResetHelper && model.GetDeclaredSymbol(enclosingMethod)?.ContainingType.ToDisplayString() == ResetExtensions)
                    {
                        continue;
                    }

                    bool frameworkRegistration = overloads.Any(method => SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, definition));

                    bool helperRegistration = definition?.ContainingType.ToDisplayString() == ResetExtensions && definition.Name == ResetHelper;

                    if ((!frameworkRegistration && !helperRegistration) || bound?.TypeArguments.Length != 1 || bound.TypeArguments[0] is not INamedTypeSymbol implementation || implementation.IsAbstract || implementation.TypeKind != TypeKind.Class || ContainsOpenType(implementation) || !implementation.AllInterfaces.Any(static type => type.ToDisplayString() == "Microsoft.Extensions.Hosting.IHostedService"))
                    {
                        diagnostics.Add(new("HOSTED_REGISTRATION_UNSUPPORTED_SHAPE", Location(invocation), invocation.ToString()));

                        continue;
                    }

                    services.Add(implementation.Name);
                }
            }
        }

        return new(services, diagnostics);
    }

    private static bool ContainsOpenType(ITypeSymbol type) => type.TypeKind == TypeKind.TypeParameter || type is INamedTypeSymbol named && named.TypeArguments.Any(ContainsOpenType);

    private static string Location(SyntaxNode node) => $"{node.SyntaxTree.FilePath}:{node.SpanStart}";
}
