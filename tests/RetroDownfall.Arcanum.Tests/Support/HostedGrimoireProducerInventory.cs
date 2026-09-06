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

    private sealed record AuthoredMember(IMethodSymbol Symbol, SyntaxNode Syntax, SemanticModel Model);

    private sealed class ProducerGraph
    {
        private readonly Dictionary<string, AuthoredMember> members = new(StringComparer.Ordinal);

        private readonly Dictionary<string, HashSet<string>> bindings = new(StringComparer.Ordinal);

        private readonly List<(InvocationExpressionSyntax Call, SemanticModel Model)> invocations = [];

        private readonly List<HostedProducerInventoryDiagnostic> diagnostics = [];

        private readonly Dictionary<string, HostedProducerSite> sites = new(StringComparer.Ordinal);

        private readonly HashSet<string> lifecycleMembers = new(StringComparer.Ordinal);

        private HostedProducerOperationEntry[] declaredOperations = [];

        private readonly Dictionary<string, InvocationExpressionSyntax[]> admissionCalls = new(StringComparer.Ordinal);

        private readonly Dictionary<string, List<SyntaxNode>> selectedRoots = new(StringComparer.Ordinal);

        private readonly Dictionary<string, AuthoredMember?> resolvedMembers = new(StringComparer.Ordinal);

        private long emittedSiteEvidence;

        internal ProducerGraph(IReadOnlyList<CSharpCompilation> compilations)
        {
            foreach (CSharpCompilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    if (!tree.FilePath.StartsWith("src/", StringComparison.Ordinal) || tree.FilePath.Contains("/obj/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    SemanticModel model = compilation.GetSemanticModel(tree);

                    foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                    {
                        if (node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax && model.GetDeclaredSymbol(node) is IMethodSymbol method)
                        {
                            members[MethodKey(method)] = new(method, node, model);
                        }

                        if (node is PropertyDeclarationSyntax { ExpressionBody.Expression: { } expression } property && model.GetDeclaredSymbol(property)?.GetMethod is { } getter)
                        {
                            members[MethodKey(getter)] = new(getter, expression, model);
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

            if (symbol is IMethodSymbol factory && members.TryGetValue(MethodKey(factory), out AuthoredMember? authored))
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
                AuthoredMember[] roots = members.Values.Where(member => member.Syntax.SyntaxTree.FilePath == operation.SourcePath && TypeKey(member.Symbol.ContainingType) == operation.EnclosingType && member.Symbol.Name == operation.Member).ToArray();

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

            foreach (AuthoredMember root in members.Values.Where(member => hosted.Contains(member.Symbol.ContainingType.Name) && IsLifecycle(member.Symbol)))
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
            string key = MethodKey(method);

            if (resolvedMembers.TryGetValue(key, out AuthoredMember? cached))
            {
                return cached;
            }

            if (members.TryGetValue(key, out AuthoredMember? authored) && authored.Syntax is not MethodDeclarationSyntax { Body: null, ExpressionBody: null })
            {
                return resolvedMembers[key] = authored;
            }

            if (method.ContainingType.TypeKind == TypeKind.Interface && bindings.TryGetValue(TypeKey(method.ContainingType), out HashSet<string>? targets) && targets.Count == 1)
            {
                return resolvedMembers[key] = members.Values.FirstOrDefault(candidate => TypeKey(candidate.Symbol.ContainingType) == targets.Single() && candidate.Symbol.ContainingType.AllInterfaces.SelectMany(static contract => contract.GetMembers()).OfType<IMethodSymbol>().Any(slot => MethodKey(slot) == key && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation && MethodKey(implementation) == MethodKey(candidate.Symbol)));
            }

            return resolvedMembers[key] = null;
        }

        private void Traverse(AuthoredMember member, string rootType, string operationId, HashSet<string> visited, bool lifecycle, string? inheritedWork = null, string? inheritedEffect = null, SyntaxNode? selection = null, bool fieldPublication = false)
        {
            if (!visited.Add(MethodKey(member.Symbol)))
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

                foreach (SyntaxNode node in (selection ?? member.Syntax).DescendantNodesAndSelf(node => node == (selection ?? member.Syntax) || node is not LocalFunctionStatementSyntax && (node is not AnonymousFunctionExpressionSyntax lambda || IsJoinedCallback(lambda, member.Model))))
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

                        if (target?.Symbol.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "GrimoireConnectionAcquisitionRouteAttribute") == true && kind is null)
                        {
                            AddSite(HostedProducerSiteKind.OrdinaryConnectionRoute, Normalize(target.Symbol), member, rootType, operationId, node, effectGroup, workAdmitted);
                        }

                        if (kind is null || aggregate)
                        {
                            if (target is not null)
                            {
                                if (declaredOperations.Any(entry => entry.SourcePath == target.Syntax.SyntaxTree.FilePath && entry.EnclosingType == TypeKey(target.Symbol.ContainingType) && entry.Member == target.Symbol.Name && entry.OperationId == entry.EnclosingType + "." + entry.Member))
                                {
                                    continue;
                                }

                                long before = emittedSiteEvidence;

                                bool carrier = node is InvocationExpressionSyntax call && VerifyFieldPublication(member, call, target, operationId, inheritedEffect);

                                Traverse(target, rootType, operationId + "/call@" + Location(node), visited, lifecycle, workAdmitted, effectGroup, fieldPublication: carrier);

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

                        if (property.GetMethod is { } getter && Resolve(getter) is { } target)
                        {
                            Traverse(target, rootType, operationId + "/get@" + Location(node), visited, lifecycle, workAdmitted, effectGroup);
                        }
                    }
                    else if (node is InvocationExpressionSyntax && symbol is null)
                    {
                        diagnostics.Add(new("HOSTED_CALL_TARGET_UNRESOLVED", Location(node), node.ToString()));
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

                if (!fieldPublication)
                {
                    ValidatePublicationRegions(member, selection ?? member.Syntax, operationId, inheritedEffect);
                }

            }
            finally
            {
                // Only the active recursion path is a cycle. A later sibling may carry different authority.
                visited.Remove(MethodKey(member.Symbol));
            }
        }

        private static int DisposalPosition(VariableDeclarationSyntax declaration) => declaration.Parent is UsingStatementSyntax statement ? statement.Statement.Span.End - 1 : declaration.Ancestors().OfType<BlockSyntax>().First().CloseBraceToken.SpanStart;

        private static bool IsJoinedCallback(AnonymousFunctionExpressionSyntax lambda, SemanticModel model) => lambda.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is { } call && model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) is "System.Threading.Tasks.Task.Run" or "System.Threading.Tasks.Parallel.ForEachAsync";

        private bool VerifyFieldPublication(AuthoredMember caller, InvocationExpressionSyntax factoryCall, AuthoredMember factory, string operationId, string? inherited)
        {
            ITypeSymbol result = factory.Symbol.ReturnType;

            if (result is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                result = named.TypeArguments[0];
            }

            IFieldSymbol[] fields = result.GetMembers().OfType<IFieldSymbol>().Where(static field => field.Type.ToDisplayString() == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter").ToArray();

            if (fields.Length == 0 || factoryCall.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is not { } variable || caller.Model.GetDeclaredSymbol(variable) is not { } owner)
            {
                return false;
            }

            string? group = EffectAt(caller, factoryCall, operationId, inherited);

            InvocationExpressionSyntax[] ownerCalls = caller.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(caller.Model.GetSymbolInfo(access.Expression).Symbol, owner)).ToArray();

            List<string?> disposalGroups = ownerCalls.Where(call => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Dispose" or "DisposeAsync" }).Select(call => EffectAt(caller, call, operationId, inherited)).ToList();

            if (variable.Parent is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax))
            {
                disposalGroups.Add(EffectAt(caller, declaration, operationId, inherited, DisposalPosition(declaration), declaration));
            }

            ObjectCreationExpressionSyntax[] returns = factory.Syntax.DescendantNodes().OfType<ReturnStatementSyntax>().Select(static statement => statement.Expression).OfType<ObjectCreationExpressionSyntax>().ToArray();

            bool mapped = returns.Length == 1 && factory.Model.GetSymbolInfo(returns[0]).Symbol is IMethodSymbol constructor && Resolve(constructor) is { } constructorBody && fields.All(field => constructorBody.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => SymbolEqualityComparer.Default.Equals(constructorBody.Model.GetSymbolInfo(assignment.Left).Symbol, field) && constructorBody.Model.GetSymbolInfo(assignment.Right).Symbol is IParameterSymbol parameter && returns[0].ArgumentList?.Arguments.ElementAtOrDefault(parameter.Ordinal)?.Expression is { } argument && factory.Model.GetSymbolInfo(argument).Symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.Single().GetSyntax() is VariableDeclaratorSyntax value && value.Initializer?.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => factory.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync") == true));

            bool FieldHasTerminal(IFieldSymbol field, string terminal) => members.Values.Where(member => SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, result) && (member.Symbol.Name == "CompleteAsync" || member.Symbol.Name is "Dispose" or "DisposeAsync")).Any(member => member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => call.Expression is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(access.Expression).Symbol, field) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && (terminal == "Dispose" ? method.Name is "Dispose" or "DisposeAsync" : method.Name == terminal)));

            bool valid = mapped && group is not null && disposalGroups.Count > 0 && disposalGroups.All(disposal => disposal == group) && ownerCalls.Any(call => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "CompleteAsync" }) && ownerCalls.All(call => EffectAt(caller, call, operationId, inherited) == group) && fields.All(field => FieldHasTerminal(field, "CompleteAsync") && FieldHasTerminal(field, "Dispose"));

            if (!valid)
            {
                diagnostics.Add(new("HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE", operationId + "/site@" + Location(factoryCall), "Field-backed publication requires exact constructor ownership and completion/disposal under its caller's one retained group."));
            }

            return valid;
        }

        private string? EffectAt(AuthoredMember member, SyntaxNode site, string operationId, string? inherited, int? position = null, SyntaxNode? disposing = null)
        {
            string? local = FindRetainedAdmission(member, site, "TryBeginExternalEffectGroup", null, position, disposing);

            return local is null ? inherited : operationId + "@effect:" + local;
        }

        private string? WorkAt(AuthoredMember member, SyntaxNode site, string operationId, GrimoireWorkKind? kind, string? inherited, int? position = null, SyntaxNode? disposing = null)
        {
            string? local = kind is null ? null : FindRetainedAdmission(member, site, "TryAcquireWorkLease", kind, position, disposing);

            return local is null ? inherited : operationId + "@work:" + local;
        }

        private void ValidatePublicationRegions(AuthoredMember member, SyntaxNode selection, string operationId, string? inherited)
        {
            InvocationExpressionSyntax[] calls = member.Syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray();

            foreach (InvocationExpressionSyntax create in calls.Where(call => selection.Span.Contains(call.Span) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync"))
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

            string memberKey = MethodKey(member.Symbol);

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

                bool ReferencesGroup(SyntaxNode expression) => expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(identifier).Symbol, group));

                bool retained = block.Statements.OfType<LocalDeclarationStatementSyntax>().Any(declaration => declaration.UsingKeyword.RawKind != 0 && declaration.SpanStart > admittedAt && declaration.Span.End < position && (disposing is null || disposing.Ancestors().OfType<BlockSyntax>().FirstOrDefault() != block || declaration.SpanStart < disposing.SpanStart) && declaration.Declaration.Variables.Any(variable => variable.Initializer is not null && ReferencesGroup(variable.Initializer.Value)))
                    || site.Ancestors().OfType<UsingStatementSyntax>().Any(statement => statement.Expression is not null && ReferencesGroup(statement.Expression));

                if (retained)
                {
                    HashSet<ISymbol> aliases = new(SymbolEqualityComparer.Default) { group };

                    foreach (VariableDeclaratorSyntax variable in block.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(static statement => statement.Declaration.Variables).Where(variable => variable.Initializer is not null && variable.SpanStart < position && ReferencesGroup(variable.Initializer.Value)))
                    {
                        if (member.Model.GetDeclaredSymbol(variable) is { } alias)
                        {
                            aliases.Add(alias);
                        }
                    }

                    bool disposed = block.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => call.SpanStart > admittedAt && call.Span.End < position && call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" or "DisposeAsync" } access && member.Model.GetSymbolInfo(access.Expression).Symbol is { } receiver && aliases.Contains(receiver));

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
