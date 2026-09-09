using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using Microsoft.CodeAnalysis.Diagnostics;

using System.Collections.Immutable;

using System.Diagnostics;

using System.Globalization;

using System.Reflection;

using System.Runtime.CompilerServices;

using System.Security.Cryptography;

using System.Text;

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

    DatabaseAccess = 7,
}

internal sealed record HostedProducerSite(
    string RootType,
    string OperationId,
    string SourcePath,
    string EnclosingType,
    string Member,
    HostedProducerSiteKind Kind,
    string Callee,
    string AuthorityOperationId = "",
    string MemberIdentity = "",
    int SyntaxOrdinal = -1,
    string SyntaxFingerprint = "",
    string? WorkFrontierCapsuleId = null,
    string? EffectFrontierCapsuleId = null,
    GrimoireWorkKind? AdmittedWorkKind = null)
{
    internal string CapsuleId => HostedGrimoireProducerInventory.CapsuleId(this);
}

internal sealed record HostedProducerOperationEntry(string OperationId, string SourcePath, string EnclosingType, string Member, HostedProducerAuthorityKind Authority, GrimoireWorkKind? WorkKind, string? Proof, IReadOnlyList<HostedProducerSite> Sites);

internal sealed record HostedProducerServiceEntry(string ServiceType, IReadOnlyList<HostedProducerOperationEntry> Operations);

internal sealed record NonHostedProducerChainEntry(string ChainId, string SourcePath, string EnclosingType, string Member, HostedProducerAuthorityKind Authority, string Proof, IReadOnlyList<HostedProducerSite> Sites);

internal sealed record HostedProducerInventoryValidation(IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics)
{
    internal bool IsValid => Diagnostics.Count == 0;
}

internal sealed record HostedProducerInventoryDiagnostic(string Code, string Identity, string Detail);

internal sealed record HostedProducerDiscovery<T>(IReadOnlyList<T> Items, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics);

internal sealed record HostedProducerCapsuleManifest(IReadOnlyList<HostedProducerSite> Sites, IReadOnlyList<HostedProducerInventoryDiagnostic> Diagnostics);

internal static class HostedGrimoireProducerInventory
{
    private static readonly Lazy<IReadOnlyList<CSharpCompilation>> SourceCompilations = new(BuildProductionCompilations);

    internal static IReadOnlyList<CSharpCompilation> ProductionCompilations => SourceCompilations.Value;

    private static readonly Lazy<HostedProducerDiscovery<HostedProducerSite>> ProductionSites = new(DiscoverProductionSites);

    internal static HostedProducerDiscovery<HostedProducerSite> ProductionSiteDiscovery => ProductionSites.Value;

    private static readonly Lazy<HostedProducerInventoryValidation> ProductionValidation = new(ValidateProductionSources);

    private static readonly Lazy<HostedProducerCapsuleManifest> ReviewedCapsules = new(LoadReviewedCapsules);

    private const string CapsuleManifestHeader = "# arcanum-hosted-producer-capsules v1";

    private const string CapsuleManifestColumns = "# root_type\tauthority_operation_id\tsource_path\tenclosing_type\tmember\tmember_identity\tkind\tcallee\tsyntax_ordinal\tsyntax_sha256\twork_frontier_capsule_sha256\teffect_frontier_capsule_sha256\tadmitted_work_kind";

    private static IReadOnlyList<CSharpCompilation> BuildProductionCompilations()
    {
        string repository = FindRepositoryRoot();

        List<CSharpCompilation> compilations = [];

        string[] projects = ["RetroDownfall.Arcanum.Core", "RetroDownfall.Arcanum.Secrets", "RetroDownfall.Arcanum.Infrastructure", "RetroDownfall.Arcanum.Api", "RetroDownfall.Arcanum.Cli"];

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

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        string sourceDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("The inventory source path has no directory.");

        foreach (string startDirectory in new[] { sourceDirectory, global::System.Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(startDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate the Arcanum source root.");
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

        HostedProducerInventoryValidation validation = Validate(Catalog, NonHostedCatalog, registrations, ProductionSiteDiscovery);

        return new([.. ReviewedCapsules.Value.Diagnostics, .. ValidateManifestRoots(ReviewedCapsules.Value.Sites), .. validation.Diagnostics]);
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverProductionSites()
    {
        HostedProducerDiscovery<string> registrations = DiscoverApplicationHostedServices(ProductionCompilations);

        return DiscoverProducerSites(ProductionCompilations, registrations);
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
        ["RetroDownfall.Arcanum.Core.Security.IDnsResolver.GetHostAddressesAsync"] = HostedProducerSiteKind.ProviderCall,
        ["RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonRunner.RunScheduledAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.Daemons.IDaemonJob.RunAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpClient.GetToolsAsync"] = HostedProducerSiteKind.ProviderCall,
        ["RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpClient.InitializeAsync"] = HostedProducerSiteKind.ProviderCall,
        ["RetroDownfall.Arcanum.Infrastructure.Weave.TapestryWeaver.WeaveAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StartAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync"] = (HostedProducerSiteKind)3,
        ["RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync"] = HostedProducerSiteKind.FileSystemEffect,
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
        ["RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerCredentialCapabilitySource.OpenFixedSlot"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerCredentialCapabilitySource.ProveFixedSlotDurablyAbsent"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Secrets.Security.IOsCredentialPresenceProbe.ProbePresence"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.Directory.Exists"] = (HostedProducerSiteKind)4,
        ["System.IO.Directory.EnumerateDirectories"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.Directory.EnumerateFileSystemEntries"] = (HostedProducerSiteKind)4,
        ["System.IO.Directory.EnumerateFiles"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.Directory.GetFileSystemEntries"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.Directory.ResolveLinkTarget"] = (HostedProducerSiteKind)4,
        ["System.IO.Directory.Move"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.File.Exists"] = (HostedProducerSiteKind)4,
        ["System.IO.File.GetAttributes"] = (HostedProducerSiteKind)4,
        ["System.IO.File.GetLastWriteTimeUtc"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.File.GetUnixFileMode"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.File.OpenRead"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.File.ReadAllBytes"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.File.ReadAllBytesAsync"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.File.ReadAllText"] = (HostedProducerSiteKind)4,
        ["System.IO.File.ReadAllTextAsync"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.File.ResolveLinkTarget"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.FileStream.Length"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.FileStream.Position setter"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileStream.Flush"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileStream.FlushAsync"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileStream.Read"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.FileStream.ReadAsync"] = HostedProducerSiteKind.FileSystemRead,
        ["System.IO.FileStream.Write"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileStream.WriteAsync"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileStream.DisposeAsync"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileStream.SetLength"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.FileInfo.Length"] = (HostedProducerSiteKind)4,
        ["System.IO.FileInfo.LastWriteTimeUtc"] = (HostedProducerSiteKind)4,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.CreateFile"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.GetFileInformationByHandle"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.NtOpenFile"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.OpenAtUnix"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.OpenUnix"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.fstat"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.lstat"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.stat"] = HostedProducerSiteKind.FileSystemRead,
        ["RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions.GetEffectiveUserIdNative"] = HostedProducerSiteKind.FileSystemRead,
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
        ["RetroDownfall.Arcanum.Secrets.Security.IHostProcessToolsMarkerNativeRecordCapability.CompareDeleteExact"] = HostedProducerSiteKind.FileSystemEffect,
        ["RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.MigrateAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionFileProcessor.ReencryptAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Infrastructure.Backup.OwnedTemporaryDirectory.TryDelete"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.DataLifecycle.IDataRetentionService.ApplyAsync"] = (HostedProducerSiteKind)5,
        ["RetroDownfall.Arcanum.Core.Storage.IUploadedFileRepository.CreateForOwnedFileAsync"] = (HostedProducerSiteKind)5,
        ["System.IO.Directory.CreateDirectory"] = (HostedProducerSiteKind)5,
        ["System.IO.Directory.Delete"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.File.Copy"] = HostedProducerSiteKind.FileSystemEffect,
        ["System.IO.File.WriteAllText"] = (HostedProducerSiteKind)5,
        ["System.IO.File.Delete"] = (HostedProducerSiteKind)5,
        ["System.IO.File.Move"] = (HostedProducerSiteKind)5,
        ["System.IO.File.SetUnixFileMode"] = HostedProducerSiteKind.FileSystemEffect,
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

    private static readonly IReadOnlySet<string> PureSensitiveMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.RtlNtStatusToDosError",
    };

    private static readonly IReadOnlySet<string> ReviewedBoundedIntrinsicMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Array.Resize",
        "System.Boolean.TryParse",
        "System.Buffers.Text.Base64.DecodeFromUtf8",
        "System.Buffers.Text.Base64.EncodeToUtf8",
        "System.Buffers.Text.Base64Url.TryDecodeFromChars",
        "System.Collections.Concurrent.ConcurrentDictionary`2.TryRemove",
        "System.Collections.Concurrent.ConcurrentQueue`1.TryDequeue",
        "System.Collections.Generic.Dictionary`2.Remove",
        "System.Collections.Generic.Queue`1.TryDequeue",
        "System.Collections.Generic.Queue`1.TryPeek",
        "System.Collections.Generic.Stack`1.TryPop",
        "System.Convert.TryFromBase64String",
        "System.Convert.TryToBase64Chars",
        "System.DateTimeOffset.TryParseExact",
        "System.Decimal.TryParse",
        "System.Enum.TryParse",
        "System.Guid.TryParse",
        "System.Guid.TryParseExact",
        "System.Guid.TryWriteBytes",
        "System.Int32.TryParse",
        "System.Int64.TryParse",
        "System.Net.IPAddress.TryParse",
        "System.Text.Encoding.TryGetBytes",
        "System.Text.StringBuilder.AppendLine",
        "System.Text.Json.JsonElement.TryGetProperty",
        "System.Text.Json.Nodes.JsonValue.TryGetValue",
        "System.Uri.TryCreate",
        "System.UInt32.TryParse",
        "System.UInt64.TryParse",
    };

    private static readonly IReadOnlySet<string> ReviewedInProcessSynchronizationMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Threading.Interlocked.Add",
        "System.Threading.Interlocked.CompareExchange",
        "System.Threading.Interlocked.Decrement",
        "System.Threading.Interlocked.Exchange",
        "System.Threading.Interlocked.Increment",
        "System.Threading.Volatile.Read",
        "System.Threading.Volatile.Write",
    };

    private static readonly IReadOnlySet<string> ReviewedInMemoryEnumerableTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.String",
    };

    private static readonly IReadOnlySet<string> ReviewedProcessRuntimeInitializationMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "SQLitePCL.SQLite3Provider_e_sqlcipher..ctor",
        "SQLitePCL.raw.FreezeProvider",
        "SQLitePCL.raw.SetProvider",
    };

    private static readonly IReadOnlySet<string> ReviewedIntrinsicExternalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.AggregateException",
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException",
        "System.Array",
        "System.BitConverter",
        "System.Boolean",
        "System.Buffers.Binary.BinaryPrimitives",
        "System.Buffers.Text.Base64",
        "System.Buffers.Text.Base64Url",
        "System.Char",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Generic.CollectionExtensions",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.KeyValuePair`2",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Immutable.ImmutableArray",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.ImmutableArray`1.Builder",
        "System.Collections.ObjectModel.Collection`1",
        "System.Convert",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.Decimal",
        "System.Diagnostics.Stopwatch",
        "System.Double",
        "System.Enum",
        "System.Exception",
        "System.FormatException",
        "System.Globalization.CultureInfo",
        "System.Guid",
        "System.IO.InvalidDataException",
        "System.IO.DirectoryNotFoundException",
        "System.IO.EndOfStreamException",
        "System.IO.FileNotFoundException",
        "System.IO.IOException",
        "System.IO.MemoryStream",
        "System.IO.Path",
        "System.Int32",
        "System.Int64",
        "System.IntPtr",
        "System.InvalidCastException",
        "System.InvalidOperationException",
        "System.Math",
        "System.Memory`1",
        "System.MemoryExtensions",
        "System.Net.IPAddress",
        "System.Net.WebUtility",
        "System.NotSupportedException",
        "System.Nullable`1",
        "System.Numerics.BigInteger",
        "System.Object",
        "System.ObjectDisposedException",
        "System.OperatingSystem",
        "System.OperationCanceledException",
        "System.OverflowException",
        "System.ReadOnlyMemory`1",
        "System.ReadOnlySpan`1",
        "System.Reflection.AssemblyInformationalVersionAttribute",
        "System.Reflection.AssemblyName",
        "System.Reflection.MemberInfo",
        "System.Runtime.InteropServices.ImmutableCollectionsMarshal",
        "System.Runtime.InteropServices.MemoryMarshal",
        "System.Runtime.InteropServices.RuntimeInformation",
        "System.Security.Cryptography.CryptographicOperations",
        "System.Security.Cryptography.IncrementalHash",
        "System.Span`1",
        "System.String",
        "System.StringComparer",
        "System.Text.Encoding",
        "System.Text.Json.JsonElement",
        "System.Text.Json.JsonException",
        "System.Text.Json.Nodes.JsonArray",
        "System.Text.Json.Nodes.JsonNode",
        "System.Text.Json.Nodes.JsonValue",
        "System.Text.RegularExpressions.Capture",
        "System.Text.RegularExpressions.Group",
        "System.Text.RegularExpressions.Match",
        "System.Text.RegularExpressions.Regex",
        "System.Text.StringBuilder",
        "System.Text.UTF8Encoding",
        "System.Threading.Interlocked",
        "System.Threading.SemaphoreSlim",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask`1",
        "System.Threading.Volatile",
        "System.TimeSpan",
        "System.UInt32",
        "System.UInt64",
        "System.Uri",
        "System.Version",
    };

    private static readonly IReadOnlySet<string> ReviewedNonProducerExternalMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Api.Intelligence.IBatchJsonlRecordObserver.SpillCreated",
        "Microsoft.Extensions.AI.Embedding`1.Vector",
        "Microsoft.Extensions.DependencyInjection.AsyncServiceScope.DisposeAsync",
        "Microsoft.Extensions.DependencyInjection.AsyncServiceScope.ServiceProvider",
        "Microsoft.Extensions.DependencyInjection.IServiceScope.ServiceProvider",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices",
        "Microsoft.Extensions.Hosting.BackgroundService.StartAsync",
        "Microsoft.Extensions.Hosting.BackgroundService.StopAsync",
        "Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping",
        "Microsoft.Extensions.Logging.LoggerExtensions.LogDebug",
        "Microsoft.Extensions.Logging.LoggerExtensions.LogError",
        "Microsoft.Extensions.Logging.LoggerExtensions.LogInformation",
        "Microsoft.Extensions.Logging.LoggerExtensions.LogWarning",
        "Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger",
        "Microsoft.Extensions.Options.IOptionsMonitor`1.CurrentValue",
        "Microsoft.Extensions.Options.IOptionsMonitor`1.OnChange",
        "Microsoft.Extensions.Options.IOptions`1.Value",
        "Microsoft.Win32.SafeHandles.SafeFileHandle..ctor",
        "Microsoft.Win32.SafeHandles.SafeFileHandle.IsInvalid",
        "ModelContextProtocol.Client.HttpClientTransport..ctor",
        "ModelContextProtocol.Client.HttpClientTransportOptions..ctor",
        "ModelContextProtocol.Client.HttpClientTransportOptions.Endpoint",
        "ModelContextProtocol.Client.HttpClientTransportOptions.Endpoint setter",
        "ModelContextProtocol.Client.HttpClientTransportOptions.TransportMode",
        "ModelContextProtocol.Client.HttpClientTransportOptions.TransportMode setter",
        "ModelContextProtocol.Client.McpClientHandlers..ctor",
        "ModelContextProtocol.Client.McpClientHandlers.ElicitationHandler",
        "ModelContextProtocol.Client.McpClientHandlers.ElicitationHandler setter",
        "ModelContextProtocol.Client.McpClientOptions..ctor",
        "ModelContextProtocol.Client.McpClientOptions.ClientInfo",
        "ModelContextProtocol.Client.McpClientOptions.ClientInfo setter",
        "ModelContextProtocol.Client.McpClientOptions.Handlers",
        "ModelContextProtocol.Client.McpClientOptions.Handlers setter",
        "ModelContextProtocol.Client.McpClientOptions.InitializationTimeout",
        "ModelContextProtocol.Client.McpClientOptions.InitializationTimeout setter",
        "ModelContextProtocol.Client.StdioClientTransport..ctor",
        "ModelContextProtocol.Client.StdioClientTransportOptions..ctor",
        "ModelContextProtocol.Client.StdioClientTransportOptions.Arguments",
        "ModelContextProtocol.Client.StdioClientTransportOptions.Arguments setter",
        "ModelContextProtocol.Client.StdioClientTransportOptions.Command",
        "ModelContextProtocol.Client.StdioClientTransportOptions.Command setter",
        "ModelContextProtocol.Client.StdioClientTransportOptions.EnvironmentVariables",
        "ModelContextProtocol.Client.StdioClientTransportOptions.EnvironmentVariables setter",
        "ModelContextProtocol.Client.StdioClientTransportOptions.InheritEnvironmentVariables",
        "ModelContextProtocol.Client.StdioClientTransportOptions.InheritEnvironmentVariables setter",
        "ModelContextProtocol.Client.StdioClientTransportOptions.StandardErrorLines",
        "ModelContextProtocol.Client.StdioClientTransportOptions.StandardErrorLines setter",
        "ModelContextProtocol.Client.StdioClientTransportOptions.WorkingDirectory",
        "ModelContextProtocol.Client.StdioClientTransportOptions.WorkingDirectory setter",
        "ModelContextProtocol.Protocol.Implementation..ctor",
        "ModelContextProtocol.Protocol.Implementation.Name",
        "ModelContextProtocol.Protocol.Implementation.Name setter",
        "ModelContextProtocol.Protocol.Implementation.Version",
        "ModelContextProtocol.Protocol.Implementation.Version setter",
        "ModelContextProtocol.Protocol.ElicitRequestParams.ElicitationId",
        "ModelContextProtocol.Protocol.ElicitRequestParams.Message",
        "ModelContextProtocol.Protocol.ElicitRequestParams.Mode",
        "ModelContextProtocol.Protocol.ElicitRequestParams.RequestedSchema",
        "ModelContextProtocol.Protocol.ElicitRequestParams.RequestSchema.Properties",
        "ModelContextProtocol.Protocol.ElicitRequestParams.Url",
        "ModelContextProtocol.Protocol.ElicitResult..ctor",
        "ModelContextProtocol.Protocol.ElicitResult.Action",
        "ModelContextProtocol.Protocol.ElicitResult.Action setter",
        "ModelContextProtocol.Protocol.ElicitResult.Content",
        "ModelContextProtocol.Protocol.ElicitResult.Content setter",
        "Serilog.Configuration.LoggerMinimumLevelConfiguration.Verbose",
        "Serilog.Core.Logger.Dispose",
        "Serilog.Formatting.Compact.CompactJsonFormatter..ctor",
        "Serilog.Log.Error",
        "Serilog.Log.Fatal",
        "Serilog.Log.Information",
        "Serilog.Log.Warning",
        "Serilog.LoggerConfiguration..ctor",
        "Serilog.LoggerConfiguration.CreateLogger",
        "Serilog.LoggerConfiguration.MinimumLevel",
        "Serilog.LoggerConfiguration.WriteTo",
        "System.Buffers.ArrayPool`1.Rent",
        "System.Buffers.ArrayPool`1.Return",
        "System.Buffers.ArrayPool`1.Shared",
        "System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue",
        "System.Collections.Generic.KeyValuePair.Create",
        "System.Collections.Generic.KeyNotFoundException..ctor",
        "System.ComponentModel.Win32Exception..ctor",
        "System.ComponentModel.Win32Exception.NativeErrorCode",
        "System.Console.Error",
        "System.Console.Out",
        "System.Console.ReadKey",
        "System.ConsoleKeyInfo.Key",
        "System.ConsoleKeyInfo.KeyChar",
        "System.ConsoleKeyInfo.Modifiers",
        "System.Diagnostics.Debug.Assert",
        "System.Diagnostics.Metrics.Counter`1.Add",
        "System.Diagnostics.Process..ctor",
        "System.Diagnostics.Process.StartInfo",
        "System.Diagnostics.Process.StartInfo setter",
        "System.Diagnostics.ProcessStartInfo..ctor",
        "System.Diagnostics.ProcessStartInfo.ArgumentList",
        "System.Diagnostics.ProcessStartInfo.CreateNoWindow",
        "System.Diagnostics.ProcessStartInfo.CreateNoWindow setter",
        "System.Diagnostics.ProcessStartInfo.FileName",
        "System.Diagnostics.ProcessStartInfo.FileName setter",
        "System.Diagnostics.ProcessStartInfo.RedirectStandardError",
        "System.Diagnostics.ProcessStartInfo.RedirectStandardError setter",
        "System.Diagnostics.ProcessStartInfo.RedirectStandardOutput",
        "System.Diagnostics.ProcessStartInfo.RedirectStandardOutput setter",
        "System.Diagnostics.ProcessStartInfo.UseShellExecute",
        "System.Diagnostics.ProcessStartInfo.UseShellExecute setter",
        "System.Environment.GetEnvironmentVariable",
        "System.Environment.GetFolderPath",
        "System.Environment.MachineName",
        "System.Environment.ProcessId",
        "System.GC.SuppressFinalize",
        "System.IAsyncDisposable.DisposeAsync",
        "System.IDisposable.Dispose",
        "System.IEquatable`1.Equals",
        "System.IO.DirectoryInfo..ctor",
        "System.IO.DriveInfo..ctor",
        "System.IO.EnumerationOptions..ctor",
        "System.IO.EnumerationOptions.AttributesToSkip",
        "System.IO.EnumerationOptions.AttributesToSkip setter",
        "System.IO.EnumerationOptions.IgnoreInaccessible",
        "System.IO.EnumerationOptions.IgnoreInaccessible setter",
        "System.IO.EnumerationOptions.RecurseSubdirectories",
        "System.IO.EnumerationOptions.RecurseSubdirectories setter",
        "System.IO.FileInfo..ctor",
        "System.IO.FileStreamOptions..ctor",
        "System.IO.FileStreamOptions.Access",
        "System.IO.FileStreamOptions.Access setter",
        "System.IO.FileStreamOptions.BufferSize",
        "System.IO.FileStreamOptions.BufferSize setter",
        "System.IO.FileStreamOptions.Mode",
        "System.IO.FileStreamOptions.Mode setter",
        "System.IO.FileStreamOptions.Options",
        "System.IO.FileStreamOptions.Options setter",
        "System.IO.FileStreamOptions.Share",
        "System.IO.FileStreamOptions.Share setter",
        "System.IO.FileStreamOptions.UnixCreateMode",
        "System.IO.FileStreamOptions.UnixCreateMode setter",
        "System.IO.FileSystemEventArgs.FullPath",
        "System.IO.FileSystemWatcher..ctor",
        "System.IO.FileSystemWatcher.Dispose",
        "System.IO.FileSystemWatcher.IncludeSubdirectories",
        "System.IO.FileSystemWatcher.IncludeSubdirectories setter",
        "System.IO.FileSystemWatcher.InternalBufferSize",
        "System.IO.FileSystemWatcher.InternalBufferSize setter",
        "System.IO.FileSystemWatcher.NotifyFilter",
        "System.IO.FileSystemWatcher.NotifyFilter setter",
        "System.IO.RenamedEventArgs.OldFullPath",
        "System.IO.ErrorEventArgs.GetException",
        "System.Lazy`1..ctor",
        "System.Lazy`1.IsValueCreated",
        "System.Linq.Enumerable.Empty",
        "System.Linq.Enumerable.ToArray",
        "System.Linq.Enumerable.ToList",
        "System.Linq.IGrouping`2.Key",
        "System.Linq.ImmutableArrayExtensions.ToArray",
        "System.MemoryExtensions.Contains",
        "System.MemoryExtensions.SequenceEqual",
        "System.Net.Http.IHttpClientFactory.CreateClient",
        "System.Random.Next",
        "System.Random.Shared",
        "System.Reflection.Assembly.GetName",
        "System.Reflection.Assembly.GetManifestResourceNames",
        "System.Reflection.Assembly.GetManifestResourceStream",
        "System.Reflection.CustomAttributeExtensions.GetCustomAttribute",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2.Add",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2.GetOrCreateValue",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2.TryGetValue",
        "System.Runtime.CompilerServices.TaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.TaskAwaiter`1.GetResult",
        "System.Runtime.CompilerServices.Unsafe.As",
        "System.Runtime.CompilerServices.Unsafe.SizeOf",
        "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture",
        "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw",
        "System.Runtime.InteropServices.Marshal.Copy",
        "System.Runtime.InteropServices.Marshal.GetLastPInvokeError",
        "System.Runtime.InteropServices.Marshal.ReadByte",
        "System.Runtime.InteropServices.Marshal.ReadInt16",
        "System.Runtime.InteropServices.Marshal.SetLastPInvokeError",
        "System.Runtime.InteropServices.Marshal.SizeOf",
        "System.Runtime.InteropServices.SafeHandle.DangerousAddRef",
        "System.Runtime.InteropServices.SafeHandle.DangerousGetHandle",
        "System.Runtime.InteropServices.SafeHandle.DangerousRelease",
        "System.Runtime.InteropServices.SafeHandle.Dispose",
        "System.Runtime.InteropServices.SafeHandle.IsClosed",
        "System.Security.AccessControl.RawAcl..ctor",
        "System.Security.Cryptography.AesGcm..ctor",
        "System.Security.Cryptography.AesGcm.Decrypt",
        "System.Security.Cryptography.AesGcm.Dispose",
        "System.Security.Cryptography.AesGcm.Encrypt",
        "System.Security.Cryptography.AsymmetricAlgorithm.KeySize",
        "System.Security.Cryptography.CryptographicException..ctor",
        "System.Security.Cryptography.ECAlgorithm.ExportParameters",
        "System.Security.Cryptography.ECAlgorithm.ImportSubjectPublicKeyInfo",
        "System.Security.Cryptography.ECCurve.Oid",
        "System.Security.Cryptography.ECDsa.Create",
        "System.Security.Cryptography.ECDsa.VerifyData",
        "System.Security.Cryptography.HKDF.DeriveKey",
        "System.Security.Cryptography.HMACSHA256.HashData",
        "System.Security.Cryptography.HashAlgorithmName.SHA256",
        "System.Security.Cryptography.Oid.Value",
        "System.Security.Cryptography.RandomNumberGenerator.Fill",
        "System.Security.Cryptography.RandomNumberGenerator.GetBytes",
        "System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2",
        "System.Security.Cryptography.SHA256.HashData",
        "System.Security.Principal.SecurityIdentifier..ctor",
        "System.Security.Principal.SecurityIdentifier.Equals",
        "System.Security.Principal.SecurityIdentifier.Value",
        "System.Text.Json.JsonDocument.Parse",
        "System.Text.Json.JsonDocument.RootElement",
        "System.Text.Json.JsonSerializer.Deserialize",
        "System.Text.Json.JsonSerializer.Serialize",
        "System.Text.Json.JsonSerializer.SerializeToElement",
        "System.Text.Json.JsonSerializer.SerializeToUtf8Bytes",
        "System.Text.Json.JsonSerializerOptions..ctor",
        "System.Text.Json.JsonSerializerOptions.WriteIndented",
        "System.Text.Json.JsonSerializerOptions.WriteIndented setter",
        "System.Threading.AsyncLocal`1.Value",
        "System.Threading.AsyncLocal`1.Value setter",
        "System.Threading.CancellationToken.CanBeCanceled",
        "System.Threading.CancellationToken.IsCancellationRequested",
        "System.Threading.CancellationToken.None",
        "System.Threading.CancellationToken.ThrowIfCancellationRequested",
        "System.Threading.CancellationTokenSource..ctor",
        "System.Threading.CancellationTokenSource.Cancel",
        "System.Threading.CancellationTokenSource.CancelAfter",
        "System.Threading.CancellationTokenSource.CancelAsync",
        "System.Threading.CancellationTokenSource.CreateLinkedTokenSource",
        "System.Threading.CancellationTokenSource.Dispose",
        "System.Threading.CancellationTokenSource.IsCancellationRequested",
        "System.Threading.CancellationTokenSource.Token",
        "System.Threading.Channels.BoundedChannelOptions..ctor",
        "System.Threading.Channels.BoundedChannelOptions.FullMode",
        "System.Threading.Channels.BoundedChannelOptions.FullMode setter",
        "System.Threading.Channels.Channel.CreateBounded",
        "System.Threading.Channels.ChannelOptions.AllowSynchronousContinuations",
        "System.Threading.Channels.ChannelOptions.AllowSynchronousContinuations setter",
        "System.Threading.Channels.ChannelOptions.SingleReader",
        "System.Threading.Channels.ChannelOptions.SingleReader setter",
        "System.Threading.Channels.ChannelOptions.SingleWriter",
        "System.Threading.Channels.ChannelOptions.SingleWriter setter",
        "System.Threading.Channels.ChannelReader`1.Count",
        "System.Threading.Channels.ChannelReader`1.ReadAllAsync",
        "System.Threading.Channels.ChannelReader`1.TryRead",
        "System.Threading.Channels.ChannelReader`1.WaitToReadAsync",
        "System.Threading.Channels.ChannelWriter`1.Complete",
        "System.Threading.Channels.ChannelWriter`1.TryComplete",
        "System.Threading.Channels.ChannelWriter`1.TryWrite",
        "System.Threading.Channels.Channel`2.Reader",
        "System.Threading.Channels.Channel`2.Writer",
        "System.Threading.Lock.EnterScope",
        "System.Threading.Mutex..ctor",
        "System.Threading.Mutex.ReleaseMutex",
        "System.Threading.NamedWaitHandleOptions..ctor",
        "System.Threading.NamedWaitHandleOptions.CurrentSessionOnly",
        "System.Threading.NamedWaitHandleOptions.CurrentSessionOnly setter",
        "System.Threading.NamedWaitHandleOptions.CurrentUserOnly",
        "System.Threading.NamedWaitHandleOptions.CurrentUserOnly setter",
        "System.Threading.PeriodicTimer..ctor",
        "System.Threading.PeriodicTimer.Period",
        "System.Threading.PeriodicTimer.Period setter",
        "System.Threading.Tasks.ParallelOptions..ctor",
        "System.Threading.Tasks.ParallelOptions.CancellationToken",
        "System.Threading.Tasks.ParallelOptions.CancellationToken setter",
        "System.Threading.Tasks.ParallelOptions.MaxDegreeOfParallelism",
        "System.Threading.Tasks.ParallelOptions.MaxDegreeOfParallelism setter",
        "System.Threading.Tasks.Task.CompletedTask",
        "System.Threading.Tasks.Task.ConfigureAwait",
        "System.Threading.Tasks.Task.Exception",
        "System.Threading.Tasks.Task.Factory",
        "System.Threading.Tasks.Task.FromCanceled",
        "System.Threading.Tasks.Task.FromException",
        "System.Threading.Tasks.Task.FromResult",
        "System.Threading.Tasks.Task.GetAwaiter",
        "System.Threading.Tasks.Task.IsCompleted",
        "System.Threading.Tasks.Task.IsCompletedSuccessfully",
        "System.Threading.Tasks.Task.IsFaulted",
        "System.Threading.Tasks.Task.WaitAsync",
        "System.Threading.Tasks.Task.WhenAll",
        "System.Threading.Tasks.Task.WhenAny",
        "System.Threading.Tasks.Task.Yield",
        "System.Threading.Tasks.TaskAsyncEnumerableExtensions.ConfigureAwait",
        "System.Threading.Tasks.TaskCompletionSource..ctor",
        "System.Threading.Tasks.TaskCompletionSource.Task",
        "System.Threading.Tasks.TaskCompletionSource.TrySetException",
        "System.Threading.Tasks.TaskCompletionSource.TrySetResult",
        "System.Threading.Tasks.TaskCompletionSource`1..ctor",
        "System.Threading.Tasks.TaskCompletionSource`1.SetResult",
        "System.Threading.Tasks.TaskCompletionSource`1.Task",
        "System.Threading.Tasks.TaskCompletionSource`1.TrySetCanceled",
        "System.Threading.Tasks.TaskCompletionSource`1.TrySetResult",
        "System.Threading.Tasks.TaskExtensions.Unwrap",
        "System.Threading.Tasks.TaskScheduler.Default",
        "System.Threading.Tasks.Task`1.ConfigureAwait",
        "System.Threading.Tasks.Task`1.GetAwaiter",
        "System.Threading.Tasks.Task`1.WaitAsync",
        "System.Threading.WaitHandle.WaitOne",
        "System.TimeProvider.GetTimestamp",
        "System.TimeProvider.GetUtcNow",
        "System.TimeProvider.System",
        "System.Type.Assembly",
    };

    private static readonly IReadOnlySet<string> CompletionOwnedExternalAwaitables = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Threading.Channels.ChannelWriter`1.WriteAsync",
        "System.Threading.PeriodicTimer.WaitForNextTickAsync",
        "System.Threading.Tasks.Task.Delay",
    };

    private static readonly IReadOnlySet<string> DetachedExternalCallbackMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "Microsoft.Data.Sqlite.SqliteConnection.CreateFunction",
        "Microsoft.Extensions.Options.IOptionsMonitor`1.OnChange",
        "System.Threading.CancellationToken.Register",
    };

    private static readonly IReadOnlySet<string> DetachedExternalCallbackProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "ModelContextProtocol.Client.McpClientHandlers.ElicitationHandler setter",
        "ModelContextProtocol.Client.StdioClientTransportOptions.StandardErrorLines setter",
    };

    internal static readonly IReadOnlySet<string> AggregateBoundaries = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync",
        "RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.InitializeAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.ApplyOrResumeHostedPruneAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverFactoryResetAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverMutationAsync",
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneAsync",
        "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetStartupRecovery.RecoverBeforeBootstrapAsync",
        "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.IGrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync",
        "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseBootstrapper.EnsureInitializedAsync",
        "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync",
        "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler.ReconcileAsync",
        "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexProcessor.ProcessAsync",
    };

    private static readonly IReadOnlySet<string> ExactConstantControlParameters = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService.RecoverPruneCoreAsync#pendingJournalKnownUnstarted",
        "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#authority",
        "RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager.EnsureGlobalLoadedAsync#authority",
        "RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager.RunGlobalInitOperationAsync#authority",
    };

    private static readonly IReadOnlySet<string> TransparentProducerBoundaries = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcherFactory.Create",
    };

    internal static readonly IReadOnlySet<string> SchemaBackfillStrategies = new HashSet<string>(StringComparer.Ordinal)
    {
        "RetroDownfall.Arcanum.Infrastructure.Data.Schema.IdentitySpellingBackfill",
        "RetroDownfall.Arcanum.Infrastructure.Data.Schema.MemoryAnnalsBackfill",
        "RetroDownfall.Arcanum.Infrastructure.Data.Schema.SagaExtractionCursorBackfill",
        "RetroDownfall.Arcanum.Infrastructure.Data.Schema.SagaMemoryCampaignScopeBackfill",
        "RetroDownfall.Arcanum.Infrastructure.Data.Schema.UtcInstantCanonicalizationBackfill",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ReviewedClosedDispatchTargets =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["RetroDownfall.Arcanum.Core.Backup.IBackupRestoreEffectDigestCalculator.Compute"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Core.Backup.BackupRestoreEffectDigestCalculator",
            },
            ["RetroDownfall.Arcanum.Core.Covenant.ICovenantLeaseRegistration.RevalidateAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate.ScopedRegistration",
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate.ExclusiveRegistration",
            },
            ["RetroDownfall.Arcanum.Core.Covenant.ICovenantLeaseRegistration.ReleaseAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate.ScopedRegistration",
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate.ExclusiveRegistration",
            },
            ["RetroDownfall.Arcanum.Core.Covenant.ICovenantExclusiveLeaseRegistration.CompleteAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate.ExclusiveRegistration",
            },
            ["RetroDownfall.Arcanum.Core.Covenant.ICovenantExclusiveLeaseRegistration.ExecuteWhileHeld"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantOperationGate.ExclusiveRegistration",
            },
            ["RetroDownfall.Arcanum.Core.Covenant.ICovenantOperationLease.RevalidateAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Core.Covenant.CovenantOperationLease",
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantManagementService.NullLease",
            },
            ["RetroDownfall.Arcanum.Core.Intelligence.IHumanPromptLiveEmitter.EmitAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Api.Intelligence.WizardIntelligenceProvider.ChannelHumanPromptLiveEmitter",
            },
            ["RetroDownfall.Arcanum.Core.Intelligence.IHumanPromptReservation.WaitAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Api.Intelligence.HumanPromptRegistry.Reservation",
            },
            ["RetroDownfall.Arcanum.Core.Storage.ISessionTurnBeginStore.CreateBoundSessionAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Infrastructure.Repositories.GrimoireRepository",
            },
            ["RetroDownfall.Arcanum.Core.Covenant.ICovenantExclusivePostDispositionFinalizer.FinalizeAfterSuccessfulDispositionAsync"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "RetroDownfall.Arcanum.Core.Covenant.CovenantNoOpPostDispositionFinalizer",
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CampaignPathMarkerLifecycle.MarkerChildCompletionFinalizer",
                "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantSchemaRepairPostDispositionFinalizer",
                "RetroDownfall.Arcanum.Infrastructure.Repositories.ProtectedArtifactTransferStore.TransferJournalFinalizer",
            },
        };

    // Task 14 fills independently reviewed site/frontier evidence. Producer tasks must not edit this catalog.
    private static IReadOnlyList<HostedProducerServiceEntry> CatalogRoots { get; } =
    [
        Lifecycle("Hosting", "GrimoireDatabaseHostedService", HostedProducerAuthorityKind.PreReadinessStartup, "installation maintenance lock and bootstrap awaited by Generic Host", HostedProducerAuthorityKind.StoppedHost, "host shutdown retains installation maintenance lock"),
        Lifecycle("Covenant", "CovenantFeatureConfigurationPublisher", HostedProducerAuthorityKind.EffectFree, "in-memory configuration subscription", HostedProducerAuthorityKind.EffectFree, "in-memory subscription disposal"),
        Lifecycle("Hosting", "PidFileService", HostedProducerAuthorityKind.PreReadinessStartup, "awaited process PID-file startup", HostedProducerAuthorityKind.StoppedHost, "process PID-file shutdown"),
        new("LongRunningOperationStartupHostedService",
        [
            AtCall(Operation("Operations", "LongRunningOperationStartupHostedService", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "awaited readiness reconciliation budget"), "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync", 0, "RunStartupPassAsync(startedAt, ownerId, budget.Token)"),
            Operation("Operations", "LongRunningOperationStartupHostedService", "ContinueInBackgroundAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.LongRunningOperationRecovery),
            Operation("Operations", "LongRunningOperationStartupHostedService", "StopAsync", HostedProducerAuthorityKind.EffectFree, null, "cancels and joins the background task"),
        ]),
        Lifecycle("Hosting", "SessionAttachmentPendingGcHostedService", HostedProducerAuthorityKind.PreReadinessStartup, "pending-file and row reconciliation awaited by Generic Host", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Ordinary("Covenant", "CovenantMaintenanceHostedService", GrimoireWorkKind.CovenantMaintenance),
        Ordinary("Covenant", "GrimoireSchemaTransitionHostedService", GrimoireWorkKind.GrimoireSchemaTransition),
        Ordinary("Hosting", "EntryWeavingService", GrimoireWorkKind.EntryWeaving),
        Ordinary("Weave", "SessionAttachmentIndexingService", GrimoireWorkKind.SessionAttachmentIndexing),
        new("WorkspaceIndexingService",
        [
            Operation("Hosting", "WorkspaceIndexingService", "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, sourceFile: "WorkspaceIndexingService.Watchers.cs"),
            Operation("Hosting", "WorkspaceIndexingService", "StopAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, sourceFile: "WorkspaceIndexingService.Watchers.cs"),
            Operation("Hosting", "WorkspaceIndexingService", "RegisterWorkspace", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request publishes one tracked, self-admitted workspace handle", sourceFile: "WorkspaceIndexingService.Scheduler.cs"),
            Operation("Hosting", "WorkspaceIndexingService", "QueueIndexNow", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request coalesces one tracked, self-admitted workspace handle", sourceFile: "WorkspaceIndexingService.Scheduler.cs"),
        ]),
        Ordinary("Hosting", "SagaExtractionService", GrimoireWorkKind.SagaExtraction),
        Ordinary("Hosting", "TapestryWeavingService", GrimoireWorkKind.TapestryWeaving),
        Lifecycle("Hosting", "ArcanumSettingsClampStartupLogger", HostedProducerAuthorityKind.EffectFree, "configuration warning logging only", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Lifecycle("Hosting", "ArcanumSecurityStartupChecks", HostedProducerAuthorityKind.PreReadinessStartup, "filesystem permission self-check awaited by Generic Host", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Lifecycle("Hosting", "FileEncryptionKeyBootstrapHostedService", HostedProducerAuthorityKind.PreReadinessStartup, "no-follow ciphertext inventory and conditional credential validation awaited by Generic Host", HostedProducerAuthorityKind.EffectFree, "completed task only"),
        Ordinary("Data", "DataRetentionSweepHostedService", GrimoireWorkKind.DataRetentionSweep),
        Ordinary("A2A", "A2ASendingLeaseRenewer", GrimoireWorkKind.A2ASendingLeaseRenewal),
        Ordinary("Hosting", "Loremaster", GrimoireWorkKind.LoremasterSummarization),
        new("ApprenticeService",
        [
            Operation("Hosting", "ApprenticeService", "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.ApprenticeExecution),
            Operation("Hosting", "ApprenticeService", "RunApprenticeAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.ApprenticeExecution),
            Operation("Hosting", "ApprenticeService", "StopAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.ApprenticeExecution),
            Operation("Hosting", "ApprenticeService", "StartAsync", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request starts one tracked self-admitted execution"),
            Operation("Hosting", "ApprenticeService", "PauseAsync", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request cancels one tracked execution generation and persists its state before returning"),
            Operation("Hosting", "ApprenticeService", "ResumeAsync", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request persists one resume and starts one tracked self-admitted execution"),
            Operation("Hosting", "ApprenticeService", "ReweaveAsync", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request validates and persists one plan revision before returning"),
            Operation("Hosting", "ApprenticeService", "InterveneAsync", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request persists one intervention before returning"),
            Operation("Hosting", "ApprenticeService", "CancelAsync", HostedProducerAuthorityKind.FiniteRequest, null, "bounded request cancels and persists one apprentice before returning"),
        ]),
        new("McpServerBootstrapHostedService",
        [
            AtCall(Operation("Mcp", "McpServerBootstrapHostedService", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "blocking branch awaits global initialization"), "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync", 0, "_coordinator.InitializeGlobalAsync(McpGlobalInitializationAuthority.PreReadinessStartup, CancellationToken.None)"),
            AtCall(Operation("Mcp", "McpServerBootstrapHostedService", "RunOrdinaryBootstrapAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.McpServerBootstrap), "RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync", 0, "_coordinator.InitializeGlobalAsync(McpGlobalInitializationAuthority.OrdinaryHostedWork, _lifetime.ApplicationStopping)"),
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

    private static IReadOnlyList<NonHostedProducerChainEntry> NonHostedCatalogRoots { get; } =
    [
        Chain("Cli", "Commands", "BackupCommands", "Create", HostedProducerAuthorityKind.OwnerBoundMaintenance, "IGrimoireCliInitialization.RunExclusiveAsync holds installation maintenance lock and client mutation boundary; BackupService.CreateAsync owns durable backup operation and Covenant installation read lease"),
        Chain("Infrastructure", "Backup", "BackupRestoreService", "RestoreAsync", HostedProducerAuthorityKind.StoppedHost, "restore safety-backup under exact stopped-host restore ownership"),
        Chain("Infrastructure", "InstallationReset", "InstallationResetService", "ApplyFullUnderMaintenanceLockAsync", HostedProducerAuthorityKind.OwnerBoundMaintenance, "generation-bound installation reset maintenance lock and authenticated reset owner"),
        Chain("Infrastructure", "GrimoireTransitions", "GrimoireOfflineTransitionStartupRecovery", "RecoverBeforeBootstrapAsync", HostedProducerAuthorityKind.OwnerBoundRecovery, "authenticated offline-transition owner before Grimoire bootstrap"),
    ];

    internal static IReadOnlyList<HostedProducerServiceEntry> Catalog { get; } = AttachHostedSites(CatalogRoots, ReviewedCapsules.Value.Sites);

    internal static IReadOnlyList<NonHostedProducerChainEntry> NonHostedCatalog { get; } = AttachNonHostedSites(NonHostedCatalogRoots, ReviewedCapsules.Value.Sites);

    internal static string CapsuleManifestPath => Path.Combine(FindRepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests", "Support", "hosted-grimoire-producer-capsules-v1.tsv");

    private static HostedProducerCapsuleManifest LoadReviewedCapsules()
    {
        if (!File.Exists(CapsuleManifestPath))
        {
            return new([], [new("HOSTED_MANIFEST_MISSING", CapsuleManifestPath, "The reviewed producer capsule manifest is required.")]);
        }

        return ParseCapsuleManifest(File.ReadAllLines(CapsuleManifestPath));
    }

    internal static HostedProducerCapsuleManifest ParseCapsuleManifest(IReadOnlyList<string> lines)
    {
        List<HostedProducerInventoryDiagnostic> diagnostics = [];

        List<HostedProducerSite> sites = [];

        if (lines.Count == 0 || lines[0] != CapsuleManifestHeader)
        {
            diagnostics.Add(new("HOSTED_MANIFEST_VERSION_INVALID", lines.Count == 0 ? "<empty>" : lines[0], "The capsule manifest must start with the exact supported schema/version header."));
        }

        if (lines.Count < 2 || lines[1] != CapsuleManifestColumns)
        {
            diagnostics.Add(new("HOSTED_MANIFEST_COLUMNS_INVALID", lines.Count < 2 ? "<missing>" : lines[1], "The capsule manifest columns must match the v1 contract exactly."));
        }

        for (int index = 2; index < lines.Count; index++)
        {
            string line = lines[index];

            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split('\t');

            if (fields.Length != 13)
            {
                diagnostics.Add(new("HOSTED_MANIFEST_ROW_INVALID", $"line:{index + 1}", $"Expected 13 tab-separated fields, found {fields.Length}."));

                continue;
            }

            if (!Enum.TryParse(fields[6], out HostedProducerSiteKind kind) || !Enum.IsDefined(kind) || !int.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) || ordinal < 0 || !IsSha256(fields[9]))
            {
                diagnostics.Add(new("HOSTED_MANIFEST_ROW_INVALID", $"line:{index + 1}", "The kind, nonnegative syntax ordinal, and uppercase SHA-256 syntax fingerprint must be valid."));

                continue;
            }

            string? workFrontier = ParseOptionalSha256(fields[10], index, "work", diagnostics);

            string? effectFrontier = ParseOptionalSha256(fields[11], index, "effect", diagnostics);

            GrimoireWorkKind? admittedWorkKind = null;

            if (fields[12] != "-" && (!Enum.TryParse(fields[12], out GrimoireWorkKind parsedWorkKind) || !Enum.IsDefined(parsedWorkKind)))
            {
                diagnostics.Add(new("HOSTED_MANIFEST_ROW_INVALID", $"line:{index + 1}", "The admitted work kind must be '-' or one defined GrimoireWorkKind."));
            }
            else if (fields[12] != "-")
            {
                admittedWorkKind = Enum.Parse<GrimoireWorkKind>(fields[12]);
            }

            bool invalidRequiredIdentity = fields.Take(8).Where(static (_, fieldIndex) => fieldIndex != 4).Any(static field => string.IsNullOrWhiteSpace(field) || field.Contains('\r') || field.Contains('\n'));

            if (invalidRequiredIdentity || fields[4].Contains('\r') || fields[4].Contains('\n'))
            {
                diagnostics.Add(new("HOSTED_MANIFEST_ROW_INVALID", $"line:{index + 1}", "Required identity fields must be nonempty single-line values; anonymous-function member names may be empty."));

                continue;
            }

            HostedProducerSite site = new(
                fields[0],
                fields[1] + "/capsule@" + CapsuleId(fields[2], fields[5], kind, fields[7], ordinal, fields[9]),
                fields[2],
                fields[3],
                fields[4],
                kind,
                fields[7],
                fields[1],
                fields[5],
                ordinal,
                fields[9],
                workFrontier,
                effectFrontier,
                admittedWorkKind);

            sites.Add(site);
        }

        foreach (IGrouping<string, HostedProducerSite> duplicate in sites.GroupBy(StableSiteIdentity, StringComparer.Ordinal).Where(static group => group.Count() > 1))
        {
            diagnostics.Add(new("HOSTED_MANIFEST_ROW_DUPLICATE", duplicate.Key, "Each root/operation/capsule/frontier association must occur exactly once."));
        }

        foreach (HostedProducerSite site in sites)
        {
            ValidateManifestFrontier(site, site.WorkFrontierCapsuleId, ".TryAcquireWorkLease", "work", sites, diagnostics);

            ValidateManifestFrontier(site, site.EffectFrontierCapsuleId, ".TryBeginExternalEffectGroup", "effect", sites, diagnostics);

            bool workFrontier = site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal);

            if (workFrontier != site.AdmittedWorkKind.HasValue)
            {
                diagnostics.Add(new("HOSTED_MANIFEST_WORK_KIND_INVALID", StableSiteIdentity(site), "Exactly a work-admission frontier must name its admitted work kind."));
            }
        }

        return new(sites, diagnostics.Distinct().ToArray());
    }

    private static string? ParseOptionalSha256(string value, int index, string frontier, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        if (value == "-")
        {
            return null;
        }

        if (!IsSha256(value))
        {
            diagnostics.Add(new("HOSTED_MANIFEST_ROW_INVALID", $"line:{index + 1}", $"The {frontier} frontier must be '-' or an uppercase SHA-256 capsule identity."));

            return null;
        }

        return value;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void ValidateManifestFrontier(HostedProducerSite site, string? frontierId, string expectedCalleeSuffix, string frontierKind, IReadOnlyList<HostedProducerSite> sites, List<HostedProducerInventoryDiagnostic> diagnostics)
    {
        if (frontierId is null)
        {
            return;
        }

        HostedProducerSite[] matches = sites.Where(candidate => candidate.RootType == site.RootType && candidate.AuthorityOperationId == site.AuthorityOperationId && candidate.CapsuleId == frontierId && candidate.Kind == HostedProducerSiteKind.EffectFrontier && candidate.Callee.EndsWith(expectedCalleeSuffix, StringComparison.Ordinal)).ToArray();

        if (matches.Length != 1)
        {
            diagnostics.Add(new("HOSTED_MANIFEST_FRONTIER_UNRESOLVED", StableSiteIdentity(site), $"The exact same-operation {frontierKind} frontier capsule must resolve once; found {matches.Length}."));
        }
    }

    private static IReadOnlyList<HostedProducerInventoryDiagnostic> ValidateManifestRoots(IReadOnlyList<HostedProducerSite> sites)
    {
        HashSet<(string RootType, string OperationId)> roots = CatalogRoots.SelectMany(service => service.Operations.Select(operation => (service.ServiceType, operation.OperationId)))
            .Concat(NonHostedCatalogRoots.Select(static chain => (chain.EnclosingType, chain.ChainId)))
            .ToHashSet();

        return sites
            .Where(site => !roots.Contains((site.RootType, site.AuthorityOperationId)))
            .Select(site => new HostedProducerInventoryDiagnostic("HOSTED_MANIFEST_ROOT_UNRESOLVED", StableSiteIdentity(site), "The manifest row must map to one exact declared hosted operation or non-hosted authority chain."))
            .ToArray();
    }

    private static IReadOnlyList<HostedProducerServiceEntry> AttachHostedSites(IReadOnlyList<HostedProducerServiceEntry> roots, IReadOnlyList<HostedProducerSite> sites) => roots
        .Select(service => service with
        {
            Operations = service.Operations.Select(operation => operation with
            {
                Sites = sites.Where(site => site.RootType == service.ServiceType && site.AuthorityOperationId == operation.OperationId).ToArray(),
            }).ToArray(),
        })
        .ToArray();

    private static IReadOnlyList<NonHostedProducerChainEntry> AttachNonHostedSites(IReadOnlyList<NonHostedProducerChainEntry> roots, IReadOnlyList<HostedProducerSite> sites) => roots
        .Select(chain => chain with
        {
            Sites = sites.Where(site => site.RootType == chain.EnclosingType && site.AuthorityOperationId == chain.ChainId).ToArray(),
        })
        .ToArray();

    internal static string RenderCapsuleManifest(IEnumerable<HostedProducerSite> sites)
    {
        IEnumerable<string> rows = sites.OrderBy(StableSiteIdentity, StringComparer.Ordinal).Select(static site => string.Join('\t',
            site.RootType,
            site.AuthorityOperationId,
            site.SourcePath,
            site.EnclosingType,
            site.Member,
            site.MemberIdentity,
            site.Kind,
            site.Callee,
            site.SyntaxOrdinal.ToString(CultureInfo.InvariantCulture),
            site.SyntaxFingerprint,
            site.WorkFrontierCapsuleId ?? "-",
            site.EffectFrontierCapsuleId ?? "-",
            site.AdmittedWorkKind?.ToString() ?? "-"));

        return string.Join('\n', new[] { CapsuleManifestHeader, CapsuleManifestColumns }.Concat(rows)) + "\n";
    }

    private static NonHostedProducerChainEntry Chain(string project, string folder, string type, string member, HostedProducerAuthorityKind authority, string proof)
    {
        string enclosingType = $"RetroDownfall.Arcanum.{project}.{folder}.{type}";

        return new(enclosingType + "." + member, $"src/RetroDownfall.Arcanum.{project}/{folder}/{type}.cs", enclosingType, member, authority, enclosingType + "." + member + ": " + proof, []);
    }

    private static HostedProducerOperationEntry Operation(string folder, string type, string member, HostedProducerAuthorityKind authority, GrimoireWorkKind? kind, string? proof = null, string project = "Infrastructure", string? sourceFile = null)
    {
        string enclosingType = $"RetroDownfall.Arcanum.{project}.{folder}.{type}";

        return new(enclosingType + "." + member, $"src/RetroDownfall.Arcanum.{project}/{folder}/{sourceFile ?? type + ".cs"}", enclosingType, member, authority, kind, proof is null ? null : enclosingType + "." + member + ": " + proof, []);
    }

    private static HostedProducerOperationEntry AtCall(HostedProducerOperationEntry operation, string callee, int occurrence, string exactExpression) => operation with { OperationId = operation.OperationId + "::call:" + callee + "#" + occurrence + "~" + Fingerprint(SyntaxFactory.ParseExpression(exactExpression)) };

    private static HostedProducerServiceEntry Ordinary(string folder, string type, GrimoireWorkKind kind, bool stop = false, string? sourceFile = null) => new(type, stop ? [Operation(folder, type, "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, kind, sourceFile: sourceFile), Operation(folder, type, "StopAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, kind, sourceFile: sourceFile)] : [Operation(folder, type, "ExecuteAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, kind, sourceFile: sourceFile)]);

    private static HostedProducerServiceEntry Lifecycle(string folder, string type, HostedProducerAuthorityKind startAuthority, string startProof, HostedProducerAuthorityKind stopAuthority, string stopProof) => new(type, [Operation(folder, type, "StartAsync", startAuthority, null, startProof), Operation(folder, type, "StopAsync", stopAuthority, null, stopProof)]);

    internal static HostedProducerDiscovery<HostedProducerSite> DiscoverProducerSites(IReadOnlyList<CSharpCompilation> compilations, HostedProducerDiscovery<string> registrations) => DiscoverProducerSites(compilations, registrations, Catalog, NonHostedCatalog);

    internal static HostedProducerDiscovery<HostedProducerSite> DiscoverProducerSites(IReadOnlyList<CSharpCompilation> compilations, HostedProducerDiscovery<string> registrations, IReadOnlyList<HostedProducerServiceEntry> catalog, IReadOnlyList<NonHostedProducerChainEntry> nonHostedCatalog) => new ProducerGraph(compilations).Discover(registrations, catalog, nonHostedCatalog);

    private enum RecoveryDisposition : byte
    {
        OrdinaryDbOnly = 1,

        OrdinaryExternalEffect = 2,

        OwnerBound = 3,

        Unsupported = 4,
    }

    private readonly record struct RecoveryTuple(
        string Kind,
        int CheckpointVersion,
        RecoveryDisposition Disposition);

    private sealed record RecoveryDescriptor(
        string Kind,
        int MinimumCheckpointVersion,
        int MaximumCheckpointVersion);

    private sealed record RecoveryMatrix(
        IReadOnlyList<RecoveryDescriptor> Descriptors,
        IReadOnlyList<RecoveryTuple> Tuples,
        bool IsValid);

    private sealed record RecoveryHandlerTarget(
        AuthoredMember Member,
        string Kind,
        int SupportedCheckpointVersion,
        bool OwnerOnly);

    private sealed record BoundValueSource(
        AuthoredMember Caller,
        ExpressionSyntax Expression);

    private sealed record AuthoredMember(
        IMethodSymbol Symbol,
        SyntaxNode Syntax,
        SemanticModel Model,
        IReadOnlyDictionary<ISymbol, IReadOnlySet<string>>? AdmissionBindings = null,
        IReadOnlyDictionary<ISymbol, AuthoredMember>? CallableBindings = null,
        IReadOnlySet<ISymbol>? AbsentCallables = null,
        IReadOnlyDictionary<ISymbol, ITypeSymbol>? ConcreteBindings = null,
        IReadOnlyDictionary<ISymbol, RecoveryTuple>? RecoveryBindings = null,
        RecoveryTuple? RecoveryContext = null,
        IReadOnlyDictionary<ISymbol, BoundValueSource>? ValueBindings = null);

    private sealed class ProducerGraph
    {
        private readonly record struct MethodIdentity(AssemblyIdentity Assembly, string Method);

        private readonly record struct GraphMemberIdentity(AssemblyIdentity Assembly, int Compilation, int Tree, string Method, int SpanStart, int SpanLength);

        private readonly record struct TraversalStateIdentity(
            string RootType,
            string RootOperation,
            GraphMemberIdentity Member,
            int SelectionStart,
            int SelectionLength,
            string AdmissionBindings,
            string CallableBindings,
            string ValueBindings,
            string ConcreteBindings,
            string RecoveryBindings,
            string? WorkFrontier,
            string? EffectFrontier,
            string? RecoveryEffectFrontier,
            string FieldPublications,
            bool CompletionOwned,
            bool Lifecycle);

        private enum TraversalEvidence : byte
        {
            NoEvidence = 0,

            Evidence = 1,

            Inconclusive = 2,
        }

        private enum BoundedDataProof : byte
        {
            PureBoundedData = 1,

            Producer = 2,

            Unknown = 3,
        }

        private readonly record struct BoundTypeIdentity(AssemblyIdentity Assembly, int? Compilation, string Type);

        private readonly record struct BoundContractIdentity(AssemblyIdentity Assembly, string Type);

        private readonly record struct KnownValue(bool IsKnown, object? Value);

        private readonly record struct AuthoredTypeIdentity(AssemblyIdentity Assembly, int Compilation, string Type);

        private readonly record struct ServiceBinding(BoundTypeIdentity Contract, BoundTypeIdentity Implementation);

        private readonly record struct ClosedCallableValue(
            bool IsKnown,
            AuthoredMember? Target,
            bool CanBeAbsent,
            bool EffectFreeExternal,
            IReadOnlyList<AuthoredMember>? Targets = null);

        private sealed record LazyFactoryResolution(
            ISymbol Storage,
            BaseObjectCreationExpressionSyntax Construction,
            AuthoredMember Factory,
            bool UsesExecutionAndPublication,
            bool UsesAtomicPublication);

        private readonly Dictionary<string, List<AuthoredMember>> members = new(StringComparer.Ordinal);

        private readonly Dictionary<MethodIdentity, List<AuthoredMember>> membersByIdentity = [];

        private readonly Dictionary<AuthoredTypeIdentity, List<AuthoredMember>> membersByType = [];

        private readonly HashSet<ServiceBinding> bindings = [];

        private readonly HashSet<BoundContractIdentity> boundContracts = [];

        private readonly HashSet<MethodIdentity> nonAuthoredMethods = [];

        private readonly List<(InvocationExpressionSyntax Call, SemanticModel Model)> invocations = [];

        private readonly List<(BaseObjectCreationExpressionSyntax Creation, SemanticModel Model)> objectCreations = [];

        private readonly List<(ConstructorInitializerSyntax Initializer, SemanticModel Model)> constructorInitializers = [];

        private readonly List<HostedProducerInventoryDiagnostic> diagnostics = [];

        private readonly Dictionary<string, HostedProducerSite> sites = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> admissionCapsules = new(StringComparer.Ordinal);

        private readonly HashSet<GraphMemberIdentity> lifecycleMembers = [];

        private HostedProducerOperationEntry[] declaredOperations = [];

        private readonly Dictionary<GraphMemberIdentity, InvocationExpressionSyntax[]> admissionCalls = [];

        private readonly Dictionary<(GraphMemberIdentity Member, string Bindings), bool> admissionSeeds = [];

        private readonly Dictionary<TraversalStateIdentity, TraversalEvidence> analyzedStates = [];

        private readonly Dictionary<string, int> analyzedStateCounts = new(StringComparer.Ordinal);

        private readonly HashSet<string> limitedRoots = new(StringComparer.Ordinal);

        private readonly HashSet<(string RootOperation, string Kind, int CheckpointVersion)>
            ownerRecoveryAssociations = [];

        private readonly Dictionary<ISymbol, bool> absentTestCallables = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<(GraphMemberIdentity Member, int ExpressionStart, int ExpressionLength, string Bindings), IReadOnlySet<string>> admissionOrigins = [];

        private readonly Dictionary<ISymbol, BoundedDataProof> boundedLazyData = new(SymbolEqualityComparer.Default);

        private readonly HashSet<ISymbol> activeBoundedLazyData = new(SymbolEqualityComparer.Default);

        private readonly HashSet<(GraphMemberIdentity Member, int DeclarationStart)>
            activeRecoveryLocals = [];

        private readonly Dictionary<ISymbol, List<string>> boundedLazyDataEvidence = new(SymbolEqualityComparer.Default);

        private ISymbol? activeBoundedDataProbe;

        private readonly Dictionary<GraphMemberIdentity, List<SyntaxNode>> selectedRoots = [];

        private readonly Dictionary<Compilation, Dictionary<IAssemblySymbol, Dictionary<string, AuthoredMember>>> resolvedMembers = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<Compilation, int> compilationIdentities = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<SyntaxTree, int> treeIdentities = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<SyntaxTree, SemanticModel> semanticModels = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<Compilation, Dictionary<IAssemblySymbol, int>> authoredCompilationIdentities = new(ReferenceEqualityComparer.Instance);

        private RecoveryMatrix? recoveryMatrix;

        private bool recoveryMatrixResolved;

        private const int MaximumAnalyzedStatesPerRoot = 8192;

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

                    semanticModels.Add(tree, model);

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

                        if (node is BaseObjectCreationExpressionSyntax creation)
                        {
                            objectCreations.Add((creation, model));
                        }

                        if (node is ConstructorInitializerSyntax initializer)
                        {
                            constructorInitializers.Add((initializer, model));
                        }
                    }
                }
            }

            foreach ((InvocationExpressionSyntax call, SemanticModel model) in invocations)
            {
                ReadBinding(call, model);
            }
        }

        private static MethodIdentity Identity(IMethodSymbol method) => new(method.ContainingAssembly.Identity, MethodKey(method));

        private IEnumerable<AuthoredMember> AuthoredMembers => members.Values.SelectMany(static group => group);

        private void AddMember(AuthoredMember member)
        {
            string key = MethodKey(member.Symbol);

            AuthoredTypeIdentity typeIdentity = new(member.Symbol.ContainingAssembly.Identity, CompilationIdentity(member.Model.Compilation), TypeKey(member.Symbol.ContainingType));

            if (!membersByType.TryGetValue(typeIdentity, out List<AuthoredMember>? typeMembers))
            {
                membersByType.Add(typeIdentity, typeMembers = []);
            }

            typeMembers.Add(member);

            if (!members.TryGetValue(key, out List<AuthoredMember>? candidates))
            {
                members.Add(key, candidates = []);
            }

            candidates.Add(member);

            if (member.Syntax is not MethodDeclarationSyntax { Body: null, ExpressionBody: null })
            {
                MethodIdentity identity = new(member.Symbol.ContainingAssembly.Identity, key);

                if (!membersByIdentity.TryGetValue(identity, out List<AuthoredMember>? exactCandidates))
                {
                    membersByIdentity.Add(identity, exactCandidates = []);
                }

                exactCandidates.Add(member);
            }
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

        private GraphMemberIdentity MemberIdentity(AuthoredMember member) => new(member.Symbol.ContainingAssembly.Identity, CompilationIdentity(member.Model.Compilation), TreeIdentity(member.Syntax.SyntaxTree), MethodKey(member.Symbol), member.Syntax.SpanStart, member.Syntax.Span.Length);

        private TraversalStateIdentity TraversalState(AuthoredMember member, string rootType, string operationId, bool lifecycle, string? inheritedWork, string? inheritedEffect, string? recoveryEffect, SyntaxNode? selection, IReadOnlySet<int>? fieldPublications, bool completionOwned)
        {
            SyntaxNode selected = selection ?? member.Syntax;

            return new(
                rootType,
                RootOperation(operationId),
                MemberIdentity(member),
                selected.SpanStart,
                selected.Span.Length,
                AdmissionBindingIdentity(member),
                CallableBindingIdentity(member, []),
                ValueBindingIdentity(member, []),
                ConcreteBindingIdentity(member),
                RecoveryBindingIdentity(member),
                inheritedWork,
                inheritedEffect,
                recoveryEffect,
                fieldPublications is null ? "" : string.Join(",", fieldPublications.Order()),
                completionOwned,
                lifecycle);
        }

        private static string RecoveryBindingIdentity(AuthoredMember member) =>
            (member.RecoveryContext is { } context
                ? context.Kind
                    + ":"
                    + context.CheckpointVersion
                    + ":"
                    + context.Disposition
                : "unbound")
            + "|"
            + (member.RecoveryBindings is null || member.RecoveryBindings.Count == 0
                ? "unbound"
                : string.Join(
                    ";",
                    member.RecoveryBindings
                        .OrderBy(static pair => pair.Key is IParameterSymbol parameter
                            ? parameter.Ordinal
                            : int.MaxValue)
                        .ThenBy(static pair => pair.Key.ToDisplayString(), StringComparer.Ordinal)
                        .Select(static pair =>
                            (pair.Key is IParameterSymbol parameter ? parameter.Ordinal : -1)
                            + ":"
                            + pair.Value.Kind
                            + ":"
                            + pair.Value.CheckpointVersion
                            + ":"
                            + pair.Value.Disposition)));

        private string CallableBindingIdentity(AuthoredMember member, HashSet<GraphMemberIdentity> path)
        {
            if ((member.CallableBindings is null || member.CallableBindings.Count == 0) && (member.AbsentCallables is null || member.AbsentCallables.Count == 0))
            {
                return "unbound";
            }

            GraphMemberIdentity identity = MemberIdentity(member);

            if (!path.Add(identity))
            {
                return "cycle:" + identity.Method;
            }

            try
            {
                System.Text.StringBuilder value = new();

                foreach (ISymbol absent in member.AbsentCallables?.OrderBy(static symbol => symbol is IParameterSymbol parameter ? parameter.Ordinal : int.MaxValue).ThenBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal) ?? Enumerable.Empty<ISymbol>())
                {
                    value.Append("absent:").Append(absent is IParameterSymbol parameter ? parameter.Ordinal : -1).Append(';');
                }

                foreach ((ISymbol symbol, AuthoredMember target) in member.CallableBindings?.OrderBy(static pair => pair.Key is IParameterSymbol parameter ? parameter.Ordinal : int.MaxValue).ThenBy(static pair => pair.Key.ToDisplayString(), StringComparer.Ordinal) ?? Enumerable.Empty<KeyValuePair<ISymbol, AuthoredMember>>())
                {
                    GraphMemberIdentity targetIdentity = MemberIdentity(target);

                    value.Append(symbol is IParameterSymbol parameter ? parameter.Ordinal : -1).Append(':').Append(targetIdentity.Compilation).Append(':').Append(targetIdentity.Tree).Append(':').Append(targetIdentity.SpanStart).Append(':').Append(targetIdentity.SpanLength).Append(':').Append(targetIdentity.Method).Append(':').Append(AdmissionBindingIdentity(target)).Append(':').Append(ConcreteBindingIdentity(target)).Append(':').Append(CallableBindingIdentity(target, path)).Append(';');
                }

                return value.ToString();
            }
            finally
            {
                path.Remove(identity);
            }
        }

        private string ValueBindingIdentity(
            AuthoredMember member,
            HashSet<GraphMemberIdentity> path) => ValueBindingIdentity(
                member,
                path,
                new Dictionary<AuthoredMember, string>(
                    ReferenceEqualityComparer.Instance));

        private string ValueBindingIdentity(
            AuthoredMember member,
            HashSet<GraphMemberIdentity> path,
            Dictionary<AuthoredMember, string> memoized)
        {
            if (member.ValueBindings is null || member.ValueBindings.Count == 0)
            {
                return "unbound";
            }

            if (memoized.TryGetValue(member, out string? memoizedIdentity))
            {
                return memoizedIdentity;
            }

            GraphMemberIdentity identity = MemberIdentity(member);

            if (!path.Add(identity))
            {
                return CompactIdentity("cycle:" + identity.Method);
            }

            try
            {
                System.Text.StringBuilder value = new();

                foreach ((ISymbol symbol, BoundValueSource source) in
                    member.ValueBindings
                        .OrderBy(static pair => pair.Key is IParameterSymbol parameter
                            ? parameter.Ordinal
                            : int.MaxValue)
                        .ThenBy(static pair => pair.Key.ToDisplayString(), StringComparer.Ordinal))
                {
                    GraphMemberIdentity caller = MemberIdentity(source.Caller);

                    value.Append(symbol is IParameterSymbol parameter
                            ? parameter.Ordinal
                            : -1)
                        .Append(':')
                        .Append(caller.Compilation)
                        .Append(':')
                        .Append(caller.Tree)
                        .Append(':')
                        .Append(caller.SpanStart)
                        .Append(':')
                        .Append(caller.SpanLength)
                        .Append(':')
                        .Append(source.Expression.SpanStart)
                        .Append(':')
                        .Append(source.Expression.Span.Length)
                        .Append(':')
                        .Append(ValueBindingIdentity(
                            source.Caller,
                            path,
                            memoized))
                        .Append(';');
                }

                string result = CompactIdentity(value.ToString());

                memoized.Add(member, result);

                return result;
            }
            finally
            {
                path.Remove(identity);
            }
        }

        private static string CompactIdentity(string value) =>
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value)));

        private string ConcreteBindingIdentity(AuthoredMember member)
        {
            if (member.ConcreteBindings is null || member.ConcreteBindings.Count == 0)
            {
                return "unbound";
            }

            return string.Join(";", member.ConcreteBindings.OrderBy(static pair => pair.Key is IParameterSymbol parameter ? parameter.Ordinal : int.MaxValue).ThenBy(static pair => pair.Key.ToDisplayString(), StringComparer.Ordinal).Select(pair =>
            {
                BoundTypeIdentity type = TypeIdentity(pair.Value, member.Model.Compilation);

                return (pair.Key is IParameterSymbol parameter ? parameter.Ordinal : -1) + ":" + type.Assembly.Name + ":" + type.Compilation + ":" + type.Type;
            }));
        }

        private string RootOperation(string operationId)
        {
            string? declared = declaredOperations.Select(static operation => operation.OperationId).Where(root => operationId == root || operationId.StartsWith(root + "/", StringComparison.Ordinal)).OrderByDescending(static root => root.Length).FirstOrDefault();

            if (declared is not null)
            {
                return declared;
            }

            int separator = new[] { "/call@", "/callback@", "/get@", "/set@", "/dispose@", "/work@", "/effect@", "/site@" }.Select(marker => operationId.IndexOf(marker, StringComparison.Ordinal)).Where(static index => index >= 0).DefaultIfEmpty(operationId.Length).Min();

            return operationId[..separator];
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

            foreach (ITypeSymbol? implementation in implementations.DefaultIfEmpty(null))
            {
                bool assignable = implementation is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } concrete && (SymbolEqualityComparer.Default.Equals(concrete, contract) || concrete.AllInterfaces.Any(slot => SymbolEqualityComparer.Default.Equals(slot, contract)) || IsBase(concrete, contract));

                if (assignable)
                {
                    BoundTypeIdentity contractIdentity = TypeIdentity(contract, model.Compilation);

                    bindings.Add(new(contractIdentity, TypeIdentity(implementation!, model.Compilation)));

                    boundContracts.Add(new(contractIdentity.Assembly, contractIdentity.Type));
                }
            }
        }

        private BoundTypeIdentity TypeIdentity(ITypeSymbol type, Compilation consumingCompilation) => new(type.ContainingAssembly.Identity, AuthoredCompilationIdentity(type, consumingCompilation), TypeKey(type));

        private int? AuthoredCompilationIdentity(ISymbol symbol, Compilation consumingCompilation)
        {
            if (!authoredCompilationIdentities.TryGetValue(consumingCompilation, out Dictionary<IAssemblySymbol, int>? identities))
            {
                authoredCompilationIdentities.Add(consumingCompilation, identities = new(ReferenceEqualityComparer.Instance));
            }

            IAssemblySymbol assembly = symbol.ContainingAssembly;

            if (identities.TryGetValue(assembly, out int cached))
            {
                return cached;
            }

            if (ReferenceEquals(consumingCompilation.Assembly, assembly))
            {
                int exact = CompilationIdentity(consumingCompilation);

                identities.Add(assembly, exact);

                return exact;
            }

            CSharpCompilation[] identityMatches = compilationIdentities.Keys.OfType<CSharpCompilation>().Where(compilation => compilation.Assembly.Identity.Equals(assembly.Identity)).ToArray();

            if (identityMatches.Length == 1)
            {
                int exact = CompilationIdentity(identityMatches[0]);

                identities.Add(assembly, exact);

                return exact;
            }

            HashSet<SyntaxTree> declaringTrees = symbol.DeclaringSyntaxReferences.Select(static reference => reference.SyntaxTree).ToHashSet<SyntaxTree>(ReferenceEqualityComparer.Instance);

            CSharpCompilation[] declaringCompilations = compilationIdentities.Keys
                .OfType<CSharpCompilation>()
                .Where(compilation => declaringTrees.Any(tree => compilation.SyntaxTrees.Contains(tree, ReferenceEqualityComparer.Instance)))
                .ToArray();

            if (declaringCompilations.Length == 1)
            {
                int exact = CompilationIdentity(declaringCompilations[0]);

                identities.Add(assembly, exact);

                return exact;
            }

            return null;
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

            if (symbol is IMethodSymbol factory && Resolve(factory, model.Compilation) is { } authored)
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
            else if (model.GetTypeInfo(expression).Type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } concrete)
            {
                resultTypes = [concrete];
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

                if (resolvedRoots.Any(existing => MemberIdentity(existing.Member) == MemberIdentity(roots[0]) && existing.Selection.Span.OverlapsWith(selection.Span)))
                {
                    diagnostics.Add(new("HOSTED_ROOT_OVERLAP", operation.OperationId, "Declared authority roots must select disjoint source regions; whole-member mixed claims are forbidden."));
                }

                resolvedRoots.Add((operation, roots[0], selection));

                if (selection != roots[0].Syntax)
                {
                    GraphMemberIdentity key = MemberIdentity(roots[0]);

                    if (!selectedRoots.TryGetValue(key, out List<SyntaxNode>? selections))
                    {
                        selectedRoots[key] = selections = [];
                    }

                    selections.Add(selection);
                }
            }

            foreach (AuthoredMember root in AuthoredMembers.Where(member => hosted.Contains(member.Symbol.ContainingType.Name) && IsLifecycle(member.Symbol)))
            {
                _ = Traverse(root, root.Symbol.ContainingType.Name, TypeKey(root.Symbol.ContainingType) + "." + root.Symbol.Name, [], true);
            }

            foreach ((HostedProducerOperationEntry operation, AuthoredMember root, SyntaxNode selection) in resolvedRoots)
            {
                string rootType = catalog.FirstOrDefault(service => service.Operations.Contains(operation))?.ServiceType ?? nonHostedCatalog.FirstOrDefault(chain => chain.ChainId == operation.OperationId)?.EnclosingType ?? operation.EnclosingType;

                _ = Traverse(root, rootType, operation.OperationId, [], false, selection: selection);
            }

            ValidateOwnerRecoveryAssociations();

            foreach ((InvocationExpressionSyntax call, SemanticModel model) in invocations)
            {
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol target)
                {
                    continue;
                }

                AuthoredMember? authored = Resolve(target, model.Compilation);

                if (authored is null || !hosted.Contains(authored.Symbol.ContainingType.Name) || IsLifecycle(target))
                {
                    continue;
                }

                IMethodSymbol? caller = model.GetEnclosingSymbol(call.SpanStart) as IMethodSymbol;

                if (caller is not null && SymbolEqualityComparer.Default.Equals(caller.ContainingType, authored.Symbol.ContainingType))
                {
                    continue;
                }

                if (caller is not null && Resolve(caller, model.Compilation) is { } authoredCaller && lifecycleMembers.Contains(MemberIdentity(authoredCaller)))
                {
                    continue;
                }

                bool catalogued = declared.Any(entry => entry.EnclosingType == TypeKey(authored.Symbol.ContainingType) && entry.Member == authored.Symbol.Name && entry.SourcePath == authored.Syntax.SyntaxTree.FilePath);

                int before = sites.Count;

                string operation = TypeKey(authored.Symbol.ContainingType) + "." + authored.Symbol.Name;

                _ = Traverse(authored, authored.Symbol.ContainingType.Name, operation, [], false);

                if (!catalogued && sites.Count > before)
                {
                    diagnostics.Add(new("HOSTED_EXTERNAL_OPERATION_UNCATALOGUED", Location(call), operation));
                }
            }

            return new(sites.Values.OrderBy(SiteIdentity, StringComparer.Ordinal).ToArray(), diagnostics.Distinct().ToArray());
        }

        private void ValidateOwnerRecoveryAssociations()
        {
            if (recoveryMatrix is not { IsValid: true } matrix)
            {
                return;
            }

            RecoveryTuple[] ownerTuples = matrix.Tuples
                .Where(static tuple => tuple.Disposition == RecoveryDisposition.OwnerBound)
                .ToArray();

            foreach (RecoveryTuple tuple in ownerTuples)
            {
                if (!ownerRecoveryAssociations.Any(association =>
                        association.Kind == tuple.Kind
                        && association.CheckpointVersion == tuple.CheckpointVersion))
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_OWNER_ROOT_UNPROVEN",
                        tuple.Kind + ":" + tuple.CheckpointVersion,
                        "The owner-bound checkpoint must be reached from a declared exact-owner recovery root."));
                }
            }

            foreach (HostedProducerOperationEntry root in declaredOperations.Where(static operation =>
                operation.Authority == HostedProducerAuthorityKind.OwnerBoundRecovery))
            {
                if (!ownerRecoveryAssociations.Any(association =>
                        association.RootOperation == root.OperationId))
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_OWNER_ROOT_UNPROVEN",
                        root.OperationId,
                        "The declared exact-owner recovery root must reach at least one owner-bound checkpoint handler."));
                }
            }
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

            int fingerprintSeparator = operationId.LastIndexOf('~');

            if (separator < prefix.Length || fingerprintSeparator <= separator || !int.TryParse(operationId[(separator + 1)..fingerprintSeparator], NumberStyles.None, CultureInfo.InvariantCulture, out int occurrence) || occurrence < 0 || !IsSha256(operationId[(fingerprintSeparator + 1)..]))
            {
                return null;
            }

            string callee = operationId[prefix.Length..separator];

            InvocationExpressionSyntax? selected = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol target && Normalize(target) == callee).ElementAtOrDefault(occurrence);

            return selected is not null && Fingerprint(selected) == operationId[(fingerprintSeparator + 1)..]
                ? selected
                : null;
        }

        private AuthoredMember? Resolve(IMethodSymbol method, Compilation consumingCompilation)
        {
            IMethodSymbol definition = (method.ReducedFrom ?? method).OriginalDefinition;

            static bool IsClosedInternalType(INamedTypeSymbol type) => type.DeclaredAccessibility is Accessibility.Internal or Accessibility.Private
                || type.ContainingType is { } containing && IsClosedInternalType(containing);

            MethodIdentity identity = Identity(method);

            if (resolvedMembers.TryGetValue(consumingCompilation, out Dictionary<IAssemblySymbol, Dictionary<string, AuthoredMember>>? assemblyResolutions)
                && assemblyResolutions.TryGetValue(definition.ContainingAssembly, out Dictionary<string, AuthoredMember>? methodResolutions)
                && methodResolutions.TryGetValue(identity.Method, out AuthoredMember? cached))
            {
                return cached;
            }

            if (nonAuthoredMethods.Contains(identity))
            {
                return null;
            }

            bool hasAuthoredCandidates = membersByIdentity.TryGetValue(identity, out List<AuthoredMember>? identityCandidates);

            bool hasBoundContract = method.ContainingType.TypeKind == TypeKind.Interface && boundContracts.Contains(new(identity.Assembly, TypeKey(method.ContainingType)));

            bool hasClosedInternalContract = method.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } contract && IsClosedInternalType(contract);

            if (!hasAuthoredCandidates && !hasBoundContract && !hasClosedInternalContract)
            {
                nonAuthoredMethods.Add(identity);

                return null;
            }

            int? authoredCompilation = AuthoredCompilationIdentity(definition, consumingCompilation);

            void Cache(AuthoredMember target)
            {
                if (!resolvedMembers.TryGetValue(consumingCompilation, out Dictionary<IAssemblySymbol, Dictionary<string, AuthoredMember>>? exactAssemblyResolutions))
                {
                    resolvedMembers.Add(consumingCompilation, exactAssemblyResolutions = new(ReferenceEqualityComparer.Instance));
                }

                if (!exactAssemblyResolutions.TryGetValue(definition.ContainingAssembly, out Dictionary<string, AuthoredMember>? exactMethodResolutions))
                {
                    exactAssemblyResolutions.Add(definition.ContainingAssembly, exactMethodResolutions = new(StringComparer.Ordinal));
                }

                exactMethodResolutions.Add(identity.Method, target);
            }

            AuthoredMember? BoundImplementation()
            {
                if (method.ContainingType.TypeKind != TypeKind.Interface)
                {
                    return null;
                }

                BoundTypeIdentity contract = TypeIdentity(method.ContainingType, consumingCompilation);

                ServiceBinding[] eligibleBindings = bindings
                    .Where(binding => binding.Contract.Assembly.Equals(contract.Assembly)
                        && binding.Contract.Type == contract.Type
                        && (contract.Compilation is null || binding.Contract.Compilation == contract.Compilation))
                    .ToArray();

                HashSet<GraphMemberIdentity> seen = [];

                List<AuthoredMember> implementations = [];

                foreach (ServiceBinding binding in eligibleBindings)
                {
                    if (binding.Implementation.Compilation is not int implementationCompilation || !membersByType.TryGetValue(new(binding.Implementation.Assembly, implementationCompilation, binding.Implementation.Type), out List<AuthoredMember>? implementationMembers))
                    {
                        continue;
                    }

                    foreach (AuthoredMember candidate in implementationMembers)
                    {
                        bool implements = candidate.Symbol.ContainingType.AllInterfaces
                            .Where(slotType => slotType.ContainingAssembly.Identity.Equals(binding.Contract.Assembly) && TypeKey(slotType) == binding.Contract.Type)
                            .SelectMany(static slotType => slotType.GetMembers())
                            .OfType<IMethodSymbol>()
                            .Any(slot => MethodKey(slot) == MethodKey(method)
                                && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                                && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, candidate.Symbol.OriginalDefinition));

                        if (implements && seen.Add(MemberIdentity(candidate)))
                        {
                            implementations.Add(candidate);
                        }
                    }
                }

                return implementations.Count == 1 ? implementations[0] : null;
            }

            AuthoredMember? ClosedInternalImplementation()
            {
                if (method.ContainingType is not INamedTypeSymbol contract || contract.TypeKind != TypeKind.Interface || !IsClosedInternalType(contract))
                {
                    return null;
                }

                HashSet<GraphMemberIdentity> seen = [];

                List<AuthoredMember> implementations = [];

                foreach (AuthoredMember candidate in AuthoredMembers.Where(candidate => candidate.Symbol.ContainingAssembly.Identity.Equals(contract.ContainingAssembly.Identity)))
                {
                    bool implements = candidate.Symbol.ContainingType.AllInterfaces
                        .Where(slotType => slotType.ContainingAssembly.Identity.Equals(contract.ContainingAssembly.Identity) && TypeKey(slotType) == TypeKey(contract))
                        .SelectMany(static slotType => slotType.GetMembers())
                        .OfType<IMethodSymbol>()
                        .Any(slot => MethodKey(slot) == MethodKey(method)
                            && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                            && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, candidate.Symbol.OriginalDefinition));

                    if (implements && seen.Add(MemberIdentity(candidate)))
                    {
                        implementations.Add(candidate);
                    }
                }

                return implementations.Count == 1 ? implementations[0] : null;
            }

            if (!hasAuthoredCandidates)
            {
                AuthoredMember? implementation = BoundImplementation() ?? ClosedInternalImplementation();

                if (implementation is not null)
                {
                    Cache(implementation);
                }

                return implementation;
            }

            AuthoredMember? exactAuthored = null;

            AuthoredMember? onlyAuthored = null;

            int exactCount = 0;

            int candidateCount = 0;

            foreach (AuthoredMember candidate in identityCandidates!)
            {
                if (authoredCompilation is not null && CompilationIdentity(candidate.Model.Compilation) != authoredCompilation)
                {
                    continue;
                }

                onlyAuthored = candidate;

                candidateCount++;

                if (SymbolEqualityComparer.Default.Equals(candidate.Symbol.OriginalDefinition, definition))
                {
                    exactAuthored = candidate;

                    exactCount++;
                }
            }

            if (exactCount == 1)
            {
                Cache(exactAuthored!);

                return exactAuthored;
            }

            if (candidateCount == 1)
            {
                Cache(onlyAuthored!);

                return onlyAuthored;
            }

            AuthoredMember? bound = BoundImplementation() ?? ClosedInternalImplementation();

            if (bound is not null)
            {
                Cache(bound);
            }

            return bound;
        }

        private AuthoredMember? ResolveInvocationTarget(IMethodSymbol method, AuthoredMember member, SyntaxNode node)
        {
            if (method.ContainingType.TypeKind == TypeKind.Interface && node is InvocationExpressionSyntax call && AdmissionReceiver(member, call) is { } receiver && ConcreteType(member, receiver) is { } concrete)
            {
                IMethodSymbol[] implementations = concrete.AllInterfaces
                    .Where(contract => contract.ContainingAssembly.Identity.Equals(method.ContainingAssembly.Identity) && TypeKey(contract) == TypeKey(method.ContainingType))
                    .SelectMany(static contract => contract.GetMembers())
                    .OfType<IMethodSymbol>()
                    .Where(slot => MethodKey(slot) == MethodKey(method))
                    .Select(slot => concrete.FindImplementationForInterfaceMember(slot))
                    .OfType<IMethodSymbol>()
                    .ToArray();

                if (implementations is [IMethodSymbol implementation] && Resolve(implementation, member.Model.Compilation) is { } target)
                {
                    return target;
                }
            }

            return Resolve(method, member.Model.Compilation);
        }

        private AuthoredMember[] ResolveBoundImplementations(
            IMethodSymbol method,
            Compilation consumingCompilation)
        {
            if (method.ContainingType.TypeKind != TypeKind.Interface)
            {
                return [];
            }

            BoundTypeIdentity contract = TypeIdentity(method.ContainingType, consumingCompilation);

            if (contract.Compilation is null)
            {
                return [];
            }

            ServiceBinding[] eligibleBindings = bindings
                .Where(binding => binding.Contract.Assembly.Equals(contract.Assembly)
                    && binding.Contract.Type == contract.Type
                    && binding.Contract.Compilation == contract.Compilation)
                .ToArray();

            HashSet<GraphMemberIdentity> seen = [];

            List<AuthoredMember> implementations = [];

            foreach (ServiceBinding binding in eligibleBindings)
            {
                if (binding.Implementation.Compilation is not int implementationCompilation
                    || !membersByType.TryGetValue(
                        new(
                            binding.Implementation.Assembly,
                            implementationCompilation,
                            binding.Implementation.Type),
                        out List<AuthoredMember>? implementationMembers))
                {
                    continue;
                }

                foreach (AuthoredMember candidate in implementationMembers)
                {
                    bool implements = candidate.Symbol.ContainingType.AllInterfaces
                        .Where(slotType => slotType.ContainingAssembly.Identity.Equals(contract.Assembly)
                            && TypeKey(slotType) == contract.Type)
                        .SelectMany(static slotType => slotType.GetMembers())
                        .OfType<IMethodSymbol>()
                        .Any(slot => MethodKey(slot) == MethodKey(method)
                            && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                            && SymbolEqualityComparer.Default.Equals(
                                implementation.OriginalDefinition,
                                candidate.Symbol.OriginalDefinition));

                    if (implements && seen.Add(MemberIdentity(candidate)))
                    {
                        implementations.Add(candidate);
                    }
                }
            }

            return implementations
                .OrderBy(static implementation => TypeKey(implementation.Symbol.ContainingType), StringComparer.Ordinal)
                .ThenBy(static implementation => MethodKey(implementation.Symbol), StringComparer.Ordinal)
                .ToArray();
        }

        private AuthoredMember[] ResolveStrategyTargets(IMethodSymbol method)
        {
            if (Normalize(method) == "RetroDownfall.Arcanum.Infrastructure.Data.Schema.IGrimoireSchemaBackfill.AdvanceBatchAsync")
            {
                return AuthoredMembers.Where(candidate => SchemaBackfillStrategies.Contains(TypeKey(candidate.Symbol.ContainingType)) && candidate.Symbol.ContainingType.AllInterfaces
                        .Where(contract => contract.ContainingAssembly.Identity.Equals(method.ContainingAssembly.Identity) && TypeKey(contract) == TypeKey(method.ContainingType))
                        .SelectMany(static contract => contract.GetMembers())
                        .OfType<IMethodSymbol>()
                        .Any(slot => MethodKey(slot) == MethodKey(method) && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, candidate.Symbol.OriginalDefinition)))
                    .OrderBy(static candidate => TypeKey(candidate.Symbol.ContainingType), StringComparer.Ordinal)
                    .ToArray();
            }

            return TypeKey(method.ContainingType) == "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.IGrimoireOfflineTransitionHandler"
                ? ResolveClosedProductionRegistryTargets(method)
                : [];
        }

        private AuthoredMember[] ResolveReviewedClosedDispatchTargets(IMethodSymbol method, SyntaxNode node)
        {
            string contractMethod = TypeKey(method.ContainingType) + "." + method.Name;

            if (method.ContainingType.TypeKind != TypeKind.Interface
                || !ReviewedClosedDispatchTargets.TryGetValue(contractMethod, out IReadOnlySet<string>? expectedTypes))
            {
                return [];
            }

            AuthoredMember[] targets = AuthoredMembers
                .Where(candidate => candidate.Symbol.ContainingType.AllInterfaces
                    .Where(contract => contract.ContainingAssembly.Identity.Equals(method.ContainingAssembly.Identity)
                        && TypeKey(contract) == TypeKey(method.ContainingType))
                    .SelectMany(static contract => contract.GetMembers())
                    .OfType<IMethodSymbol>()
                    .Any(slot => MethodKey(slot) == MethodKey(method)
                        && candidate.Symbol.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                        && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, candidate.Symbol.OriginalDefinition)))
                .DistinctBy(MemberIdentity)
                .OrderBy(static target => TypeKey(target.Symbol.ContainingType), StringComparer.Ordinal)
                .ThenBy(static target => MethodKey(target.Symbol), StringComparer.Ordinal)
                .ToArray();

            string[] observedTypes = targets
                .Select(static target => TypeKey(target.Symbol.ContainingType))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (!new HashSet<string>(expectedTypes, StringComparer.Ordinal).SetEquals(observedTypes))
            {
                diagnostics.Add(new(
                    "HOSTED_CLOSED_DISPATCH_CHANGED",
                    Location(node),
                    contractMethod + "; expected exactly [" + string.Join(", ", expectedTypes.Order(StringComparer.Ordinal)) + "] but found [" + string.Join(", ", observedTypes) + "]."));

                return [];
            }

            return targets;
        }

        private AuthoredMember[] ResolveClosedProductionRegistryTargets(IMethodSymbol method)
        {
            if (method.ContainingType is not INamedTypeSymbol
                {
                    TypeKind: TypeKind.Interface,
                    DeclaredAccessibility: Accessibility.Internal or Accessibility.Private,
                })
            {
                return [];
            }

            var candidates = semanticModels.SelectMany(pair => pair.Key.GetRoot()
                    .DescendantNodes()
                    .OfType<PropertyDeclarationSyntax>()
                    .Select(property => (
                        Property: property,
                        Model: pair.Value,
                        Symbol: pair.Value.GetDeclaredSymbol(property))))
                .Where(candidate => candidate.Symbol is
                {
                    Name: "Production",
                    IsStatic: true,
                    SetMethod: null,
                }
                    && candidate.Property.Initializer?.Value is not null
                    && candidate.Symbol.ContainingAssembly.Identity.Equals(
                        method.ContainingAssembly.Identity))
                .ToArray();

            if (candidates is not [var production]
                || production.Symbol is not IPropertySymbol productionSymbol
                || productionSymbol.ContainingType is not
                {
                    IsSealed: true,
                    IsAbstract: false,
                } registryType
                || production.Property.Initializer?.Value is not InvocationExpressionSyntax valueCall
                || valueCall.ArgumentList.Arguments is not [ArgumentSyntax valueArgument]
                || valueArgument.Expression is not InvocationExpressionSyntax createCall
                || createCall.ArgumentList.Arguments is not [ArgumentSyntax createArgument]
                || createArgument.Expression is not CollectionExpressionSyntax collection
                || production.Model.GetSymbolInfo(valueCall).Symbol is not IMethodSymbol
                {
                    Name: "Value",
                    IsStatic: true,
                } valueMethod
                || production.Model.GetSymbolInfo(createCall).Symbol is not IMethodSymbol
                {
                    Name: "Create",
                    IsStatic: true,
                } createMethod
                || !SymbolEqualityComparer.Default.Equals(
                    valueMethod.ContainingType,
                    registryType)
                || !SymbolEqualityComparer.Default.Equals(
                    createMethod.ContainingType,
                    registryType)
                || registryType.InstanceConstructors.Any(static constructor =>
                    !constructor.IsImplicitlyDeclared
                    && constructor.DeclaredAccessibility != Accessibility.Private))
            {
                return [];
            }

            InvocationExpressionSyntax[] createCalls = invocations
                .Where(candidate => candidate.Model.GetSymbolInfo(candidate.Call).Symbol is IMethodSymbol target
                    && Identity(target) == Identity(createMethod))
                .Select(static candidate => candidate.Call)
                .ToArray();

            if (createCalls is not [InvocationExpressionSyntax onlyCreate]
                || onlyCreate != createCall
                || Resolve(createMethod, production.Model.Compilation) is not { } createTarget)
            {
                return [];
            }

            bool ContainsContract(ITypeSymbol type)
            {
                if (type.ContainingAssembly is { } containingAssembly
                    && containingAssembly.Identity.Equals(method.ContainingAssembly.Identity)
                    && TypeKey(type) == TypeKey(method.ContainingType))
                {
                    return true;
                }

                return type is INamedTypeSymbol named
                    && named.TypeArguments.Any(ContainsContract);
            }

            IFieldSymbol[] handlerFields = registryType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(field => !field.IsImplicitlyDeclared && ContainsContract(field.Type))
                .ToArray();

            if (handlerFields.Any(static field => !field.IsReadOnly)
                || AuthoredMembers.Any(owner => SymbolEqualityComparer.Default.Equals(
                        owner.Symbol.ContainingType,
                        registryType)
                    && owner.Symbol.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor)
                    && owner.Syntax.DescendantNodesAndSelf()
                        .OfType<AssignmentExpressionSyntax>()
                        .Any(assignment => owner.Model.GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol field
                            && handlerFields.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, field))))
                || createTarget.Syntax.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(call => createTarget.Model.GetTypeInfo(call).Type is { } returnType
                        && ContainsContract(returnType))
                || createTarget.Syntax.DescendantNodesAndSelf()
                    .OfType<BaseObjectCreationExpressionSyntax>()
                    .Any(creation => createTarget.Model.GetTypeInfo(creation).Type is INamedTypeSymbol created
                        && created.AllInterfaces.Any(ContainsContract)))
            {
                return [];
            }

            BaseObjectCreationExpressionSyntax[] registryCreations = objectCreations
                .Where(candidate => candidate.Model.GetSymbolInfo(candidate.Creation).Symbol is IMethodSymbol constructor
                    && SymbolEqualityComparer.Default.Equals(constructor.ContainingType, registryType))
                .Select(static candidate => candidate.Creation)
                .ToArray();

            if (registryCreations.Length == 0
                || registryCreations.Any(creation => creation.SyntaxTree != createTarget.Syntax.SyntaxTree
                    || !createTarget.Syntax.Span.Contains(creation.Span)))
            {
                return [];
            }

            List<AuthoredMember> targets = [];

            foreach (CollectionElementSyntax element in collection.Elements)
            {
                if (element is not ExpressionElementSyntax
                    {
                        Expression: BaseObjectCreationExpressionSyntax creation,
                    }
                    || production.Model.GetTypeInfo(creation).Type is not INamedTypeSymbol
                    {
                        IsSealed: true,
                        IsAbstract: false,
                    } concrete)
                {
                    return [];
                }

                IMethodSymbol[] implementations = concrete.AllInterfaces
                    .Where(contract => contract.ContainingAssembly.Identity.Equals(
                            method.ContainingAssembly.Identity)
                        && TypeKey(contract) == TypeKey(method.ContainingType))
                    .SelectMany(static contract => contract.GetMembers())
                    .OfType<IMethodSymbol>()
                    .Where(slot => MethodKey(slot) == MethodKey(method))
                    .Select(slot => concrete.FindImplementationForInterfaceMember(slot))
                    .OfType<IMethodSymbol>()
                    .ToArray();

                if (implementations is not [IMethodSymbol implementation]
                    || Resolve(implementation, production.Model.Compilation) is not { } target)
                {
                    return [];
                }

                targets.Add(target);
            }

            return targets
                .DistinctBy(MemberIdentity)
                .OrderBy(static target => TypeKey(target.Symbol.ContainingType), StringComparer.Ordinal)
                .ThenBy(static target => MethodKey(target.Symbol), StringComparer.Ordinal)
                .ToArray();
        }

        private static ITypeSymbol? ConcreteType(AuthoredMember member, ExpressionSyntax expression)
        {
            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            if (symbol is not null && member.ConcreteBindings?.TryGetValue(symbol, out ITypeSymbol? bound) == true)
            {
                return bound;
            }

            return member.Model.GetTypeInfo(expression).Type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } concrete ? concrete : null;
        }

        private RecoveryMatrix ResolveRecoveryMatrix(SyntaxNode anchor)
        {
            if (recoveryMatrixResolved)
            {
                return recoveryMatrix!;
            }

            recoveryMatrixResolved = true;

            List<string> failures = [];

            RecoveryDescriptor[] descriptors = objectCreations
                .Where(candidate => candidate.Model.GetTypeInfo(candidate.Creation).Type is { } type
                    && TypeKey(type)
                        == "RetroDownfall.Arcanum.Core.Operations.LongRunningOperationRecoveryDescriptor"
                    && candidate.Creation.Ancestors()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault() is { } owner
                    && candidate.Model.GetDeclaredSymbol(owner) is INamedTypeSymbol ownerType
                    && TypeKey(ownerType)
                        == "RetroDownfall.Arcanum.Core.Operations.LongRunningOperationRecoveryRegistry")
                .Select(candidate =>
                {
                    if (candidate.Model.GetOperation(candidate.Creation) is not IObjectCreationOperation creation)
                    {
                        return null;
                    }

                    object? Argument(string name) => creation.Arguments
                        .SingleOrDefault(argument => argument.Parameter?.Name == name)
                        ?.Value.ConstantValue is
                        {
                            HasValue: true,
                        } value
                        ? value.Value
                        : null;

                    return Argument("Kind") is string kind
                        && Argument("MinCheckpointVersion") is int minimum
                        && Argument("MaxCheckpointVersion") is int maximum
                        ? new RecoveryDescriptor(kind, minimum, maximum)
                        : null;
                })
                .OfType<RecoveryDescriptor>()
                .ToArray();

            if (descriptors.Length == 0
                || descriptors.Any(static descriptor =>
                    descriptor.MinimumCheckpointVersion < 0
                    || descriptor.MaximumCheckpointVersion
                        < descriptor.MinimumCheckpointVersion)
                || descriptors.Select(static descriptor => descriptor.Kind)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != descriptors.Length)
            {
                failures.Add("The recovery registry must expose one valid descriptor per durable kind.");
            }

            AuthoredMember[] classifiers = AuthoredMembers
                .Where(static member => member.Symbol.Name == "Classify")
                .Where(member => TypeKey(member.Symbol.ContainingType)
                    == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationRecoveryAdmission")
                .ToArray();

            SwitchExpressionSyntax? classification = classifiers is [AuthoredMember classifier]
                ? classifier.Syntax.DescendantNodesAndSelf()
                    .OfType<SwitchExpressionSyntax>()
                    .SingleOrDefault(selection => selection.GoverningExpression is TupleExpressionSyntax)
                : null;

            if (classifiers is not [AuthoredMember exactClassifier]
                || classification?.GoverningExpression is not TupleExpressionSyntax
                {
                    Arguments: [ArgumentSyntax kindArgument, ArgumentSyntax versionArgument],
                }
                || exactClassifier.Model.GetSymbolInfo(kindArgument.Expression).Symbol is not
                    IPropertySymbol { Name: "Kind" } kindProperty
                || exactClassifier.Model.GetSymbolInfo(versionArgument.Expression).Symbol is not
                    IPropertySymbol { Name: "CheckpointVersion" } versionProperty
                || kindProperty.ContainingType is null
                || versionProperty.ContainingType is null
                || !SymbolEqualityComparer.Default.Equals(
                    kindProperty.ContainingType,
                    versionProperty.ContainingType))
            {
                failures.Add("Classify must switch exactly on one operation's (Kind, CheckpointVersion) tuple.");
            }

            Dictionary<(string Kind, int Version), RecoveryDisposition> explicitRows = [];

            bool hasUnsupportedDefault = false;

            if (classification is not null && classifiers is [AuthoredMember classifierMember])
            {
                foreach (SwitchExpressionArmSyntax arm in classification.Arms)
                {
                    RecoveryDisposition? disposition = RecoveryDispositionOf(
                        classifierMember,
                        arm.Expression);

                    if (arm.WhenClause is not null || disposition is null)
                    {
                        failures.Add("Every recovery classification arm must be unconditional and resolve to one known disposition.");

                        continue;
                    }

                    if (arm.Pattern is DiscardPatternSyntax)
                    {
                        hasUnsupportedDefault = disposition
                            is RecoveryDisposition.Unsupported;

                        continue;
                    }

                    if (arm.Pattern is not RecursivePatternSyntax
                        {
                            PositionalPatternClause.Subpatterns:
                            [SubpatternSyntax kindPattern, SubpatternSyntax versionPattern],
                        })
                    {
                        failures.Add("Every non-default classification arm must be one exact kind/version tuple pattern.");

                        continue;
                    }

                    string[] kinds = RecoveryPatternConstants<string>(
                        classifierMember,
                        kindPattern.Pattern);

                    int[] versions = RecoveryPatternConstants<int>(
                        classifierMember,
                        versionPattern.Pattern);

                    if (kinds.Length == 0 || versions.Length == 0)
                    {
                        failures.Add("Every classification tuple must use compile-time kind and checkpoint constants.");

                        continue;
                    }

                    foreach (string kind in kinds)
                    {
                        foreach (int version in versions)
                        {
                            if (!explicitRows.TryAdd((kind, version), disposition.Value))
                            {
                                failures.Add($"Recovery tuple {kind} V{version} is classified more than once.");
                            }
                        }
                    }
                }
            }

            if (!hasUnsupportedDefault)
            {
                failures.Add("The recovery classifier must fail closed with an unsupported default arm.");
            }

            HashSet<(string Kind, int Version)> registeredRows = descriptors
                .SelectMany(static descriptor => Enumerable.Range(
                        descriptor.MinimumCheckpointVersion,
                        descriptor.MaximumCheckpointVersion
                            - descriptor.MinimumCheckpointVersion
                            + 1)
                    .Select(version => (descriptor.Kind, version)))
                .ToHashSet();

            foreach ((string kind, int version) in explicitRows.Keys)
            {
                if (!registeredRows.Contains((kind, version)))
                {
                    failures.Add($"Recovery classification names unregistered tuple {kind} V{version}.");
                }
            }

            RecoveryTuple[] tuples = registeredRows
                .OrderBy(static row => row.Kind, StringComparer.Ordinal)
                .ThenBy(static row => row.Version)
                .Select(row => new RecoveryTuple(
                    row.Kind,
                    row.Version,
                    explicitRows.GetValueOrDefault(
                        row,
                        RecoveryDisposition.Unsupported)))
                .ToArray();

            if (failures.Count != 0)
            {
                foreach (string failure in failures.Distinct(StringComparer.Ordinal))
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_MATRIX_UNPROVEN",
                        Location(anchor),
                        failure));
                }
            }

            recoveryMatrix = new(
                descriptors,
                tuples,
                failures.Count == 0);

            return recoveryMatrix;
        }

        private static RecoveryDisposition? RecoveryDispositionOf(
            AuthoredMember classifier,
            ExpressionSyntax expression)
        {
            if (classifier.Model.GetSymbolInfo(expression).Symbol is IFieldSymbol field)
            {
                return field.Name switch
                {
                    "OrdinaryDbOnly" => RecoveryDisposition.OrdinaryDbOnly,
                    "OrdinaryExternalEffect" => RecoveryDisposition.OrdinaryExternalEffect,
                    "UnsupportedCheckpointVersion" => RecoveryDisposition.Unsupported,
                    _ => null,
                };
            }

            return expression is InvocationExpressionSyntax call
                && classifier.Model.GetSymbolInfo(call).Symbol is IMethodSymbol
                {
                    Name: "OwnerBoundKind",
                }
                ? RecoveryDisposition.OwnerBound
                : null;
        }

        private static T[] RecoveryPatternConstants<T>(
            AuthoredMember member,
            PatternSyntax pattern)
        {
            if (pattern is BinaryPatternSyntax binary
                && binary.IsKind(SyntaxKind.OrPattern))
            {
                return
                [
                    .. RecoveryPatternConstants<T>(member, binary.Left),
                    .. RecoveryPatternConstants<T>(member, binary.Right),
                ];
            }

            return pattern is ConstantPatternSyntax constant
                && member.Model.GetConstantValue(constant.Expression) is
                {
                    HasValue: true,
                    Value: T value,
                }
                ? [value]
                : [];
        }

        private RecoveryHandlerTarget[] ResolveRecoveryHandlerTargets(
            IMethodSymbol contractMethod,
            SyntaxNode anchor,
            RecoveryMatrix matrix)
        {
            string contract = TypeKey(contractMethod.ContainingType);

            List<RecoveryHandlerTarget> targets = [];

            foreach (AuthoredMember candidate in AuthoredMembers
                .Where(member => member.Symbol.Name == contractMethod.Name)
                .Where(member => member.Symbol.ContainingType.AllInterfaces.Any(type =>
                    type.ContainingAssembly.Identity.Equals(
                        contractMethod.ContainingAssembly.Identity)
                    && TypeKey(type) == contract)))
            {
                IMethodSymbol[] slots = candidate.Symbol.ContainingType.AllInterfaces
                    .Where(type => type.ContainingAssembly.Identity.Equals(
                            contractMethod.ContainingAssembly.Identity)
                        && TypeKey(type) == contract)
                    .SelectMany(static type => type.GetMembers())
                    .OfType<IMethodSymbol>()
                    .Where(slot => MethodKey(slot) == MethodKey(contractMethod))
                    .ToArray();

                if (!slots.Any(slot => candidate.Symbol.ContainingType
                        .FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                    && SymbolEqualityComparer.Default.Equals(
                        implementation.OriginalDefinition,
                        candidate.Symbol.OriginalDefinition))
                    || ConstantRecoveryHandlerProperty(candidate, "Kind") is not string kind
                    || ConstantRecoveryHandlerProperty(candidate, "SupportedCheckpointVersion") is not
                        int supported)
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                        Location(candidate.Syntax),
                        TypeKey(candidate.Symbol.ContainingType)
                            + "; Kind and SupportedCheckpointVersion must be compile-time constants."));

                    continue;
                }

                InvocationExpressionSyntax[] registrations = invocations
                    .Where(registration => registration.Model.GetSymbolInfo(registration.Call).Symbol is
                        IMethodSymbol registrationMethod
                        && registrationMethod.Name is "AddScoped" or "TryAddScoped" or "Scoped"
                        && (registrationMethod.ReducedFrom ?? registrationMethod)
                            .ContainingNamespace.ToDisplayString()
                            == "Microsoft.Extensions.DependencyInjection"
                        && registrationMethod.TypeArguments.Length == 2
                        && TypeKey(registrationMethod.TypeArguments[0]) == contract
                        && TypeKey(registrationMethod.TypeArguments[1])
                            == TypeKey(candidate.Symbol.ContainingType))
                    .Select(static registration => registration.Call)
                    .ToArray();

                if (registrations.Length == 0)
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                        Location(candidate.Syntax),
                        TypeKey(candidate.Symbol.ContainingType)
                            + "; the handler has no exact closed registration."));

                    continue;
                }

                bool ownerOnly = registrations.All(IsStoppedHostOnlyRecoveryRegistration);

                targets.Add(new(candidate, kind, supported, ownerOnly));
            }

            foreach (RecoveryDescriptor descriptor in matrix.Descriptors)
            {
                RecoveryHandlerTarget[] generic = targets
                    .Where(target => !target.OwnerOnly
                        && target.Kind == descriptor.Kind)
                    .ToArray();

                if (generic is not [RecoveryHandlerTarget exact]
                    || exact.SupportedCheckpointVersion
                        != descriptor.MaximumCheckpointVersion)
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                        Location(anchor),
                        descriptor.Kind
                            + "; the host must register exactly one generic handler whose supported maximum matches the registry."));
                }
            }

            foreach (RecoveryHandlerTarget ownerOnly in targets.Where(static target => target.OwnerOnly))
            {
                bool exactOwnerTuple = matrix.Tuples.Any(tuple =>
                    tuple.Kind == ownerOnly.Kind
                    && tuple.CheckpointVersion
                        == ownerOnly.SupportedCheckpointVersion
                    && tuple.Disposition == RecoveryDisposition.OwnerBound);

                if (!exactOwnerTuple)
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                        Location(ownerOnly.Member.Syntax),
                        TypeKey(ownerOnly.Member.Symbol.ContainingType)
                            + "; a stopped-host-only handler must name exactly an owner-bound checkpoint."));
                }
            }

            foreach (RecoveryTuple tuple in matrix.Tuples.Where(static tuple =>
                tuple.Disposition == RecoveryDisposition.OwnerBound))
            {
                RecoveryHandlerTarget[] exactOwners = targets
                    .Where(target => target.OwnerOnly
                        && target.Kind == tuple.Kind
                        && target.SupportedCheckpointVersion == tuple.CheckpointVersion)
                    .ToArray();

                if (exactOwners.Length != 1)
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                        tuple.Kind + ":" + tuple.CheckpointVersion,
                        "Each owner-bound checkpoint must have exactly one stopped-host-only handler; found "
                            + exactOwners.Length
                            + "."));
                }
            }

            return [.. targets];
        }

        private object? ConstantRecoveryHandlerProperty(
            AuthoredMember handler,
            string propertyName)
        {
            IPropertySymbol[] properties = handler.Symbol.ContainingType
                .GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .ToArray();

            if (properties is not [IPropertySymbol property]
                || property.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not
                    PropertyDeclarationSyntax declaration)
            {
                return null;
            }

            ExpressionSyntax? expression = declaration.ExpressionBody?.Expression
                ?? declaration.Initializer?.Value
                ?? declaration.AccessorList?.Accessors
                    .SingleOrDefault(static accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                    ?.ExpressionBody?.Expression
                ?? declaration.AccessorList?.Accessors
                    .SingleOrDefault(static accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                    ?.Body?.Statements
                    .OfType<ReturnStatementSyntax>()
                    .SingleOrDefault()
                    ?.Expression;

            if (expression is null
                || !semanticModels.TryGetValue(expression.SyntaxTree, out SemanticModel? model)
                || model.GetConstantValue(expression) is not
                    {
                        HasValue: true,
                    } constant)
            {
                return null;
            }

            return constant.Value;
        }

        private bool IsStoppedHostOnlyRecoveryRegistration(
            InvocationExpressionSyntax registration)
        {
            SyntaxNode? containingMember = registration.Ancestors()
                .FirstOrDefault(static ancestor => ancestor is BaseMethodDeclarationSyntax
                    or LocalFunctionStatementSyntax);

            if (containingMember is null)
            {
                return false;
            }

            return containingMember.DescendantNodes()
                .OfType<BaseObjectCreationExpressionSyntax>()
                .Any(creation => semanticModels[creation.SyntaxTree].GetTypeInfo(creation).Type is
                        { } type
                    && TypeKey(type)
                        == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler"
                    && semanticModels[creation.SyntaxTree].GetOperation(creation) is
                        IObjectCreationOperation operation
                    && operation.Arguments
                        .Where(argument => argument.Parameter?.Name is
                            "discovery"
                            or "classifiedLeaseAcquisition"
                            or "scopeFactory")
                        .Count(static argument => argument.Value.ConstantValue is
                        {
                            HasValue: true,
                            Value: null,
                        }) == 3);
        }

        private string? RecoveryEffectFrontierAt(
            AuthoredMember member,
            InvocationExpressionSyntax dispatch,
            string operationId)
        {
            TryStatementSyntax? protector = dispatch.Ancestors()
                .OfType<TryStatementSyntax>()
                .FirstOrDefault(candidate => candidate.Block.Span.Contains(dispatch.Span)
                    && candidate.Finally is not null);

            IfStatementSyntax? gate = dispatch.Ancestors()
                .OfType<IfStatementSyntax>()
                .FirstOrDefault(candidate => candidate.Else?.Statement.Span.Contains(dispatch.Span)
                    == true);

            if (protector?.Finally is not { } finalizer
                || gate?.Condition is not BinaryExpressionSyntax condition
                || !condition.IsKind(SyntaxKind.LogicalAndExpression)
                || !TryMatchRecoveryDisposition(
                    member.Model,
                    condition.Left,
                    "OrdinaryExternalEffect",
                    negated: false,
                    out _)
                || condition.Right is not PrefixUnaryExpressionSyntax refusal
                || !refusal.IsKind(SyntaxKind.LogicalNotExpression)
                || refusal.Operand is not InvocationExpressionSyntax admission
                || member.Model.GetSymbolInfo(admission).Symbol is not IMethodSymbol admissionMethod
                || Normalize(admissionMethod)
                    != "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup"
                || admission.ArgumentList.Arguments.LastOrDefault()?.Expression is not { } outExpression)
            {
                return null;
            }

            SingleVariableDesignationSyntax? designation = outExpression
                .DescendantNodesAndSelf()
                .OfType<SingleVariableDesignationSyntax>()
                .SingleOrDefault();

            ISymbol? group = designation is null
                ? member.Model.GetSymbolInfo(outExpression).Symbol
                : member.Model.GetDeclaredSymbol(designation);

            if (group is null)
            {
                return null;
            }

            AssignmentExpressionSyntax[] writes = member.Syntax.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(assignment => SymbolEqualityComparer.Default.Equals(
                    member.Model.GetSymbolInfo(assignment.Left).Symbol,
                    group))
                .ToArray();

            InvocationExpressionSyntax[] cleanups = finalizer.Block.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => AdmissionReceiver(member, call) is { } receiver
                    && SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(receiver).Symbol,
                        group)
                    && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && method.Name is "Dispose" or "DisposeAsync")
                .ToArray();

            if (writes.Length != 0
                || cleanups is not [InvocationExpressionSyntax cleanup]
                || member.Model.GetSymbolInfo(cleanup).Symbol is not IMethodSymbol cleanupMethod
                || cleanupMethod.Name == "DisposeAsync"
                    && CompletionPoint(member, cleanup, false) is null
                || cleanupMethod.Name == "Dispose"
                    && TypeKey(cleanupMethod.ReturnType) != "System.Void"
                || cleanup.Ancestors()
                    .TakeWhile(ancestor => ancestor != finalizer)
                    .OfType<IfStatementSyntax>()
                    .Any(guard => guard.Condition is not IsPatternExpressionSyntax
                    {
                        Expression: { } tested,
                        Pattern: UnaryPatternSyntax
                        {
                            Pattern: ConstantPatternSyntax
                            {
                                Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                            },
                        },
                    }
                        || !SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(tested).Symbol,
                            group))
                || AdmissionEndedBefore(
                    member,
                    Location(admission),
                    dispatch.SpanStart,
                    []))
            {
                return null;
            }

            return RootOperation(operationId)
                + "@effect:"
                + Location(admission);
        }

        private bool RuntimeRecoveryDispatchRejectsNonGenericTuples(
            IReadOnlySet<TraversalStateIdentity> active,
            SyntaxNode anchor)
        {
            AuthoredMember[] candidates = AuthoredMembers
                .Where(static member => member.Symbol.Name == "SettleDiscoveredRuntimeAsync")
                .Where(member => TypeKey(member.Symbol.ContainingType)
                    == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler")
                .ToArray();

            if (candidates is not [AuthoredMember method]
                || !active.Any(state => state.Member == MemberIdentity(method)))
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                    Location(anchor),
                    "The ordinary recovery call path must pass through the one runtime settlement entry."));

                return false;
            }

            InvocationExpressionSyntax[] settlementCalls = method.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => method.Model.GetSymbolInfo(call).Symbol is IMethodSymbol target
                    && target.Name == "SettleLeasedAsync"
                    && SymbolEqualityComparer.Default.Equals(
                        target.ContainingType,
                        method.Symbol.ContainingType))
                .ToArray();

            if (settlementCalls is not [InvocationExpressionSyntax settlement])
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                    Location(method.Syntax),
                    "Generic runtime recovery must have one exact handler-settlement continuation."));

                return false;
            }

            bool HasTerminatingClassificationGuard(string disposition) =>
                method.Syntax.DescendantNodes()
                    .OfType<IfStatementSyntax>()
                    .Any(guard => guard.Span.End < settlement.SpanStart
                        && DefinitelyTerminates(guard.Statement)
                        && IsRecoveryDispositionTest(
                            method,
                            guard.Condition,
                            disposition));

            bool ownerRejected = HasTerminatingClassificationGuard(
                "OwnerBoundAwaitingExactOwner");

            bool unsupportedRejected = HasTerminatingClassificationGuard(
                "UnsupportedCheckpointVersion");

            bool supported = ownerRejected && unsupportedRejected;

            if (!supported)
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                    Location(method.Syntax),
                    "Generic runtime recovery must terminate owner-bound and unsupported classifications before handler dispatch."));
            }

            return supported;
        }

        private bool ReadinessRecoveryDispatchRejectsNonGenericTuples(
            IReadOnlySet<TraversalStateIdentity> active,
            SyntaxNode anchor)
        {
            AuthoredMember[] methods = AuthoredMembers
                .Where(static member => member.Symbol.Name == "ReconcileAsync")
                .Where(member => TypeKey(member.Symbol.ContainingType)
                    == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler")
                .ToArray();

            if (methods is not [AuthoredMember method]
                || !active.Any(state => state.Member == MemberIdentity(method)))
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                    Location(anchor),
                    "The readiness recovery call path must pass through the one bounded reconciliation entry."));

                return false;
            }

            AuthoredMember[] settlers = AuthoredMembers
                .Where(static member => member.Symbol.Name == "SettleAsync")
                .Where(member => member.Syntax.Ancestors().Contains(method.Syntax))
                .ToArray();

            if (settlers is not [AuthoredMember settle]
                || !active.Any(state => state.Member == MemberIdentity(settle)))
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                    Location(anchor),
                    "Readiness reconciliation must enter its one closed per-operation settlement function."));

                return false;
            }

            InvocationExpressionSyntax[] handlerSettlements = settle.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => settle.Model.GetSymbolInfo(call).Symbol is IMethodSymbol target
                    && target.Name == "SettleLeasedAsync"
                    && SymbolEqualityComparer.Default.Equals(
                        target.ContainingType,
                        method.Symbol.ContainingType))
                .ToArray();

            if (handlerSettlements is not [InvocationExpressionSyntax handlerSettlement])
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                    Location(settle.Syntax),
                    "Readiness reconciliation must have one exact handler-settlement continuation."));

                return false;
            }

            foreach (LocalDeclarationStatementSyntax declaration in settle.Syntax
                .DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>())
            {
                if (declaration.Declaration.Variables is not
                    [VariableDeclaratorSyntax
                    {
                        Initializer.Value: InvocationExpressionSyntax classification,
                    } variable]
                    || settle.Model.GetDeclaredSymbol(variable) is not ILocalSymbol decision
                    || settle.Model.GetSymbolInfo(classification).Symbol is not IMethodSymbol
                    {
                        Name: "Classify",
                    } classifier
                    || TypeKey(classifier.ContainingType)
                        != "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationRecoveryAdmission")
                {
                    continue;
                }

                bool MatchesDecision(
                    ExpressionSyntax expression,
                    string disposition,
                    bool negated) =>
                    IsRecoveryDispositionTest(
                        settle,
                        expression,
                        disposition,
                        negated);

                bool ownerRejected = settle.Syntax.DescendantNodes()
                    .OfType<IfStatementSyntax>()
                    .Any(guard => guard.SpanStart > declaration.Span.End
                        && guard.Span.End < handlerSettlement.SpanStart
                        && DefinitelyTerminates(guard.Statement)
                        && MatchesDecision(
                            guard.Condition,
                            "OwnerBoundAwaitingExactOwner",
                            negated: false));

                bool unsupportedRejected = handlerSettlement.Ancestors()
                    .OfType<ConditionalExpressionSyntax>()
                    .Any(conditional => conditional.WhenFalse.Span.Contains(handlerSettlement.Span)
                        && MatchesDecision(
                            conditional.Condition,
                            "UnsupportedCheckpointVersion",
                            negated: false));

                if (ownerRejected && unsupportedRejected)
                {
                    return true;
                }
            }

            diagnostics.Add(new(
                "HOSTED_RECOVERY_DISPATCH_UNPROVEN",
                Location(settle.Syntax),
                "Readiness reconciliation must reject owner-bound and unsupported classifications before handler dispatch."));

            return false;
        }

        private TraversalEvidence TraverseRecoveryDispatch(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            IMethodSymbol contractMethod,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? workAdmitted,
            string? recoveryEffect)
        {
            RecoveryMatrix matrix = ResolveRecoveryMatrix(call);

            string rootOperation = RootOperation(operationId);

            HostedProducerOperationEntry[] roots = declaredOperations
                .Where(operation => operation.OperationId == rootOperation)
                .ToArray();

            if (!matrix.IsValid)
            {
                return TraversalEvidence.Inconclusive;
            }

            if (roots is not [HostedProducerOperationEntry root])
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_AUTHORITY_UNPROVEN",
                    operationId,
                    "Recovery-handler dispatch must originate from one declared authority root."));

                return TraversalEvidence.Inconclusive;
            }

            RecoveryHandlerTarget[] targets = ResolveRecoveryHandlerTargets(
                contractMethod,
                call,
                matrix);

            if (OwnerRecoveryDispatchIsExact(active, out string exactnessFailure))
            {
                if (root.Authority != HostedProducerAuthorityKind.OwnerBoundRecovery)
                {
                    // SettleExactlyAsync is the authority handoff. The canonical owner-recovery
                    // root below traverses every owner-only handler and closes the matrix over every
                    // tuple; re-attributing those same handlers to each bootstrap caller both gives
                    // their effects the wrong owner and multiplies the graph until its safety cap.
                    return TraversalEvidence.Evidence;
                }

                return TraverseOwnerRecoveryDispatch(
                    caller,
                    call,
                    rootType,
                    operationId,
                    active,
                    lifecycle,
                    matrix,
                    targets);
            }

            if (root.Authority == HostedProducerAuthorityKind.OwnerBoundRecovery)
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_OWNER_DISPATCH_UNPROVEN",
                    operationId,
                    "Exact-owner recovery must reach the shared handler only through SettleExactlyAsync after owner evidence classifies the operation as OwnerBoundOffline; "
                        + exactnessFailure
                        + "."));

                return TraversalEvidence.Inconclusive;
            }

            if (root.Authority is not HostedProducerAuthorityKind.OrdinaryHostedWork
                and not HostedProducerAuthorityKind.PreReadinessStartup)
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_AUTHORITY_UNPROVEN",
                    operationId,
                    "Generic recovery-handler dispatch requires readiness or ordinary-work authority; owner-only dispatch requires authenticated owner evidence."));

                return TraversalEvidence.Inconclusive;
            }

            bool genericDispatchProven = root.Authority switch
            {
                HostedProducerAuthorityKind.OrdinaryHostedWork =>
                    RuntimeRecoveryDispatchRejectsNonGenericTuples(active, call),
                HostedProducerAuthorityKind.PreReadinessStartup =>
                    ReadinessRecoveryDispatchRejectsNonGenericTuples(active, call),
                _ => true,
            };

            if (!genericDispatchProven)
            {
                return TraversalEvidence.Inconclusive;
            }

            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (RecoveryHandlerTarget target in targets
                .Where(static target => !target.OwnerOnly))
            {
                RecoveryDescriptor? descriptor = matrix.Descriptors
                    .SingleOrDefault(candidate => candidate.Kind == target.Kind);

                if (descriptor is null)
                {
                    continue;
                }

                foreach (RecoveryTuple tuple in matrix.Tuples.Where(tuple =>
                    tuple.Kind == target.Kind
                    && tuple.CheckpointVersion
                        <= target.SupportedCheckpointVersion
                    && tuple.Disposition is RecoveryDisposition.OrdinaryDbOnly
                        or RecoveryDisposition.OrdinaryExternalEffect))
                {
                    IParameterSymbol? operation = target.Member.Symbol.Parameters
                        .SingleOrDefault(parameter => TypeKey(parameter.Type)
                            == "RetroDownfall.Arcanum.Core.Operations.LongRunningOperation");

                    if (operation is null)
                    {
                        diagnostics.Add(new(
                            "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                            Location(target.Member.Syntax),
                            TypeKey(target.Member.Symbol.ContainingType)
                                + "; RecoverAsync must receive one exact durable operation."));

                        continue;
                    }

                    if (root.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork
                        && tuple.Disposition == RecoveryDisposition.OrdinaryExternalEffect
                        && recoveryEffect is null)
                    {
                        diagnostics.Add(new(
                            "HOSTED_RECOVERY_EFFECT_FRONTIER_UNPROVEN",
                            operationId + "/recovery@" + tuple.Kind + ":" + tuple.CheckpointVersion,
                            "Ordinary external-effect recovery must retain the conditional effect frontier through its exact handler."));
                    }

                    AuthoredMember bound = BindAdmissionArguments(
                        caller,
                        call,
                        target.Member);

                    Dictionary<ISymbol, RecoveryTuple> bindings = bound.RecoveryBindings is null
                        ? new(SymbolEqualityComparer.Default)
                        : new(bound.RecoveryBindings, SymbolEqualityComparer.Default);

                    bindings[operation] = tuple;

                    bound = bound with
                    {
                        RecoveryBindings = bindings,
                        RecoveryContext = tuple,
                    };

                    TraversalEvidence targetEvidence = Traverse(
                        bound,
                        rootType,
                        operationId
                            + "/recovery@"
                            + Uri.EscapeDataString(tuple.Kind)
                            + ":"
                            + tuple.CheckpointVersion
                            + "/call@"
                            + Location(call),
                        active,
                        lifecycle,
                        workAdmitted,
                        root.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork
                            && tuple.Disposition == RecoveryDisposition.OrdinaryExternalEffect
                            ? recoveryEffect
                            : null,
                        completionOwned: true,
                        recoveryEffect: recoveryEffect);

                    evidence = MergeEvidence(evidence, targetEvidence);
                }
            }

            return evidence;
        }

        private bool OwnerRecoveryDispatchIsExact(
            IReadOnlySet<TraversalStateIdentity> active,
            out string failure)
        {
            AuthoredMember[] candidates = AuthoredMembers
                .Where(static member => member.Symbol.Name == "SettleExactlyAsync")
                .Where(member => TypeKey(member.Symbol.ContainingType)
                    == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler")
                .Where(static member => member.Symbol.Parameters.Any(parameter =>
                    TypeKey(parameter.Type)
                        == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningRecoveryOwnerEvidence"))
                .ToArray();

            if (candidates is not [AuthoredMember method]
                || !active.Any(state => state.Member == MemberIdentity(method)))
            {
                failure = candidates.Length == 1
                    ? "the authenticated settlement entry is not present in the active call path"
                    : $"the production graph contains {candidates.Length} authenticated settlement entries";

                return false;
            }

            InvocationExpressionSyntax[] settlements = method.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => method.Model.GetSymbolInfo(call).Symbol is IMethodSymbol target
                    && target.Name == "SettleLeasedAsync"
                    && SymbolEqualityComparer.Default.Equals(
                        target.ContainingType,
                        method.Symbol.ContainingType))
                .ToArray();

            if (settlements is not [InvocationExpressionSyntax settlement])
            {
                failure = $"the authenticated settlement entry contains {settlements.Length} shared settlements";

                return false;
            }

            IParameterSymbol? ownerEvidence = method.Symbol.Parameters
                .SingleOrDefault(parameter => TypeKey(parameter.Type)
                    == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningRecoveryOwnerEvidence");

            if (ownerEvidence is null)
            {
                failure = "the authenticated settlement entry has no exact owner-evidence parameter";

                return false;
            }

            foreach (LocalDeclarationStatementSyntax declaration in method.Syntax
                .DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>())
            {
                if (declaration.Declaration.Variables is not
                    [VariableDeclaratorSyntax
                    {
                        Initializer.Value: InvocationExpressionSyntax classification,
                    } variable]
                    || method.Model.GetDeclaredSymbol(variable) is not ILocalSymbol
                    || method.Model.GetSymbolInfo(classification).Symbol is not IMethodSymbol
                    {
                        Name: "Classify",
                    } classifier
                    || TypeKey(classifier.ContainingType)
                        != "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationRecoveryAdmission"
                    || AdmissionArguments(method, classification)
                        .Select(static argument => argument.Expression)
                        .Skip(1)
                        .SingleOrDefault() is not { } evidenceExpression
                    || !SymbolEqualityComparer.Default.Equals(
                        method.Model.GetSymbolInfo(evidenceExpression).Symbol,
                        ownerEvidence))
                {
                    continue;
                }

                bool IsOwnerRefusal(ExpressionSyntax expression)
                    => IsRecoveryDispositionTest(
                        method,
                        expression,
                        "OwnerBoundOffline",
                        negated: true);

                if (method.Syntax.DescendantNodes()
                    .OfType<IfStatementSyntax>()
                    .Any(guard => guard.SpanStart > declaration.Span.End
                        && guard.Span.End < settlement.SpanStart
                        && DefinitelyTerminates(guard.Statement)
                        && IsOwnerRefusal(guard.Condition)))
                {
                    failure = string.Empty;

                    return true;
                }
            }

            failure = "the authenticated settlement entry does not terminate every non-owner classification before shared dispatch";

            return false;
        }

        private TraversalEvidence TraverseOwnerRecoveryDispatch(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            RecoveryMatrix matrix,
            IReadOnlyList<RecoveryHandlerTarget> targets)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (RecoveryTuple tuple in matrix.Tuples.Where(static tuple =>
                tuple.Disposition == RecoveryDisposition.OwnerBound))
            {
                RecoveryHandlerTarget[] exactTargets = targets
                    .Where(target => target.OwnerOnly
                        && target.Kind == tuple.Kind
                        && target.SupportedCheckpointVersion == tuple.CheckpointVersion)
                    .ToArray();

                if (exactTargets is not [RecoveryHandlerTarget target])
                {
                    continue;
                }

                IParameterSymbol? operation = target.Member.Symbol.Parameters
                    .SingleOrDefault(parameter => TypeKey(parameter.Type)
                        == "RetroDownfall.Arcanum.Core.Operations.LongRunningOperation");

                if (operation is null)
                {
                    diagnostics.Add(new(
                        "HOSTED_RECOVERY_HANDLER_UNPROVEN",
                        Location(target.Member.Syntax),
                        TypeKey(target.Member.Symbol.ContainingType)
                            + "; RecoverAsync must receive one exact durable operation."));

                    continue;
                }

                AuthoredMember bound = BindAdmissionArguments(
                    caller,
                    call,
                    target.Member);

                Dictionary<ISymbol, RecoveryTuple> bindings = bound.RecoveryBindings is null
                    ? new(SymbolEqualityComparer.Default)
                    : new(bound.RecoveryBindings, SymbolEqualityComparer.Default);

                bindings[operation] = tuple;

                bound = bound with
                {
                    RecoveryBindings = bindings,
                    RecoveryContext = tuple,
                };

                string tupleOperation = operationId
                    + "/recovery@"
                    + Uri.EscapeDataString(tuple.Kind)
                    + ":"
                    + tuple.CheckpointVersion
                    + "/call@"
                    + Location(call);

                ownerRecoveryAssociations.Add((
                    RootOperation(operationId),
                    tuple.Kind,
                    tuple.CheckpointVersion));

                evidence = MergeEvidence(
                    evidence,
                    Traverse(
                        bound,
                        rootType,
                        tupleOperation,
                        active,
                        lifecycle,
                        completionOwned: true));
            }

            return evidence;
        }

        private bool RecoveryNodeMayExecute(AuthoredMember member, SyntaxNode node)
        {
            bool hasRecoveryBindings = member.RecoveryBindings is { Count: > 0 };

            bool hasValueBindings = member.ValueBindings is { Count: > 0 };

            if (!hasRecoveryBindings && !hasValueBindings)
            {
                return true;
            }

            bool? Boolean(ExpressionSyntax expression)
            {
                if (!hasRecoveryBindings
                    && !expression.DescendantNodesAndSelf()
                        .OfType<IdentifierNameSyntax>()
                        .Any(reference => member.Model.GetSymbolInfo(reference).Symbol is { } symbol
                            && member.ValueBindings!.ContainsKey(symbol)))
                {
                    return null;
                }

                return RecoveryValue(member, expression, []).Value is bool value
                    ? value
                    : null;
            }

            foreach (SyntaxNode ancestor in node.Ancestors()
                .TakeWhile(ancestor => ancestor != member.Syntax))
            {
                if (ancestor is IfStatementSyntax conditional)
                {
                    bool? value = Boolean(conditional.Condition);

                    if (value == true && conditional.Else?.Statement.Span.Contains(node.Span) == true
                        || value == false && conditional.Statement.Span.Contains(node.Span))
                    {
                        return false;
                    }
                }
                else if (ancestor is ConditionalExpressionSyntax choice)
                {
                    bool? value = Boolean(choice.Condition);

                    if (value == true && choice.WhenFalse.Span.Contains(node.Span)
                        || value == false && choice.WhenTrue.Span.Contains(node.Span))
                    {
                        return false;
                    }
                }
                else if (ancestor is SwitchExpressionArmSyntax arm
                    && arm.Parent is SwitchExpressionSyntax selection
                    && RecoveryValue(member, selection.GoverningExpression, []) is
                    {
                        IsKnown: true,
                    } governing
                    && !RecoveryPatternMatches(member, arm.Pattern, governing.Value))
                {
                    return false;
                }
                else if (ancestor is BinaryExpressionSyntax binary
                    && binary.Right.Span.Contains(node.Span)
                    && Boolean(binary.Left) is { } left
                    && (binary.IsKind(SyntaxKind.LogicalAndExpression) && !left
                        || binary.IsKind(SyntaxKind.LogicalOrExpression) && left))
                {
                    return false;
                }
            }

            foreach (BlockSyntax block in node.Ancestors()
                .OfType<BlockSyntax>()
                .TakeWhile(block => block != member.Syntax))
            {
                StatementSyntax? path = block.Statements
                    .FirstOrDefault(statement => statement.Span.Contains(node.Span));

                if (path is null)
                {
                    continue;
                }

                foreach (IfStatementSyntax guard in block.Statements
                    .TakeWhile(statement => statement != path)
                    .OfType<IfStatementSyntax>())
                {
                    bool? value = Boolean(guard.Condition);

                    if (value == true && DefinitelyTerminates(guard.Statement)
                        || value == false
                            && guard.Else is { } alternate
                            && DefinitelyTerminates(alternate.Statement))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private KnownValue RecoveryValue(
            AuthoredMember member,
            ExpressionSyntax expression,
            HashSet<GraphMemberIdentity> path)
        {
            expression = expression switch
            {
                ParenthesizedExpressionSyntax parentheses => parentheses.Expression,
                CastExpressionSyntax cast => cast.Expression,
                PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                    suppression.Operand,
                _ => expression,
            };

            Optional<object?> constant = member.Model.GetConstantValue(expression);

            if (constant.HasValue)
            {
                return new(true, constant.Value);
            }

            if (member.Model.GetSymbolInfo(expression).Symbol is IFieldSymbol
                {
                    HasConstantValue: true,
                } constantField)
            {
                return new(true, constantField.ConstantValue);
            }

            if (expression is PrefixUnaryExpressionSyntax negation
                && negation.IsKind(SyntaxKind.LogicalNotExpression)
                && RecoveryValue(member, negation.Operand, path) is
                {
                    IsKnown: true,
                    Value: bool operand,
                })
            {
                return new(true, !operand);
            }

            if (expression is BinaryExpressionSyntax binary)
            {
                KnownValue left = RecoveryValue(member, binary.Left, path);

                if (binary.IsKind(SyntaxKind.LogicalAndExpression)
                    && left is { IsKnown: true, Value: false }
                    || binary.IsKind(SyntaxKind.LogicalOrExpression)
                    && left is { IsKnown: true, Value: true })
                {
                    return left;
                }

                KnownValue right = RecoveryValue(member, binary.Right, path);

                if (binary.IsKind(SyntaxKind.LogicalAndExpression)
                    && right is { IsKnown: true, Value: false }
                    || binary.IsKind(SyntaxKind.LogicalOrExpression)
                    && right is { IsKnown: true, Value: true })
                {
                    return right;
                }

                if (left.IsKnown && right.IsKnown)
                {
                    if (binary.IsKind(SyntaxKind.LogicalAndExpression)
                        && left.Value is bool leftAnd
                        && right.Value is bool rightAnd)
                    {
                        return new(true, leftAnd && rightAnd);
                    }

                    if (binary.IsKind(SyntaxKind.LogicalOrExpression)
                        && left.Value is bool leftOr
                        && right.Value is bool rightOr)
                    {
                        return new(true, leftOr || rightOr);
                    }

                    if (binary.IsKind(SyntaxKind.EqualsExpression)
                        || binary.IsKind(SyntaxKind.NotEqualsExpression))
                    {
                        bool equal = Equals(left.Value, right.Value);

                        return new(
                            true,
                            binary.IsKind(SyntaxKind.EqualsExpression)
                                ? equal
                                : !equal);
                    }
                }
            }

            if (expression is IsPatternExpressionSyntax pattern
                && RecoveryValue(member, pattern.Expression, path) is
                {
                    IsKnown: true,
                } input)
            {
                return new(
                    true,
                    RecoveryPatternMatches(member, pattern.Pattern, input.Value));
            }

            if (expression is ConditionalExpressionSyntax conditional
                && RecoveryValue(member, conditional.Condition, path) is
                {
                    IsKnown: true,
                    Value: bool condition,
                })
            {
                return RecoveryValue(
                    member,
                    condition ? conditional.WhenTrue : conditional.WhenFalse,
                    path);
            }

            if (expression is SwitchExpressionSyntax selection
                && RecoveryValue(member, selection.GoverningExpression, path) is
                {
                    IsKnown: true,
                } governing)
            {
                SwitchExpressionArmSyntax[] matching = selection.Arms
                    .Where(arm => RecoveryPatternMatches(member, arm.Pattern, governing.Value)
                        && (arm.WhenClause is null
                            || RecoveryValue(member, arm.WhenClause.Condition, path) is
                            {
                                IsKnown: true,
                                Value: true,
                            }))
                    .ToArray();

                return matching is [SwitchExpressionArmSyntax exact]
                    ? RecoveryValue(member, exact.Expression, path)
                    : default;
            }

            if (expression is MemberAccessExpressionSyntax access
                && BoundRecoveryTuple(member, access.Expression, []) is { } recovery)
            {
                return access.Name.Identifier.ValueText switch
                {
                    "Kind" => new(true, recovery.Kind),
                    "CheckpointVersion" => new(true, recovery.CheckpointVersion),
                    _ => default,
                };
            }

            if (expression is InvocationExpressionSyntax call
                && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method)
            {
                if (method.Name == "Equals"
                    && TypeKey(method.ContainingType) == "System.String")
                {
                    ExpressionSyntax[] values = AdmissionArguments(member, call)
                        .Select(static argument => argument.Expression)
                        .Take(2)
                        .ToArray();

                    if (values is [ExpressionSyntax first, ExpressionSyntax second])
                    {
                        KnownValue left = RecoveryValue(member, first, path);
                        KnownValue right = RecoveryValue(member, second, path);

                        if (left.IsKnown && right.IsKnown)
                        {
                            return new(true, Equals(left.Value, right.Value));
                        }
                    }
                }

                if (ResolveInvocationTarget(method, member, call) is { } target)
                {
                    AuthoredMember bound = BindAdmissionArguments(member, call, target);

                    GraphMemberIdentity identity = MemberIdentity(bound);

                    if (!path.Add(identity))
                    {
                        return default;
                    }

                    try
                    {
                        ExpressionSyntax[] returns = bound.Syntax switch
                        {
                            MethodDeclarationSyntax { ExpressionBody.Expression: { } result } =>
                                [result],
                            MethodDeclarationSyntax { Body: { } body } =>
                                [.. body.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax
                                        and not LocalFunctionStatementSyntax)
                                    .OfType<ReturnStatementSyntax>()
                                    .Select(static statement => statement.Expression)
                                    .OfType<ExpressionSyntax>()],
                            LocalFunctionStatementSyntax { ExpressionBody.Expression: { } result } =>
                                [result],
                            LocalFunctionStatementSyntax { Body: { } body } =>
                                [.. body.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax
                                        and not LocalFunctionStatementSyntax)
                                    .OfType<ReturnStatementSyntax>()
                                    .Select(static statement => statement.Expression)
                                    .OfType<ExpressionSyntax>()],
                            _ => [],
                        };

                        KnownValue[] returned = returns
                            .Where(result => RecoveryNodeMayExecute(bound, result))
                            .Select(result => RecoveryValue(bound, result, path))
                            .ToArray();

                        if (returned.Length != 0
                            && returned.All(static result => result.IsKnown)
                            && returned.Select(static result => result.Value)
                                .Distinct()
                                .Count() == 1)
                        {
                            return returned[0];
                        }
                    }
                    finally
                    {
                        path.Remove(identity);
                    }
                }
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            if (symbol is IParameterSymbol parameter
                && member.ValueBindings?.TryGetValue(
                    parameter,
                    out BoundValueSource? binding) == true
                && binding is not null
                && !path.Contains(MemberIdentity(binding.Caller)))
            {
                return RecoveryValue(
                    binding.Caller,
                    binding.Expression,
                    path);
            }

            if (symbol is IPropertySymbol property
                && SymbolEqualityComparer.Default.Equals(
                    property.ContainingType,
                    member.Symbol.ContainingType)
                && ConstantRecoveryHandlerProperty(member, property.Name) is { } propertyValue)
            {
                return new(true, propertyValue);
            }

            if (symbol is ILocalSymbol local
                && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is
                    VariableDeclaratorSyntax { Initializer.Value: { } initializer })
            {
                var localKey = (MemberIdentity(member), initializer.SpanStart);

                if (!activeRecoveryLocals.Add(localKey))
                {
                    return default;
                }

                try
                {
                    List<ExpressionSyntax> values = [initializer];

                    if (initializer.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is { } block)
                    {
                        values.AddRange(block.DescendantNodes()
                            .OfType<AssignmentExpressionSyntax>()
                            .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                                && assignment.SpanStart < expression.SpanStart
                                && RecoveryNodeMayExecute(member, assignment)
                                && SymbolEqualityComparer.Default.Equals(
                                    member.Model.GetSymbolInfo(assignment.Left).Symbol,
                                    local))
                            .Select(static assignment => assignment.Right));
                    }

                    KnownValue[] candidates = values
                        .Select(value => RecoveryValue(member, value, path))
                        .ToArray();

                    return candidates.Length != 0
                        && candidates.All(static candidate => candidate.IsKnown)
                        && candidates.Select(static candidate => candidate.Value)
                            .Distinct()
                            .Count() == 1
                        ? candidates[0]
                        : default;
                }
                finally
                {
                    _ = activeRecoveryLocals.Remove(localKey);
                }
            }

            return default;
        }

        private bool RecoveryPatternMatches(
            AuthoredMember member,
            PatternSyntax pattern,
            object? value) =>
            pattern switch
            {
                DiscardPatternSyntax => true,
                VarPatternSyntax => true,
                ConstantPatternSyntax constant
                    when RecoveryValue(member, constant.Expression, []) is
                    {
                        IsKnown: true,
                    } expected =>
                    Equals(value, expected.Value),
                UnaryPatternSyntax unary
                    when unary.IsKind(SyntaxKind.NotPattern) =>
                    !RecoveryPatternMatches(member, unary.Pattern, value),
                BinaryPatternSyntax binary
                    when binary.IsKind(SyntaxKind.OrPattern) =>
                    RecoveryPatternMatches(member, binary.Left, value)
                    || RecoveryPatternMatches(member, binary.Right, value),
                BinaryPatternSyntax binary
                    when binary.IsKind(SyntaxKind.AndPattern) =>
                    RecoveryPatternMatches(member, binary.Left, value)
                    && RecoveryPatternMatches(member, binary.Right, value),
                RecursivePatternSyntax => value is not null,
                _ => false,
            };

        private static bool DefinitelyTerminates(StatementSyntax statement) =>
            statement switch
            {
                ReturnStatementSyntax or ThrowStatementSyntax => true,
                BlockSyntax block when block.Statements.LastOrDefault() is { } last =>
                    DefinitelyTerminates(last),
                _ => false,
            };

        private static bool IsRecoveryDispositionTest(
            AuthoredMember member,
            ExpressionSyntax expression,
            string disposition,
            bool negated = false)
        {
            if (expression is ParenthesizedExpressionSyntax parentheses)
            {
                return IsRecoveryDispositionTest(
                    member,
                    parentheses.Expression,
                    disposition,
                    negated);
            }

            if (expression is BinaryExpressionSyntax disjunction
                && disjunction.IsKind(SyntaxKind.LogicalOrExpression))
            {
                return IsRecoveryDispositionTest(
                        member,
                        disjunction.Left,
                        disposition,
                        negated)
                    || IsRecoveryDispositionTest(
                        member,
                        disjunction.Right,
                        disposition,
                        negated);
            }

            if (!TryMatchRecoveryDisposition(
                    member.Model,
                    expression,
                    disposition,
                    negated,
                    out ExpressionSyntax receiver)
                || member.Model.GetSymbolInfo(receiver).Symbol is not ILocalSymbol decision
                || decision.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not
                    VariableDeclaratorSyntax
                    {
                        Initializer.Value: InvocationExpressionSyntax classification,
                    } declaration
                || member.Model.GetSymbolInfo(classification).Symbol is not IMethodSymbol classifier
                || classifier.Name != "Classify"
                || TypeKey(classifier.ContainingType)
                    != "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationRecoveryAdmission"
                || member.Syntax.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Any(assignment => assignment.SpanStart > declaration.Span.End
                        && assignment.Span.End < expression.SpanStart
                        && SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(assignment.Left).Symbol,
                            decision)))
            {
                return false;
            }

            return true;
        }

        private static bool TryMatchRecoveryDisposition(
            SemanticModel model,
            ExpressionSyntax expression,
            string disposition,
            bool negated,
            out ExpressionSyntax receiver)
        {
            receiver = null!;

            ExpressionSyntax? expected = null;

            if (!negated
                && expression is BinaryExpressionSyntax classic
                && classic.IsKind(SyntaxKind.IsExpression)
                && classic.Left is MemberAccessExpressionSyntax
                {
                    Expression: { } classicReceiver,
                    Name.Identifier.ValueText: "Kind",
                })
            {
                receiver = classicReceiver;
                expected = classic.Right;
            }
            else if (expression is IsPatternExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Expression: { } patternReceiver,
                        Name.Identifier.ValueText: "Kind",
                    },
                    Pattern: { } candidate,
                })
            {
                PatternSyntax? pattern = negated
                    ? candidate is UnaryPatternSyntax unary
                        && unary.IsKind(SyntaxKind.NotPattern)
                            ? unary.Pattern
                            : null
                    : candidate;

                if (pattern is ConstantPatternSyntax { Expression: { } constant })
                {
                    receiver = patternReceiver;
                    expected = constant;
                }
            }

            return expected is not null
                && model.GetSymbolInfo(expected).Symbol is IFieldSymbol field
                && field.Name == disposition;
        }

        private bool HasAuthoredTarget(IMethodSymbol method) => members.TryGetValue(MethodKey(method), out List<AuthoredMember>? group) && group.Any(static candidate => candidate.Syntax is not MethodDeclarationSyntax { Body: null, ExpressionBody: null });

        private TraversalEvidence Traverse(AuthoredMember member, string rootType, string operationId, HashSet<TraversalStateIdentity> active, bool lifecycle, string? inheritedWork = null, string? inheritedEffect = null, SyntaxNode? selection = null, IReadOnlySet<int>? fieldPublications = null, bool completionOwned = false, string? recoveryEffect = null)
        {
            GraphMemberIdentity visitKey = MemberIdentity(member);

            TraversalStateIdentity state = TraversalState(member, rootType, operationId, lifecycle, inheritedWork, inheritedEffect, recoveryEffect, selection, fieldPublications, completionOwned);

            if (analyzedStates.TryGetValue(state, out TraversalEvidence cachedEvidence))
            {
                return cachedEvidence;
            }

            string root = rootType + "|" + state.RootOperation;

            int stateCount = analyzedStateCounts.GetValueOrDefault(root);

            if (stateCount >= MaximumAnalyzedStatesPerRoot)
            {
                if (limitedRoots.Add(root))
                {
                    diagnostics.Add(new("HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED", state.RootOperation, $"The exact semantic graph exceeded {MaximumAnalyzedStatesPerRoot} states for one authority root."));
                }

                return TraversalEvidence.Inconclusive;
            }

            if (!active.Add(state))
            {
                return TraversalEvidence.Inconclusive;
            }

            analyzedStateCounts[root] = stateCount + 1;

            TraversalEvidence emittedEvidence = TraversalEvidence.NoEvidence;

            try
            {
                if (lifecycle)
                {
                    lifecycleMembers.Add(visitKey);
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
                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(HostedProducerSiteKind.EffectFrontier, Normalize(gate), member, rootType, operationId, admission));
                        }
                    }
                }

                foreach (SyntaxNode node in (selection ?? member.Syntax).DescendantNodesAndSelf(node => node == (selection ?? member.Syntax) || node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax))
                {
                    if (!RecoveryNodeMayExecute(member, node))
                    {
                        continue;
                    }

                    if (selection is null && operationId == TypeKey(member.Symbol.ContainingType) + "." + member.Symbol.Name && selectedRoots.TryGetValue(visitKey, out List<SyntaxNode>? selections) && selections.Any(selected => selected.Span.Contains(node.Span)))
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

                        bool asynchronousCallback = callee is
                            "System.Threading.Tasks.Task.Run"
                                or "System.Threading.Tasks.TaskFactory.StartNew"
                                or "System.Threading.Tasks.Task.ContinueWith"
                                or "System.Threading.Tasks.Task`1.ContinueWith"
                                or "System.Threading.Tasks.Parallel.ForEachAsync";

                        bool lazyCallback = IsKnownLazyCallback(method);

                        bool synchronousCallback = method.MethodKind == MethodKind.DelegateInvoke
                            || IsKnownSynchronousCallback(method)
                                && method.Parameters.Any(static parameter =>
                                    parameter.Type.TypeKind == TypeKind.Delegate);

                        bool knownCallback = asynchronousCallback || synchronousCallback || lazyCallback;

                        bool ownedTransparentCallbackCarrier =
                            TransparentProducerBoundaries.Contains(callee)
                            && node is InvocationExpressionSyntax transparentCall
                            && HasOwnedLongLivedReturn(member, transparentCall);

                        if (callee == "System.ArgumentNullException.ThrowIfNull")
                        {
                            continue;
                        }

                        if (CompletionOwnedExternalAwaitables.Contains(callee))
                        {
                            if (node is not InvocationExpressionSyntax ownedAwaitable
                                || CompletionPoint(
                                    member,
                                    ownedAwaitable,
                                    completionOwned) is null)
                            {
                                diagnostics.Add(new(
                                    "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                                    RootOperation(operationId)
                                        + "/call@"
                                        + Location(node),
                                    callee
                                        + "; Timer-backed asynchronous work must complete within its caller's retained lifetime."));
                            }

                            continue;
                        }

                        if (callee == "System.Lazy`1..ctor")
                        {
                            continue;
                        }

                        if (node is InvocationExpressionSyntax recoveryTerminal
                            && IsReviewedEffectFreeRecoveryTerminal(
                                member,
                                recoveryTerminal,
                                method))
                        {
                            emittedEvidence = MergeEvidence(
                                emittedEvidence,
                                TraversalEvidence.Evidence);

                            continue;
                        }

                        if (node is InvocationExpressionSyntax testSeamCall
                            && IsReviewedDormantTestSeam(method))
                        {
                            if (IsAwaitable(method.ReturnType)
                                && CompletionPoint(
                                    member,
                                    testSeamCall,
                                    completionOwned) is null)
                            {
                                diagnostics.Add(new(
                                    "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                                    RootOperation(operationId)
                                        + "/call@"
                                        + Location(node),
                                    callee
                                        + "; The dormant test checkpoint must still be completion-owned by its caller."));
                            }

                            continue;
                        }

                        if (DetachedExternalCallbackMethods.Contains(callee)
                            && node is InvocationExpressionSyntax detachedCarrier)
                        {
                            ExpressionSyntax[] detachedCallbacks = detachedCarrier
                                .ArgumentList
                                .Arguments
                                .Where(argument =>
                                    !argument.RefKindKeyword.IsKind(
                                        SyntaxKind.OutKeyword)
                                    && IsDelegateExpression(
                                        member.Model,
                                        argument.Expression))
                                .Select(static argument => argument.Expression)
                                .ToArray();

                            emittedEvidence = MergeEvidence(
                                emittedEvidence,
                                TraverseDetachedExternalCallbacks(
                                    member,
                                    detachedCallbacks,
                                    callee,
                                    node,
                                    rootType,
                                    operationId,
                                    active,
                                    lifecycle));

                            continue;
                        }

                        ArgumentSyntax[] opaqueCallbackArguments = node switch
                        {
                            InvocationExpressionSyntax opaqueCall =>
                                opaqueCall.ArgumentList.Arguments.ToArray(),
                            BaseObjectCreationExpressionSyntax opaqueCreation
                                when opaqueCreation.ArgumentList is not null =>
                                opaqueCreation.ArgumentList.Arguments.ToArray(),
                            _ => [],
                        };

                        if (!knownCallback
                            && !ownedTransparentCallbackCarrier
                            && Resolve(method, member.Model.Compilation) is null
                            && method.Locations.All(static location =>
                                !location.IsInSource)
                            && opaqueCallbackArguments.Any(argument =>
                                !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                                && IsDelegateExpression(
                                    member.Model,
                                    argument.Expression)))
                        {
                            ExpressionSyntax[] opaqueCallbacks = opaqueCallbackArguments
                                .Where(argument =>
                                    !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                                    && IsDelegateExpression(
                                        member.Model,
                                        argument.Expression))
                                .Select(static argument => argument.Expression)
                                .ToArray();

                            if (opaqueCallbacks.Any(callback => ResolveCallable(callback, member.Model, member.CallableBindings, member.AbsentCallables) is not { } body || HasAdmissionSeed(body) || ContainsPotentialProducerSite(body)))
                            {
                                diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", RootOperation(operationId) + "/callback@" + Location(node), callee + "; Opaque callback semantics cannot prove execution or completion ownership."));
                            }
                        }

                        if (node is InvocationExpressionSyntax callbackCall && knownCallback)
                        {
                            ExpressionSyntax[] callbacks = method.MethodKind == MethodKind.DelegateInvoke
                                ? [DelegateReceiver(member, callbackCall)]
                                : callbackCall.ArgumentList.Arguments.Where(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) && IsDelegateExpression(member.Model, argument.Expression)).Select(static argument => argument.Expression).ToArray();

                            if (method.MethodKind == MethodKind.DelegateInvoke && member.Model.GetSymbolInfo(callbacks[0]).Symbol is { } callable && IsProvenAbsentCallable(callable))
                            {
                                continue;
                            }

                            if (method.MethodKind == MethodKind.DelegateInvoke && member.Model.GetSymbolInfo(callbacks[0]).Symbol is { } invoked && member.AbsentCallables?.Contains(invoked) == true)
                            {
                                continue;
                            }

                            if (IsReviewedHostedTaskCallbackTransfer(
                                member,
                                callbackCall))
                            {
                                foreach (ExpressionSyntax callback in callbacks)
                                {
                                    foreach (AuthoredMember body in ResolveCallableTargets(
                                        callback,
                                        member.Model,
                                        member.CallableBindings,
                                        member.AbsentCallables))
                                    {
                                        emittedEvidence = MergeEvidence(
                                            emittedEvidence,
                                            Traverse(
                                                WithoutInheritedAdmission(body),
                                                rootType,
                                                operationId
                                                    + "/callback@"
                                                    + Location(callbackCall),
                                                active,
                                                lifecycle,
                                                inheritedWork: null,
                                                inheritedEffect: null,
                                                completionOwned: true,
                                                recoveryEffect: null));
                                    }
                                }

                                continue;
                            }

                            if (IsReviewedTrackedTaskHandoff(
                                member,
                                callbackCall))
                            {
                                foreach (ExpressionSyntax callback in callbacks)
                                {
                                    foreach (AuthoredMember body in ResolveCallableTargets(
                                        callback,
                                        member.Model,
                                        member.CallableBindings,
                                        member.AbsentCallables))
                                    {
                                        AuthoredMember ownedBody = WithoutInheritedAdmission(
                                            body with
                                            {
                                                AdmissionBindings = member.AdmissionBindings,
                                                CallableBindings = member.CallableBindings,
                                                AbsentCallables = member.AbsentCallables,
                                                ConcreteBindings = member.ConcreteBindings,
                                                RecoveryBindings = member.RecoveryBindings,
                                                RecoveryContext = member.RecoveryContext,
                                                ValueBindings = member.ValueBindings,
                                            });

                                        emittedEvidence = MergeEvidence(
                                            emittedEvidence,
                                            Traverse(
                                                ownedBody,
                                                rootType,
                                                operationId
                                                    + "/callback@"
                                                    + Location(callbackCall),
                                                active,
                                                lifecycle,
                                                inheritedWork: null,
                                                inheritedEffect: null,
                                                completionOwned: true,
                                                recoveryEffect: null));
                                    }
                                }

                                continue;
                            }

                            if (TryHostHandoff(member, callbackCall) is { } handoff)
                            {
                                // A host-owned task transfers to an independently admitted root, never its caller's handles.
                                emittedEvidence = MergeEvidence(emittedEvidence, Traverse(
                                    handoff.Target,
                                    rootType,
                                    handoff.Root.OperationId,
                                    active,
                                    lifecycle,
                                    selection: handoff.Selection));

                                continue;
                            }

                            if (TrySelfAdmittedTaskHandoff(member, callbackCall) is { } selfAdmitted)
                            {
                                emittedEvidence = MergeEvidence(emittedEvidence, Traverse(selfAdmitted.Target, rootType, selfAdmitted.Root.OperationId, active, lifecycle));

                                continue;
                            }

                            bool awaitableReturn = TypeKey(method.ReturnType) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1";

                            SyntaxNode? completion = awaitableReturn ? CompletionPoint(member, callbackCall, completionOwned) : lazyCallback ? LazyCompletionPoint(member, callbackCall) : synchronousCallback ? callbackCall : null;

                            string? completionWork = completion is null
                                ? null
                                : WorkAt(
                                    member,
                                    completion,
                                    operationId,
                                    ordinary?.WorkKind,
                                    inheritedWork);

                            string? completionEffect = completion is null
                                ? null
                                : EffectAt(
                                    member,
                                    completion,
                                    operationId,
                                    inheritedEffect);

                            bool owned = knownCallback
                                && completion is not null
                                && completionWork == workAdmitted
                                && completionEffect == effectGroup;

                            foreach (ExpressionSyntax callback in callbacks)
                            {
                                AuthoredMember[] bodies = ResolveCallableTargets(
                                    callback,
                                    member.Model,
                                    member.CallableBindings,
                                    member.AbsentCallables);

                                bool everyBodyOwned = bodies.Length != 0;

                                TraversalEvidence callbackEvidence = TraversalEvidence.NoEvidence;

                                if (bodies.Length == 0
                                    && member.Model.GetSymbolInfo(callback).Symbol
                                        is IMethodSymbol externalCallback)
                                {
                                    string callbackCallee = Normalize(
                                        externalCallback);

                                    HostedProducerSiteKind? callbackKind = Classify(
                                        externalCallback,
                                        callbackCallee,
                                        callback,
                                        member);

                                    if (callbackKind is { } externalKind)
                                    {
                                        everyBodyOwned = owned;

                                        callbackEvidence = AddSite(
                                            externalKind,
                                            callbackCallee,
                                            member,
                                            rootType,
                                            operationId,
                                            callback,
                                            owned ? effectGroup : null,
                                            owned ? workAdmitted : null);

                                        emittedEvidence = MergeEvidence(
                                            emittedEvidence,
                                            callbackEvidence);
                                    }
                                    else if (RequiresExternalBoundaryClassification(
                                        externalCallback,
                                        member.Model.Compilation,
                                        callback,
                                        member.Model,
                                        member))
                                    {
                                        diagnostics.Add(new(
                                            "HOSTED_SITE_UNCLASSIFIED",
                                            Location(callback),
                                            callbackCallee));
                                    }
                                }

                                foreach (AuthoredMember body in bodies)
                                {
                                    bool callbackOwned = owned
                                        && (body.Symbol.IsAsync != true
                                            || awaitableReturn && !body.Symbol.ReturnsVoid);

                                    everyBodyOwned &= callbackOwned;

                                    TraversalEvidence bodyEvidence = Traverse(body, rootType, operationId + "/callback@" + Location(callbackCall) + "/body@" + Location(callback), active, lifecycle, callbackOwned ? workAdmitted : null, callbackOwned ? effectGroup : null, completionOwned: callbackOwned, recoveryEffect: callbackOwned ? recoveryEffect : null);

                                    callbackEvidence = MergeEvidence(
                                        callbackEvidence,
                                        bodyEvidence);

                                    emittedEvidence = MergeEvidence(emittedEvidence, bodyEvidence);
                                }

                                if (!everyBodyOwned
                                    && !IsEffectFreeExternalCallable(callback, member)
                                    && (bodies.Length == 0
                                        || callbackEvidence != TraversalEvidence.NoEvidence
                                        || bodies.Any(HasAdmissionSeed)))
                                {
                                    string reason = completion is null
                                        ? "no exact completion observation"
                                        : completionWork != workAdmitted
                                            ? "work admission does not survive to completion"
                                            : completionEffect != effectGroup
                                                ? "effect admission does not survive to completion"
                                                : bodies.Length == 0
                                                    ? "callback target is not exact"
                                                    : "callback body cannot inherit this completion";

                                    diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", operationId + "/callback@" + Location(callbackCall), callee + "; Callback execution must bind once and complete before its inherited admission lifetime ends; " + reason + "."));
                                }
                            }

                            continue;
                        }

                        if (method.Name is "Dispose" or "DisposeAsync" && node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } && member.Model.GetTypeInfo(access.Expression).Type is { } receiver && TypeKey(receiver) is "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter" or "System.IO.StreamWriter")
                        {
                            callee = TypeKey(receiver) + "." + method.Name;
                        }

                        bool aggregate = AggregateBoundaries.Contains(callee);

                        HostedProducerSiteKind? kind = Classify(
                            method,
                            callee,
                            node,
                            member);

                        if (kind is not null && !aggregate)
                        {
                            if (node is InvocationExpressionSyntax lazyRead && IsLazyDirectoryEnumeration(callee))
                            {
                                SyntaxNode? consumption = LazyCompletionPoint(member, lazyRead);

                                bool ownedByIteratorConsumer = consumption is null && completionOwned && IsIteratorMember(member);

                                string? consumedWork = ownedByIteratorConsumer ? workAdmitted : consumption is null ? null : WorkAt(member, consumption, operationId, ordinary?.WorkKind, inheritedWork);

                                if ((!ownedByIteratorConsumer && consumption is null) || consumedWork != workAdmitted)
                                {
                                    diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", RootOperation(operationId) + "/call@" + Location(node), callee + "; A lazy filesystem sequence must be consumed synchronously before its work lease ends."));

                                    workAdmitted = null;
                                }
                            }

                            if (node is InvocationExpressionSyntax sensitiveCall && IsAwaitable(method.ReturnType) && CompletionPoint(member, sensitiveCall, completionOwned) is null)
                            {
                                diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", RootOperation(operationId) + "/call@" + Location(node), callee + "; An awaitable sensitive boundary must complete before its retained admission lifetime ends."));

                                workAdmitted = null;

                                effectGroup = null;
                            }

                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(kind.Value, callee, member, rootType, operationId, node, effectGroup, workAdmitted));
                        }

                        if (node is InvocationExpressionSyntax recoveryCall
                            && method.Name == "RecoverAsync"
                            && TypeKey(method.ContainingType)
                                == "RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler")
                        {
                            emittedEvidence = MergeEvidence(
                                emittedEvidence,
                                TraverseRecoveryDispatch(
                                    member,
                                    recoveryCall,
                                    method,
                                    rootType,
                                    operationId,
                                    active,
                                    lifecycle,
                                    workAdmitted,
                                    recoveryEffect));

                            continue;
                        }

                        AuthoredMember? target = ResolveInvocationTarget(method, member, node);

                        AuthoredMember[] boundTargets = target is null
                            ? ResolveBoundImplementations(method, member.Model.Compilation)
                            : [];

                        AuthoredMember[] strategyTargets = ResolveStrategyTargets(method);

                        AuthoredMember[] reviewedClosedTargets = ResolveReviewedClosedDispatchTargets(method, node);

                        if (callee == "RetroDownfall.Arcanum.Infrastructure.Data.Schema.IGrimoireSchemaBackfill.AdvanceBatchAsync" && strategyTargets.Length != SchemaBackfillStrategies.Count)
                        {
                            diagnostics.Add(new("HOSTED_STRATEGY_BINDING_UNRESOLVED", Location(node), callee + "; Every configured concrete strategy must bind exactly once."));
                        }

                        bool unresolvedAuthoredTarget = target is null
                            && boundTargets.Length == 0
                            && strategyTargets.Length == 0
                            && reviewedClosedTargets.Length == 0
                            && HasAuthoredTarget(method);

                        if (kind is null
                            && target is null
                            && !callee.StartsWith("<ambiguous:", StringComparison.Ordinal)
                            && RequiresExternalBoundaryClassification(
                                method,
                                member.Model.Compilation,
                                node,
                                member.Model,
                                member))
                        {
                            diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), callee));
                        }

                        if (node is InvocationExpressionSyntax handleCall && !IsCompletionCombinator(method) && (AdmissionArguments(member, handleCall).Any(argument => argument.Expression is not DeclarationExpressionSyntax && MayCarryAdmissionHandle(member, argument.Expression) && (argument.RefKind is RefKind.Ref or RefKind.Out || target is null && boundTargets.Length == 0 && reviewedClosedTargets.Length == 0))
                            || AdmissionReceiver(member, handleCall) is { } receiverExpression && MayCarryAdmissionHandle(member, receiverExpression) && !PreservesAdmissionReceiver(method) && !ConsumesAdmissionReceiver(method)))
                        {
                            diagnostics.Add(new("HOSTED_ADMISSION_HANDLE_ESCAPE", operationId + "/call@" + Location(node), callee + "; A retained handle passed by ref/out or to an opaque callee has no proven lifetime continuation."));
                        }

                        if (target?.Symbol.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "GrimoireConnectionAcquisitionRouteAttribute") == true && kind is null)
                        {
                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(HostedProducerSiteKind.OrdinaryConnectionRoute, Normalize(target.Symbol), member, rootType, operationId, node, effectGroup, workAdmitted));

                            kind = HostedProducerSiteKind.OrdinaryConnectionRoute;
                        }

                        if (kind is null
                            || aggregate
                            || TransparentProducerBoundaries.Contains(callee))
                        {
                            AuthoredMember[] targets = strategyTargets.Length != 0
                                ? strategyTargets
                                : target is not null
                                    ? [target]
                                    : boundTargets.Length != 0
                                        ? boundTargets
                                        : reviewedClosedTargets;

                            if (targets.Length != 0)
                            {
                                bool directlyJoined = node is InvocationExpressionSyntax invocation
                                    && CompletionPoint(
                                        member,
                                        invocation,
                                        completionOwned) is not null;

                                TraversalEvidence targetEvidence = TraversalEvidence.NoEvidence;

                                foreach (AuthoredMember exactTarget in targets)
                                {
                                    bool hostedTaskTransfer =
                                        node is InvocationExpressionSyntax hostedTaskCall
                                        && IsReviewedHostedTaskTransfer(
                                            member,
                                            hostedTaskCall,
                                            exactTarget);

                                    bool joined = directlyJoined
                                        || node is InvocationExpressionSyntax ownedCall
                                            && IsReviewedOwnedCompletionTransfer(
                                                member,
                                                ownedCall,
                                                exactTarget)
                                        || hostedTaskTransfer;

                                    IReadOnlySet<int>? carrier = node is InvocationExpressionSyntax call ? VerifyFieldPublication(member, call, exactTarget, operationId, inheritedEffect) : null;

                                    string? nextRecoveryEffect = recoveryEffect;

                                    if (node is InvocationExpressionSyntax recoveryEntry
                                        && exactTarget.Symbol.Name == "SettleDiscoveredRuntimeAsync"
                                        && TypeKey(exactTarget.Symbol.ContainingType)
                                            == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler")
                                    {
                                        nextRecoveryEffect = RecoveryEffectFrontierAt(
                                            member,
                                            recoveryEntry,
                                            operationId);
                                    }

                                    SyntaxNode? lazyCompletion = node is InvocationExpressionSyntax lazyCall && IsLazySequence(exactTarget.Symbol.ReturnType) ? LazyCompletionPoint(member, lazyCall) : null;

                                    bool lazyOwned = lazyCompletion is not null && WorkAt(member, lazyCompletion, operationId, ordinary?.WorkKind, inheritedWork) == workAdmitted && EffectAt(member, lazyCompletion, operationId, inheritedEffect) == effectGroup;

                                    bool detached = IsAwaitable(exactTarget.Symbol.ReturnType) && !joined || IsLazySequence(exactTarget.Symbol.ReturnType) && !lazyOwned;

                                    if (detached)
                                    {
                                        diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", operationId + "/call@" + Location(node), callee + "; An awaitable or lazy helper must complete within its caller's retained lifetime."));
                                    }

                                    AuthoredMember boundTarget = node switch
                                    {
                                        InvocationExpressionSyntax boundCall =>
                                            BindInvocationContext(
                                                member,
                                                boundCall,
                                                exactTarget),
                                        BaseObjectCreationExpressionSyntax creation =>
                                            BindConstructorArguments(
                                                member,
                                                creation,
                                                exactTarget),
                                        _ => exactTarget,
                                    };

                                    if (hostedTaskTransfer)
                                    {
                                        boundTarget = WithoutInheritedAdmission(boundTarget);
                                    }

                                    TraversalEvidence exactEvidence = Traverse(boundTarget, rootType, operationId + "/call@" + Location(node), active, lifecycle, detached || hostedTaskTransfer ? null : workAdmitted, detached || hostedTaskTransfer ? null : effectGroup, fieldPublications: carrier, completionOwned: joined || lazyOwned, recoveryEffect: detached || hostedTaskTransfer ? null : nextRecoveryEffect);

                                    targetEvidence = MergeEvidence(targetEvidence, exactEvidence);

                                    emittedEvidence = MergeEvidence(emittedEvidence, exactEvidence);
                                }

                                if (AggregateBoundaries.Contains(callee) && targetEvidence == TraversalEvidence.NoEvidence)
                                {
                                    diagnostics.Add(new("HOSTED_AGGREGATE_PROOF_MISSING", operationId + "/call@" + Location(node), "The exact bound aggregate target set produced no executable site evidence; prose is not a proof."));
                                }

                                if (TransparentProducerBoundaries.Contains(callee)
                                    && (!ownedTransparentCallbackCarrier
                                        || targetEvidence == TraversalEvidence.NoEvidence))
                                {
                                    diagnostics.Add(new(
                                        "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                                        operationId + "/callback@" + Location(node),
                                        callee + "; A long-lived callback boundary must publish one disposable owner and expose its concrete activation under the same work frontier."));
                                }
                            }
                            else if (aggregate)
                            {
                                diagnostics.Add(new("HOSTED_AGGREGATE_PROOF_MISSING", Location(node), callee));
                            }
                            else if ((unresolvedAuthoredTarget || method.ContainingType.TypeKind == TypeKind.Interface && method.ContainingNamespace.ToDisplayString().StartsWith("RetroDownfall.", StringComparison.Ordinal) || method.ContainingType.TypeKind == TypeKind.Interface && method.ContainingType.Locations.Any(static location => location.IsInSource))
                                && !IsReviewedEffectFreeExternalMember(
                                    method,
                                    node,
                                    member.Model))
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

                        string setter = callee + " setter";

                        bool propertyMutation = node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node
                            || node.Parent?.IsKind(SyntaxKind.PostIncrementExpression) == true
                            || node.Parent?.IsKind(SyntaxKind.PreIncrementExpression) == true
                            || node.Parent?.IsKind(SyntaxKind.PostDecrementExpression) == true
                            || node.Parent?.IsKind(SyntaxKind.PreDecrementExpression) == true;

                        if (propertyMutation
                            && DetachedExternalCallbackProperties.Contains(setter)
                            && node.Parent is AssignmentExpressionSyntax callbackAssignment)
                        {
                            emittedEvidence = MergeEvidence(
                                emittedEvidence,
                                TraverseDetachedExternalCallbacks(
                                    member,
                                    [callbackAssignment.Right],
                                    callee,
                                    node,
                                    rootType,
                                    operationId,
                                    active,
                                    lifecycle));
                        }

                        if (!propertyMutation
                            && TypeKey(property.ContainingType) == "System.Lazy`1"
                            && property.Name == "Value"
                            && node is MemberAccessExpressionSyntax lazyValue)
                        {
                            if (IsProvenCreatedLazyRead(member, lazyValue))
                            {
                                continue;
                            }

                            LazyFactoryResolution? resolution = ResolveLazyFactory(
                                member,
                                lazyValue.Expression);

                            if (resolution is null)
                            {
                                diagnostics.Add(new(
                                    "HOSTED_SITE_UNCLASSIFIED",
                                    Location(node),
                                    callee));

                                continue;
                            }

                            BoundedDataProof boundedData = ProveBoundedLazyData(
                                resolution);

                            if (boundedData == BoundedDataProof.PureBoundedData)
                            {
                                continue;
                            }

                            if (boundedData == BoundedDataProof.Unknown)
                            {
                                diagnostics.Add(new(
                                    "HOSTED_SITE_UNCLASSIFIED",
                                    Location(node),
                                    callee));

                                continue;
                            }

                            TraversalEvidence factoryEvidence = Traverse(
                                resolution.Factory,
                                rootType,
                                operationId
                                    + "/lazy@"
                                    + Location(node),
                                active,
                                lifecycle,
                                workAdmitted,
                                effectGroup,
                                completionOwned: true,
                                recoveryEffect: recoveryEffect);

                            emittedEvidence = MergeEvidence(
                                emittedEvidence,
                                factoryEvidence);

                            if ((workAdmitted is null
                                    && !HasAdmissionSeed(resolution.Factory))
                                || HasEscapingDeferredWork(
                                    resolution.Factory,
                                    new HashSet<GraphMemberIdentity>()))
                            {
                                diagnostics.Add(new(
                                    "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                                    RootOperation(operationId)
                                        + "/lazy@"
                                        + Location(node),
                                    "System.Lazy`1..ctor; A deferred factory must execute under the consumer's retained admission or reacquire its own. Proof evidence: "
                                        + string.Join(
                                            ", ",
                                            boundedLazyDataEvidence.GetValueOrDefault(
                                                resolution.Storage)
                                                ?? [])));
                            }

                            continue;
                        }

                        HostedProducerSiteKind? externalPropertyKind =
                            IsBootstrapConsoleRedirection(member)
                            && TypeKey(property.ContainingType) == "System.IO.StreamWriter"
                            && property.Name == "AutoFlush"
                            && propertyMutation
                                ? HostedProducerSiteKind.FileSystemEffect
                                : TryClassifyExternalProducer(
                                    property,
                                    propertyMutation,
                                    out HostedProducerSiteKind classifiedProperty)
                                        ? classifiedProperty
                                        : null;

                        if (TypeKey(property.ContainingType)
                                == "System.IO.FileSystemWatcher"
                            && property.Name == "EnableRaisingEvents"
                            && node is MemberAccessExpressionSyntax watcherActivation
                            && !HasOwnedFileSystemWatcherLifetime(
                                member,
                                watcherActivation.Expression))
                        {
                            diagnostics.Add(new(
                                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                                RootOperation(operationId)
                                    + "/watcher@"
                                    + Location(node),
                                callee
                                    + "; Watcher activation requires one exact disposable lifecycle owner."));
                        }

                        if (propertyMutation
                            && (Vocabulary.ContainsKey(setter)
                                || externalPropertyKind is not null
                                || RequiresExternalBoundaryClassification(
                                    property,
                                    member.Model.Compilation,
                                    mutation: true,
                                    node,
                                    member.Model,
                                    member)))
                        {
                            bool authoredAutoProperty = property.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is PropertyDeclarationSyntax { AccessorList.Accessors.Count: > 0 } propertyDeclaration
                                && propertyDeclaration.AccessorList.Accessors.All(static accessor => accessor.Body is null && accessor.ExpressionBody is null);

                            if (Vocabulary.TryGetValue(setter, out HostedProducerSiteKind setterKind))
                            {
                                emittedEvidence = MergeEvidence(emittedEvidence, AddSite(setterKind, setter, member, rootType, operationId, node, effectGroup, workAdmitted));
                            }
                            else if (externalPropertyKind is { } externalSetterKind)
                            {
                                emittedEvidence = MergeEvidence(emittedEvidence, AddSite(externalSetterKind, setter, member, rootType, operationId, node, effectGroup, workAdmitted));
                            }
                            else if (!authoredAutoProperty)
                            {
                                diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), setter));
                            }

                            continue;
                        }

                        if (Vocabulary.TryGetValue(callee, out HostedProducerSiteKind kind))
                        {
                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(kind, callee, member, rootType, operationId, node, effectGroup, workAdmitted));
                        }
                        else if (externalPropertyKind is { } externalGetterKind)
                        {
                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(externalGetterKind, callee, member, rootType, operationId, node, effectGroup, workAdmitted));
                        }
                        else if (RequiresExternalBoundaryClassification(
                            property,
                            member.Model.Compilation,
                            mutation: false,
                            node,
                            member.Model,
                            member))
                        {
                            diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), callee));
                        }

                        bool writes = node.Parent is AssignmentExpressionSyntax write && write.Left == node || node.Parent?.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PostIncrementExpression or SyntaxKind.PreDecrementExpression or SyntaxKind.PostDecrementExpression;

                        bool reads = node.Parent is not AssignmentExpressionSyntax simple || simple.Left != node || !simple.IsKind(SyntaxKind.SimpleAssignmentExpression);

                        foreach (IMethodSymbol accessor in new[] { reads ? property.GetMethod : null, writes ? property.SetMethod : null }.OfType<IMethodSymbol>())
                        {
                            if (Resolve(accessor, member.Model.Compilation) is { } target)
                            {
                                emittedEvidence = MergeEvidence(emittedEvidence, Traverse(target, rootType, operationId + (accessor.MethodKind == MethodKind.PropertyGet ? "/get@" : "/set@") + Location(node), active, lifecycle, workAdmitted, effectGroup, recoveryEffect: recoveryEffect));
                            }
                        }
                    }
                    else if (symbol is IEventSymbol declaredEvent
                        && node.Parent is AssignmentExpressionSyntax eventMutation
                        && eventMutation.Left == node
                        && eventMutation.Kind() is SyntaxKind.AddAssignmentExpression
                            or SyntaxKind.SubtractAssignmentExpression
                        && declaredEvent.Locations.All(static location => !location.IsInSource))
                    {
                        bool addsHandler = eventMutation.IsKind(
                            SyntaxKind.AddAssignmentExpression);

                        string operation = addsHandler ? " add" : " remove";

                        bool ownedWatcherEvent =
                            TypeKey(declaredEvent.ContainingType)
                                == "System.IO.FileSystemWatcher"
                            && node is MemberAccessExpressionSyntax eventAccess
                            && HasOwnedFileSystemWatcherLifetime(
                                member,
                                eventAccess.Expression);

                        if (!ownedWatcherEvent)
                        {
                            diagnostics.Add(new(
                                "HOSTED_SITE_UNCLASSIFIED",
                                Location(node),
                                Normalize(declaredEvent) + operation));
                        }

                        if (addsHandler
                            && IsDelegateExpression(
                                member.Model,
                                eventMutation.Right))
                        {
                            AuthoredMember? handler = ResolveCallable(
                                    eventMutation.Right,
                                    member.Model,
                                    member.CallableBindings,
                                    member.AbsentCallables);

                            if (handler is not null)
                            {
                                handler = handler with
                                {
                                    AdmissionBindings = member.AdmissionBindings,
                                    CallableBindings = member.CallableBindings,
                                    AbsentCallables = member.AbsentCallables,
                                    ConcreteBindings = member.ConcreteBindings,
                                    RecoveryBindings = member.RecoveryBindings,
                                    RecoveryContext = member.RecoveryContext,
                                    ValueBindings = member.ValueBindings,
                                };
                            }

                            TraversalEvidence handlerEvidence = handler is null
                                ? TraversalEvidence.Inconclusive
                                : Traverse(
                                    WithoutInheritedAdmission(handler),
                                    rootType,
                                    operationId + "/callback@" + Location(node),
                                    active,
                                    lifecycle,
                                    inheritedWork: null,
                                    inheritedEffect: null,
                                    completionOwned: true,
                                    recoveryEffect: null);

                            emittedEvidence = MergeEvidence(
                                emittedEvidence,
                                handlerEvidence);

                            if (!ownedWatcherEvent
                                || handler is null
                                || handlerEvidence != TraversalEvidence.NoEvidence
                                    && !HasAdmissionSeed(handler))
                            {
                                diagnostics.Add(new(
                                    "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                                    RootOperation(operationId)
                                        + "/callback@"
                                        + Location(node),
                                    Normalize(declaredEvent)
                                        + "; External event lifetime ownership is not proven."));
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
                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(HostedProducerSiteKind.FileSystemEffect, resourceType.ToDisplayString() + "." + disposeName, member, rootType, operationId, resource, retainedEffect, retainedWork));
                        }
                        else if (dispose is not null && Resolve(dispose, member.Model.Compilation) is { } cleanup)
                        {
                            emittedEvidence = MergeEvidence(emittedEvidence, Traverse(cleanup, rootType, operationId + "/dispose@" + Location(resource), active, lifecycle, retainedWork, retainedEffect, recoveryEffect: recoveryEffect));
                        }
                        else if (resourceType?.ToDisplayString() is "System.IDisposable" or "System.IAsyncDisposable" && IsExplicitlyUnknownDisposable(member, resource) && AdmissionOrigins(member, resource, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Count == 0)
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

                            emittedEvidence = MergeEvidence(emittedEvidence, AddSite(HostedProducerSiteKind.FileSystemEffect, type.ToDisplayString() + "." + (asynchronous ? "DisposeAsync" : "Dispose"), member, rootType, operationId, node, disposalGroup, retainedWork));
                        }
                        else if (type is INamedTypeSymbol disposable)
                        {
                            bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                            IMethodSymbol? dispose = disposable.GetMembers(asynchronous ? "DisposeAsync" : "Dispose").OfType<IMethodSymbol>().SingleOrDefault(static method => method.Parameters.Length == 0);

                            if (dispose is not null && Resolve(dispose, member.Model.Compilation) is { } target)
                            {
                                int position = DisposalPosition(declaration);

                                string? retainedWork = WorkAt(member, declaration, operationId, ordinary?.WorkKind, inheritedWork, position, declaration);

                                emittedEvidence = MergeEvidence(emittedEvidence, Traverse(target, rootType, operationId + "/dispose@" + Location(declaration), active, lifecycle, retainedWork, EffectAt(member, declaration, operationId, inheritedEffect, position, declaration), recoveryEffect: recoveryEffect));
                            }
                        }
                    }
                }

                ValidatePublicationRegions(member, selection ?? member.Syntax, operationId, inheritedEffect, fieldPublications);
            }
            finally
            {
                active.Remove(state);
            }

            analyzedStates[state] = emittedEvidence;

            return emittedEvidence;
        }

        private static TraversalEvidence MergeEvidence(TraversalEvidence left, TraversalEvidence right)
        {
            if (left == TraversalEvidence.Evidence || right == TraversalEvidence.Evidence)
            {
                return TraversalEvidence.Evidence;
            }

            return left == TraversalEvidence.Inconclusive || right == TraversalEvidence.Inconclusive ? TraversalEvidence.Inconclusive : TraversalEvidence.NoEvidence;
        }

        private static int DisposalPosition(VariableDeclarationSyntax declaration) => declaration.Parent is UsingStatementSyntax statement ? statement.Statement.Span.End - 1 : declaration.Ancestors().OfType<BlockSyntax>().First().CloseBraceToken.SpanStart;

        private static bool IsExplicitlyUnknownDisposable(AuthoredMember member, ExpressionSyntax resource)
        {
            if (member.Model.GetSymbolInfo(resource).Symbol is IParameterSymbol)
            {
                return true;
            }

            if (member.Model.GetSymbolInfo(resource).Symbol is not ILocalSymbol local || local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax variable)
            {
                return false;
            }

            return variable.Initializer?.Value is { } initializer && (member.Model.GetConstantValue(initializer) is { HasValue: true, Value: null } || initializer is DefaultExpressionSyntax);
        }

        private sealed record HostHandoffProof(AuthoredMember Target, HostedProducerOperationEntry Root, SyntaxNode Selection, IFieldSymbol TaskField, string DispatchAnchor, string JoinAnchor);

        private sealed record SelfAdmittedTaskHandoffProof(AuthoredMember Target, HostedProducerOperationEntry Root);

        private bool IsReviewedOwnedCompletionTransfer(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            AuthoredMember target)
        {
            string type = TypeKey(caller.Symbol.ContainingType);

            string edge = type
                + "."
                + caller.Symbol.Name
                + "->"
                + target.Symbol.Name;

            if (edge is
                "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.StartHandle->StopAsync")
            {
                return true;
            }

            if (edge is
                    "RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager.EnsureGlobalLoadedAsync->RunGlobalInitOperationAsync")
            {
                if (call.Parent is not AssignmentExpressionSyntax
                    {
                        Right: { } assigned,
                        Left: { } localTarget,
                    } assignment
                    || assigned != call
                    || caller.Model.GetSymbolInfo(localTarget).Symbol
                        is not ILocalSymbol operation)
                {
                    return false;
                }

                bool published = caller.Syntax.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Any(write => write.SpanStart > assignment.Span.End
                        && caller.Model.GetSymbolInfo(write.Left).Symbol
                            is IFieldSymbol { Name: "_globalInitOperation" }
                        && SymbolEqualityComparer.Default.Equals(
                            caller.Model.GetSymbolInfo(write.Right).Symbol,
                            operation));

                bool joined = caller.Syntax.DescendantNodes()
                    .OfType<AwaitExpressionSyntax>()
                    .Any(awaited => awaited.SpanStart > assignment.Span.End
                        && awaited.DescendantNodesAndSelf()
                            .OfType<ExpressionSyntax>()
                            .Any(expression => SymbolEqualityComparer.Default.Equals(
                                caller.Model.GetSymbolInfo(expression).Symbol,
                                operation)));

                return published && joined;
            }

            if (edge is not
                ("RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.StopAsync->CompleteStopAsync"
                    or "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.GetOrStartStopOperation->CompleteStopOperationAsync"
                    or "RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager.GetOrStartStopOperation->CompleteStopOperationAsync"))
            {
                return false;
            }

            IParameterSymbol? owner = target.Symbol.Parameters
                .FirstOrDefault(static parameter => parameter.Name == "owner");

            if (owner is null
                || TypeKey(owner.Type)
                    != "System.Threading.Tasks.TaskCompletionSource")
            {
                return false;
            }

            InvocationExpressionSyntax[] completions = target.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(candidate => target.Model.GetSymbolInfo(candidate).Symbol
                    is IMethodSymbol method
                    && method.Name is "TrySetResult" or "TrySetException"
                    && candidate.Expression is MemberAccessExpressionSyntax access
                    && SymbolEqualityComparer.Default.Equals(
                        target.Model.GetSymbolInfo(access.Expression).Symbol,
                        owner))
                .ToArray();

            return completions.Select(candidate =>
                    ((IMethodSymbol)target.Model.GetSymbolInfo(candidate).Symbol!).Name)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(["TrySetResult", "TrySetException"]);
        }

        private bool IsReviewedHostedTaskTransfer(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            AuthoredMember target)
        {
            const string sagaType =
                "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService";

            const string servantType =
                "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService";

            string type = TypeKey(caller.Symbol.ContainingType);

            if (type == sagaType
                && caller.Symbol.Name == "ScheduleRetry"
                && target.Symbol.Name == "RetryAfterDelayAsync")
            {
                if (call.Parent is not EqualsValueClauseSyntax
                    {
                        Parent: VariableDeclaratorSyntax taskDeclaration,
                    }
                    || caller.Model.GetDeclaredSymbol(taskDeclaration)
                        is not ILocalSymbol scheduledTask)
                {
                    return false;
                }

                bool published = caller.Syntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(candidate => candidate.SpanStart > call.Span.End
                        && caller.Model.GetSymbolInfo(candidate).Symbol
                            is IMethodSymbol { Name: "Add" }
                        && candidate.Expression is MemberAccessExpressionSyntax
                        {
                            Expression: { } receiver,
                        }
                        && caller.Model.GetSymbolInfo(receiver).Symbol
                            is IFieldSymbol { Name: "_scheduledRetryTasks" }
                        && candidate.ArgumentList.Arguments.Any(argument =>
                            SymbolEqualityComparer.Default.Equals(
                                caller.Model.GetSymbolInfo(argument.Expression).Symbol,
                                scheduledTask))
                        && candidate.Ancestors().OfType<LockStatementSyntax>().Any());

                bool observed = AuthoredMembers.Any(observer =>
                        TypeKey(observer.Symbol.ContainingType) == sagaType
                        && observer.Symbol.Name == "ObserveScheduledRetriesAsync"
                        && observer.Syntax.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .Any(candidate => observer.Model.GetSymbolInfo(candidate).Symbol
                                    is IMethodSymbol method
                                && Normalize(method) == "System.Threading.Tasks.Task.WhenAll"
                                && candidate.FirstAncestorOrSelf<AwaitExpressionSyntax>()
                                    is not null))
                    && AuthoredMembers.Any(owner =>
                        TypeKey(owner.Symbol.ContainingType) == sagaType
                        && owner.Symbol.Name == "ExecuteAsync"
                        && owner.Syntax.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .Any(candidate => owner.Model.GetSymbolInfo(candidate).Symbol
                                    is IMethodSymbol method
                                && method.Name == "ObserveScheduledRetriesAsync"
                                && candidate.FirstAncestorOrSelf<AwaitExpressionSyntax>()
                                    is not null
                                && candidate.Ancestors().OfType<FinallyClauseSyntax>()
                                    .Any()));

                return published && observed;
            }

            if (type != servantType
                || caller.Symbol.Name != "DispatchDueJobsCore"
                || target.Symbol.Name != "TrackJobTask"
                || call.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    is not { } registry
                || caller.Model.GetSymbolInfo(registry).Symbol
                    is not IFieldSymbol { Name: "_activeJobTasks" }
                || target.Symbol.Parameters is not
                    [IParameterSymbol activeTasks,
                        IParameterSymbol taskId,
                        IParameterSymbol startJob])
            {
                return false;
            }

            InvocationExpressionSyntax[] starts = target.Syntax.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(candidate => target.Model.GetSymbolInfo(candidate).Symbol
                        is IMethodSymbol method
                    && method.MethodKind == MethodKind.DelegateInvoke
                    && candidate.Expression is ExpressionSyntax invoked
                    && SymbolEqualityComparer.Default.Equals(
                        target.Model.GetSymbolInfo(invoked).Symbol,
                        startJob))
                .ToArray();

            bool proxyPublishedFirst = starts is [InvocationExpressionSyntax start]
                && target.Syntax.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Any(assignment => assignment.Span.End < start.SpanStart
                        && assignment.Left is ElementAccessExpressionSyntax index
                        && SymbolEqualityComparer.Default.Equals(
                            target.Model.GetSymbolInfo(index.Expression).Symbol,
                            activeTasks)
                        && index.ArgumentList.Arguments.Any(argument =>
                            SymbolEqualityComparer.Default.Equals(
                                target.Model.GetSymbolInfo(argument.Expression).Symbol,
                                taskId)));

            return proxyPublishedFirst
                && HasReviewedTrackedTaskStopJoin(
                    caller.Symbol.ContainingType,
                    "_activeJobTasks");
        }

        private bool IsReviewedHostedTaskCallbackTransfer(
            AuthoredMember member,
            InvocationExpressionSyntax callbackCall)
        {
            if (TypeKey(member.Symbol.ContainingType)
                    != "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService"
                || member.Symbol.Name != "TrackJobTask"
                || member.Model.GetSymbolInfo(callbackCall).Symbol
                    is not IMethodSymbol
                    {
                        MethodKind: MethodKind.DelegateInvoke,
                    }
                || callbackCall.Expression is not ExpressionSyntax invoked
                || member.Model.GetSymbolInfo(invoked).Symbol
                    is not IParameterSymbol { Name: "startJob" }
                || callbackCall.Parent is not EqualsValueClauseSyntax
                    {
                        Parent: VariableDeclaratorSyntax taskDeclaration,
                    }
                || member.Model.GetDeclaredSymbol(taskDeclaration)
                    is not ILocalSymbol jobTask)
            {
                return false;
            }

            InvocationExpressionSyntax[] transfers = member.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => call.SpanStart > callbackCall.Span.End
                    && member.Model.GetSymbolInfo(call).Symbol
                        is IMethodSymbol { Name: "SetResult" }
                    && call.ArgumentList.Arguments.Any(argument =>
                        SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(argument.Expression).Symbol,
                            jobTask)))
                .ToArray();

            return transfers is [InvocationExpressionSyntax]
                && HasReviewedTrackedTaskStopJoin(
                    member.Symbol.ContainingType,
                    "_activeJobTasks");
        }

        private bool IsReviewedTrackedTaskHandoff(
            AuthoredMember caller,
            InvocationExpressionSyntax dispatch)
        {
            if (caller.Model.GetSymbolInfo(dispatch).Symbol is not IMethodSymbol taskRun
                || Normalize(taskRun) != "System.Threading.Tasks.Task.Run"
                || dispatch.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    is not AnonymousFunctionExpressionSyntax callback)
            {
                return false;
            }

            string type = TypeKey(caller.Symbol.ContainingType);

            if (type == "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService"
                && caller.Symbol.Name == "TickAsync")
            {
                InvocationExpressionSyntax[] ownedRuns = callback.Body
                    .DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => caller.Model.GetSymbolInfo(call).Symbol
                        is IMethodSymbol method
                        && method.Name == "ProcessBatchWithCleanupAsync"
                        && SymbolEqualityComparer.Default.Equals(
                            method.ContainingType,
                            caller.Symbol.ContainingType))
                    .ToArray();

                if (ownedRuns is not [InvocationExpressionSyntax ownedRun]
                    || ownedRun.FirstAncestorOrSelf<AwaitExpressionSyntax>() is null
                    || callback.Body is not BlockSyntax
                    {
                        Statements: [TryStatementSyntax protectedRun],
                    }
                    || protectedRun.Finally is null)
                {
                    return false;
                }

                ISymbol? completion = protectedRun.Finally.Block
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => caller.Model.GetSymbolInfo(call).Symbol
                        is IMethodSymbol { Name: "TrySetResult" })
                    .Select(call => call.Expression is MemberAccessExpressionSyntax access
                        ? caller.Model.GetSymbolInfo(access.Expression).Symbol
                        : null)
                    .SingleOrDefault();

                if (completion is not ILocalSymbol
                    || !caller.Syntax.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(call => call.Span.End < dispatch.SpanStart
                            && caller.Model.GetSymbolInfo(call).Symbol
                                is IMethodSymbol { Name: "TryAdd" }
                            && call.ArgumentList.Arguments.Any(argument =>
                                argument.Expression
                                    is MemberAccessExpressionSyntax
                                    {
                                        Expression: { } owner,
                                        Name.Identifier.ValueText: "Task",
                                    }
                                && SymbolEqualityComparer.Default.Equals(
                                    caller.Model.GetSymbolInfo(owner).Symbol,
                                    completion))))
                {
                    return false;
                }

                return HasReviewedTrackedTaskStopJoin(
                    caller.Symbol.ContainingType,
                    "_inFlight");
            }

            if (type == "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService"
                && caller.Symbol.Name == "StartHandle")
            {
                InvocationExpressionSyntax[] ownedRuns = callback.Body
                    .DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => caller.Model.GetSymbolInfo(call).Symbol
                        is IMethodSymbol method
                        && method.Name == "RunHandleAsync"
                        && SymbolEqualityComparer.Default.Equals(
                            method.ContainingType,
                            caller.Symbol.ContainingType))
                    .ToArray();

                AuthoredMember? run = ownedRuns is [InvocationExpressionSyntax ownedRun]
                    && caller.Model.GetSymbolInfo(ownedRun).Symbol is IMethodSymbol runMethod
                    ? Resolve(runMethod, caller.Model.Compilation)
                    : null;

                if (run is null)
                {
                    return false;
                }

                AuthoredMember[] finishers = AuthoredMembers
                    .Where(member => SymbolEqualityComparer.Default.Equals(
                            member.Symbol.ContainingType,
                            caller.Symbol.ContainingType)
                        && member.Symbol.Name == "FinishHandle")
                    .ToArray();

                return finishers is [AuthoredMember finisher]
                    && finisher.Syntax.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(call => finisher.Model.GetSymbolInfo(call).Symbol
                            is IMethodSymbol { Name: "TrySetResult" });
            }

            return false;
        }

        private bool HasReviewedTrackedTaskStopJoin(
            INamedTypeSymbol owner,
            string trackedFieldName)
        {
            IFieldSymbol? tracked = owner.GetMembers(trackedFieldName)
                .OfType<IFieldSymbol>()
                .SingleOrDefault();

            if (tracked is null)
            {
                return false;
            }

            return AuthoredMembers
                .Where(member => SymbolEqualityComparer.Default.Equals(
                        member.Symbol.ContainingType,
                        owner)
                    && member.Symbol.Name == "StopAsync")
                .Any(stop => stop.Syntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(call => stop.Model.GetSymbolInfo(call).Symbol
                        is IMethodSymbol aggregate
                        && Normalize(aggregate)
                            == "System.Threading.Tasks.Task.WhenAll"
                        && call.Ancestors().FirstOrDefault() is not null
                        && stop.Syntax.DescendantNodes()
                            .OfType<ExpressionSyntax>()
                            .Any(expression => SymbolEqualityComparer.Default.Equals(
                                stop.Model.GetSymbolInfo(expression).Symbol,
                                tracked))));
        }

        private SelfAdmittedTaskHandoffProof? TrySelfAdmittedTaskHandoff(AuthoredMember caller, InvocationExpressionSyntax dispatch)
        {
            if (TypeKey(caller.Symbol.ContainingType) != "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService" || caller.Symbol.Name != "BeginExecutionTask" || caller.Model.GetSymbolInfo(dispatch).Symbol is not IMethodSymbol taskRun || Normalize(taskRun) != "System.Threading.Tasks.Task.Run" || dispatch.ArgumentList.Arguments.FirstOrDefault()?.Expression is not AnonymousFunctionExpressionSyntax callback)
            {
                return null;
            }

            if (dispatch.Parent is not AssignmentExpressionSyntax dispatchAssignment
                || dispatchAssignment.Right != dispatch
                || caller.Model.GetSymbolInfo(dispatchAssignment.Left).Symbol is not ILocalSymbol taskLocal
                || TypeKey(taskLocal.Type) != "System.Threading.Tasks.Task"
                || taskLocal.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax()
                    is not VariableDeclaratorSyntax
                    {
                        Initializer.Value.RawKind: (int)SyntaxKind.NullLiteralExpression,
                    }
                || dispatchAssignment.Parent is not ExpressionStatementSyntax
                    {
                        Parent: BlockSyntax publicationBlock,
                    }
                || publicationBlock.Parent is not LockStatementSyntax publicationLock
                || caller.Model.GetSymbolInfo(publicationLock.Expression).Symbol
                    is not IFieldSymbol
                    {
                        Name: "_executionLifecycleLock",
                    } lifecycleLock
                || callback.Body is not BlockSyntax
                    {
                        Statements:
                        [
                            LockStatementSyntax callbackBarrier,
                            TryStatementSyntax guardedRun,
                        ],
                    }
                || callbackBarrier.Statement is not BlockSyntax
                    {
                        Statements.Count: 0,
                    }
                || !SymbolEqualityComparer.Default.Equals(
                    caller.Model.GetSymbolInfo(callbackBarrier.Expression).Symbol,
                    lifecycleLock)
                || guardedRun.Block.Statements is not
                    [ExpressionStatementSyntax { Expression: AwaitExpressionSyntax awaitedRun }]
                || guardedRun.Catches is not [CatchClauseSyntax observedFault]
                || observedFault.Filter is not null
                || observedFault.Declaration is not { Type: { } caughtType }
                || TypeKey(caller.Model.GetTypeInfo(caughtType).Type!) != "System.Exception"
                || observedFault.Block.DescendantNodes()
                    .Any(static node => node is ThrowStatementSyntax)
                || guardedRun.Finally?.Block.Statements is not
                    [ExpressionStatementSyntax { Expression: InvocationExpressionSyntax cleanupCall }])
            {
                return null;
            }

            InvocationExpressionSyntax[] runCalls = guardedRun.Block
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol candidate
                    && Resolve(candidate, caller.Model.Compilation) is { } resolved
                    && resolved.Symbol.Name == "RunApprenticeAsync"
                    && SymbolEqualityComparer.Default.Equals(
                        resolved.Symbol.ContainingType,
                        caller.Symbol.ContainingType))
                .ToArray();

            if (runCalls is not [InvocationExpressionSyntax runCall]
                || !awaitedRun.DescendantNodesAndSelf().Contains(runCall)
                || guardedRun.Block.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(call => call != runCall
                        && (caller.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
                            || Normalize(method) is not
                                "System.Threading.Tasks.Task.ConfigureAwait"
                                    and not "System.Threading.Tasks.Task`1.ConfigureAwait"))
                || caller.Model.GetSymbolInfo(runCall).Symbol is not IMethodSymbol targetSymbol
                || Resolve(targetSymbol, caller.Model.Compilation) is not { } target)
            {
                return null;
            }

            IParameterSymbol? apprenticeId = caller.Symbol.Parameters
                .SingleOrDefault(static parameter => parameter.Name == "apprenticeId");

            ISymbol? generation = runCall.ArgumentList.Arguments.Count == 2
                ? caller.Model.GetSymbolInfo(
                    StripTransparentExpression(
                        runCall.ArgumentList.Arguments[1].Expression)).Symbol
                : null;

            bool RefersTo(ExpressionSyntax expression, ISymbol? symbol) =>
                symbol is not null
                && SymbolEqualityComparer.Default.Equals(
                    caller.Model.GetSymbolInfo(
                        StripTransparentExpression(expression)).Symbol,
                    symbol);

            if (apprenticeId is null
                || generation is not ILocalSymbol
                || runCall.ArgumentList.Arguments.Count != 2
                || !RefersTo(runCall.ArgumentList.Arguments[0].Expression, apprenticeId)
                || caller.Model.GetSymbolInfo(cleanupCall).Symbol is not IMethodSymbol cleanupSymbol
                || Resolve(cleanupSymbol, caller.Model.Compilation) is not { } cleanup
                || cleanup.Symbol.Name != "CleanupExecution"
                || !SymbolEqualityComparer.Default.Equals(
                    cleanup.Symbol.ContainingType,
                    caller.Symbol.ContainingType)
                || cleanupCall.ArgumentList.Arguments.Count != 3
                || !RefersTo(cleanupCall.ArgumentList.Arguments[0].Expression, apprenticeId)
                || !RefersTo(cleanupCall.ArgumentList.Arguments[1].Expression, generation)
                || !RefersTo(cleanupCall.ArgumentList.Arguments[2].Expression, taskLocal))
            {
                return null;
            }

            foreach (InvocationExpressionSyntax catchCall in observedFault.Block
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                if (caller.Model.GetSymbolInfo(catchCall).Symbol is not IMethodSymbol method
                    || !IsReviewedEffectFreeExternalMember(
                        method,
                        catchCall,
                        caller.Model))
                {
                    return null;
                }
            }

            AssignmentExpressionSyntax[] taskWrites = caller.Syntax
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(write => RefersTo(write.Left, taskLocal))
                .ToArray();

            AssignmentExpressionSyntax[] publications = publicationBlock.Statements
                .OfType<ExpressionStatementSyntax>()
                .Select(static statement => statement.Expression)
                .OfType<AssignmentExpressionSyntax>()
                .Where(write => write.SpanStart > dispatchAssignment.Span.End
                    && RefersTo(write.Right, taskLocal)
                    && write.Left is ElementAccessExpressionSyntax index
                    && caller.Model.GetSymbolInfo(index.Expression).Symbol
                        is IFieldSymbol
                        {
                            Name: "_activeTasks",
                        } activeTasks
                    && SymbolEqualityComparer.Default.Equals(
                        activeTasks.ContainingType,
                        caller.Symbol.ContainingType)
                    && index.ArgumentList.Arguments is [ArgumentSyntax key]
                    && RefersTo(key.Expression, apprenticeId))
                .ToArray();

            bool taskEscapes = caller.Syntax.DescendantNodes()
                .OfType<ArgumentSyntax>()
                .Any(argument => argument.RefKindKeyword.Kind() is
                        SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                    && RefersTo(argument.Expression, taskLocal));

            if (taskWrites is not [AssignmentExpressionSyntax exactWrite]
                || exactWrite != dispatchAssignment
                || publications is not [AssignmentExpressionSyntax]
                || taskEscapes)
            {
                return null;
            }

            HostedProducerOperationEntry[] roots = declaredOperations.Where(entry => entry.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && entry.WorkKind == GrimoireWorkKind.ApprenticeExecution && entry.SourcePath == target.Syntax.SyntaxTree.FilePath && entry.EnclosingType == TypeKey(target.Symbol.ContainingType) && entry.Member == target.Symbol.Name && entry.OperationId == entry.EnclosingType + "." + entry.Member).ToArray();

            return roots.Length == 1 ? new(target, roots[0]) : null;
        }

        private HostHandoffProof? TryHostHandoff(AuthoredMember caller, InvocationExpressionSyntax dispatch)
        {
            if (caller.Symbol.Name != "StartAsync" || !IsLifecycle(caller.Symbol) || !caller.Symbol.ContainingType.AllInterfaces.Any(static type => TypeKey(type) == "Microsoft.Extensions.Hosting.IHostedService") || caller.Model.GetSymbolInfo(dispatch).Symbol is not IMethodSymbol taskDispatch)
            {
                return null;
            }

            string dispatchKind = Normalize(taskDispatch);

            InvocationExpressionSyntax publishedTask = dispatch;

            if (dispatchKind == "System.Threading.Tasks.TaskFactory.StartNew")
            {
                if (dispatch.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Unwrap", Parent: InvocationExpressionSyntax unwrap } || caller.Model.GetSymbolInfo(unwrap).Symbol is not IMethodSymbol unwrapMethod || Normalize(unwrapMethod) != "System.Threading.Tasks.TaskExtensions.Unwrap")
                {
                    return null;
                }

                publishedTask = unwrap;
            }
            else if (dispatchKind != "System.Threading.Tasks.Task.Run")
            {
                return null;
            }

            if (publishedTask.Parent is not AssignmentExpressionSyntax assignment || assignment.Right != publishedTask || caller.Model.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol field || TypeKey(field.Type) != "System.Threading.Tasks.Task" || !SymbolEqualityComparer.Default.Equals(field.ContainingType, caller.Symbol.ContainingType))
            {
                return null;
            }

            AuthoredMember[] owners = AuthoredMembers.Where(member => SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, caller.Symbol.ContainingType)).ToArray();

            bool RefersToField(AuthoredMember member, SyntaxNode syntax) => syntax.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, field));

            bool escapes = owners.Any(member => member.Syntax.DescendantNodes().Any(node => node is ArgumentSyntax argument && argument.RefKindKeyword.RawKind != 0 && RefersToField(member, argument.Expression) || node is RefExpressionSyntax reference && RefersToField(member, reference.Expression) || node is PrefixUnaryExpressionSyntax address && address.IsKind(SyntaxKind.AddressOfExpression) && RefersToField(member, address.Operand)));

            ExpressionSyntax? callback = dispatch.ArgumentList.Arguments.FirstOrDefault()?.Expression;

            AuthoredMember? target = callback is AnonymousFunctionExpressionSyntax { Body: InvocationExpressionSyntax targetCall } && caller.Model.GetSymbolInfo(targetCall).Symbol is IMethodSymbol targetSymbol
                ? Resolve(targetSymbol, caller.Model.Compilation)
                : callback is null ? null : ResolveCallable(callback, caller.Model, caller.CallableBindings, caller.AbsentCallables);

            (AuthoredMember Member, AssignmentExpressionSyntax Write)[] writes = owners.SelectMany(member => member.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(write => RefersToField(member, write.Left)).Select(write => (member, write))).ToArray();

            static bool Terminates(StatementSyntax statement)
            {
                StatementSyntax? terminal = statement is BlockSyntax block ? block.Statements.LastOrDefault() : statement;

                return terminal is ReturnStatementSyntax or ThrowStatementSyntax;
            }

            bool MutuallyExclusiveWithDispatch(AuthoredMember owner, AssignmentExpressionSyntax write)
            {
                if (owner != caller)
                {
                    return false;
                }

                foreach (IfStatementSyntax branch in write.Ancestors().OfType<IfStatementSyntax>().TakeWhile(candidate => caller.Syntax.Span.Contains(candidate.Span)))
                {
                    bool writeInThen = branch.Statement.Span.Contains(write.Span);

                    bool writeInElse = branch.Else?.Statement.Span.Contains(write.Span) == true;

                    bool dispatchInThen = branch.Statement.Span.Contains(assignment.Span);

                    bool dispatchInElse = branch.Else?.Statement.Span.Contains(assignment.Span) == true;

                    if (writeInThen && dispatchInElse || writeInElse && dispatchInThen)
                    {
                        return true;
                    }

                    StatementSyntax? writeBranch = writeInThen ? branch.Statement : writeInElse ? branch.Else?.Statement : null;

                    if (writeBranch is not null && branch.Span.End < assignment.SpanStart && Terminates(writeBranch))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool exactPublication = writes.Count(candidate => candidate.Write == assignment) == 1 && writes.Where(candidate => candidate.Write != assignment).All(candidate => MutuallyExclusiveWithDispatch(candidate.Member, candidate.Write));

            if (escapes || !exactPublication || target is null)
            {
                return null;
            }

            (HostedProducerOperationEntry Root, SyntaxNode Selection)[] roots = declaredOperations
                .Where(entry => entry.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork
                    && entry.WorkKind is not null
                    && entry.SourcePath == target.Syntax.SyntaxTree.FilePath
                    && entry.EnclosingType == TypeKey(target.Symbol.ContainingType)
                    && entry.Member == target.Symbol.Name)
                .Select(entry => (Root: entry, Selection: SelectRoot(target, entry.OperationId)))
                .Where(static candidate => candidate.Selection is not null)
                .Select(static candidate => (candidate.Root, candidate.Selection!))
                .ToArray();

            if (roots.Length != 1)
            {
                return null;
            }

            List<AwaitExpressionSyntax> joins = [];

            HashSet<GraphMemberIdentity> stopReachable = [];

            Queue<AuthoredMember> pendingStopMembers = new(owners.Where(static member => member.Symbol.Name == "StopAsync" && IsLifecycle(member.Symbol)));

            while (pendingStopMembers.TryDequeue(out AuthoredMember? reachable))
            {
                if (!stopReachable.Add(MemberIdentity(reachable)))
                {
                    continue;
                }

                foreach (InvocationExpressionSyntax call in reachable.Syntax.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<InvocationExpressionSyntax>())
                {
                    if (reachable.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Resolve(method, reachable.Model.Compilation) is { } next && SymbolEqualityComparer.Default.Equals(next.Symbol.ContainingType, caller.Symbol.ContainingType))
                    {
                        pendingStopMembers.Enqueue(next);
                    }
                }
            }

            foreach (AuthoredMember stop in owners.Where(member => stopReachable.Contains(MemberIdentity(member))))
            {
                bool RefersToPublishedTask(ExpressionSyntax expression)
                {
                    ISymbol? symbol = stop.Model.GetSymbolInfo(expression).Symbol;

                    if (SymbolEqualityComparer.Default.Equals(symbol, field))
                    {
                        return true;
                    }

                    if (symbol is not ILocalSymbol local
                        || local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax()
                            is not VariableDeclaratorSyntax variable)
                    {
                        return false;
                    }

                    BlockSyntax block = variable.Ancestors().OfType<BlockSyntax>().First();

                    if (variable.Initializer?.Value is { } initializer)
                    {
                        return RefersToField(stop, initializer)
                            && !block.DescendantNodes()
                                .OfType<AssignmentExpressionSyntax>()
                                .Any(write => SymbolEqualityComparer.Default.Equals(
                                    stop.Model.GetSymbolInfo(write.Left).Symbol,
                                    local));
                    }

                    AssignmentExpressionSyntax[] writes = block.DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(write => SymbolEqualityComparer.Default.Equals(
                            stop.Model.GetSymbolInfo(write.Left).Symbol,
                            local))
                        .ToArray();

                    if (writes is not [AssignmentExpressionSyntax exactWrite]
                        || !exactWrite.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        || exactWrite.Span.End >= expression.SpanStart
                        || !RefersToField(stop, exactWrite.Right)
                        || exactWrite.Ancestors()
                            .TakeWhile(ancestor => ancestor != block)
                            .Any(static ancestor => ancestor is IfStatementSyntax
                                or ElseClauseSyntax
                                or SwitchStatementSyntax
                                or ForStatementSyntax
                                or ForEachStatementSyntax
                                or ForEachVariableStatementSyntax
                                or WhileStatementSyntax
                                or DoStatementSyntax
                                or CatchClauseSyntax
                                or AnonymousFunctionExpressionSyntax
                                or LocalFunctionStatementSyntax))
                    {
                        return false;
                    }

                    return !block.DescendantNodes()
                        .Any(candidate => candidate.SpanStart >= variable.Span.End
                            && candidate.Span.End < exactWrite.SpanStart
                            && candidate is ReturnStatementSyntax
                                or ThrowStatementSyntax
                                or GotoStatementSyntax);
                }

                foreach (AwaitExpressionSyntax awaited in stop.Syntax.DescendantNodes(node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).OfType<AwaitExpressionSyntax>())
                {
                    bool ExactNonNullGuard(IfStatementSyntax guard) => guard.Statement.Span.Contains(awaited.Span) && guard.Condition is IsPatternExpressionSyntax { Expression: { } tested, Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression } } } && RefersToPublishedTask(tested);

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

                    if (RefersToPublishedTask(expression))
                    {
                        joins.Add(awaited);
                    }
                }
            }

            return joins.Count == 1
                ? new(
                    target,
                    roots[0].Root,
                    roots[0].Selection,
                    field,
                    Location(dispatch),
                    Location(joins[0]))
                : null;
        }

        private static bool IsKnownSynchronousCallback(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            string type = TypeKey(definition.ContainingType);

            return type == "System.Linq.Enumerable"
                    && definition.Name is "Aggregate" or "All" or "Any" or "Average" or "Count" or "First" or "FirstOrDefault" or "Last" or "LastOrDefault" or "LongCount" or "Max" or "MaxBy" or "Min" or "MinBy" or "Single" or "SingleOrDefault" or "Sum" or "ToDictionary" or "ToHashSet" or "ToLookup"
                || type == "System.Array"
                    && definition.Name is "Exists" or "Sort"
                || type == "System.Collections.Concurrent.ConcurrentDictionary`2"
                    && definition.Name is "AddOrUpdate" or "GetOrAdd"
                || type == "System.Collections.Generic.List`1"
                    && definition.Name is "ConvertAll" or "FindIndex" or "ForEach" or "RemoveAll" or "Sort"
                || type == "System.Collections.Generic.HashSet`1"
                    && definition.Name == "RemoveWhere"
                || type == "System.String"
                    && definition.Name == "Create"
                || type == "System.Linq.ImmutableArrayExtensions"
                    && definition.Name is "First" or "FirstOrDefault"
                || type == "System.Threading.Tasks.Parallel"
                    && definition.Name is "For" or "ForEach" or "Invoke";
        }

        private static bool IsReviewedEffectFreeRecoveryTerminal(
            AuthoredMember member,
            InvocationExpressionSyntax call,
            IMethodSymbol method)
        {
            if (member.RecoveryContext is not
                {
                    Kind: "data-retention-mutation",
                    CheckpointVersion: 0,
                    Disposition: RecoveryDisposition.OrdinaryDbOnly,
                }
                || TypeKey(member.Symbol.ContainingType)
                    != "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService"
                || member.Symbol.Name != "RecoverMutationAsync"
                || TypeKey(method.ContainingType)
                    != "RetroDownfall.Arcanum.Core.Operations.LongRunningOperationRecoveryResult"
                || method.Name != "Abandoned"
                || call.Parent is not ReturnStatementSyntax
                || call.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault() is not
                    { Condition: { } condition })
            {
                return false;
            }

            return condition.DescendantNodesAndSelf()
                .OfType<BinaryExpressionSyntax>()
                .Any(comparison => comparison.IsKind(SyntaxKind.EqualsExpression)
                    && member.Model.GetConstantValue(comparison.Right) is
                    {
                        HasValue: true,
                        Value: 0,
                    }
                    && member.Model.GetSymbolInfo(comparison.Left).Symbol is
                        IPropertySymbol { Name: "CheckpointVersion" });
        }

        private bool IsReviewedDormantTestSeam(IMethodSymbol method)
        {
            const string cleanupObserverType =
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetOfflineCleanupTestObserver";

            if (TypeKey(method.ContainingType) == cleanupObserverType
                && method.Name is
                    "AfterInitialCapture" or "AfterFileDeleted" or "AfterDirectoryDeleted")
            {
                const string cleanupType =
                    "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetOfflineCleanup";

                bool productionConstructsObserver = AuthoredMembers
                    .SelectMany(member => member.Syntax.DescendantNodes()
                        .OfType<BaseObjectCreationExpressionSyntax>()
                        .Select(creation => (member, creation)))
                    .Any(candidate => candidate.member.Model.GetSymbolInfo(candidate.creation).Symbol
                            is IMethodSymbol constructor
                        && TypeKey(constructor.ContainingType) == cleanupType
                        && constructor.Parameters.Any(parameter =>
                            TypeKey(parameter.Type) == cleanupObserverType));

                int invocationCount = AuthoredMembers
                    .Where(member => TypeKey(member.Symbol.ContainingType) == cleanupType)
                    .SelectMany(member => member.Syntax.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Select(call => (member, call)))
                    .Count(candidate => candidate.member.Model.GetSymbolInfo(candidate.call).Symbol
                            is IMethodSymbol called
                        && TypeKey(called.ContainingType) == cleanupObserverType
                        && called.Name == method.Name);

                return !productionConstructsObserver && invocationCount == 1;
            }

            const string seamType =
                "RetroDownfall.Arcanum.Infrastructure.Data.GrimoireScopedConsumerTestSeam";

            if (TypeKey(method.ContainingType) != seamType
                || method.Name != "PauseAsync")
            {
                return false;
            }

            AuthoredMember[] pauses = AuthoredMembers
                .Where(member => SymbolEqualityComparer.Default.Equals(
                    member.Symbol.OriginalDefinition,
                    method.OriginalDefinition))
                .ToArray();

            if (pauses is not [AuthoredMember pause])
            {
                return false;
            }

            AuthoredMember[] overrides = AuthoredMembers
                .Where(member => TypeKey(member.Symbol.ContainingType) == seamType
                    && member.Symbol.Name == "Override")
                .ToArray();

            if (overrides is not [AuthoredMember])
            {
                return false;
            }

            bool productionRegistersCheckpoint = AuthoredMembers
                .Where(member => TypeKey(member.Symbol.ContainingType) != seamType)
                .Any(member => member.Syntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(call => member.Model.GetSymbolInfo(call).Symbol
                            is IMethodSymbol called
                        && TypeKey(called.ContainingType) == seamType
                        && called.Name == "Override"));

            InvocationExpressionSyntax[] callbackInvocations = pause.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => pause.Model.GetSymbolInfo(call).Symbol
                    is IMethodSymbol
                    {
                        MethodKind: MethodKind.DelegateInvoke,
                    })
                .ToArray();

            return !productionRegistersCheckpoint
                && callbackInvocations is [InvocationExpressionSyntax];
        }

        private TraversalEvidence TraverseDetachedExternalCallbacks(
            AuthoredMember member,
            IReadOnlyList<ExpressionSyntax> callbacks,
            string callee,
            SyntaxNode carrier,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle)
        {
            bool proven = callbacks.Count != 0;

            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (ExpressionSyntax callback in callbacks)
            {
                AuthoredMember[] bodies = ResolveCallableTargets(
                    callback,
                    member.Model,
                    member.CallableBindings,
                    member.AbsentCallables);

                proven &= bodies.Length != 0;

                foreach (AuthoredMember body in bodies)
                {
                    AuthoredMember detachedBody = WithoutInheritedAdmission(
                        body with
                        {
                            AdmissionBindings = member.AdmissionBindings,
                            CallableBindings = member.CallableBindings,
                            AbsentCallables = member.AbsentCallables,
                            ConcreteBindings = member.ConcreteBindings,
                            RecoveryBindings = member.RecoveryBindings,
                            RecoveryContext = member.RecoveryContext,
                            ValueBindings = member.ValueBindings,
                        });

                    TraversalEvidence bodyEvidence = Traverse(
                        detachedBody,
                        rootType,
                        operationId + "/callback@" + Location(carrier),
                        active,
                        lifecycle,
                        inheritedWork: null,
                        inheritedEffect: null,
                        completionOwned: true,
                        recoveryEffect: null);

                    evidence = MergeEvidence(evidence, bodyEvidence);

                    proven &= bodyEvidence == TraversalEvidence.NoEvidence
                        || HasAdmissionSeed(detachedBody);
                }
            }

            if (!proven)
            {
                diagnostics.Add(new(
                    "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                    RootOperation(operationId)
                        + "/callback@"
                        + Location(carrier),
                    callee
                        + "; A retained external callback must be exactly bound and reacquire admission for every later producer unit."));
            }

            return evidence;
        }

        private LazyFactoryResolution? ResolveLazyFactory(
            AuthoredMember member,
            ExpressionSyntax receiver)
        {
            ITypeSymbol? lazyType = member.Model.GetTypeInfo(receiver).Type;

            if (lazyType is null
                || TypeKey(lazyType) != "System.Lazy`1")
            {
                return null;
            }

            List<BaseObjectCreationExpressionSyntax> constructions = [];

            bool usesAtomicPublication = TryResolveAtomicLazyPublication(
                member,
                receiver,
                lazyType,
                out ISymbol? storage,
                out BaseObjectCreationExpressionSyntax? atomicConstruction);

            if (usesAtomicPublication)
            {
                constructions.Add(atomicConstruction!);
            }
            else
            {
                storage = member.Model.GetSymbolInfo(
                    StripTransparentExpression(receiver)).Symbol;

                if (storage is not IFieldSymbol and not ILocalSymbol
                    and not IPropertySymbol)
                {
                    return null;
                }

                CollectLazyConstructions(
                    receiver,
                    member.Model,
                    lazyType,
                    constructions,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default));
            }

            BaseObjectCreationExpressionSyntax[] exactConstructions = constructions
                .DistinctBy(static construction =>
                    (construction.SyntaxTree, construction.SpanStart))
                .ToArray();

            if (exactConstructions is not
                [BaseObjectCreationExpressionSyntax construction])
            {
                return null;
            }

            SemanticModel model = semanticModels[construction.SyntaxTree];

            if (model.GetOperation(construction) is not
                IObjectCreationOperation operation)
            {
                return null;
            }

            IArgumentOperation[] factoryArguments = operation.Arguments
                .Where(static argument => argument.Parameter?.Type.TypeKind
                    == TypeKind.Delegate)
                .ToArray();

            if (factoryArguments is not [IArgumentOperation factoryArgument]
                || (factoryArgument.Syntax is ArgumentSyntax argument
                        ? argument.Expression
                        : factoryArgument.Value.Syntax) is not
                    ExpressionSyntax factoryExpression)
            {
                return null;
            }

            bool contextual = member.Syntax.SyntaxTree == construction.SyntaxTree
                && member.Syntax.Span.Contains(construction.Span);

            AuthoredMember[] factories = ResolveCallableTargets(
                factoryExpression,
                model,
                contextual ? member.CallableBindings : null,
                contextual ? member.AbsentCallables : null);

            if (factories is not [AuthoredMember factory])
            {
                return null;
            }

            IArgumentOperation[] modeArguments = operation.Arguments
                .Where(static argument => argument.Parameter is { Type: { } type }
                    && TypeKey(type)
                        == "System.Threading.LazyThreadSafetyMode")
                .ToArray();

            bool usesExecutionAndPublication = modeArguments.Length switch
            {
                0 => operation.Constructor is { } constructor
                    && !constructor.Parameters.Any(static parameter =>
                        parameter.Type.SpecialType
                            == SpecialType.System_Boolean),
                1 => modeArguments[0].Value.ConstantValue is
                    {
                        HasValue: true,
                        Value: int value,
                    }
                    && value
                        == (int)System.Threading.LazyThreadSafetyMode
                            .ExecutionAndPublication,
                _ => false,
            };

            return new(
                storage!,
                construction,
                factory,
                usesExecutionAndPublication,
                usesAtomicPublication);
        }

        private static bool TryResolveAtomicLazyPublication(
            AuthoredMember member,
            ExpressionSyntax receiver,
            ITypeSymbol lazyType,
            out ISymbol? storage,
            out BaseObjectCreationExpressionSyntax? construction)
        {
            storage = null;

            construction = null;

            if (StripTransparentExpression(receiver) is not
                    InvocationExpressionSyntax call
                || member.Model.GetOperation(call) is not
                    IInvocationOperation operation
                || operation.TargetMethod.Name != "GetOrAdd"
                || TypeKey(operation.TargetMethod.ContainingType)
                    != "System.Collections.Concurrent.ConcurrentDictionary`2"
                || !SymbolEqualityComparer.Default.Equals(
                    operation.TargetMethod.ReturnType,
                    lazyType))
            {
                return false;
            }

            IArgumentOperation[] factories = operation.Arguments
                .Where(static argument => argument.Parameter?.Type.TypeKind
                    == TypeKind.Delegate)
                .ToArray();

            if (factories is not [IArgumentOperation factoryArgument]
                || (factoryArgument.Syntax is ArgumentSyntax argument
                        ? argument.Expression
                        : factoryArgument.Value.Syntax) is not
                    ExpressionSyntax factoryExpression)
            {
                return false;
            }

            factoryExpression = StripTransparentExpression(factoryExpression);

            construction = factoryExpression switch
            {
                SimpleLambdaExpressionSyntax
                {
                    Body: BaseObjectCreationExpressionSyntax created,
                } => created,
                ParenthesizedLambdaExpressionSyntax
                {
                    Body: BaseObjectCreationExpressionSyntax created,
                } => created,
                _ => null,
            };

            if (construction is null
                || member.Model.GetTypeInfo(construction).Type is not
                    { } constructedType
                || !SymbolEqualityComparer.Default.Equals(
                    constructedType,
                    lazyType)
                || member.Model.GetSymbolInfo(factoryExpression).Symbol is not
                    IMethodSymbol factorySymbol)
            {
                construction = null;

                return false;
            }

            storage = factorySymbol;

            return true;
        }

        private static bool IsProvenCreatedLazyRead(
            AuthoredMember member,
            MemberAccessExpressionSyntax lazyValue)
        {
            ISymbol? storage = member.Model.GetSymbolInfo(
                StripTransparentExpression(lazyValue.Expression)).Symbol;

            if (storage is null)
            {
                return false;
            }

            bool ProvesCreated(ExpressionSyntax condition, bool whenTrue) =>
                condition switch
                {
                    ParenthesizedExpressionSyntax parentheses =>
                        ProvesCreated(parentheses.Expression, whenTrue),
                    PrefixUnaryExpressionSyntax negation
                        when negation.IsKind(SyntaxKind.LogicalNotExpression) =>
                        ProvesCreated(negation.Operand, !whenTrue),
                    BinaryExpressionSyntax conjunction
                        when whenTrue
                            && conjunction.IsKind(
                                SyntaxKind.LogicalAndExpression) =>
                        ProvesCreated(conjunction.Left, true)
                            || ProvesCreated(conjunction.Right, true),
                    BinaryExpressionSyntax disjunction
                        when !whenTrue
                            && disjunction.IsKind(
                                SyntaxKind.LogicalOrExpression) =>
                        ProvesCreated(disjunction.Left, false)
                            || ProvesCreated(disjunction.Right, false),
                    MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "IsValueCreated",
                    } created
                        when whenTrue
                            && member.Model.GetSymbolInfo(created).Symbol is
                                IPropertySymbol
                                {
                                    ContainingType: { } containing,
                                }
                            && TypeKey(containing) == "System.Lazy`1"
                            && SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(
                                    StripTransparentExpression(
                                        created.Expression)).Symbol,
                                storage) => true,
                    _ => false,
                };

            foreach (IfStatementSyntax guard in lazyValue.Ancestors()
                .OfType<IfStatementSyntax>())
            {
                if (guard.Statement.Span.Contains(lazyValue.Span)
                        && ProvesCreated(guard.Condition, true)
                    || guard.Else?.Statement.Span.Contains(lazyValue.Span) == true
                        && ProvesCreated(guard.Condition, false))
                {
                    return true;
                }
            }

            StatementSyntax? useStatement = lazyValue.Ancestors()
                .OfType<StatementSyntax>()
                .FirstOrDefault();

            if (useStatement?.Parent is not BlockSyntax block)
            {
                return false;
            }

            foreach (IfStatementSyntax guard in block.Statements
                .OfType<IfStatementSyntax>()
                .Where(guard => guard.Span.End < useStatement.SpanStart
                    && guard.Else is null
                    && FailureTerminates(guard)
                    && ProvesCreated(guard.Condition, false)))
            {
                bool reassigned = block.DescendantNodes()
                    .Where(node => node.SpanStart > guard.Span.End
                        && node.Span.End < lazyValue.SpanStart)
                    .Any(node => node switch
                    {
                        AssignmentExpressionSyntax assignment =>
                            SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(
                                    assignment.Left).Symbol,
                                storage),
                        ArgumentSyntax argument
                            when argument.RefKindKeyword.Kind() is
                                SyntaxKind.RefKeyword or SyntaxKind.OutKeyword =>
                            SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(
                                    argument.Expression).Symbol,
                                storage),
                        _ => false,
                    });

                if (!reassigned)
                {
                    return true;
                }
            }

            return false;
        }

        private BoundedDataProof ProveBoundedLazyData(
            LazyFactoryResolution resolution)
        {
            if (boundedLazyData.TryGetValue(
                    resolution.Storage,
                    out BoundedDataProof cached))
            {
                return cached;
            }

            if (!activeBoundedLazyData.Add(resolution.Storage))
            {
                return BoundedDataProof.Unknown;
            }

            BoundedDataProof proof;

            ISymbol? previousProbe = activeBoundedDataProbe;

            activeBoundedDataProbe = resolution.Storage;

            boundedLazyDataEvidence[resolution.Storage] = [];

            try
            {
                BoundedDataProof structure = InspectBoundedDataStructure(
                    resolution.Factory,
                    new HashSet<GraphMemberIdentity>());

                proof = structure == BoundedDataProof.PureBoundedData
                    ? ProbeBoundedDataTraversal(resolution)
                    : structure;

                if (proof == BoundedDataProof.PureBoundedData
                    && (!resolution.UsesExecutionAndPublication
                        || !resolution.UsesAtomicPublication
                            && !HasStableLazyStorage(resolution)))
                {
                    proof = BoundedDataProof.Unknown;
                }
            }
            finally
            {
                activeBoundedDataProbe = previousProbe;

                activeBoundedLazyData.Remove(resolution.Storage);
            }

            boundedLazyData[resolution.Storage] = proof;

            return proof;
        }

        private bool HasStableLazyStorage(LazyFactoryResolution resolution)
        {
            bool exactInitializer = resolution.Storage
                .DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() is VariableDeclaratorSyntax
                    {
                        Initializer.Value: { } initializer,
                    }
                    && initializer.Span.Contains(
                        resolution.Construction.Span));

            bool stableKind = resolution.Storage is ILocalSymbol
                || resolution.Storage is IFieldSymbol
                {
                    IsStatic: true,
                    IsReadOnly: true,
                };

            if (!exactInitializer || !stableKind)
            {
                return false;
            }

            return !AuthoredMembers.Any(owner => owner.Syntax
                .DescendantNodesAndSelf()
                .Any(node => node switch
                {
                    AssignmentExpressionSyntax assignment =>
                        SymbolEqualityComparer.Default.Equals(
                            owner.Model.GetSymbolInfo(assignment.Left).Symbol,
                            resolution.Storage),
                    ArgumentSyntax argument
                        when argument.RefKindKeyword.Kind() is
                            SyntaxKind.RefKeyword or SyntaxKind.OutKeyword =>
                        SymbolEqualityComparer.Default.Equals(
                            owner.Model.GetSymbolInfo(argument.Expression).Symbol,
                            resolution.Storage),
                    _ => false,
                }));
        }

        private BoundedDataProof ProbeBoundedDataTraversal(
            LazyFactoryResolution resolution)
        {
            int diagnosticStart = diagnostics.Count;

            HashSet<string> existingSites = [.. sites.Keys];

            HashSet<string> existingCapsules = [.. admissionCapsules.Keys];

            TraversalEvidence evidence;

            bool emittedDiagnostic;

            ISymbol? previousProbe = activeBoundedDataProbe;

            activeBoundedDataProbe = resolution.Storage;

            try
            {
                evidence = Traverse(
                    resolution.Factory,
                    "<bounded-data-proof>",
                    "<bounded-data-proof>:" + Location(resolution.Construction),
                    new HashSet<TraversalStateIdentity>(),
                    lifecycle: false,
                    completionOwned: true);

                emittedDiagnostic = diagnostics.Count != diagnosticStart;
            }
            finally
            {
                activeBoundedDataProbe = previousProbe;

                if (diagnostics.Count > diagnosticStart)
                {
                    diagnostics.RemoveRange(
                        diagnosticStart,
                        diagnostics.Count - diagnosticStart);
                }

                foreach (string site in sites.Keys
                    .Where(site => !existingSites.Contains(site))
                    .ToArray())
                {
                    sites.Remove(site);
                }

                foreach (string capsule in admissionCapsules.Keys
                    .Where(capsule => !existingCapsules.Contains(capsule))
                    .ToArray())
                {
                    admissionCapsules.Remove(capsule);
                }
            }

            if (evidence == TraversalEvidence.Evidence)
            {
                return BoundedDataProof.Producer;
            }

            return evidence == TraversalEvidence.Inconclusive
                    || emittedDiagnostic
                ? BoundedDataProof.Unknown
                : BoundedDataProof.PureBoundedData;
        }

        private BoundedDataProof InspectBoundedDataStructure(
            AuthoredMember member,
            HashSet<GraphMemberIdentity> path)
        {
            GraphMemberIdentity identity = MemberIdentity(member);

            if (!path.Add(identity))
            {
                return BoundedDataProof.Unknown;
            }

            try
            {
                if (member.Symbol.IsAsync
                    || IsDeferredDataCarrier(member.Symbol.ReturnType)
                    || IsIteratorMember(member))
                {
                    return RecordBoundedDataProducer(
                        "deferred-member:"
                            + Normalize(member.Symbol));
                }

                BoundedDataProof proof = BoundedDataProof.PureBoundedData;

                foreach (SyntaxNode node in member.Syntax.DescendantNodesAndSelf(
                    candidate => candidate == member.Syntax
                        || candidate is not AnonymousFunctionExpressionSyntax
                            and not LocalFunctionStatementSyntax))
                {
                    if (node is AssignmentExpressionSyntax
                            or PrefixUnaryExpressionSyntax
                            or PostfixUnaryExpressionSyntax
                        && WritesSharedState(member, node)
                        || node is ArgumentSyntax refArgument
                            && refArgument.RefKindKeyword.Kind() is
                                SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                            && member.Model.GetSymbolInfo(
                                refArgument.Expression).Symbol is IFieldSymbol)
                    {
                        return RecordBoundedDataProducer(
                            "shared-mutation@" + Location(node));
                    }

                    if (node is InvocationExpressionSyntax call
                        && member.Model.GetOperation(call) is
                            IDynamicInvocationOperation)
                    {
                        return BoundedDataProof.Unknown;
                    }

                    if (node is not InvocationExpressionSyntax
                            and not BaseObjectCreationExpressionSyntax
                        || member.Model.GetSymbolInfo(node).Symbol is not
                            IMethodSymbol method)
                    {
                        continue;
                    }

                    string callee = Normalize(method);

                    if (IsDetachedBoundedDataCarrier(method, callee))
                    {
                        return RecordBoundedDataProducer(
                            "detached-call:" + callee + "@" + Location(node));
                    }

                    if (node is InvocationExpressionSyntax invocation)
                    {
                        if ((IsAsynchronousCallback(method)
                                || CompletionOwnedExternalAwaitables.Contains(
                                    callee))
                            && CompletionPoint(member, invocation, false) is null
                            || IsKnownLazyCallback(method)
                                && LazyCompletionPoint(member, invocation) is null)
                        {
                            return RecordBoundedDataProducer(
                                "unjoined-call:" + callee + "@" + Location(node));
                        }
                    }

                    ExpressionSyntax[] callbacks = node switch
                    {
                        InvocationExpressionSyntax callbackCall
                            when method.MethodKind
                                == MethodKind.DelegateInvoke =>
                            [DelegateReceiver(member, callbackCall)],
                        InvocationExpressionSyntax callbackCall =>
                            callbackCall.ArgumentList.Arguments
                                .Where(argument =>
                                    !argument.RefKindKeyword.IsKind(
                                        SyntaxKind.OutKeyword)
                                    && IsDelegateExpression(
                                        member.Model,
                                        argument.Expression))
                                .Select(static argument => argument.Expression)
                                .ToArray(),
                        BaseObjectCreationExpressionSyntax creation
                            when creation.ArgumentList is not null =>
                            creation.ArgumentList.Arguments
                                .Where(argument =>
                                    !argument.RefKindKeyword.IsKind(
                                        SyntaxKind.OutKeyword)
                                    && IsDelegateExpression(
                                        member.Model,
                                        argument.Expression))
                                .Select(static argument => argument.Expression)
                                .ToArray(),
                        _ => [],
                    };

                    if (callee != "System.Lazy`1..ctor")
                    {
                        foreach (ExpressionSyntax callback in callbacks)
                        {
                            AuthoredMember[] bodies = ResolveCallableTargets(
                                callback,
                                member.Model,
                                member.CallableBindings,
                                member.AbsentCallables);

                            if (bodies.Length == 0)
                            {
                                if (!IsEffectFreeExternalCallable(
                                        callback,
                                        member))
                                {
                                    proof = MergeBoundedDataProof(
                                        proof,
                                        BoundedDataProof.Unknown);
                                }

                                continue;
                            }

                            foreach (AuthoredMember body in bodies)
                            {
                                proof = MergeBoundedDataProof(
                                    proof,
                                    InspectBoundedDataStructure(
                                        body,
                                        path));
                            }
                        }
                    }

                    AuthoredMember? target = ResolveInvocationTarget(
                        method,
                        member,
                        node);

                    AuthoredMember[] boundTargets = target is null
                        ? ResolveBoundImplementations(
                            method,
                            member.Model.Compilation)
                        : [];

                    AuthoredMember[] strategyTargets = ResolveStrategyTargets(
                        method);

                    AuthoredMember[] reviewedClosedTargets =
                        ResolveReviewedClosedDispatchTargets(method, node);

                    AuthoredMember[] targets = strategyTargets.Length != 0
                        ? strategyTargets
                        : target is not null
                            ? [target]
                            : boundTargets.Length != 0
                                ? boundTargets
                                : reviewedClosedTargets;

                    foreach (AuthoredMember exactTarget in targets)
                    {
                        AuthoredMember boundTarget = node switch
                        {
                            InvocationExpressionSyntax boundCall =>
                                BindAdmissionArguments(
                                    member,
                                    boundCall,
                                    exactTarget),
                            BaseObjectCreationExpressionSyntax creation =>
                                BindConstructorArguments(
                                    member,
                                    creation,
                                    exactTarget),
                            _ => exactTarget,
                        };

                        proof = MergeBoundedDataProof(
                            proof,
                            InspectBoundedDataStructure(boundTarget, path));
                    }

                    if (proof == BoundedDataProof.Producer)
                    {
                        RecordBoundedDataProducer(
                            "producer-target:" + callee + "@" + Location(node));

                        return proof;
                    }
                }

                foreach (MemberAccessExpressionSyntax lazyValue in member.Syntax
                    .DescendantNodesAndSelf(candidate =>
                        candidate == member.Syntax
                            || candidate is not AnonymousFunctionExpressionSyntax
                                and not LocalFunctionStatementSyntax)
                    .OfType<MemberAccessExpressionSyntax>())
                {
                    if (member.Model.GetSymbolInfo(lazyValue).Symbol is not
                            IPropertySymbol
                            {
                                Name: "Value",
                                ContainingType: { } lazy,
                            }
                        || TypeKey(lazy) != "System.Lazy`1")
                    {
                        continue;
                    }

                    LazyFactoryResolution? nested = ResolveLazyFactory(
                        member,
                        lazyValue.Expression);

                    proof = MergeBoundedDataProof(
                        proof,
                        nested is null
                            ? BoundedDataProof.Unknown
                            : ProveBoundedLazyData(nested));
                }

                return proof;
            }
            finally
            {
                path.Remove(identity);
            }
        }

        private BoundedDataProof RecordBoundedDataProducer(string evidence)
        {
            if (activeBoundedDataProbe is { } storage)
            {
                boundedLazyDataEvidence
                    .GetValueOrDefault(storage)?
                    .Add(evidence);
            }

            return BoundedDataProof.Producer;
        }

        private bool HasEscapingDeferredWork(
            AuthoredMember member,
            HashSet<GraphMemberIdentity> path,
            bool root = true)
        {
            GraphMemberIdentity identity = MemberIdentity(member);

            if (!path.Add(identity))
            {
                return true;
            }

            try
            {
                if (root && (member.Symbol.IsAsync
                    || IsDeferredDataCarrier(member.Symbol.ReturnType)
                    || IsIteratorMember(member)))
                {
                    return true;
                }

                foreach (InvocationExpressionSyntax call in member.Syntax
                    .DescendantNodesAndSelf(candidate =>
                        candidate == member.Syntax
                            || candidate is not AnonymousFunctionExpressionSyntax
                                and not LocalFunctionStatementSyntax)
                    .OfType<InvocationExpressionSyntax>())
                {
                    if (member.Model.GetSymbolInfo(call).Symbol is not
                        IMethodSymbol method)
                    {
                        return true;
                    }

                    string callee = Normalize(method);

                    if (IsDetachedBoundedDataCarrier(method, callee)
                        || (IsAsynchronousCallback(method)
                                || CompletionOwnedExternalAwaitables.Contains(
                                    callee))
                            && CompletionPoint(member, call, false) is null
                        || IsKnownLazyCallback(method)
                            && LazyCompletionPoint(member, call) is null)
                    {
                        return true;
                    }

                    AuthoredMember? target = ResolveInvocationTarget(
                        method,
                        member,
                        call);

                    if (target is not null
                        && HasEscapingDeferredWork(
                            BindAdmissionArguments(member, call, target),
                            path,
                            root: false))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                path.Remove(identity);
            }
        }

        private static bool IsDeferredDataCarrier(ITypeSymbol type) =>
            IsAwaitable(type)
            || IsLazySequence(type)
            || TypeKey(type) is
                "System.Collections.Generic.IAsyncEnumerable`1"
                    or "System.Lazy`1";

        private static bool IsAsynchronousCallback(IMethodSymbol method) =>
            Normalize(method) is
                "System.Threading.Tasks.Task.Run"
                    or "System.Threading.Tasks.TaskFactory.StartNew"
                    or "System.Threading.Tasks.Task.ContinueWith"
                    or "System.Threading.Tasks.Task`1.ContinueWith"
                    or "System.Threading.Tasks.Parallel.ForEachAsync";

        private static bool IsDetachedBoundedDataCarrier(
            IMethodSymbol method,
            string callee) =>
            DetachedExternalCallbackMethods.Contains(callee)
            || TypeKey(method.ContainingType) is
                "System.IO.FileSystemWatcher"
                    or "System.Threading.Timer"
                    or "System.Threading.PeriodicTimer"
                    or "System.Timers.Timer";

        private static BoundedDataProof MergeBoundedDataProof(
            BoundedDataProof left,
            BoundedDataProof right)
        {
            if (left == BoundedDataProof.Producer
                || right == BoundedDataProof.Producer)
            {
                return BoundedDataProof.Producer;
            }

            return left == BoundedDataProof.Unknown
                    || right == BoundedDataProof.Unknown
                ? BoundedDataProof.Unknown
                : BoundedDataProof.PureBoundedData;
        }

        private static bool WritesSharedState(
            AuthoredMember member,
            SyntaxNode node)
        {
            ExpressionSyntax? target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                        || prefix.IsKind(SyntaxKind.PreDecrementExpression) =>
                    prefix.Operand,
                PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                        || postfix.IsKind(SyntaxKind.PostDecrementExpression) =>
                    postfix.Operand,
                _ => null,
            };

            if (target is null)
            {
                return false;
            }

            if (node is AssignmentExpressionSyntax initializerAssignment
                && initializerAssignment.Ancestors()
                    .TakeWhile(ancestor => ancestor != member.Syntax)
                    .OfType<InitializerExpressionSyntax>()
                    .Any(initializer => initializer.Parent is
                        BaseObjectCreationExpressionSyntax
                            or WithExpressionSyntax))
            {
                return false;
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(target).Symbol
                ?? member.Model.GetOperation(target) switch
                {
                    IFieldReferenceOperation field => field.Field,
                    IPropertyReferenceOperation property => property.Property,
                    IEventReferenceOperation declaredEvent => declaredEvent.Event,
                    _ => null,
                };

            if (symbol is not IFieldSymbol and not IPropertySymbol
                and not IEventSymbol)
            {
                return false;
            }

            if (symbol.IsStatic)
            {
                return true;
            }

            ExpressionSyntax? receiver = target switch
            {
                MemberAccessExpressionSyntax access => access.Expression,
                ElementAccessExpressionSyntax element => element.Expression,
                _ => null,
            };

            if (receiver is not null
                && HasLocallyOwnedMutationReceiver(
                    member,
                    receiver,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default)))
            {
                return false;
            }

            return member.Symbol.MethodKind != MethodKind.Constructor
                || !SymbolEqualityComparer.Default.Equals(
                    symbol.ContainingType,
                    member.Symbol.ContainingType);
        }

        private static bool HasLocallyOwnedMutationReceiver(
            AuthoredMember member,
            ExpressionSyntax expression,
            HashSet<ISymbol> path)
        {
            expression = StripTransparentExpression(expression);

            if (expression is BaseObjectCreationExpressionSyntax
                    or ArrayCreationExpressionSyntax
                    or ImplicitArrayCreationExpressionSyntax
                    or CollectionExpressionSyntax)
            {
                return true;
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            if (symbol is IParameterSymbol
                && member.ValueBindings?.TryGetValue(
                    symbol,
                    out BoundValueSource? binding) == true
                && binding is not null)
            {
                if (!path.Add(symbol))
                {
                    return false;
                }

                try
                {
                    return HasLocallyOwnedMutationReceiver(
                        binding.Caller,
                        binding.Expression,
                        path);
                }
                finally
                {
                    path.Remove(symbol);
                }
            }

            if (symbol is not ILocalSymbol local || !path.Add(local))
            {
                return false;
            }

            try
            {
                ExpressionSyntax[] sources = local.DeclaringSyntaxReferences
                    .Select(reference => reference.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .Select(static variable => variable.Initializer?.Value)
                    .OfType<ExpressionSyntax>()
                    .Concat(member.Syntax.DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(assignment => assignment.IsKind(
                                SyntaxKind.SimpleAssignmentExpression)
                            && assignment.SpanStart < expression.SpanStart
                            && SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(assignment.Left).Symbol,
                                local))
                        .Select(static assignment => assignment.Right))
                    .Where(source => member.Model.GetConstantValue(source) is not
                        {
                            HasValue: true,
                            Value: null,
                        })
                    .ToArray();

                return sources.Length != 0
                    && sources.All(source => HasLocallyOwnedMutationReceiver(
                        member,
                        source,
                        path));
            }
            finally
            {
                path.Remove(local);
            }
        }

        private void CollectLazyConstructions(
            ExpressionSyntax expression,
            SemanticModel model,
            ITypeSymbol lazyType,
            List<BaseObjectCreationExpressionSyntax> constructions,
            HashSet<ISymbol> path)
        {
            expression = expression switch
            {
                ParenthesizedExpressionSyntax parenthesized =>
                    parenthesized.Expression,
                CastExpressionSyntax cast => cast.Expression,
                PostfixUnaryExpressionSyntax suppressed
                    when suppressed.IsKind(
                        SyntaxKind.SuppressNullableWarningExpression) =>
                    suppressed.Operand,
                _ => expression,
            };

            if (expression is BaseObjectCreationExpressionSyntax creation
                && SymbolEqualityComparer.Default.Equals(
                    model.GetTypeInfo(creation).Type,
                    lazyType))
            {
                constructions.Add(creation);

                return;
            }

            if (expression is ConditionalExpressionSyntax conditional)
            {
                CollectLazyConstructions(
                    conditional.WhenTrue,
                    model,
                    lazyType,
                    constructions,
                    path);

                CollectLazyConstructions(
                    conditional.WhenFalse,
                    model,
                    lazyType,
                    constructions,
                    path);

                return;
            }

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is null || !path.Add(symbol))
            {
                return;
            }

            try
            {
                foreach (SyntaxReference reference in
                    symbol.DeclaringSyntaxReferences)
                {
                    ExpressionSyntax? initializer = reference.GetSyntax() switch
                    {
                        VariableDeclaratorSyntax variable =>
                            variable.Initializer?.Value,
                        PropertyDeclarationSyntax property =>
                            property.Initializer?.Value
                                ?? property.ExpressionBody?.Expression,
                        _ => null,
                    };

                    if (initializer is not null)
                    {
                        CollectLazyConstructions(
                            initializer,
                            semanticModels[initializer.SyntaxTree],
                            lazyType,
                            constructions,
                            path);
                    }
                }
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private bool HasOwnedLongLivedReturn(
            AuthoredMember member,
            InvocationExpressionSyntax call)
        {
            ISymbol? carrier = call.Parent switch
            {
                AssignmentExpressionSyntax assignment
                    when assignment.Right == call =>
                    member.Model.GetSymbolInfo(assignment.Left).Symbol,
                EqualsValueClauseSyntax
                    {
                        Parent: VariableDeclaratorSyntax variable,
                    } when variable.Initializer?.Value == call =>
                    member.Model.GetDeclaredSymbol(variable),
                _ => null,
            };

            if (carrier is IFieldSymbol field
                && IsWorkspaceWatcherType(field.Type))
            {
                return LifecycleReachesWatcherDispose(
                    field.ContainingType);
            }

            if (carrier is not ILocalSymbol local
                || !IsWorkspaceWatcherType(local.Type))
            {
                return false;
            }

            TryStatementSyntax? guarded = call.Ancestors()
                .OfType<TryStatementSyntax>()
                .FirstOrDefault(candidate => candidate.Finally is not null);

            bool fallbackDispose = guarded?.Finally?.Block.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(disposal =>
                    member.Model.GetSymbolInfo(disposal).Symbol
                        is IMethodSymbol { Name: "Dispose" }
                    && AdmissionReceiver(member, disposal) is { } receiver
                    && SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(receiver).Symbol,
                        local)) == true;

            bool transferred = member.Syntax.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(assignment =>
                    assignment.SpanStart > call.Span.End
                    && SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(assignment.Right).Symbol,
                        local)
                    && member.Model.GetTypeInfo(assignment.Left).Type is { } type
                    && IsWorkspaceWatcherType(type));

            return fallbackDispose
                && transferred
                && LifecycleReachesWatcherDispose(
                    member.Symbol.ContainingType);
        }

        private bool HasOwnedFileSystemWatcherLifetime(
            AuthoredMember member,
            ExpressionSyntax receiver)
        {
            ISymbol? carrier = member.Model.GetSymbolInfo(receiver).Symbol;

            if (carrier is not IFieldSymbol
                {
                    IsReadOnly: true,
                    Type: { } fieldType,
                } field
                || TypeKey(fieldType) != "System.IO.FileSystemWatcher"
                || !SymbolEqualityComparer.Default.Equals(
                    field.ContainingType,
                    member.Symbol.ContainingType))
            {
                return false;
            }

            AuthoredMember[] owners = AuthoredMembers
                .Where(candidate => SymbolEqualityComparer.Default.Equals(
                    candidate.Symbol.ContainingType,
                    field.ContainingType))
                .ToArray();

            AssignmentExpressionSyntax[] assignments = owners
                .SelectMany(owner => owner.Syntax.DescendantNodesAndSelf()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => SymbolEqualityComparer.Default.Equals(
                        owner.Model.GetSymbolInfo(assignment.Left).Symbol,
                        field)))
                .ToArray();

            bool oneConstruction = assignments is [AssignmentExpressionSyntax construction]
                && construction.Right is BaseObjectCreationExpressionSyntax creation
                && member.Model.GetTypeInfo(creation).Type is { } createdType
                && TypeKey(createdType) == "System.IO.FileSystemWatcher";

            bool exactDisposal = owners
                .Where(static owner => owner.Symbol.Name == "Dispose")
                .SelectMany(static owner => owner.Syntax.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Select(call => (Owner: owner, Call: call)))
                .Count(candidate =>
                    candidate.Owner.Model.GetSymbolInfo(candidate.Call).Symbol
                        is IMethodSymbol { Name: "Dispose" }
                    && AdmissionReceiver(candidate.Owner, candidate.Call)
                        is { } disposalReceiver
                    && SymbolEqualityComparer.Default.Equals(
                        candidate.Owner.Model.GetSymbolInfo(disposalReceiver).Symbol,
                        field)) == 1;

            return oneConstruction && exactDisposal;
        }

        private static AuthoredMember WithoutInheritedAdmission(
            AuthoredMember member)
        {
            IReadOnlyDictionary<ISymbol, AuthoredMember>? detachedCallables =
                member.CallableBindings?.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value with
                    {
                        AdmissionBindings = null,
                        RecoveryBindings = null,
                        RecoveryContext = null,
                    },
                    SymbolEqualityComparer.Default);

            return member with
            {
                AdmissionBindings = null,
                CallableBindings = detachedCallables,
                RecoveryBindings = null,
                RecoveryContext = null,
            };
        }

        private bool LifecycleReachesWatcherDispose(INamedTypeSymbol ownerType)
        {
            AuthoredMember[] roots = AuthoredMembers
                .Where(member => SymbolEqualityComparer.Default.Equals(
                        member.Symbol.ContainingType,
                        ownerType)
                    && member.Symbol.Name is "StopAsync" or "Dispose" or "DisposeAsync")
                .ToArray();

            HashSet<GraphMemberIdentity> visited = [];

            bool Reaches(AuthoredMember current)
            {
                if (!visited.Add(MemberIdentity(current)))
                {
                    return false;
                }

                foreach (InvocationExpressionSyntax invocation in current.Syntax
                    .DescendantNodesAndSelf(node => node == current.Syntax
                        || node is not AnonymousFunctionExpressionSyntax
                            and not LocalFunctionStatementSyntax)
                    .OfType<InvocationExpressionSyntax>())
                {
                    if (current.Model.GetSymbolInfo(invocation).Symbol
                            is not IMethodSymbol method)
                    {
                        continue;
                    }

                    if (method.Name == "Dispose"
                        && AdmissionReceiver(current, invocation)
                            is { } receiver
                        && current.Model.GetTypeInfo(receiver).Type is { } receiverType
                        && IsWorkspaceWatcherType(receiverType))
                    {
                        return true;
                    }

                    if (Resolve(method, current.Model.Compilation) is { } target
                        && SymbolEqualityComparer.Default.Equals(
                            target.Symbol.ContainingType,
                            ownerType)
                        && Reaches(target))
                    {
                        return true;
                    }
                }

                return false;
            }

            return roots.Any(Reaches);
        }

        private static bool IsWorkspaceWatcherType(ITypeSymbol type) =>
            TypeKey(type)
                == "RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcher"
            || type is INamedTypeSymbol named
                && named.AllInterfaces.Any(static contract =>
                    TypeKey(contract)
                        == "RetroDownfall.Arcanum.Infrastructure.Hosting.IWorkspaceFileWatcher");

        private static bool IsKnownLazyCallback(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            string type = TypeKey(definition.ContainingType);

            return type == "System.Linq.Enumerable"
                    && definition.Name is "Append" or "Cast" or "Chunk" or "Concat" or "DefaultIfEmpty" or "Distinct" or "DistinctBy" or "Except" or "ExceptBy" or "GroupBy" or "GroupJoin" or "Intersect" or "IntersectBy" or "Join" or "OfType" or "Order" or "OrderBy" or "OrderByDescending" or "Prepend" or "Reverse" or "Select" or "SelectMany" or "Skip" or "SkipLast" or "SkipWhile" or "Take" or "TakeLast" or "TakeWhile" or "ThenBy" or "ThenByDescending" or "Union" or "UnionBy" or "Where" or "Zip"
                || type == "System.Linq.ImmutableArrayExtensions"
                    && definition.Name is "Select" or "Where";
        }

        private static bool IsLazySequence(ITypeSymbol type) => TypeKey(type) is "System.Collections.IEnumerable" or "System.Collections.Generic.IEnumerable`1";

        private static bool IsIteratorMember(AuthoredMember member) => member.Syntax.DescendantNodesAndSelf(node => node == member.Syntax || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).Any(static node => node is YieldStatementSyntax);

        private static bool IsLazyDirectoryEnumeration(string callee) => callee is "System.IO.Directory.EnumerateDirectories" or "System.IO.Directory.EnumerateFileSystemEntries" or "System.IO.Directory.EnumerateFiles";

        private static bool IsEagerEnumerationTerminal(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            if (TypeKey(definition.ContainingType) == "System.Collections.Generic.List`1"
                && definition.Name == "AddRange")
            {
                return true;
            }

            return TypeKey(definition.ContainingType) == "System.Linq.Enumerable"
                && definition.Name is "Aggregate" or "All" or "Any" or "Average" or "Contains" or "Count" or "ElementAt" or "ElementAtOrDefault" or "First" or "FirstOrDefault" or "Last" or "LastOrDefault" or "LongCount" or "Max" or "MaxBy" or "Min" or "MinBy" or "SequenceEqual" or "Single" or "SingleOrDefault" or "Sum" or "ToArray" or "ToDictionary" or "ToHashSet" or "ToList" or "ToLookup";
        }

        private SyntaxNode? LazyCompletionPoint(AuthoredMember member, InvocationExpressionSyntax source)
        {
            SyntaxNode? Consumption(SyntaxNode expression)
            {
                foreach (SyntaxNode ancestor in expression.AncestorsAndSelf())
                {
                    if (ancestor != expression && ancestor is (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                    {
                        return null;
                    }

                    if (ancestor is ForEachStatementSyntax loop && loop.Expression.Span.Contains(expression.Span)
                        || ancestor is ForEachVariableStatementSyntax variableLoop && variableLoop.Expression.Span.Contains(expression.Span))
                    {
                        return ancestor;
                    }

                    if (ancestor is InvocationExpressionSyntax terminal && terminal != source && member.Model.GetSymbolInfo(terminal).Symbol is IMethodSymbol method && IsEagerEnumerationTerminal(method))
                    {
                        return terminal;
                    }

                    if (ancestor is CollectionExpressionSyntax collection && collection.Elements.OfType<SpreadElementSyntax>().Any(spread => spread.Expression.Span.Contains(expression.Span)))
                    {
                        return collection;
                    }

                    if (ancestor == member.Syntax)
                    {
                        break;
                    }
                }

                return null;
            }

            if (Consumption(source) is { } direct)
            {
                return direct;
            }

            if (source.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault() is { } argument
                && argument.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax consumerCall }
                && member.Model.GetOperation(argument) is IArgumentOperation
                {
                    Parameter: { } suppliedParameter,
                }
                && member.Model.GetSymbolInfo(consumerCall).Symbol is IMethodSymbol consumerMethod
                && ResolveInvocationTarget(consumerMethod, member, consumerCall) is { } consumer
                && suppliedParameter.Ordinal < consumer.Symbol.Parameters.Length
                && ConsumesLazyParameterSynchronously(
                    consumer,
                    consumer.Symbol.Parameters[suppliedParameter.Ordinal]))
            {
                return consumerCall;
            }

            VariableDeclaratorSyntax? declaration = source.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault(variable => variable.Initializer?.Value.Span.Contains(source.Span) == true);

            if (declaration is null || member.Model.GetDeclaredSymbol(declaration) is not ILocalSymbol local || declaration.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is not { } block)
            {
                return null;
            }

            IdentifierNameSyntax[] uses = block.DescendantNodes().OfType<IdentifierNameSyntax>().Where(identifier => identifier.SpanStart > declaration.Span.End && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(identifier).Symbol, local)).ToArray();

            if (declaration.Initializer?.Value is InvocationExpressionSyntax enumeratorFactory
                && enumeratorFactory.Span.Contains(source.Span)
                && member.Model.GetSymbolInfo(enumeratorFactory).Symbol is IMethodSymbol { Name: "GetEnumerator", Parameters.Length: 0, ReturnType: { } enumeratorType }
                && TypeKey(enumeratorType) is "System.Collections.IEnumerator" or "System.Collections.Generic.IEnumerator`1"
                && declaration.Parent?.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } usingDeclaration
                && uses is [IdentifierNameSyntax enumeratorUse]
                && enumeratorUse.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MoveNext" } moveNextAccess
                && moveNextAccess.Parent is InvocationExpressionSyntax moveNext
                && member.Model.GetSymbolInfo(moveNext).Symbol is IMethodSymbol { Name: "MoveNext", Parameters.Length: 0, ReturnType.SpecialType: SpecialType.System_Boolean }
                && IsImmediateFollowingStatement(usingDeclaration, moveNext))
            {
                return moveNext;
            }

            return uses is [IdentifierNameSyntax use] ? Consumption(use) : null;
        }

        private static bool ConsumesLazyParameterSynchronously(
            AuthoredMember consumer,
            IParameterSymbol parameter)
        {
            if (IsAwaitable(consumer.Symbol.ReturnType)
                || IsLazySequence(consumer.Symbol.ReturnType)
                || IsIteratorMember(consumer))
            {
                return false;
            }

            IdentifierNameSyntax[] references = consumer.Syntax.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Where(reference => SymbolEqualityComparer.Default.Equals(
                    consumer.Model.GetSymbolInfo(reference).Symbol,
                    parameter))
                .ToArray();

            bool consumed = false;

            foreach (IdentifierNameSyntax reference in references)
            {
                if (reference.Ancestors()
                    .TakeWhile(ancestor => ancestor != consumer.Syntax)
                    .Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                {
                    return false;
                }

                if (reference.Ancestors().OfType<ForEachStatementSyntax>()
                    .Any(loop => loop.Expression.Span.Contains(reference.Span))
                    || reference.Ancestors().OfType<ForEachVariableStatementSyntax>()
                        .Any(loop => loop.Expression.Span.Contains(reference.Span)))
                {
                    consumed = true;

                    continue;
                }

                InvocationExpressionSyntax? call = reference.Ancestors()
                    .OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault();

                if (call is not null
                    && consumer.Model.GetOperation(call) is INameOfOperation)
                {
                    continue;
                }

                if (call is not null
                    && consumer.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && Normalize(method) == "System.ArgumentNullException.ThrowIfNull")
                {
                    continue;
                }

                if (call is not null
                    && consumer.Model.GetSymbolInfo(call).Symbol is IMethodSymbol terminal
                    && IsEagerEnumerationTerminal(terminal))
                {
                    consumed = true;

                    continue;
                }

                return false;
            }

            return consumed;
        }

        private static bool IsImmediateFollowingStatement(LocalDeclarationStatementSyntax declaration, InvocationExpressionSyntax invocation)
        {
            if (declaration.Parent is not BlockSyntax block || invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault() is not ExpressionStatementSyntax statement || statement.Parent != block)
            {
                return false;
            }

            int declarationIndex = block.Statements.IndexOf(declaration);

            return declarationIndex >= 0 && declarationIndex + 1 < block.Statements.Count && block.Statements[declarationIndex + 1] == statement;
        }

        private static ExpressionSyntax DelegateReceiver(AuthoredMember member, InvocationExpressionSyntax call)
        {
            if (member.Model.GetOperation(call) is IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver } && receiver != call)
            {
                return receiver;
            }

            if (call.Expression is MemberAccessExpressionSyntax access)
            {
                return access.Expression;
            }

            return call.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(conditional => conditional.WhenNotNull.Span.Contains(call.Span))?.Expression ?? call.Expression;
        }

        private bool IsEffectFreeExternalCallable(
            ExpressionSyntax expression,
            AuthoredMember member)
        {
            ClosedCallableValue value = ResolveContextualCallable(
                expression,
                member.Model,
                member.CallableBindings,
                member.AbsentCallables,
                new HashSet<ISymbol>(SymbolEqualityComparer.Default));

            return value.IsKnown
                && CallableTargets(value).Length == 0
                && value.EffectFreeExternal;
        }

        private bool IsEffectFreeExternalCallable(
            IMethodSymbol method,
            Compilation compilation) => Resolve(method, compilation) is null
                && !Vocabulary.ContainsKey(Normalize(method))
                && IsReviewedEffectFreeExternalMember(method);

        private bool RequiresExternalBoundaryClassification(
            IMethodSymbol method,
            Compilation compilation,
            SyntaxNode? node = null,
            SemanticModel? model = null,
            AuthoredMember? context = null)
        {
            string callee = Normalize(method);

            if (Vocabulary.ContainsKey(callee)
                || PureSensitiveMethods.Contains(callee)
                || ReviewedInProcessSynchronizationMembers.Contains(callee)
                || ReviewedProcessRuntimeInitializationMembers.Contains(callee)
                || IsReviewedEnumeratorMethod(method, node, model, context)
                || IsReviewedInMemoryStreamReaderMember(
                    method,
                    node,
                    model,
                    context)
                || IsReviewedEffectFreeExternalMember(
                    method,
                    node,
                    model))
            {
                return false;
            }

            if (SensitiveBoundaryTypes.Contains(TypeKey(method.ContainingType)))
            {
                return Resolve(method, compilation) is null;
            }

            return Resolve(method, compilation) is null
                && method.Locations.All(static location => !location.IsInSource)
                && !method.ContainingNamespace.ToDisplayString()
                    .StartsWith("RetroDownfall.", StringComparison.Ordinal);
        }

        private bool RequiresExternalBoundaryClassification(
            IPropertySymbol property,
            Compilation compilation,
            bool mutation,
            SyntaxNode? node = null,
            SemanticModel? model = null,
            AuthoredMember? context = null)
        {
            string callee = Normalize(property);

            if (Vocabulary.ContainsKey(callee)
                || IsPureFileSystemMetadataProperty(property)
                || IsReviewedEnumeratorProperty(
                    property,
                    mutation,
                    node,
                    model,
                    context)
                || IsReviewedNonProducerExternalMember(property, mutation))
            {
                return false;
            }

            IMethodSymbol[] accessors = new IMethodSymbol?[]
                {
                    property.GetMethod,
                    property.SetMethod,
                }
                .OfType<IMethodSymbol>()
                .ToArray();

            if (SensitiveBoundaryTypes.Contains(TypeKey(property.ContainingType)))
            {
                return accessors.All(accessor => Resolve(accessor, compilation) is null);
            }

            return accessors.Length != 0
                && accessors.All(accessor => Resolve(accessor, compilation) is null
                    && accessor.Locations.All(static location => !location.IsInSource))
                && !property.ContainingNamespace.ToDisplayString()
                    .StartsWith("RetroDownfall.", StringComparison.Ordinal);
        }

        private bool IsReviewedEffectFreeExternalMember(
            IMethodSymbol method,
            SyntaxNode? node = null,
            SemanticModel? model = null)
        {
            if (CompletionOwnedExternalAwaitables.Contains(Normalize(method))
                || ReviewedBoundedIntrinsicMembers.Contains(Normalize(method))
                || ReviewedNonProducerExternalMembers.Contains(Normalize(method))
                || IsReviewedDatabaseSupportMember(method)
                || IsReviewedCollectionMechanicalMember(method)
                || IsReviewedInterpolatedStringCreate(method)
                || IsReviewedSequenceOperation(method, node, model)
                || IsReviewedInMemoryOrderingOperation(method, node, model))
            {
                return true;
            }

            if (!ReviewedIntrinsicExternalTypes.Contains(
                    TypeKey(method.ContainingType))
                || method.Parameters.Any(static parameter =>
                    parameter.RefKind != RefKind.None
                    || parameter.Type.TypeKind == TypeKind.Delegate))
            {
                return false;
            }

            return HasOnlyReviewedComparerArguments(
                method,
                node,
                model);
        }

        private static bool IsReviewedInterpolatedStringCreate(
            IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            return definition.IsStatic
                && definition.Name == "Create"
                && TypeKey(definition.ContainingType) == "System.String"
                && definition.ReturnType.SpecialType == SpecialType.System_String
                && definition.Parameters is
                [
                    IParameterSymbol
                    {
                        RefKind: RefKind.None,
                        Type: { } provider,
                    },
                    IParameterSymbol
                    {
                        RefKind: RefKind.Ref,
                        Type: { } handler,
                    },
                ]
                && TypeKey(provider) == "System.IFormatProvider"
                && TypeKey(handler)
                    == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler";
        }

        private bool IsReviewedSequenceOperation(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            string type = TypeKey(definition.ContainingType);

            if (!(type == "System.Linq.Enumerable"
                    && definition.Name is "Any" or "Contains" or "Count" or "First" or "FirstOrDefault" or "Last" or "SequenceEqual" or "Single" or "Sum" or "ToHashSet"
                || type == "System.Linq.ImmutableArrayExtensions"
                    && definition.Name is "FirstOrDefault" or "SequenceEqual"))
            {
                return false;
            }

            return !definition.Parameters.Any(static parameter =>
                    parameter.RefKind != RefKind.None
                    || parameter.Type.TypeKind == TypeKind.Delegate)
                && HasOnlyReviewedComparerArguments(
                    method,
                    node,
                    model);
        }

        private bool IsReviewedInMemoryOrderingOperation(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            string type = TypeKey(definition.ContainingType);

            if (!(type == "System.Array"
                    && definition.Name == "Sort"
                || type == "System.Collections.Generic.List`1"
                    && definition.Name == "Sort"))
            {
                return false;
            }

            return !definition.Parameters.Any(static parameter =>
                    parameter.RefKind != RefKind.None
                    || parameter.Type.TypeKind == TypeKind.Delegate)
                && HasOnlyReviewedComparerArguments(
                    method,
                    node,
                    model);
        }

        private bool IsReviewedEnumeratorMethod(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context = null)
        {
            if (node is not InvocationExpressionSyntax call
                || model is null
                || model.GetOperation(call) is not IInvocationOperation
                {
                    Instance.Syntax: ExpressionSyntax receiver,
                })
            {
                return false;
            }

            string type = TypeKey(method.ContainingType);

            if (method.Name == "GetEnumerator"
                && type is "System.Collections.Generic.IEnumerable`1"
                    or "System.Collections.IEnumerable"
                || method.Name == "GetAsyncEnumerator"
                    && type == "System.Collections.Generic.IAsyncEnumerable`1")
            {
                return HasReviewedSequenceProvenance(
                    receiver,
                    model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default),
                    context);
            }

            if (method.Name is "MoveNext" or "MoveNextAsync"
                && type is "System.Collections.Generic.IEnumerator`1"
                    or "System.Collections.IEnumerator"
                    or "System.Collections.Generic.IAsyncEnumerator`1")
            {
                return HasReviewedEnumeratorProvenance(
                    receiver,
                    model,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default),
                    context);
            }

            return false;
        }

        private bool IsReviewedInMemoryStreamReaderMember(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context = null)
        {
            if (TypeKey(method.ContainingType) != "System.IO.StreamReader"
                || model is null)
            {
                return false;
            }

            if (method.Name == ".ctor"
                && node is BaseObjectCreationExpressionSyntax creation
                && model.GetOperation(creation) is IObjectCreationOperation operation)
            {
                IArgumentOperation? streamArgument = operation.Arguments
                    .SingleOrDefault(static argument =>
                        argument.Parameter?.Ordinal == 0
                        && TypeKey(argument.Parameter.Type)
                            == "System.IO.Stream");

                ExpressionSyntax? stream = streamArgument?.Syntax switch
                {
                    ArgumentSyntax argument => argument.Expression,
                    ExpressionSyntax expression => expression,
                    _ => streamArgument?.Value.Syntax as ExpressionSyntax,
                };

                return stream is not null
                    && HasReviewedInMemoryStreamProvenance(
                        stream,
                        model,
                        new HashSet<ISymbol>(
                            SymbolEqualityComparer.Default),
                        context);
            }

            if (!method.Name.StartsWith("Read", StringComparison.Ordinal)
                || node is not InvocationExpressionSyntax call
                || model.GetOperation(call) is not IInvocationOperation
                {
                    Instance.Syntax: ExpressionSyntax receiver,
                })
            {
                return false;
            }

            return HasReviewedInMemoryStreamReaderProvenance(
                receiver,
                model,
                new HashSet<ISymbol>(SymbolEqualityComparer.Default),
                context);
        }

        private bool HasReviewedInMemoryStreamReaderProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            if (expression is BaseObjectCreationExpressionSyntax creation
                && model.GetTypeInfo(creation).Type is { } createdType
                && TypeKey(createdType) == "System.IO.StreamReader"
                && creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression
                    is { } stream)
            {
                return HasReviewedInMemoryStreamProvenance(
                    stream,
                    model,
                    path,
                    context);
            }

            return HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                HasReviewedInMemoryStreamReaderProvenance);
        }

        private bool HasReviewedInMemoryStreamProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            if (expression is BaseObjectCreationExpressionSyntax creation
                && model.GetTypeInfo(creation).Type is { } createdType
                && TypeKey(createdType) == "System.IO.MemoryStream")
            {
                return true;
            }

            if (expression is InvocationExpressionSyntax call
                && model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                && Normalize(method)
                    == "System.Reflection.Assembly.GetManifestResourceStream")
            {
                return true;
            }

            return HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                HasReviewedInMemoryStreamProvenance);
        }

        private bool IsReviewedEnumeratorProperty(
            IPropertySymbol property,
            bool mutation,
            SyntaxNode? node,
            SemanticModel? model,
            AuthoredMember? context = null)
        {
            if (mutation
                || property.Name != "Current"
                || TypeKey(property.ContainingType) is not
                    ("System.Collections.Generic.IEnumerator`1"
                        or "System.Collections.IEnumerator"
                        or "System.Collections.Generic.IAsyncEnumerator`1")
                || node is not MemberAccessExpressionSyntax access
                || model is null)
            {
                return false;
            }

            return HasReviewedEnumeratorProvenance(
                access.Expression,
                model,
                new HashSet<ISymbol>(SymbolEqualityComparer.Default),
                context);
        }

        private bool HasReviewedEnumeratorProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            if (expression is InvocationExpressionSyntax factory
                && model.GetSymbolInfo(factory).Symbol is IMethodSymbol method
                && method.Name is "GetEnumerator" or "GetAsyncEnumerator"
                && model.GetOperation(factory) is IInvocationOperation
                {
                    Instance.Syntax: ExpressionSyntax sequence,
                })
            {
                return HasReviewedSequenceProvenance(
                    sequence,
                    model,
                    path,
                    context);
            }

            return HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                HasReviewedEnumeratorProvenance);
        }

        private bool HasReviewedSequenceProvenance(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context)
        {
            expression = StripTransparentExpression(expression);

            ITypeSymbol? expressionType = model.GetTypeInfo(expression).Type;

            if (expressionType is IArrayTypeSymbol
                || expressionType is not null
                    && ReviewedInMemoryEnumerableTypes.Contains(
                        TypeKey(expressionType)))
            {
                return true;
            }

            if (expression is InvocationExpressionSyntax call
                && model.GetSymbolInfo(call).Symbol is IMethodSymbol method)
            {
                string callee = Normalize(method);

                if (Vocabulary.ContainsKey(callee)
                    || Resolve(method, model.Compilation) is not null
                    || callee is
                        "System.Text.Json.JsonElement.EnumerateArray"
                            or "System.Text.Json.JsonElement.EnumerateObject"
                            or "System.Threading.Channels.ChannelReader`1.ReadAllAsync")
                {
                    return true;
                }

                if (IsKnownLazyCallback(method)
                    && call.ArgumentList.Arguments.FirstOrDefault()?.Expression
                        is { } source)
                {
                    return HasReviewedSequenceProvenance(
                        source,
                        model,
                        path,
                        context);
                }
            }

            return HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                HasReviewedSequenceProvenance);
        }

        private bool HasOnlyReviewedValueSources(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context,
            Func<ExpressionSyntax, SemanticModel, HashSet<ISymbol>, AuthoredMember?, bool>
                review)
        {
            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is IParameterSymbol
                && context?.ValueBindings?.TryGetValue(
                    symbol,
                    out BoundValueSource? bound) == true
                && bound is not null)
            {
                if (!path.Add(symbol))
                {
                    return false;
                }

                try
                {
                    return review(
                        bound.Expression,
                        bound.Caller.Model,
                        path,
                        bound.Caller);
                }
                finally
                {
                    path.Remove(symbol);
                }
            }

            if (symbol is not ILocalSymbol
                and not IFieldSymbol
                and not IPropertySymbol
                || !path.Add(symbol))
            {
                return false;
            }

            try
            {
                ExpressionSyntax[] sources = symbol.DeclaringSyntaxReferences
                    .Select(reference => reference.GetSyntax())
                    .Select(static syntax => syntax switch
                    {
                        VariableDeclaratorSyntax variable =>
                            variable.Initializer?.Value,
                        PropertyDeclarationSyntax property =>
                            property.Initializer?.Value
                                ?? property.ExpressionBody?.Expression,
                        _ => null,
                    })
                    .OfType<ExpressionSyntax>()
                    .ToArray();

                if (symbol is ILocalSymbol local)
                {
                    sources = sources
                        .Where(static source => source is not LiteralExpressionSyntax literal
                                || !literal.IsKind(SyntaxKind.NullLiteralExpression)
                            && !literal.IsKind(SyntaxKind.DefaultLiteralExpression))
                        .Where(static source => source is not DefaultExpressionSyntax)
                        .ToArray();

                    SyntaxNode root = model.SyntaxTree.GetRoot();

                    AssignmentExpressionSyntax[] assignments = root
                        .DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(assignment => SymbolEqualityComparer.Default.Equals(
                            model.GetSymbolInfo(assignment.Left).Symbol,
                            local))
                        .ToArray();

                    bool mutatedByReference = root.DescendantNodes()
                        .OfType<ArgumentSyntax>()
                        .Any(argument => argument.RefKindKeyword.Kind()
                                is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                            && SymbolEqualityComparer.Default.Equals(
                                model.GetSymbolInfo(argument.Expression).Symbol,
                                local));

                    if (mutatedByReference
                        || assignments.Any(static assignment =>
                            !assignment.IsKind(
                                SyntaxKind.SimpleAssignmentExpression)))
                    {
                        return false;
                    }

                    sources = sources
                        .Concat(assignments.Select(static assignment =>
                            assignment.Right))
                        .ToArray();
                }

                return sources.Length != 0
                    && sources.All(source => review(
                        source,
                        semanticModels[source.SyntaxTree],
                        path,
                        context));
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private static ExpressionSyntax StripTransparentExpression(
            ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized =>
                StripTransparentExpression(parenthesized.Expression),
            CastExpressionSyntax cast =>
                StripTransparentExpression(cast.Expression),
            PostfixUnaryExpressionSyntax suppression
                when suppression.IsKind(
                    SyntaxKind.SuppressNullableWarningExpression) =>
                StripTransparentExpression(suppression.Operand),
            BinaryExpressionSyntax coalesce
                when coalesce.IsKind(SyntaxKind.CoalesceExpression)
                    && coalesce.Right is ThrowExpressionSyntax =>
                StripTransparentExpression(coalesce.Left),
            _ => expression,
        };

        private static bool IsReviewedCollectionMechanicalMember(
            IMethodSymbol method)
        {
            string type = TypeKey(method.ContainingType);

            return type is
                    "System.Collections.Concurrent.ConcurrentDictionary`2"
                        or "System.Collections.Generic.Dictionary`2"
                        or "System.Collections.Generic.IReadOnlyDictionary`2"
                && method.Name == "TryGetValue"
                && !method.Parameters.Any(static parameter =>
                    parameter.Type.TypeKind == TypeKind.Delegate);
        }

        private bool HasOnlyReviewedComparerArguments(
            IMethodSymbol method,
            SyntaxNode? node,
            SemanticModel? model)
        {
            IParameterSymbol[] comparerParameters = method.Parameters
                .Where(static parameter => IsComparerType(parameter.Type))
                .ToArray();

            if (comparerParameters.Length == 0)
            {
                return true;
            }

            if (node is null || model is null)
            {
                return false;
            }

            IArgumentOperation[] arguments = model.GetOperation(node) switch
            {
                IInvocationOperation invocation => invocation.Arguments.ToArray(),
                IObjectCreationOperation creation => creation.Arguments.ToArray(),
                _ => [],
            };

            IArgumentOperation[] comparerArguments = arguments
                .Where(static argument => argument.Parameter is { Type: { } type }
                    && IsComparerType(type))
                .ToArray();

            if (comparerArguments.Length != comparerParameters.Length)
            {
                return false;
            }

            foreach (IArgumentOperation argument in comparerArguments)
            {
                if (argument.ArgumentKind == ArgumentKind.DefaultValue
                    && argument.Value.ConstantValue is
                        {
                            HasValue: true,
                            Value: null,
                        })
                {
                    continue;
                }

                ExpressionSyntax? expression = argument?.Syntax switch
                {
                    ArgumentSyntax syntax => syntax.Expression,
                    ExpressionSyntax syntax => syntax,
                    _ => argument?.Value.Syntax as ExpressionSyntax,
                };

                if (expression is null
                    || !IsReviewedPureComparerExpression(
                        expression,
                        model,
                        new HashSet<ISymbol>(
                            SymbolEqualityComparer.Default)))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsReviewedPureComparerExpression(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path)
        {
            expression = expression switch
            {
                ParenthesizedExpressionSyntax parenthesized =>
                    parenthesized.Expression,
                CastExpressionSyntax cast => cast.Expression,
                _ => expression,
            };

            if (expression is ConditionalExpressionSyntax conditional)
            {
                return IsReviewedPureComparerExpression(
                        conditional.WhenTrue,
                        model,
                        path)
                    && IsReviewedPureComparerExpression(
                        conditional.WhenFalse,
                        model,
                        path);
            }

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is IPropertySymbol property
                && (TypeKey(property.ContainingType) == "System.StringComparer"
                        && property.Name is "Ordinal" or "OrdinalIgnoreCase"
                    || TypeKey(property.ContainingType) is
                        "System.Collections.Generic.Comparer`1"
                            or "System.Collections.Generic.EqualityComparer`1"
                        && property.Name == "Default"
                    || TypeKey(property.ContainingType)
                            == "System.Collections.Generic.ReferenceEqualityComparer"
                        && property.Name == "Instance"))
            {
                return true;
            }

            if (symbol is IFieldSymbol or IPropertySymbol or ILocalSymbol)
            {
                if (!path.Add(symbol))
                {
                    return false;
                }

                try
                {
                    foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
                    {
                        ExpressionSyntax? initializer = reference.GetSyntax() switch
                        {
                            VariableDeclaratorSyntax variable =>
                                variable.Initializer?.Value,
                            PropertyDeclarationSyntax declaredProperty =>
                                declaredProperty.Initializer?.Value
                                    ?? declaredProperty.ExpressionBody?.Expression,
                            _ => null,
                        };

                        if (initializer is not null
                            && IsReviewedPureComparerExpression(
                                initializer,
                                semanticModels[initializer.SyntaxTree],
                                path))
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    path.Remove(symbol);
                }
            }

            INamedTypeSymbol? comparerType = model.GetTypeInfo(expression).Type
                as INamedTypeSymbol;

            if (comparerType is null
                || !comparerType.AllInterfaces.Any(static contract =>
                    IsComparerType(contract)))
            {
                return false;
            }

            AuthoredMember[] comparisons = comparerType
                .GetMembers()
                .OfType<IMethodSymbol>()
                .Where(static candidate => candidate.Name is
                    "Compare" or "Equals" or "GetHashCode")
                .Select(candidate => Resolve(
                    candidate,
                    model.Compilation))
                .OfType<AuthoredMember>()
                .ToArray();

            return comparisons.Length != 0
                && comparisons.All(comparison =>
                    !ContainsPotentialProducerSite(comparison));
        }

        private static bool IsComparerType(ITypeSymbol type) =>
            TypeKey(type) is
                "System.Collections.IComparer"
                    or "System.Collections.IEqualityComparer"
                    or "System.Collections.Generic.IComparer`1"
                    or "System.Collections.Generic.IEqualityComparer`1";

        private static bool IsReviewedNonProducerExternalMember(
            IPropertySymbol property,
            bool mutation) =>
            ReviewedIntrinsicExternalTypes.Contains(TypeKey(property.ContainingType))
            || IsReviewedDatabaseSupportMember(property, mutation)
            || ReviewedNonProducerExternalMembers.Contains(
                Normalize(property) + (mutation ? " setter" : string.Empty));

        private AuthoredMember? ResolveCallable(
            ExpressionSyntax expression,
            SemanticModel model,
            IReadOnlyDictionary<ISymbol, AuthoredMember>? callableBindings = null,
            IReadOnlySet<ISymbol>? absentCallables = null)
        {
            AuthoredMember[] targets = ResolveCallableTargets(
                expression,
                model,
                callableBindings,
                absentCallables);

            return targets is [AuthoredMember target]
                ? target
                : null;
        }

        private AuthoredMember[] ResolveCallableTargets(
            ExpressionSyntax expression,
            SemanticModel model,
            IReadOnlyDictionary<ISymbol, AuthoredMember>? callableBindings = null,
            IReadOnlySet<ISymbol>? absentCallables = null)
        {
            ClosedCallableValue contextual = ResolveContextualCallable(
                expression,
                model,
                callableBindings,
                absentCallables,
                new HashSet<ISymbol>(SymbolEqualityComparer.Default));

            if (contextual.IsKnown)
            {
                return CallableTargets(contextual);
            }

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is not null && callableBindings?.TryGetValue(symbol, out AuthoredMember? bound) == true)
            {
                return [bound];
            }

            if (expression is AnonymousFunctionExpressionSyntax lambda && model.GetOperation(lambda) is IAnonymousFunctionOperation operation)
            {
                return
                [
                    new(
                        operation.Symbol,
                        lambda.Body,
                        model,
                        CallableBindings: callableBindings,
                        AbsentCallables: absentCallables),
                ];
            }

            if (expression is not InvocationExpressionSyntax && model.GetSymbolInfo(expression).Symbol is IMethodSymbol method)
            {
                return Resolve(method, model.Compilation) is { } target
                    ? [target]
                    : [];
            }

            if (symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer } variable && !variable.Ancestors().OfType<BlockSyntax>().First().DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)))
            {
                return initializer == expression
                    ? []
                    : ResolveCallableTargets(initializer, model, callableBindings, absentCallables);
            }

            if (symbol is not null)
            {
                ClosedCallableValue value = ResolveClosedCallable(symbol, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

                if (value.IsKnown)
                {
                    return CallableTargets(value);
                }
            }

            return [];
        }

        private ClosedCallableValue ResolveContextualCallable(
            ExpressionSyntax expression,
            SemanticModel model,
            IReadOnlyDictionary<ISymbol, AuthoredMember>? callableBindings,
            IReadOnlySet<ISymbol>? absentCallables,
            HashSet<ISymbol> path)
        {
            while (expression is ParenthesizedExpressionSyntax
                or CastExpressionSyntax
                or PostfixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SuppressNullableWarningExpression,
                })
            {
                expression = expression switch
                {
                    ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                    CastExpressionSyntax cast => cast.Expression,
                    PostfixUnaryExpressionSyntax postfix => postfix.Operand,
                    _ => expression,
                };
            }

            if (expression is DefaultExpressionSyntax
                || expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.DefaultLiteralExpression)
                || model.GetConstantValue(expression) is
                {
                    HasValue: true,
                    Value: null,
                })
            {
                return new(true, null, true, false);
            }

            if (expression is AnonymousFunctionExpressionSyntax lambda
                && model.GetOperation(lambda) is IAnonymousFunctionOperation operation)
            {
                return new(
                    true,
                    new(
                        operation.Symbol,
                        lambda.Body,
                        model,
                        CallableBindings: callableBindings,
                        AbsentCallables: absentCallables),
                    false,
                    false);
            }

            if (expression is BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.CoalesceExpression,
                } coalesce)
            {
                ClosedCallableValue left = ResolveContextualCallable(
                    coalesce.Left,
                    model,
                    callableBindings,
                    absentCallables,
                    path);

                if (!left.IsKnown || !left.CanBeAbsent)
                {
                    return left;
                }

                ClosedCallableValue right = ResolveContextualCallable(
                    coalesce.Right,
                    model,
                    callableBindings,
                    absentCallables,
                    path);

                if (left.Target is null
                    && left.Targets is null
                    && !left.EffectFreeExternal)
                {
                    return right;
                }

                return MergeClosedCallables([
                    left with
                    {
                        CanBeAbsent = false,
                    },
                    right,
                ]);
            }

            if (expression is ConditionalExpressionSyntax conditional)
            {
                return MergeClosedCallables([
                    ResolveContextualCallable(
                        conditional.WhenTrue,
                        model,
                        callableBindings,
                        absentCallables,
                        path),
                    ResolveContextualCallable(
                        conditional.WhenFalse,
                        model,
                        callableBindings,
                        absentCallables,
                        path),
                ]);
            }

            if (expression is SwitchExpressionSyntax switchExpression)
            {
                return MergeClosedCallables(
                    switchExpression.Arms.Select(arm => ResolveContextualCallable(
                        arm.Expression,
                        model,
                        callableBindings,
                        absentCallables,
                        path)));
            }

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is not null
                && callableBindings?.TryGetValue(symbol, out AuthoredMember? bound) == true)
            {
                return new(true, bound, false, false);
            }

            if (symbol is not null
                && absentCallables?.Contains(symbol) == true)
            {
                return new(true, null, true, false);
            }

            if (symbol is IMethodSymbol method
                && expression is not InvocationExpressionSyntax)
            {
                AuthoredMember? target = Resolve(method, model.Compilation);

                return target is not null
                    ? new(true, target, false, false)
                    : IsEffectFreeExternalCallable(method, model.Compilation)
                        ? new(true, null, false, true)
                        : default;
            }

            if (symbol is ILocalSymbol local
                && path.Add(local))
            {
                try
                {
                    if (local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax()
                            is VariableDeclaratorSyntax
                            {
                                Initializer.Value: { } initializer,
                            } variable
                        && !variable.Ancestors().OfType<BlockSyntax>().First()
                            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
                            .Any(assignment => SymbolEqualityComparer.Default.Equals(
                                model.GetSymbolInfo(assignment.Left).Symbol,
                                local)))
                    {
                        return ResolveContextualCallable(
                            initializer,
                            model,
                            callableBindings,
                            absentCallables,
                            path);
                    }
                }
                finally
                {
                    _ = path.Remove(local);
                }
            }

            return symbol is null
                ? default
                : ResolveClosedCallable(
                    symbol,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        }

        private static AuthoredMember[] CallableTargets(ClosedCallableValue value) =>
            value.Targets is { } targets
                ? [.. targets]
                : value.Target is { } target
                    ? [target]
                    : [];

        private ClosedCallableValue ResolveClosedCallable(ISymbol symbol, HashSet<ISymbol> path)
        {
            if (!path.Add(symbol))
            {
                return default;
            }

            try
            {
                ITypeSymbol? callableType = symbol switch
                {
                    IEventSymbol declaredEvent => declaredEvent.Type,
                    IFieldSymbol declaredField => declaredField.Type,
                    ILocalSymbol declaredLocal => declaredLocal.Type,
                    IParameterSymbol declaredParameter => declaredParameter.Type,
                    IPropertySymbol declaredProperty => declaredProperty.Type,
                    _ => null,
                };

                if (callableType?.TypeKind != TypeKind.Delegate || !IsEffectivelyClosed(symbol))
                {
                    return default;
                }

                if (symbol is IEventSymbol eventSymbol)
                {
                    return HasAuthoredCallableMutation(eventSymbol) ? default : new(true, null, true, false);
                }

                if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol method } parameter)
                {
                    return ResolveParameter(parameter, method, path);
                }

                if (symbol is ILocalSymbol local)
                {
                    SyntaxNode? declaration = local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax();

                    if (declaration is VariableDeclaratorSyntax { Initializer.Value: { } initializer } variable
                        && !variable.Ancestors().OfType<BlockSyntax>().First().DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => SymbolEqualityComparer.Default.Equals(semanticModels[assignment.SyntaxTree].GetSymbolInfo(assignment.Left).Symbol, local)))
                    {
                        return ResolveClosedCallable(initializer, semanticModels[initializer.SyntaxTree], path);
                    }

                    if (declaration?.AncestorsAndSelf().OfType<IsPatternExpressionSyntax>().FirstOrDefault() is { } pattern)
                    {
                        return ResolveClosedCallable(pattern.Expression, semanticModels[pattern.SyntaxTree], path);
                    }

                    return default;
                }

                if (symbol is IPropertySymbol property)
                {
                    bool initialized = property.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null });

                    return initialized || HasAuthoredCallableMutation(property) ? default : new(true, null, true, false);
                }

                if (symbol is not IFieldSymbol field)
                {
                    return default;
                }

                bool mutatedByReference = AuthoredMembers.Any(member => member.Syntax.DescendantNodesAndSelf().OfType<ArgumentSyntax>().Any(argument => argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(argument.Expression).Symbol, field)));

                AssignmentExpressionSyntax[] writes = AuthoredMembers.SelectMany(member => member.Syntax.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Where(assignment => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(assignment.Left).Symbol, field))).DistinctBy(static assignment => (assignment.SyntaxTree, assignment.SpanStart)).ToArray();

                if (mutatedByReference)
                {
                    return default;
                }

                List<ClosedCallableValue> values = [];

                foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
                    {
                        values.Add(ResolveClosedCallable(initializer, semanticModels[initializer.SyntaxTree], path));
                    }
                }

                foreach (AssignmentExpressionSyntax write in writes)
                {
                    AuthoredMember[] owners = AuthoredMembers.Where(member => member.Syntax.SyntaxTree == write.SyntaxTree && member.Syntax.Span.Contains(write.Span)).OrderBy(member => member.Syntax.Span.Length).ToArray();

                    AuthoredMember? owner = owners.FirstOrDefault();

                    if (!write.IsKind(SyntaxKind.SimpleAssignmentExpression) || owner is null || owner.Symbol.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor) || !SymbolEqualityComparer.Default.Equals(owner.Symbol.ContainingType, field.ContainingType) || !IsAssignedOnEverySuccessfulConstruction(owner, write))
                    {
                        return default;
                    }

                    values.Add(ResolveClosedCallable(write.Right, owner.Model, path));
                }

                return values.Count == 0 ? new(true, null, true, false) : MergeClosedCallables(values);
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private ClosedCallableValue ResolveClosedCallable(ExpressionSyntax expression, SemanticModel model, HashSet<ISymbol> path)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized || expression is CastExpressionSyntax || expression is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
            {
                expression = expression switch
                {
                    ParenthesizedExpressionSyntax parenthesizedExpression => parenthesizedExpression.Expression,
                    CastExpressionSyntax cast => cast.Expression,
                    PostfixUnaryExpressionSyntax postfix => postfix.Operand,
                    _ => expression,
                };
            }

            if (expression is DefaultExpressionSyntax || expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.DefaultLiteralExpression) || model.GetConstantValue(expression) is { HasValue: true, Value: null })
            {
                return new(true, null, true, false);
            }

            if (expression is AnonymousFunctionExpressionSyntax lambda && model.GetOperation(lambda) is IAnonymousFunctionOperation operation)
            {
                return new(true, new(operation.Symbol, lambda.Body, model), false, false);
            }

            if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce)
            {
                ClosedCallableValue left = ResolveClosedCallable(coalesce.Left, model, path);

                if (!left.IsKnown || !left.CanBeAbsent)
                {
                    return left;
                }

                ClosedCallableValue right = ResolveClosedCallable(coalesce.Right, model, path);

                if (left.Target is null && !left.EffectFreeExternal)
                {
                    return right;
                }

                return MergeClosedCallables([left with { CanBeAbsent = false }, right]);
            }

            if (expression is ConditionalExpressionSyntax conditional)
            {
                return MergeClosedCallables([
                    ResolveClosedCallable(conditional.WhenTrue, model, path),
                    ResolveClosedCallable(conditional.WhenFalse, model, path),
                ]);
            }

            if (expression is SwitchExpressionSyntax switchExpression)
            {
                return MergeClosedCallables(switchExpression.Arms.Select(arm => ResolveClosedCallable(arm.Expression, model, path)));
            }

            if (expression is ImplicitObjectCreationExpressionSyntax or ObjectCreationExpressionSyntax && model.GetTypeInfo(expression).ConvertedType?.TypeKind == TypeKind.Delegate && expression is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [ArgumentSyntax argument] })
            {
                return ResolveClosedCallable(argument.Expression, model, path);
            }

            ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;

            if (symbol is IMethodSymbol method
                && expression is not InvocationExpressionSyntax)
            {
                AuthoredMember? target = Resolve(method, model.Compilation);

                return target is not null
                    ? new(true, target, false, false)
                    : IsEffectFreeExternalCallable(method, model.Compilation) ? new(true, null, false, true) : default;
            }

            if (symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer } variable && !variable.Ancestors().OfType<BlockSyntax>().First().DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)))
            {
                return ResolveClosedCallable(initializer, model, path);
            }

            return symbol is null ? default : ResolveClosedCallable(symbol, path);
        }

        private ClosedCallableValue ResolveParameter(IParameterSymbol parameter, IMethodSymbol method, HashSet<ISymbol> path)
        {
            List<ClosedCallableValue> values = [];

            Dictionary<GraphMemberIdentity, List<ClosedCallableValue>> constructionValues = [];

            bool SameCallableValue(ClosedCallableValue left, ClosedCallableValue right)
            {
                if (left.IsKnown != right.IsKnown
                    || left.CanBeAbsent != right.CanBeAbsent
                    || left.EffectFreeExternal != right.EffectFreeExternal)
                {
                    return false;
                }

                HashSet<GraphMemberIdentity> leftTargets = CallableTargets(left)
                    .Select(MemberIdentity)
                    .ToHashSet();

                return leftTargets.SetEquals(
                    CallableTargets(right).Select(MemberIdentity));
            }

            if (method.MethodKind == MethodKind.Constructor)
            {
                foreach ((BaseObjectCreationExpressionSyntax creation, SemanticModel model) in objectCreations)
                {
                    if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol target && Identity(target) == Identity(method))
                    {
                        ClosedCallableValue value = ResolveArgument(
                            parameter,
                            creation.ArgumentList?.Arguments ?? default,
                            model,
                            path);

                        values.Add(value);

                        AuthoredMember? owner = AuthoredMembers
                            .Where(member => member.Syntax.SyntaxTree == creation.SyntaxTree
                                && member.Syntax.Span.Contains(creation.Span))
                            .OrderBy(member => member.Syntax.Span.Length)
                            .FirstOrDefault();

                        if (owner is not null)
                        {
                            GraphMemberIdentity identity = MemberIdentity(owner);

                            if (!constructionValues.TryGetValue(
                                    identity,
                                    out List<ClosedCallableValue>? ownedValues))
                            {
                                constructionValues.Add(identity, ownedValues = []);
                            }

                            ownedValues.Add(value);
                        }
                    }
                }

                foreach ((ConstructorInitializerSyntax initializer, SemanticModel model) in constructorInitializers)
                {
                    if (model.GetSymbolInfo(initializer).Symbol is IMethodSymbol target && Identity(target) == Identity(method))
                    {
                        values.Add(ResolveArgument(parameter, initializer.ArgumentList.Arguments, model, path));
                    }
                }

                if (constructionValues.Values.Any(group => group
                    .Skip(1)
                    .Any(value => !SameCallableValue(group[0], value))))
                {
                    return default;
                }
            }
            else
            {
                foreach ((InvocationExpressionSyntax call, SemanticModel model) in invocations)
                {
                    if (model.GetSymbolInfo(call).Symbol is IMethodSymbol target && Identity(target) == Identity(method))
                    {
                        values.Add(ResolveArgument(parameter, call.ArgumentList.Arguments, model, path));
                    }
                }
            }

            return values.Count == 0 ? default : MergeClosedCallables(values);
        }

        private ClosedCallableValue ResolveArgument(IParameterSymbol parameter, SeparatedSyntaxList<ArgumentSyntax> arguments, SemanticModel model, HashSet<ISymbol> path)
        {
            ArgumentSyntax? supplied = arguments.SingleOrDefault(argument => model.GetOperation(argument) is IArgumentOperation { Parameter: { } target } && target.Ordinal == parameter.Ordinal);

            if (supplied is not null)
            {
                return ResolveClosedCallable(supplied.Expression, model, path);
            }

            return parameter.HasExplicitDefaultValue && parameter.ExplicitDefaultValue is null
                ? new(true, null, true, false)
                : default;
        }

        private ClosedCallableValue MergeClosedCallables(IEnumerable<ClosedCallableValue> values)
        {
            Dictionary<GraphMemberIdentity, AuthoredMember> targets = [];

            bool canBeAbsent = false;

            bool effectFreeExternal = false;

            foreach (ClosedCallableValue value in values)
            {
                if (!value.IsKnown)
                {
                    return default;
                }

                canBeAbsent |= value.CanBeAbsent;

                effectFreeExternal |= value.EffectFreeExternal;

                foreach (AuthoredMember target in CallableTargets(value))
                {
                    targets.TryAdd(MemberIdentity(target), target);
                }
            }

            AuthoredMember[] exactTargets = targets.Values
                .OrderBy(static target => TypeKey(target.Symbol.ContainingType), StringComparer.Ordinal)
                .ThenBy(static target => MethodKey(target.Symbol), StringComparer.Ordinal)
                .ThenBy(static target => target.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(static target => target.Syntax.SpanStart)
                .ToArray();

            return new(
                true,
                exactTargets is [AuthoredMember singleTarget] ? singleTarget : null,
                canBeAbsent,
                effectFreeExternal,
                exactTargets.Length > 1 ? exactTargets : null);
        }

        private bool HasAuthoredCallableMutation(ISymbol symbol) => AuthoredMembers.Any(member => member.Syntax.DescendantNodesAndSelf().Any(node => node switch
        {
            AssignmentExpressionSyntax assignment => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(assignment.Left).Symbol, symbol),
            ArgumentSyntax argument when argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(argument.Expression).Symbol, symbol),
            _ => false,
        }));

        private static bool IsEffectivelyClosed(ISymbol symbol) =>
            symbol is ILocalSymbol
            || symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal
            || symbol is IParameterSymbol
                && symbol.ContainingSymbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal
            || IsEffectivelyClosed(symbol.ContainingType);

        private static bool IsEffectivelyClosed(INamedTypeSymbol? type)
        {
            while (type is not null)
            {
                if (type.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal)
                {
                    return true;
                }

                type = type.ContainingType;
            }

            return false;
        }

        private static bool IsDelegateExpression(SemanticModel model, ExpressionSyntax expression)
        {
            Microsoft.CodeAnalysis.TypeInfo type = model.GetTypeInfo(expression);

            return type.Type?.TypeKind == TypeKind.Delegate || type.ConvertedType?.TypeKind == TypeKind.Delegate;
        }

        private bool IsProvenAbsentCallable(ISymbol symbol)
        {
            if (absentTestCallables.TryGetValue(symbol, out bool absent))
            {
                return absent;
            }

            ITypeSymbol? callableType = symbol switch
            {
                IEventSymbol declaredEvent => declaredEvent.Type,
                IFieldSymbol declaredField => declaredField.Type,
                IPropertySymbol declaredProperty => declaredProperty.Type,
                _ => null,
            };

            bool initialized = symbol.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()).Select(static syntax => syntax switch
            {
                VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                PropertyDeclarationSyntax property => property.Initializer?.Value,
                _ => null,
            }).OfType<ExpressionSyntax>().Any(initializer => initializer is not DefaultExpressionSyntax && !(initializer is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.DefaultLiteralExpression)) && semanticModels[initializer.SyntaxTree].GetConstantValue(initializer) is not { HasValue: true, Value: null });

            absent = symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal
                && callableType?.TypeKind == TypeKind.Delegate
                && callableType.NullableAnnotation == NullableAnnotation.Annotated
                && !initialized
                && !HasAuthoredCallableMutation(symbol);

            if (absent)
            {
                absentTestCallables[symbol] = true;

                return true;
            }

            ClosedCallableValue value = ResolveClosedCallable(symbol, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

            absent = value.IsKnown
                && CallableTargets(value).Length == 0
                && value.CanBeAbsent
                && !value.EffectFreeExternal;

            absentTestCallables[symbol] = absent;

            return absent;
        }

        private bool ContainsPotentialProducerSite(AuthoredMember member) => ContainsPotentialProducerSite(member, []);

        private bool ContainsPotentialProducerSite(AuthoredMember member, HashSet<GraphMemberIdentity> path)
        {
            if (!path.Add(MemberIdentity(member)))
            {
                return false;
            }

            try
            {
                foreach (SyntaxNode node in member.Syntax.DescendantNodesAndSelf(candidate => candidate == member.Syntax || candidate is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
                {
                    if (member.Model.GetSymbolInfo(node).Symbol is IMethodSymbol method)
                    {
                        string callee = Normalize(method);

                        if (Vocabulary.ContainsKey(callee)
                            || callee is "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope"
                                or "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope"
                                or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory.CreateScope"
                                or "System.IO.FileStream..ctor"
                                or "System.IO.File.Open"
                            || method.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "GrimoireConnectionAcquisitionRouteAttribute")
                            || TryClassifyExternalProducer(method, out _))
                        {
                            return true;
                        }

                        AuthoredMember? target = Resolve(method, member.Model.Compilation);

                        if (target is not null && ContainsPotentialProducerSite(target, path)
                            || target is null
                                && RequiresExternalBoundaryClassification(
                                    method,
                                    member.Model.Compilation,
                                    node,
                                    member.Model,
                                    member))
                        {
                            return true;
                        }
                    }
                    else if (member.Model.GetSymbolInfo(node).Symbol is IPropertySymbol property
                        && (Vocabulary.ContainsKey(Normalize(property))
                            || TryClassifyExternalProducer(
                                property,
                                node.Parent is AssignmentExpressionSyntax assignment
                                    && assignment.Left == node,
                                out _)
                            || RequiresExternalBoundaryClassification(
                                property,
                                member.Model.Compilation,
                                node.Parent is AssignmentExpressionSyntax propertyMutation
                                    && propertyMutation.Left == node,
                                node,
                                member.Model,
                                member)))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                path.Remove(MemberIdentity(member));
            }
        }

        private IReadOnlySet<string> AdmissionOrigins(AuthoredMember member, ExpressionSyntax expression, HashSet<ISymbol> path)
        {
            bool cacheable = path.Count == 0;

            GraphMemberIdentity memberIdentity = MemberIdentity(member);

            var key = (Member: memberIdentity, ExpressionStart: expression.SpanStart, ExpressionLength: expression.Span.Length, Bindings: AdmissionBindingIdentity(member));

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
            SemanticModel model = expression.SyntaxTree == member.Model.SyntaxTree ? member.Model : member.Model.Compilation.GetSemanticModel(expression.SyntaxTree);

            HashSet<string> origins = new(StringComparer.Ordinal);

            origins.UnionWith(
                ResolveReturnedAdmissionProofs(member, expression)
                    .Select(static proof => proof.Origin));

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

        private readonly record struct ReturnedAdmissionProof(
            string Origin,
            string WorkKind);

        private IReadOnlyList<ReturnedAdmissionProof> ResolveReturnedAdmissionProofs(
            AuthoredMember caller,
            ExpressionSyntax expression)
        {
            SemanticModel model = expression.SyntaxTree == caller.Model.SyntaxTree
                ? caller.Model
                : caller.Model.Compilation.GetSemanticModel(expression.SyntaxTree);

            if (model.GetTypeInfo(expression).Type is not { } expressionType
                || TypeKey(expressionType) !=
                "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease")
            {
                return [];
            }

            if (expression.DescendantNodesAndSelf().Any(static node =>
                node is ConditionalExpressionSyntax
                    or SwitchExpressionSyntax
                    or AssignmentExpressionSyntax
                    or BaseObjectCreationExpressionSyntax
                    || node is BinaryExpressionSyntax binary
                        && binary.IsKind(SyntaxKind.CoalesceExpression)))
            {
                return [];
            }

            List<ReturnedAdmissionProof> proofs = [];

            foreach (InvocationExpressionSyntax call in expression.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                ITypeSymbol returnedType = method.ReturnType;

                if (returnedType is INamedTypeSymbol
                    {
                        TypeArguments: [ITypeSymbol result],
                    } awaitable
                    && awaitable.OriginalDefinition.ToDisplayString() is
                        "System.Threading.Tasks.Task<TResult>"
                            or "System.Threading.Tasks.ValueTask<TResult>")
                {
                    returnedType = result;
                }

                if (TypeKey(returnedType) !=
                    "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease")
                {
                    continue;
                }

                SyntaxNode completed = CompletionPreservingExpression(call, model);

                if (completed.Parent is AwaitExpressionSyntax awaited
                    && awaited.Expression == completed)
                {
                    completed = CompletionPreservingExpression(awaited, model);
                }

                if (completed != expression
                    || ResolveInvocationTarget(method, caller, call) is not { } target)
                {
                    return [];
                }

                ExpressionSyntax[] returns = target.Syntax.DescendantNodesAndSelf(node =>
                        node == target.Syntax
                        || node is not AnonymousFunctionExpressionSyntax
                            and not LocalFunctionStatementSyntax)
                    .OfType<ReturnStatementSyntax>()
                    .Select(static returned => returned.Expression)
                    .OfType<ExpressionSyntax>()
                    .ToArray();

                if (returns.Length == 0)
                {
                    return [];
                }

                ReturnedAdmissionProof? exact = null;

                foreach (ExpressionSyntax returned in returns)
                {
                    if (returned.DescendantNodesAndSelf().Any(static node =>
                        node is InvocationExpressionSyntax
                            or BaseObjectCreationExpressionSyntax
                            or ConditionalExpressionSyntax
                            or SwitchExpressionSyntax
                            or AssignmentExpressionSyntax
                            || node is BinaryExpressionSyntax binary
                                && binary.IsKind(SyntaxKind.CoalesceExpression)))
                    {
                        return [];
                    }

                    IReadOnlySet<string> origins = AdmissionOrigins(
                        target,
                        returned,
                        new HashSet<ISymbol>(SymbolEqualityComparer.Default));

                    if (origins.Count != 1)
                    {
                        return [];
                    }

                    string origin = origins.Single();

                    if (target.Syntax.DescendantNodesAndSelf()
                            .OfType<InvocationExpressionSyntax>()
                            .SingleOrDefault(candidate => Location(candidate) == origin) is not { } admission
                        || target.Model.GetSymbolInfo(admission).Symbol is not IMethodSymbol gate
                        || Normalize(gate) !=
                            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease"
                        || !IsGuardedAdmission(admission)
                        || admission.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } kindExpression
                        || target.Model.GetSymbolInfo(kindExpression).Symbol is not IFieldSymbol workKind
                        || AdmissionEndedBefore(target, origin, returned.SpanStart, []))
                    {
                        return [];
                    }

                    ReturnedAdmissionProof current = new(origin, workKind.Name);

                    if (exact is { } prior && prior != current)
                    {
                        return [];
                    }

                    exact = current;
                }

                if (exact is not { } proof)
                {
                    return [];
                }

                proofs.Add(proof);
            }

            return proofs
                .Distinct()
                .ToArray();
        }

        private bool HasAdmissionSeed(AuthoredMember member)
        {
            if (member.AdmissionBindings?.Values.Any(static origins => origins.Count > 0) == true)
            {
                return true;
            }

            var key = (Member: MemberIdentity(member), Bindings: AdmissionBindingIdentity(member));

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
            return BindArguments(
                caller,
                AdmissionArguments(caller, call),
                target);
        }

        private AuthoredMember BindInvocationContext(
            AuthoredMember caller,
            InvocationExpressionSyntax call,
            AuthoredMember target)
        {
            AuthoredMember bound = BindAdmissionArguments(caller, call, target);

            if (target.Symbol.IsStatic)
            {
                return bound;
            }

            bool hasCallableContext = caller.CallableBindings is { Count: > 0 }
                || caller.AbsentCallables is { Count: > 0 };

            bool typeCarriesCallableState = target.Symbol.ContainingType
                    .InstanceConstructors.Any(static constructor =>
                        constructor.Parameters.Any(static parameter =>
                            parameter.Type.TypeKind == TypeKind.Delegate))
                || target.Symbol.ContainingType.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Any(static field => field.Type.TypeKind == TypeKind.Delegate);

            if (!hasCallableContext && !typeCarriesCallableState)
            {
                return bound;
            }

            ExpressionSyntax? receiver = call.Expression is MemberAccessExpressionSyntax access
                ? access.Expression
                : null;

            AuthoredMember? receiverContext = receiver is null
                ? hasCallableContext ? caller : null
                : ResolveConstructedReceiverContext(
                    caller,
                    receiver,
                    target.Symbol.ContainingType,
                    new HashSet<(GraphMemberIdentity Member, int Start, int Length)>());

            return receiverContext is null
                ? bound
                : MergeCallableContext(bound, receiverContext);
        }

        private AuthoredMember? ResolveConstructedReceiverContext(
            AuthoredMember caller,
            ExpressionSyntax expression,
            INamedTypeSymbol expectedType,
            HashSet<(GraphMemberIdentity Member, int Start, int Length)> path)
        {
            expression = StripTransparentExpression(expression);

            var identity = (MemberIdentity(caller), expression.SpanStart, expression.Span.Length);

            if (!path.Add(identity))
            {
                return null;
            }

            try
            {
                if (expression is BaseObjectCreationExpressionSyntax creation
                    && caller.Model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor
                    && SymbolEqualityComparer.Default.Equals(
                        constructor.ContainingType,
                        expectedType)
                    && ResolveConstructorContextTarget(
                        constructor,
                        caller.Model.Compilation) is { } target)
                {
                    return BindConstructorArguments(caller, creation, target);
                }

                if (expression is InvocationExpressionSyntax invocation
                    && caller.Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                    && ResolveInvocationTarget(method, caller, invocation) is { } factory)
                {
                    AuthoredMember boundFactory = BindInvocationContext(
                        caller,
                        invocation,
                        factory);

                    ExpressionSyntax[] returned = ReturnedExpressions(factory.Syntax);

                    AuthoredMember? exact = null;

                    foreach (ExpressionSyntax value in returned)
                    {
                        AuthoredMember? candidate = ResolveConstructedReceiverContext(
                            boundFactory,
                            value,
                            expectedType,
                            path);

                        if (candidate is null)
                        {
                            return null;
                        }

                        if (exact is not null
                            && CallableContextIdentity(exact)
                                != CallableContextIdentity(candidate))
                        {
                            return null;
                        }

                        exact = candidate;
                    }

                    return exact;
                }

                if (caller.Model.GetSymbolInfo(expression).Symbol is ILocalSymbol local
                    && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax()
                        is VariableDeclaratorSyntax
                        {
                            Initializer.Value: { } initializer,
                        } variable
                    && !variable.Ancestors().OfType<BlockSyntax>().First()
                        .DescendantNodes().OfType<AssignmentExpressionSyntax>()
                        .Any(assignment => SymbolEqualityComparer.Default.Equals(
                            caller.Model.GetSymbolInfo(assignment.Left).Symbol,
                            local)))
                {
                    return ResolveConstructedReceiverContext(
                        caller,
                        initializer,
                        expectedType,
                        path);
                }

                if (caller.Model.GetSymbolInfo(expression).Symbol is IFieldSymbol field
                    && SymbolEqualityComparer.Default.Equals(field.Type, expectedType))
                {
                    List<(ExpressionSyntax Expression, SemanticModel Model)> values = [];

                    foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
                    {
                        if (reference.GetSyntax() is VariableDeclaratorSyntax
                        {
                                Initializer.Value: { } fieldInitializer,
                            })
                        {
                            values.Add((
                                fieldInitializer,
                                semanticModels[fieldInitializer.SyntaxTree]));
                        }
                    }

                    values.AddRange(AuthoredMembers
                        .Where(member =>
                            member.Symbol.MethodKind == MethodKind.Constructor
                            && SymbolEqualityComparer.Default.Equals(
                                member.Symbol.ContainingType,
                                field.ContainingType))
                        .SelectMany(member => member.Syntax.DescendantNodesAndSelf()
                            .OfType<AssignmentExpressionSyntax>()
                            .Where(assignment =>
                                assignment.IsKind(
                                    SyntaxKind.SimpleAssignmentExpression)
                                && SymbolEqualityComparer.Default.Equals(
                                    member.Model.GetSymbolInfo(
                                        assignment.Left).Symbol,
                                    field))
                            .Select(assignment => (
                                assignment.Right,
                                member.Model))));

                    if (values.Count != 1)
                    {
                        return null;
                    }

                    return ResolveConstructedReceiverContext(
                        caller with
                        {
                            Syntax = values[0].Expression,
                            Model = values[0].Model,
                        },
                        values[0].Expression,
                        expectedType,
                        path);
                }

                return null;
            }
            finally
            {
                _ = path.Remove(identity);
            }
        }

        private AuthoredMember? ResolveConstructorContextTarget(
            IMethodSymbol constructor,
            Compilation consumingCompilation)
        {
            if (Resolve(constructor, consumingCompilation) is { } authored)
            {
                return authored;
            }

            SyntaxNode? declaration = constructor.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .SingleOrDefault(static syntax =>
                    syntax is TypeDeclarationSyntax
                    {
                        ParameterList: not null,
                    });

            return declaration is TypeDeclarationSyntax
                {
                    ParameterList: { } parameters,
                }
                ? new(
                    constructor,
                    parameters,
                    semanticModels[parameters.SyntaxTree])
                : null;
        }

        private static ExpressionSyntax[] ReturnedExpressions(SyntaxNode syntax)
        {
            ExpressionSyntax? expressionBody = syntax switch
            {
                MethodDeclarationSyntax method => method.ExpressionBody?.Expression,
                LocalFunctionStatementSyntax local => local.ExpressionBody?.Expression,
                AccessorDeclarationSyntax accessor => accessor.ExpressionBody?.Expression,
                _ => null,
            };

            if (expressionBody is not null)
            {
                return [expressionBody];
            }

            return syntax.DescendantNodes(node =>
                    node == syntax
                    || node is not AnonymousFunctionExpressionSyntax
                        and not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>()
                .Select(static returned => returned.Expression)
                .OfType<ExpressionSyntax>()
                .ToArray();
        }

        private static string CallableContextIdentity(AuthoredMember member)
        {
            IEnumerable<string> bound = member.CallableBindings?
                .OrderBy(static pair => pair.Key.ToDisplayString(), StringComparer.Ordinal)
                .Select(static pair => pair.Key.ToDisplayString()
                    + "="
                    + MethodKey(pair.Value.Symbol))
                ?? [];

            IEnumerable<string> absent = member.AbsentCallables?
                .OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal)
                .Select(static symbol => symbol.ToDisplayString())
                ?? [];

            return string.Join(";", bound)
                + "|absent:"
                + string.Join(";", absent);
        }

        private static AuthoredMember MergeCallableContext(
            AuthoredMember target,
            AuthoredMember source)
        {
            Dictionary<ISymbol, AuthoredMember> callables = source.CallableBindings?
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    SymbolEqualityComparer.Default)
                ?? new(SymbolEqualityComparer.Default);

            foreach ((ISymbol symbol, AuthoredMember callable) in
                target.CallableBindings ??
                    Enumerable.Empty<KeyValuePair<ISymbol, AuthoredMember>>())
            {
                callables[symbol] = callable;
            }

            HashSet<ISymbol> absent = new(
                source.AbsentCallables ?? Enumerable.Empty<ISymbol>(),
                SymbolEqualityComparer.Default);

            absent.UnionWith(
                target.AbsentCallables ?? Enumerable.Empty<ISymbol>());

            foreach (ISymbol callable in callables.Keys)
            {
                absent.Remove(callable);
            }

            return target with
            {
                CallableBindings = callables,
                AbsentCallables = absent,
            };
        }

        private AuthoredMember BindConstructorArguments(
            AuthoredMember caller,
            BaseObjectCreationExpressionSyntax creation,
            AuthoredMember target)
        {
            return BindArguments(
                caller,
                ConstructorArguments(caller, creation),
                target);
        }

        private AuthoredMember BindArguments(
            AuthoredMember caller,
            IEnumerable<(ExpressionSyntax Expression, IParameterSymbol? Parameter, RefKind RefKind)> arguments,
            AuthoredMember target)
        {
            Dictionary<ISymbol, IReadOnlySet<string>> bindings = new(SymbolEqualityComparer.Default);

            Dictionary<ISymbol, AuthoredMember> callableBindings = new(SymbolEqualityComparer.Default);

            HashSet<ISymbol> absentCallables = new(SymbolEqualityComparer.Default);

            Dictionary<ISymbol, ITypeSymbol> concreteBindings = new(SymbolEqualityComparer.Default);

            Dictionary<ISymbol, RecoveryTuple> recoveryBindings = new(SymbolEqualityComparer.Default);

            Dictionary<ISymbol, BoundValueSource> valueBindings = new(SymbolEqualityComparer.Default);

            foreach (IParameterSymbol parameter in target.Symbol.Parameters.Where(static parameter => parameter.Type.TypeKind == TypeKind.Delegate && parameter.HasExplicitDefaultValue && parameter.ExplicitDefaultValue is null))
            {
                absentCallables.Add(parameter);
            }

            foreach (var argument in arguments)
            {
                if (argument.Parameter is { } parameter && parameter.Ordinal < target.Symbol.Parameters.Length)
                {
                    IParameterSymbol targetParameter = target.Symbol.Parameters[parameter.Ordinal];

                    bindings[targetParameter] = AdmissionOrigins(caller, argument.Expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

                    if (RequiresExactValueProvenance(target, targetParameter)
                        || RequiresConstantControlProvenance(
                                target,
                                targetParameter)
                            && RecoveryValue(caller, argument.Expression, []).IsKnown)
                    {
                        valueBindings[targetParameter] = new(
                            caller,
                            argument.Expression);
                    }

                    if (ConcreteType(caller, argument.Expression) is { } concrete)
                    {
                        concreteBindings[targetParameter] = concrete;
                    }

                    if (BoundRecoveryTuple(caller, argument.Expression, []) is { } recovery)
                    {
                        recoveryBindings[targetParameter] = recovery;
                    }

                    if (targetParameter.Type.TypeKind == TypeKind.Delegate)
                    {
                        absentCallables.Remove(targetParameter);

                        ISymbol? callableSymbol = caller.Model.GetSymbolInfo(argument.Expression).Symbol;

                        AuthoredMember? callback = null;

                        bool carriesCapturedContext = callableSymbol is not null && caller.CallableBindings?.TryGetValue(callableSymbol, out callback) == true;

                        callback ??= ResolveCallable(
                            argument.Expression,
                            caller.Model,
                            caller.CallableBindings,
                            caller.AbsentCallables);

                        if (callback is not null)
                        {
                            callableBindings[targetParameter] = carriesCapturedContext
                                ? callback
                                : callback with
                            {
                                AdmissionBindings = caller.AdmissionBindings,
                                CallableBindings = caller.CallableBindings,
                                AbsentCallables = caller.AbsentCallables,
                                ConcreteBindings = caller.ConcreteBindings,
                                RecoveryBindings = caller.RecoveryBindings,
                                RecoveryContext = caller.RecoveryContext,
                                ValueBindings = caller.ValueBindings,
                            };
                        }
                        else if (callableSymbol is not null && caller.AbsentCallables?.Contains(callableSymbol) == true)
                        {
                            absentCallables.Add(targetParameter);
                        }
                        else if (caller.Model.GetConstantValue(argument.Expression) is { HasValue: true, Value: null })
                        {
                            absentCallables.Add(targetParameter);
                        }
                    }
                }
            }

            return target with
            {
                AdmissionBindings = bindings,
                CallableBindings = callableBindings,
                AbsentCallables = absentCallables,
                ConcreteBindings = concreteBindings,
                RecoveryBindings = recoveryBindings,
                RecoveryContext = caller.RecoveryContext,
                ValueBindings = valueBindings,
            };
        }

        private static bool RequiresExactValueProvenance(
            AuthoredMember target,
            IParameterSymbol parameter)
        {
            if (RequiresSourceProvenance(parameter.Type))
            {
                return true;
            }

            foreach (SyntaxNode node in target.Syntax.DescendantNodesAndSelf(
                candidate => candidate == target.Syntax
                    || candidate is not AnonymousFunctionExpressionSyntax
                        and not LocalFunctionStatementSyntax))
            {
                ExpressionSyntax? mutationTarget = node switch
                {
                    AssignmentExpressionSyntax assignment => assignment.Left,
                    PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                            || prefix.IsKind(SyntaxKind.PreDecrementExpression) =>
                        prefix.Operand,
                    PostfixUnaryExpressionSyntax postfix
                        when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                            || postfix.IsKind(SyntaxKind.PostDecrementExpression) =>
                        postfix.Operand,
                    _ => null,
                };

                if (mutationTarget is not null
                    && SymbolEqualityComparer.Default.Equals(
                        target.Model.GetSymbolInfo(
                            MutationRoot(mutationTarget)).Symbol,
                        parameter))
                {
                    return true;
                }

                if (node is ArgumentSyntax argument
                    && argument.RefKindKeyword.Kind() is
                        SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                    && SymbolEqualityComparer.Default.Equals(
                        target.Model.GetSymbolInfo(argument.Expression).Symbol,
                        parameter))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RequiresConstantControlProvenance(
            AuthoredMember target,
            IParameterSymbol parameter)
        {
            if (ExactConstantControlParameters.Contains(
                    Normalize(target.Symbol) + "#" + parameter.Name))
            {
                return true;
            }

            return target.Symbol.ExplicitInterfaceImplementations.Any(
                implementation => ExactConstantControlParameters.Contains(
                    TypeKey(implementation.ContainingType)
                        + "."
                        + implementation.Name
                        + "#"
                        + parameter.Name));
        }

        private static bool RequiresSourceProvenance(ITypeSymbol type)
        {
            string key = TypeKey(type);

            if (type is IArrayTypeSymbol
                || ReviewedInMemoryEnumerableTypes.Contains(key))
            {
                return false;
            }

            if (key is
                    "System.IO.Stream"
                        or "System.IO.StreamReader"
                        or "System.Collections.IEnumerable"
                        or "System.Collections.Generic.IEnumerable`1"
                        or "System.Collections.Generic.IAsyncEnumerable`1"
                        or "System.Collections.IEnumerator"
                        or "System.Collections.Generic.IEnumerator`1"
                        or "System.Collections.Generic.IAsyncEnumerator`1")
            {
                return true;
            }

            for (INamedTypeSymbol? current = type as INamedTypeSymbol;
                current is not null;
                current = current.BaseType)
            {
                if (TypeKey(current) is
                    "System.IO.Stream" or "System.IO.StreamReader")
                {
                    return true;
                }
            }

            return type is INamedTypeSymbol named
                && named.AllInterfaces.Any(static candidate =>
                    TypeKey(candidate) is
                        "System.Collections.IEnumerable"
                            or "System.Collections.Generic.IEnumerable`1"
                            or "System.Collections.Generic.IAsyncEnumerable`1"
                            or "System.Collections.IEnumerator"
                            or "System.Collections.Generic.IEnumerator`1"
                            or "System.Collections.Generic.IAsyncEnumerator`1");
        }

        private static ExpressionSyntax MutationRoot(ExpressionSyntax target)
        {
            target = StripTransparentExpression(target);

            while (target is MemberAccessExpressionSyntax member)
            {
                target = StripTransparentExpression(member.Expression);
            }

            while (target is ElementAccessExpressionSyntax element)
            {
                target = StripTransparentExpression(element.Expression);

                while (target is MemberAccessExpressionSyntax member)
                {
                    target = StripTransparentExpression(member.Expression);
                }
            }

            return target;
        }

        private RecoveryTuple? BoundRecoveryTuple(
            AuthoredMember member,
            ExpressionSyntax expression,
            HashSet<ISymbol> path)
        {
            expression = expression switch
            {
                ParenthesizedExpressionSyntax parentheses => parentheses.Expression,
                CastExpressionSyntax cast => cast.Expression,
                PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                    suppression.Operand,
                _ => expression,
            };

            ISymbol? symbol = member.Model.GetSymbolInfo(expression).Symbol;

            if (symbol is null || !path.Add(symbol))
            {
                return null;
            }

            try
            {
                if (member.RecoveryBindings?.TryGetValue(symbol, out RecoveryTuple bound) == true)
                {
                    return bound;
                }

                if (symbol is not ILocalSymbol local
                    || local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not
                        VariableDeclaratorSyntax variable)
                {
                    return null;
                }

                List<ExpressionSyntax> values = variable.Initializer is null
                    ? []
                    : [variable.Initializer.Value];

                if (variable.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is { } block)
                {
                    values.AddRange(block.DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                            && assignment.SpanStart < expression.SpanStart
                            && SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(assignment.Left).Symbol,
                                local))
                        .Select(static assignment => assignment.Right));
                }

                RecoveryTuple[] candidates = values
                    .SelectMany(value => BoundRecoveryTuple(member, value, path) is { } tuple
                        ? [tuple]
                        : Array.Empty<RecoveryTuple>())
                    .Distinct()
                    .ToArray();

                return candidates is [RecoveryTuple exact]
                    ? exact
                    : null;
            }
            finally
            {
                path.Remove(symbol);
            }
        }

        private static IEnumerable<(ExpressionSyntax Expression, IParameterSymbol? Parameter, RefKind RefKind)> AdmissionArguments(AuthoredMember caller, InvocationExpressionSyntax call)
        {
            if (caller.Model.GetOperation(call) is IInvocationOperation invocation)
            {
                // Roslyn includes the reduced extension receiver as an implicit argument to formal 0.
                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (argument.ArgumentKind == ArgumentKind.DefaultValue)
                    {
                        continue;
                    }

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

        private static IEnumerable<(ExpressionSyntax Expression, IParameterSymbol? Parameter, RefKind RefKind)> ConstructorArguments(
            AuthoredMember caller,
            BaseObjectCreationExpressionSyntax creation)
        {
            if (caller.Model.GetOperation(creation) is IObjectCreationOperation operation)
            {
                foreach (IArgumentOperation argument in operation.Arguments)
                {
                    if (argument.ArgumentKind == ArgumentKind.DefaultValue)
                    {
                        continue;
                    }

                    if ((argument.Syntax is ArgumentSyntax syntax
                            ? syntax.Expression
                            : argument.Value.Syntax) is ExpressionSyntax expression)
                    {
                        yield return (
                            expression,
                            argument.Parameter,
                            argument.Parameter?.RefKind ?? RefKind.None);
                    }
                }

                yield break;
            }

            foreach (ArgumentSyntax argument in creation.ArgumentList?.Arguments ?? default)
            {
                yield return (
                    argument.Expression,
                    null,
                    argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                        ? RefKind.Out
                        : argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                            ? RefKind.Ref
                            : RefKind.None);
            }
        }

        private static ExpressionSyntax? AdmissionReceiver(AuthoredMember member, InvocationExpressionSyntax call) => member.Model.GetOperation(call) is IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver } ? receiver : null;

        private bool MayCarryAdmissionHandle(AuthoredMember member, ExpressionSyntax expression)
        {
            if (AdmissionOrigins(member, expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Count == 0)
            {
                return false;
            }

            ITypeSymbol? type = member.Model.GetTypeInfo(expression).Type;

            if (CanCarryAdmissionHandle(type, []))
            {
                return true;
            }

            IEnumerable<ExpressionSyntax> storedValues = expression switch
            {
                BaseObjectCreationExpressionSyntax creation => (creation.ArgumentList?.Arguments.Select(static argument => argument.Expression) ?? []).Concat(creation.Initializer?.Expressions.Select(static value => value is AssignmentExpressionSyntax assignment ? assignment.Right : value) ?? []),
                AnonymousObjectCreationExpressionSyntax anonymous => anonymous.Initializers.Select(static initializer => initializer.Expression),
                _ => [],
            };

            return storedValues.Any(value => MayCarryAdmissionHandle(member, value));
        }

        private static bool CanCarryAdmissionHandle(ITypeSymbol? type, HashSet<ITypeSymbol> path)
        {
            if (type is null || !path.Add(type))
            {
                return false;
            }

            if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic || TypeKey(type) is "System.IDisposable" or "System.IAsyncDisposable" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease")
            {
                return true;
            }

            if (type is IArrayTypeSymbol array)
            {
                return CanCarryAdmissionHandle(array.ElementType, path);
            }

            if (type is INamedTypeSymbol named)
            {
                return named.AllInterfaces.Any(static contract => TypeKey(contract) is "System.IDisposable" or "System.IAsyncDisposable") || named.TypeArguments.Any(argument => CanCarryAdmissionHandle(argument, path)) || named.IsTupleType && named.TupleElements.Any(element => CanCarryAdmissionHandle(element.Type, path));
            }

            return false;
        }

        private static bool PreservesAdmissionReceiver(IMethodSymbol method) => Normalize(method) == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

        private static bool IsCompletionCombinator(IMethodSymbol method) => Normalize(method) is
            "System.Threading.Tasks.Task.ConfigureAwait"
            or "System.Threading.Tasks.Task`1.ConfigureAwait"
            or "System.Threading.Tasks.ValueTask.ConfigureAwait"
            or "System.Threading.Tasks.ValueTask`1.ConfigureAwait"
            or "System.Threading.Tasks.Task.WaitAsync"
            or "System.Threading.Tasks.Task`1.WaitAsync"
            or "System.Threading.Tasks.ValueTask.AsTask"
            or "System.Threading.Tasks.ValueTask`1.AsTask"
            or "System.Threading.Tasks.Task.GetAwaiter"
            or "System.Threading.Tasks.Task`1.GetAwaiter"
            or "System.Threading.Tasks.ValueTask.GetAwaiter"
            or "System.Threading.Tasks.ValueTask`1.GetAwaiter"
            or "System.Runtime.CompilerServices.TaskAwaiter.GetResult"
            or "System.Runtime.CompilerServices.TaskAwaiter`1.GetResult"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter`1.GetResult";

        private static bool ConsumesAdmissionReceiver(IMethodSymbol method)
        {
            if (method.Parameters.Length != 0 || method.Name is not ("Dispose" or "DisposeAsync"))
            {
                return false;
            }

            foreach (INamedTypeSymbol contract in method.ContainingType.AllInterfaces.Where(static contract => TypeKey(contract) is "System.IDisposable" or "System.IAsyncDisposable"))
            {
                IMethodSymbol? slot = contract.GetMembers(method.Name).OfType<IMethodSymbol>().SingleOrDefault(static candidate => candidate.Parameters.Length == 0);

                if (slot is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(slot)?.OriginalDefinition, method.OriginalDefinition))
                {
                    return true;
                }
            }

            return TypeKey(method.ContainingType) is "System.IDisposable" or "System.IAsyncDisposable";
        }

        private SyntaxNode? CancelledTimerRaceCompletionPoint(
            AuthoredMember member,
            InvocationExpressionSyntax source)
        {
            if (member.Model.GetSymbolInfo(source).Symbol is not IMethodSymbol delayMethod
                || Normalize(delayMethod) != "System.Threading.Tasks.Task.Delay"
                || member.Model.GetOperation(source) is not IInvocationOperation delayOperation
                || delayOperation.Arguments.SingleOrDefault(static argument =>
                    argument.Parameter?.Name == "cancellationToken") is not { } tokenArgument
                || (tokenArgument.Syntax is ArgumentSyntax tokenSyntax
                    ? tokenSyntax.Expression
                    : tokenArgument.Value.Syntax) is not ExpressionSyntax tokenExpression
                || StripTransparentExpression(tokenExpression)
                    is not MemberAccessExpressionSyntax
                    {
                        Expression: { } cancellationReceiver,
                        Name.Identifier.ValueText: "Token",
                    }
                || member.Model.GetSymbolInfo(cancellationReceiver).Symbol
                    is not ILocalSymbol cancellationSource
                || TypeKey(cancellationSource.Type)
                    != "System.Threading.CancellationTokenSource"
                || source.Parent is not AssignmentExpressionSyntax
                    {
                        Right: { } assigned,
                        Left: { } assignedTarget,
                        Parent: ExpressionStatementSyntax
                        {
                            Parent: BlockSyntax loopBlock,
                        } assignmentStatement,
                    } taskAssignment
                || assigned != source
                || loopBlock.Parent is not WhileStatementSyntax
                || member.Model.GetSymbolInfo(assignedTarget).Symbol
                    is not ILocalSymbol pendingTask
                || pendingTask.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax()
                    is not VariableDeclaratorSyntax
                    {
                        Initializer.Value: { } initialValue,
                        Parent.Parent: LocalDeclarationStatementSyntax
                        {
                            Parent: BlockSyntax lifetimeBlock,
                        } declarationStatement,
                    }
                || member.Model.GetConstantValue(initialValue) is not
                    {
                        HasValue: true,
                        Value: null,
                    }
                || taskAssignment.Ancestors().OfType<TryStatementSyntax>()
                    .FirstOrDefault(candidate => candidate.Parent == lifetimeBlock)
                    is not TryStatementSyntax
                    {
                        Finally.Block.Statements: [TryStatementSyntax cancellationCleanup],
                    } protectedLifetime
                || declarationStatement.SpanStart >= protectedLifetime.SpanStart)
            {
                return null;
            }

            bool RefersTo(ExpressionSyntax expression, ISymbol symbol) =>
                SymbolEqualityComparer.Default.Equals(
                    member.Model.GetSymbolInfo(
                        StripTransparentExpression(expression)).Symbol,
                    symbol);

            AssignmentExpressionSyntax[] writes = lifetimeBlock
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(write => RefersTo(write.Left, pendingTask))
                .ToArray();

            if (writes is not
                [AssignmentExpressionSyntax exactAssignment, AssignmentExpressionSyntax reset]
                || exactAssignment != taskAssignment
                || member.Model.GetConstantValue(reset.Right) is not
                    {
                        HasValue: true,
                        Value: null,
                    }
                || reset.Parent is not ExpressionStatementSyntax resetStatement
                || resetStatement.Parent != loopBlock)
            {
                return null;
            }

            int resetIndex = loopBlock.Statements.IndexOf(resetStatement);

            if (resetIndex <= 0
                || loopBlock.Statements[resetIndex - 1]
                    is not ExpressionStatementSyntax
                    {
                        Expression: AwaitExpressionSyntax directJoin,
                    }
                || !directJoin.DescendantNodesAndSelf()
                    .OfType<ExpressionSyntax>()
                    .Any(expression => RefersTo(expression, pendingTask)
                        && CompletionPreservingExpression(expression, member.Model)
                            .Parent == directJoin)
                || loopBlock.Statements
                    .Where(statement => statement.SpanStart > assignmentStatement.Span.End
                        && statement.Span.End < resetStatement.SpanStart)
                    .Any(statement => statement.DescendantNodesAndSelf()
                        .Any(static node => node is ContinueStatementSyntax
                            or GotoStatementSyntax)))
            {
                return null;
            }

            if (cancellationCleanup.Block.Statements is not
                [ExpressionStatementSyntax
                {
                    Expression: InvocationExpressionSyntax cancelCall,
                }]
                || member.Model.GetSymbolInfo(cancelCall).Symbol
                    is not IMethodSymbol cancelMethod
                || Normalize(cancelMethod)
                    != "System.Threading.CancellationTokenSource.Cancel"
                || cancelCall.Expression is not MemberAccessExpressionSyntax
                    {
                        Expression: { } cancelledReceiver,
                    }
                || !RefersTo(cancelledReceiver, cancellationSource)
                || cancellationCleanup.Catches.Count != 0
                || cancellationCleanup.Finally?.Block.Statements is not
                    [IfStatementSyntax observationGuard]
                || observationGuard.Condition is not IsPatternExpressionSyntax
                    {
                        Expression: { } testedTask,
                        Pattern: UnaryPatternSyntax
                        {
                            Pattern: ConstantPatternSyntax
                            {
                                Expression.RawKind:
                                    (int)SyntaxKind.NullLiteralExpression,
                            },
                        },
                    }
                || !RefersTo(testedTask, pendingTask)
                || observationGuard.Statement is not BlockSyntax
                    {
                        Statements:
                        [
                            ExpressionStatementSyntax
                            {
                                Expression: AwaitExpressionSyntax observedCompletion,
                            },
                        ],
                    })
            {
                return null;
            }

            InvocationExpressionSyntax[] observerCalls = observedCompletion
                .DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => call.ArgumentList.Arguments.Any(argument =>
                    RefersTo(argument.Expression, pendingTask)))
                .ToArray();

            if (observerCalls is not [InvocationExpressionSyntax observerCall]
                || member.Model.GetSymbolInfo(observerCall).Symbol
                    is not IMethodSymbol observerMethod
                || ResolveInvocationTarget(observerMethod, member, observerCall)
                    is not { } observer
                || observerCall.ArgumentList.Arguments.SingleOrDefault(argument =>
                    RefersTo(argument.Expression, pendingTask)) is not { } observedArgument
                || member.Model.GetOperation(observedArgument) is not IArgumentOperation
                    {
                        Parameter: { } suppliedParameter,
                    }
                || suppliedParameter.Ordinal >= observer.Symbol.Parameters.Length)
            {
                return null;
            }

            IParameterSymbol observedParameter =
                observer.Symbol.Parameters[suppliedParameter.Ordinal];

            if (SymbolReferences(observer, observedParameter)
                    is not [ExpressionSyntax observedReference]
                || CompletionPreservingExpression(observedReference, observer.Model)
                    .Parent is not AwaitExpressionSyntax observerJoin
                || observerJoin.Ancestors()
                    .TakeWhile(ancestor => ancestor != observer.Syntax)
                    .Any(static ancestor => ancestor is IfStatementSyntax
                        or ConditionalExpressionSyntax
                        or SwitchStatementSyntax
                        or SwitchExpressionSyntax
                        or ForStatementSyntax
                        or ForEachStatementSyntax
                        or ForEachVariableStatementSyntax
                        or WhileStatementSyntax
                        or DoStatementSyntax
                        or AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax)
                || lifetimeBlock.DescendantNodes()
                    .OfType<ArgumentSyntax>()
                    .Any(argument => argument.RefKindKeyword.Kind() is
                            SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                        && RefersTo(argument.Expression, pendingTask)))
            {
                return null;
            }

            return observedCompletion;
        }

        private SyntaxNode? RetainedQueueRaceCompletionPoint(
            AuthoredMember member,
            InvocationExpressionSyntax source)
        {
            const string ownerType =
                "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService";

            if (TypeKey(member.Symbol.ContainingType) != ownerType
                || member.Symbol.Name != "WaitForWorkAsync"
                || member.Model.GetSymbolInfo(source).Symbol is not IMethodSymbol delay
                || Normalize(delay) != "System.Threading.Tasks.Task.Delay"
                || source.Parent is not AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.CoalesceAssignmentExpression,
                    Left: MemberAccessExpressionSyntax
                    {
                        Expression: { } waitReceiver,
                        Name.Identifier.ValueText: "PendingPeriod",
                    },
                }
                || member.Model.GetSymbolInfo(waitReceiver).Symbol
                    is not IParameterSymbol waitParameter
                || waitParameter.Name != "wait")
            {
                return null;
            }

            bool IsWaitProperty(
                AuthoredMember context,
                ExpressionSyntax expression,
                ISymbol wait,
                string propertyName) =>
                expression is MemberAccessExpressionSyntax
                {
                    Expression: { } receiver,
                    Name.Identifier.ValueText: { } name,
                }
                && name == propertyName
                && SymbolEqualityComparer.Default.Equals(
                    context.Model.GetSymbolInfo(receiver).Symbol,
                    wait);

            InvocationExpressionSyntax[] races = member.Syntax.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => member.Model.GetSymbolInfo(call).Symbol
                    is IMethodSymbol method
                    && Normalize(method) == "System.Threading.Tasks.Task.WhenAny"
                    && call.FirstAncestorOrSelf<AwaitExpressionSyntax>() is not null
                    && call.ArgumentList.DescendantNodes()
                        .OfType<ExpressionSyntax>()
                        .Any(expression => IsWaitProperty(
                            member,
                            expression,
                            waitParameter,
                            "PendingRead"))
                    && call.ArgumentList.DescendantNodes()
                        .OfType<ExpressionSyntax>()
                        .Any(expression => IsWaitProperty(
                            member,
                            expression,
                            waitParameter,
                            "PendingPeriod")))
                .ToArray();

            AuthoredMember[] observers = AuthoredMembers
                .Where(candidate => TypeKey(candidate.Symbol.ContainingType) == ownerType
                    && candidate.Symbol.Name == "ObserveQueueWaitCompletionAsync")
                .ToArray();

            if (races is not [InvocationExpressionSyntax]
                || observers is not [AuthoredMember observer]
                || observer.Symbol.Parameters.SingleOrDefault(static parameter =>
                    parameter.Name == "wait") is not { } observedWait)
            {
                return null;
            }

            InvocationExpressionSyntax[] aggregateJoins = observer.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => observer.Model.GetSymbolInfo(call).Symbol
                    is IMethodSymbol method
                    && Normalize(method) == "System.Threading.Tasks.Task.WhenAll"
                    && call.FirstAncestorOrSelf<AwaitExpressionSyntax>() is not null)
                .ToArray();

            bool clearsBoth = new[] { "PendingRead", "PendingPeriod" }
                .All(propertyName => observer.Syntax.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Any(assignment => assignment.Ancestors()
                            .OfType<FinallyClauseSyntax>()
                            .Any()
                        && IsWaitProperty(
                            observer,
                            assignment.Left,
                            observedWait,
                            propertyName)
                        && observer.Model.GetConstantValue(assignment.Right) is
                            {
                                HasValue: true,
                                Value: null,
                            }));

            if (aggregateJoins is not [InvocationExpressionSyntax]
                || !clearsBoth)
            {
                return null;
            }

            foreach (AuthoredMember owner in AuthoredMembers.Where(candidate =>
                TypeKey(candidate.Symbol.ContainingType) == ownerType
                && candidate.Symbol.Name == "ExecuteAsync"))
            {
                InvocationExpressionSyntax[] waitCalls = owner.Syntax
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => owner.Model.GetSymbolInfo(call).Symbol
                        is IMethodSymbol method
                        && method.Name == "WaitForWorkAsync"
                        && SymbolEqualityComparer.Default.Equals(
                            method.ContainingType,
                            owner.Symbol.ContainingType))
                    .ToArray();

                if (waitCalls is not [InvocationExpressionSyntax waitCall]
                    || waitCall.FirstAncestorOrSelf<AwaitExpressionSyntax>() is null
                    || owner.Model.GetOperation(waitCall) is not IInvocationOperation waitOperation
                    || waitOperation.Arguments.SingleOrDefault(static argument =>
                        argument.Parameter?.Name == "wait") is not { } suppliedWait
                    || (suppliedWait.Syntax is ArgumentSyntax waitArgument
                        ? waitArgument.Expression
                        : suppliedWait.Value.Syntax) is not ExpressionSyntax waitExpression
                    || owner.Model.GetSymbolInfo(waitExpression).Symbol is not
                        ILocalSymbol waitLocal)
                {
                    continue;
                }

                InvocationExpressionSyntax[] observed = owner.Syntax
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => owner.Model.GetSymbolInfo(call).Symbol
                            is IMethodSymbol method
                        && method.Name == "ObserveQueueWaitCompletionAsync"
                        && SymbolEqualityComparer.Default.Equals(
                            method.ContainingType,
                            owner.Symbol.ContainingType)
                        && call.FirstAncestorOrSelf<AwaitExpressionSyntax>() is not null
                        && call.Ancestors().OfType<FinallyClauseSyntax>().Any()
                        && call.ArgumentList.Arguments.Any(argument =>
                            SymbolEqualityComparer.Default.Equals(
                                owner.Model.GetSymbolInfo(argument.Expression).Symbol,
                                waitLocal)))
                    .ToArray();

                bool cancelsLifetime = owner.Syntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(call => owner.Model.GetSymbolInfo(call).Symbol
                            is IMethodSymbol method
                        && Normalize(method)
                            == "System.Threading.CancellationTokenSource.CancelAsync"
                        && call.FirstAncestorOrSelf<AwaitExpressionSyntax>() is not null
                        && call.Ancestors().OfType<FinallyClauseSyntax>().Any());

                if (observed is [InvocationExpressionSyntax]
                    && cancelsLifetime)
                {
                    return source;
                }
            }

            return null;
        }

        private SyntaxNode? TrackedTaskCompletionPoint(
            AuthoredMember member,
            ILocalSymbol task,
            VariableDeclaratorSyntax declaration,
            BlockSyntax block)
        {
            if (declaration.Parent?.Parent is not LocalDeclarationStatementSyntax declarationStatement)
            {
                return null;
            }

            int declarationIndex = block.Statements.IndexOf(declarationStatement);

            int protectedLifetimeIndex = declarationIndex + 1;

            while (protectedLifetimeIndex < block.Statements.Count
                && block.Statements[protectedLifetimeIndex]
                    is LocalDeclarationStatementSyntax
                    {
                        UsingKeyword.RawKind: 0,
                        Declaration.Variables.Count: > 0,
                    } inertDeclaration
                && inertDeclaration.Declaration.Variables.All(variable =>
                    variable.Initializer?.Value is { } initializer
                    && (initializer is DefaultExpressionSyntax
                        || initializer.IsKind(SyntaxKind.DefaultLiteralExpression)
                        || member.Model.GetConstantValue(initializer) is
                            {
                                HasValue: true,
                                Value: null,
                            })))
            {
                protectedLifetimeIndex++;
            }

            if (declarationIndex < 0
                || protectedLifetimeIndex >= block.Statements.Count
                || block.Statements[protectedLifetimeIndex] is not TryStatementSyntax
                {
                    Catches.Count: > 0,
                } protectedLifetime)
            {
                return null;
            }

            bool IsDirectTaskJoin(ExpressionSyntax reference)
            {
                SyntaxNode expression = CompletionPreservingExpression(reference, member.Model);

                return expression.Parent is AwaitExpressionSyntax;
            }

            bool IsExactObserverCall(InvocationExpressionSyntax observerCall)
            {
                if (member.Model.GetSymbolInfo(observerCall).Symbol is not IMethodSymbol observerMethod
                    || !IsAwaitable(observerMethod.ReturnType)
                    || CompletionPoint(member, observerCall, false) is null
                    || Resolve(observerMethod, member.Model.Compilation) is not { } observer
                    || observerCall.ArgumentList.Arguments.SingleOrDefault(argument =>
                        member.Model.GetOperation(argument) is IArgumentOperation
                        {
                            Parameter: { } parameter,
                        }
                        && parameter.Ordinal < observer.Symbol.Parameters.Length
                        && TypeKey(observer.Symbol.Parameters[parameter.Ordinal].Type)
                            is "System.Threading.Tasks.Task"
                                or "System.Threading.Tasks.Task`1") is not { } taskArgument
                    || !SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(taskArgument.Expression).Symbol,
                        task)
                    || member.Model.GetOperation(taskArgument) is not IArgumentOperation
                    {
                        Parameter: { } sourceParameter,
                    })
                {
                    return false;
                }

                IParameterSymbol observerParameter = observer.Symbol.Parameters[sourceParameter.Ordinal];

                ExpressionSyntax[] observerReferences = SymbolReferences(observer, observerParameter);

                return observerReferences is [ExpressionSyntax observed]
                    && IsDirectObserverJoin(observer, observed);
            }

            static bool IsDirectObserverJoin(
                AuthoredMember observer,
                ExpressionSyntax reference)
            {
                SyntaxNode expression = CompletionPreservingExpression(
                    reference,
                    observer.Model);

                if (expression.Parent is not AwaitExpressionSyntax awaited)
                {
                    return false;
                }

                return !awaited.Ancestors().TakeWhile(ancestor => ancestor != observer.Syntax)
                    .Any(static ancestor => ancestor is IfStatementSyntax
                        or ConditionalExpressionSyntax
                        or SwitchStatementSyntax
                        or SwitchExpressionSyntax
                        or ForStatementSyntax
                        or ForEachStatementSyntax
                        or ForEachVariableStatementSyntax
                        or WhileStatementSyntax
                        or DoStatementSyntax
                        or AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax);
            }

            bool HasExceptionalJoin(CatchClauseSyntax caught)
            {
                if (caught.Filter is not null
                    || caught.Declaration?.Type is { } caughtType
                        && member.Model.GetTypeInfo(caughtType).Type is { } exceptionType
                        && TypeKey(exceptionType) != "System.Exception")
                {
                    return false;
                }

                foreach (TryStatementSyntax cleanup in caught.Block.Statements
                    .OfType<TryStatementSyntax>())
                {
                    if (cleanup.Finally?.Block.Statements is not
                        [ExpressionStatementSyntax observationStatement]
                        || observationStatement.Expression.DescendantNodesAndSelf()
                            .OfType<InvocationExpressionSyntax>()
                            .Where(IsExactObserverCall)
                            .ToArray() is not [InvocationExpressionSyntax]
                        || caught.Block.Statements.TakeWhile(statement => statement != cleanup)
                            .Any(static statement => statement is ReturnStatementSyntax
                                or ThrowStatementSyntax
                                or GotoStatementSyntax))
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }

            if (protectedLifetime.Catches.Any(caught => !HasExceptionalJoin(caught)))
            {
                return null;
            }

            ReturnStatementSyntax[] normalReturns = protectedLifetime.Block.DescendantNodes(
                    static node => node is not AnonymousFunctionExpressionSyntax
                        and not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>()
                .ToArray();

            if (normalReturns.Any(returned => returned.Expression is null
                || returned.Expression.DescendantNodesAndSelf()
                    .OfType<ExpressionSyntax>()
                    .Count(reference => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(reference).Symbol,
                        task)
                        && IsDirectTaskJoin(reference)) != 1))
            {
                return null;
            }

            bool fallsThrough = protectedLifetime.Block.Statements.LastOrDefault()
                is not ReturnStatementSyntax
                and not ThrowStatementSyntax;

            if (fallsThrough)
            {
                if (protectedLifetime.Block.Statements.LastOrDefault()
                    is not ExpressionStatementSyntax terminal)
                {
                    return null;
                }

                if (terminal.Expression.DescendantNodesAndSelf()
                    .OfType<ExpressionSyntax>()
                    .Count(reference => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(reference).Symbol,
                        task)
                        && IsDirectTaskJoin(reference)) != 1)
                {
                    return null;
                }
            }

            foreach (ExpressionSyntax reference in SymbolReferences(member, task))
            {
                if (IsDirectTaskJoin(reference)
                    || reference.Parent is MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "IsCompleted",
                    }
                    || reference.Parent is ArgumentSyntax argument
                        && argument.Parent?.Parent is InvocationExpressionSyntax use
                        && member.Model.GetSymbolInfo(use).Symbol is IMethodSymbol useMethod
                        && (Normalize(useMethod) is "System.Threading.Tasks.Task.WhenAny"
                            or "System.Object.ReferenceEquals"
                            || IsExactObserverCall(use)))
                {
                    continue;
                }

                return null;
            }

            return protectedLifetime;
        }

        private SyntaxNode? CompletionPoint(AuthoredMember member, InvocationExpressionSyntax call, bool inheritedCompletion)
        {
            SyntaxNode expression = CompletionPreservingExpression(call, member.Model);

            bool returnedCompletionOwned = inheritedCompletion || IsLifecycle(member.Symbol);

            if (expression.Parent is AwaitExpressionSyntax awaited)
            {
                return awaited;
            }

            if (ExactSynchronousCompletion(expression, member.Model) is { } synchronous)
            {
                return synchronous;
            }

            if (CancelledTimerRaceCompletionPoint(member, call) is { } timerJoin)
            {
                return timerJoin;
            }

            if (RetainedQueueRaceCompletionPoint(member, call) is { } queueJoin)
            {
                return queueJoin;
            }

            if (returnedCompletionOwned && (expression.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax || member.Syntax is MethodDeclarationSyntax { ExpressionBody.Expression: { } body } && body == expression || expression == member.Syntax && !member.Symbol.ReturnsVoid))
            {
                return call;
            }

            if (returnedCompletionOwned && expression.Parent is AssignmentExpressionSyntax { Right: { } right } assignment && right == expression && assignment.Parent is ExpressionStatementSyntax assignmentStatement && assignmentStatement.Parent is BlockSyntax assignmentBlock && member.Model.GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol field)
            {
                int assignmentIndex = assignmentBlock.Statements.IndexOf(assignmentStatement);

                if (assignmentIndex >= 0 && assignmentIndex + 1 < assignmentBlock.Statements.Count && assignmentBlock.Statements[assignmentIndex + 1] is ReturnStatementSyntax { Expression: { } returned } returnStatement)
                {
                    ExpressionSyntax[] references = returned.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Where(candidate => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(candidate).Symbol, field) && !candidate.Ancestors().TakeWhile(ancestor => ancestor != returned).OfType<ExpressionSyntax>().Any(parent => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(parent).Symbol, field))).ToArray();

                    if (references is [ExpressionSyntax reference] && CompletionPreservingExpression(reference, member.Model).Parent == returnStatement)
                    {
                        return returnStatement;
                    }
                }
            }

            if (expression.Parent is AssignmentExpressionSyntax
                {
                    Right: { } assigned,
                    Left: { } assignedTarget,
                    Parent: ExpressionStatementSyntax localAssignmentStatement,
                } localAssignment
                && assigned == expression
                && localAssignmentStatement.Parent is BlockSyntax localAssignmentBlock
                && member.Model.GetSymbolInfo(assignedTarget).Symbol is ILocalSymbol assignedLocal
                && assignedLocal.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is
                    VariableDeclaratorSyntax
                    {
                        Initializer: null,
                        Parent.Parent: LocalDeclarationStatementSyntax localDeclaration,
                    }
                && localDeclaration.Parent == localAssignmentBlock)
            {
                AssignmentExpressionSyntax[] localWrites = localAssignmentBlock
                    .DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(write => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(write.Left).Symbol,
                        assignedLocal))
                    .ToArray();

                ExpressionSyntax[] localReferences = localAssignmentBlock
                    .DescendantNodes()
                    .OfType<ExpressionSyntax>()
                    .Where(reference => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(reference).Symbol,
                        assignedLocal))
                    .Where(reference => !reference.Ancestors()
                        .TakeWhile(ancestor => ancestor != localAssignmentBlock)
                        .OfType<ExpressionSyntax>()
                        .Any(parent => SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(parent).Symbol,
                            assignedLocal)))
                    .ToArray();

                ExpressionSyntax[] completionReferences = localReferences
                    .Where(reference => CompletionPreservingExpression(reference, member.Model)
                        .Parent is AwaitExpressionSyntax)
                    .ToArray();

                ExpressionSyntax[] publications = localReferences
                    .Where(reference => reference != assignedTarget
                        && !completionReferences.Contains(reference))
                    .ToArray();

                bool exactPublications = publications.All(reference =>
                    reference.Parent is AssignmentExpressionSyntax
                    {
                        Right: { } publication,
                        Left: { } publicationTarget,
                    } write
                    && publication == reference
                    && member.Model.GetSymbolInfo(publicationTarget).Symbol is IFieldSymbol
                    {
                        Type: { } fieldType,
                    }
                    && TypeKey(fieldType) == "System.Threading.Tasks.Task"
                    && write.Parent is ExpressionStatementSyntax
                    {
                        Parent: { } publicationBlock,
                    }
                    && publicationBlock == localAssignmentBlock);

                if (localWrites is [AssignmentExpressionSyntax exactWrite]
                    && exactWrite == localAssignment
                    && completionReferences is [ExpressionSyntax completionReference]
                    && exactPublications)
                {
                    SyntaxNode completed = CompletionPreservingExpression(
                        completionReference,
                        member.Model);

                    if (completed.Parent is AwaitExpressionSyntax awaitedCompletion
                        && awaitedCompletion.Parent is ExpressionStatementSyntax completionStatement
                        && completionStatement.Parent == localAssignmentBlock
                        && localAssignmentBlock.Statements
                            .Where(statement => statement.SpanStart > localAssignmentStatement.SpanStart
                                && statement.Span.End < completionStatement.SpanStart)
                            .All(statement => statement.DescendantNodesAndSelf()
                                .OfType<AssignmentExpressionSyntax>()
                                .Any(write => publications.Any(reference => write.Right == reference))))
                    {
                        return awaitedCompletion;
                    }
                }
            }

            if (expression.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol local || variable.Parent?.Parent is not LocalDeclarationStatementSyntax { Parent: BlockSyntax block })
            {
                return null;
            }

            IdentifierNameSyntax[] uses = block.DescendantNodes().OfType<IdentifierNameSyntax>().Where(identifier => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(identifier).Symbol, local)).ToArray();

            if (ExactTaskWhenAllCompletion(
                    member,
                    call,
                    uses,
                    inheritedCompletion) is { } aggregateCompletion)
            {
                return aggregateCompletion;
            }

            if (ExactTaskCollectionWhenAllCompletion(
                    member,
                    call,
                    uses,
                    inheritedCompletion) is { } collectionCompletion)
            {
                return collectionCompletion;
            }

            if (uses is not [IdentifierNameSyntax use])
            {
                return TrackedTaskCompletionPoint(member, local, variable, block);
            }

            if (ExactTaskJoinHelperCompletion(
                    member,
                    call,
                    use,
                    inheritedCompletion) is { } helperCompletion)
            {
                return helperCompletion;
            }

            SyntaxNode retained = CompletionPreservingExpression(use, member.Model);

            if (returnedCompletionOwned
                && retained.Parent is ReturnStatementSyntax returnedStatement
                && returnedStatement.Expression == retained
                && !block.Statements.Any(statement =>
                    statement.SpanStart > call.SpanStart
                        && statement.Span.End < returnedStatement.SpanStart))
            {
                return returnedStatement;
            }

            SyntaxNode? join = retained.Parent is AwaitExpressionSyntax awaitedUse
                ? awaitedUse
                : ExactSynchronousCompletion(retained, member.Model);

            if (join?.Parent is not ExpressionStatementSyntax statement
                || statement.Parent != block
                || statement.Expression != join
                || join.SpanStart < call.Span.End)
            {
                return null;
            }

            if (use.Ancestors().TakeWhile(ancestor => ancestor != join).Any(static ancestor => ancestor is ConditionalExpressionSyntax or SwitchExpressionSyntax || ancestor is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.CoalesceExpression)))
            {
                return null;
            }

            // An intervening call can throw before the join and unwind the retained handles.
            return block.Statements.Any(statement => statement.SpanStart > call.SpanStart && statement.Span.End < join.SpanStart) ? null : join;
        }

        private SyntaxNode? ExactTaskJoinHelperCompletion(
            AuthoredMember caller,
            InvocationExpressionSyntax source,
            IdentifierNameSyntax taskReference,
            bool inheritedCompletion)
        {
            if (taskReference.Parent is not ArgumentSyntax argument
                || argument.Parent?.Parent is not InvocationExpressionSyntax helperCall
                || caller.Model.GetOperation(argument) is not IArgumentOperation
                {
                    Parameter: { } suppliedParameter,
                }
                || caller.Model.GetSymbolInfo(helperCall).Symbol is not IMethodSymbol helperMethod
                || ResolveInvocationTarget(helperMethod, caller, helperCall) is not { } helper
                || suppliedParameter.Ordinal >= helper.Symbol.Parameters.Length
                || CompletionPoint(caller, helperCall, inheritedCompletion) is null
                || !IsUnconditionalExecution(caller, helperCall, source.Span.End))
            {
                return null;
            }

            IParameterSymbol helperParameter = helper.Symbol.Parameters[suppliedParameter.Ordinal];

            if (SymbolReferences(helper, helperParameter) is not [ExpressionSyntax awaitedReference])
            {
                return null;
            }

            SyntaxNode retained = CompletionPreservingExpression(
                awaitedReference,
                helper.Model);

            if (retained.Parent is not AwaitExpressionSyntax awaited
                || awaited.Ancestors().TakeWhile(ancestor => ancestor != helper.Syntax)
                    .Any(static ancestor => ancestor is IfStatementSyntax
                        or ConditionalExpressionSyntax
                        or SwitchStatementSyntax
                        or SwitchExpressionSyntax
                        or ForStatementSyntax
                        or ForEachStatementSyntax
                        or ForEachVariableStatementSyntax
                        or WhileStatementSyntax
                        or DoStatementSyntax
                        or AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax)
                || helper.Syntax.DescendantNodes(candidate =>
                        candidate == helper.Syntax
                            || candidate is not AnonymousFunctionExpressionSyntax
                                and not LocalFunctionStatementSyntax)
                    .Any(candidate => candidate.Span.End < awaited.SpanStart
                        && candidate is ReturnStatementSyntax
                            or ThrowStatementSyntax
                            or GotoStatementSyntax))
            {
                return null;
            }

            return helperCall;
        }

        private SyntaxNode? ExactTaskWhenAllCompletion(
            AuthoredMember member,
            InvocationExpressionSyntax source,
            IReadOnlyList<IdentifierNameSyntax> taskReferences,
            bool inheritedCompletion)
        {
            InvocationExpressionSyntax[] aggregates = taskReferences
                .Select(static taskReference => taskReference.Parent is ArgumentSyntax
                {
                    Parent.Parent: InvocationExpressionSyntax aggregate,
                }
                    ? aggregate
                    : null)
                .OfType<InvocationExpressionSyntax>()
                .Where(aggregate => member.Model.GetSymbolInfo(aggregate).Symbol is IMethodSymbol aggregateMethod
                    && Normalize(aggregateMethod) == "System.Threading.Tasks.Task.WhenAll")
                .Distinct()
                .ToArray();

            if (aggregates is not [InvocationExpressionSyntax aggregate]
                || taskReferences.Count(reference => reference.Ancestors().Contains(aggregate)) != 1
                || CompletionPoint(member, aggregate, inheritedCompletion) is null
                || !IsUnconditionalExecution(member, aggregate, source.Span.End)
                    && !IsDirectlyObservedTryAggregation(source, aggregate))
            {
                return null;
            }

            foreach (IdentifierNameSyntax reference in taskReferences)
            {
                if (reference.Ancestors().Contains(aggregate))
                {
                    continue;
                }

                if (reference.SpanStart < aggregate.Span.End
                    || reference.Ancestors().TakeWhile(ancestor => ancestor != member.Syntax).Any(ancestor => ancestor switch
                    {
                        AssignmentExpressionSyntax assignment => assignment.Left.Span.Contains(reference.Span),
                        ArgumentSyntax argument => argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword,
                        PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression),
                        PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression),
                        RefExpressionSyntax => true,
                        _ => false,
                    }))
                {
                    return null;
                }
            }

            return aggregate;
        }

        private SyntaxNode? ExactTaskCollectionWhenAllCompletion(
            AuthoredMember member,
            InvocationExpressionSyntax source,
            IReadOnlyList<IdentifierNameSyntax> taskReferences,
            bool inheritedCompletion)
        {
            if (taskReferences is not [IdentifierNameSyntax taskReference]
                || taskReference.Parent is not ArgumentSyntax
                {
                    Parent.Parent: InvocationExpressionSyntax addCall,
                }
                || member.Model.GetSymbolInfo(addCall).Symbol is not IMethodSymbol addMethod
                || addMethod.Name != "Add"
                || TypeKey(addMethod.ContainingType)
                    != "System.Collections.Generic.List`1"
                || addCall.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: { } listReceiver,
                }
                || member.Model.GetSymbolInfo(listReceiver).Symbol
                    is not ILocalSymbol list
                || list.Type is not INamedTypeSymbol
                {
                    TypeArguments: [ITypeSymbol element],
                } listType
                || TypeKey(listType.OriginalDefinition)
                    != "System.Collections.Generic.List`1"
                || !IsAwaitable(element)
                || list.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax()
                    is not VariableDeclaratorSyntax
                    {
                        Initializer.Value: BaseObjectCreationExpressionSyntax,
                        Parent.Parent: LocalDeclarationStatementSyntax
                        {
                            Parent: BlockSyntax ownerBlock,
                        } listDeclaration,
                    })
            {
                return null;
            }

            ExpressionSyntax[] references = ownerBlock.DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Where(reference => SymbolEqualityComparer.Default.Equals(
                    member.Model.GetSymbolInfo(reference).Symbol,
                    list))
                .Where(reference => !reference.Ancestors()
                    .TakeWhile(ancestor => ancestor != ownerBlock)
                    .OfType<ExpressionSyntax>()
                    .Any(parent => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(parent).Symbol,
                        list)))
                .ToArray();

            InvocationExpressionSyntax[] additions = references
                .Select(static reference => reference.Parent
                    is MemberAccessExpressionSyntax
                    {
                        Parent: InvocationExpressionSyntax invocation,
                    }
                        ? invocation
                        : null)
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => member.Model.GetSymbolInfo(invocation).Symbol
                    is IMethodSymbol candidate
                    && candidate.Name == "Add"
                    && TypeKey(candidate.ContainingType)
                        == "System.Collections.Generic.List`1")
                .Distinct()
                .ToArray();

            InvocationExpressionSyntax[] aggregates = references
                .Select(static reference => reference.Parent is ArgumentSyntax
                    {
                        Parent.Parent: InvocationExpressionSyntax invocation,
                    }
                        ? invocation
                        : null)
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => member.Model.GetSymbolInfo(invocation).Symbol
                    is IMethodSymbol candidate
                    && Normalize(candidate)
                        == "System.Threading.Tasks.Task.WhenAll")
                .Distinct()
                .ToArray();

            if (additions is not [InvocationExpressionSyntax exactAdd]
                || exactAdd != addCall
                || aggregates is not [InvocationExpressionSyntax aggregate]
                || references.Any(reference =>
                    !reference.Ancestors().Contains(exactAdd)
                    && !reference.Ancestors().Contains(aggregate))
                || listDeclaration.Span.End >= source.SpanStart
                || aggregate.SpanStart <= source.Span.End
                || CompletionPoint(member, aggregate, inheritedCompletion) is null
                    && !IsReviewedSimulacrumAggregateJoin(member, aggregate))
            {
                return null;
            }

            return aggregate;
        }

        private static bool IsReviewedSimulacrumAggregateJoin(
            AuthoredMember member,
            InvocationExpressionSyntax aggregate)
        {
            if (TypeKey(member.Symbol.ContainingType)
                    != "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService"
                || member.Symbol.Name != "StartAndJoinBranchesAsync"
                || aggregate.Parent is not EqualsValueClauseSyntax
                {
                    Parent: VariableDeclaratorSyntax joinDeclaration,
                }
                || member.Model.GetDeclaredSymbol(joinDeclaration)
                    is not ILocalSymbol join)
            {
                return false;
            }

            ExpressionSyntax[] references = member.Syntax.DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Where(expression => SymbolEqualityComparer.Default.Equals(
                    member.Model.GetSymbolInfo(expression).Symbol,
                    join))
                .Where(expression => !expression.Ancestors()
                    .TakeWhile(ancestor => ancestor != member.Syntax)
                    .OfType<ExpressionSyntax>()
                    .Any(parent => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(parent).Symbol,
                        join)))
                .ToArray();

            return references.Count(reference =>
                    CompletionPreservingExpression(reference, member.Model)
                        .Parent is AwaitExpressionSyntax) == 2
                && references.All(reference =>
                    CompletionPreservingExpression(reference, member.Model)
                            .Parent is AwaitExpressionSyntax
                        || reference.Parent is MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Exception",
                        });
        }

        private static bool IsDirectlyObservedTryAggregation(
            InvocationExpressionSyntax source,
            InvocationExpressionSyntax aggregate)
        {
            if (aggregate.FirstAncestorOrSelf<AwaitExpressionSyntax>() is not { } awaited
                || awaited.FirstAncestorOrSelf<ExpressionStatementSyntax>() is not { } aggregationStatement
                || aggregationStatement.Parent is not BlockSyntax
                {
                    Parent: TryStatementSyntax protectedJoin,
                } protectedBlock
                || protectedJoin.Block != protectedBlock
                || protectedBlock.Statements.FirstOrDefault() != aggregationStatement
                || source.FirstAncestorOrSelf<StatementSyntax>() is not { Parent: BlockSyntax sourceBlock } sourceStatement
                || protectedJoin.Parent != sourceBlock)
            {
                return false;
            }

            int sourceIndex = sourceBlock.Statements.IndexOf(sourceStatement);

            int joinIndex = sourceBlock.Statements.IndexOf(protectedJoin);

            return sourceIndex >= 0
                && joinIndex > sourceIndex
                && !sourceBlock.Statements.Skip(sourceIndex + 1)
                    .Take(joinIndex - sourceIndex - 1)
                    .Any(static statement => statement is ReturnStatementSyntax
                        or ThrowStatementSyntax
                        or GotoStatementSyntax);
        }

        private static InvocationExpressionSyntax? ExactSynchronousCompletion(
            SyntaxNode expression,
            SemanticModel model)
        {
            if (expression is not ExpressionSyntax awaitable
                || model.GetTypeInfo(awaitable).Type is not { } awaitableType
                || !IsAwaitable(awaitableType)
                || expression.Parent is not MemberAccessExpressionSyntax
                {
                    Expression: { } getAwaiterReceiver,
                    Name.Identifier.ValueText: "GetAwaiter",
                    Parent: InvocationExpressionSyntax
                    {
                        ArgumentList.Arguments.Count: 0,
                    } getAwaiter,
                }
                || getAwaiterReceiver != expression
                || model.GetSymbolInfo(getAwaiter).Symbol is not IMethodSymbol
                {
                    Name: "GetAwaiter",
                    Parameters.Length: 0,
                } getAwaiterMethod
                || !SymbolEqualityComparer.Default.Equals(
                    getAwaiterMethod.ContainingType,
                    model.GetTypeInfo(awaitable).Type)
                || getAwaiter.Parent is not MemberAccessExpressionSyntax
                {
                    Expression: { } getResultReceiver,
                    Name.Identifier.ValueText: "GetResult",
                    Parent: InvocationExpressionSyntax
                    {
                        ArgumentList.Arguments.Count: 0,
                    } getResult,
                }
                || getResultReceiver != getAwaiter
                || model.GetSymbolInfo(getResult).Symbol is not IMethodSymbol
                {
                    Name: "GetResult",
                    Parameters.Length: 0,
                } getResultMethod
                || !SymbolEqualityComparer.Default.Equals(
                    getResultMethod.ContainingType,
                    getAwaiterMethod.ReturnType))
            {
                return null;
            }

            return getResult;
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
                else if (expression.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WaitAsync", Parent: InvocationExpressionSyntax waited } && model.GetSymbolInfo(waited).Symbol is IMethodSymbol waitMethod && Normalize(waitMethod) is "System.Threading.Tasks.Task.WaitAsync" or "System.Threading.Tasks.Task`1.WaitAsync")
                {
                    expression = waited;
                }
                else if (expression.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsTask", Parent: InvocationExpressionSyntax converted } && model.GetSymbolInfo(converted).Symbol is IMethodSymbol conversionMethod && Normalize(conversionMethod) is "System.Threading.Tasks.ValueTask.AsTask" or "System.Threading.Tasks.ValueTask`1.AsTask")
                {
                    expression = converted;
                }
                else if (expression.Parent is ArgumentSyntax
                    {
                        Parent.Parent: BaseObjectCreationExpressionSyntax
                        {
                            ArgumentList.Arguments: [_],
                        } valueTaskCreation,
                    } argument
                    && argument.Expression == expression
                    && model.GetSymbolInfo(valueTaskCreation).Symbol is IMethodSymbol valueTaskConstructor
                    && valueTaskConstructor.MethodKind == MethodKind.Constructor
                    && TypeKey(valueTaskConstructor.ContainingType)
                        == "System.Threading.Tasks.ValueTask"
                    && valueTaskConstructor.Parameters is
                        [IParameterSymbol { Type: { } taskParameter }]
                    && TypeKey(taskParameter) == "System.Threading.Tasks.Task")
                {
                    expression = valueTaskCreation;
                }
                else if (expression.Parent is ConditionalExpressionSyntax conditional && (conditional.WhenTrue == expression || conditional.WhenFalse == expression))
                {
                    expression = conditional;
                }
                else if (expression.Parent is SwitchExpressionArmSyntax { Expression: { } armExpression } arm && armExpression == expression && arm.Parent is SwitchExpressionSyntax switchExpression)
                {
                    expression = switchExpression;
                }
                else if (expression.Parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.CoalesceExpression))
                {
                    expression = binary;
                }
                else if (expression.Parent is CastExpressionSyntax cast)
                {
                    expression = cast;
                }
                else
                {
                    return expression;
                }
            }
        }

        private static ExpressionSyntax[] SymbolReferences(AuthoredMember member, ISymbol symbol) => member.Syntax.DescendantNodes().OfType<ExpressionSyntax>().Where(expression => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, symbol) && !expression.Ancestors().TakeWhile(ancestor => ancestor != member.Syntax).OfType<ExpressionSyntax>().Any(parent => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(parent).Symbol, symbol))).ToArray();

        private static bool HasOnlyReferences(AuthoredMember member, ISymbol symbol, params ExpressionSyntax[] allowed) => SymbolReferences(member, symbol) is { } references && references.Length == allowed.Length && references.All(reference => allowed.Contains(reference));

        private static bool IsAwaitable(ITypeSymbol type) => TypeKey(type) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask`1";

        private static bool IsExactWriterCompletion(IMethodSymbol method) => TypeKey(method.ContainingType) == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter"
            && method.Name == "CompleteAsync"
            && method.Parameters is [IParameterSymbol { IsOptional: true, Type: { } cancellation }] && TypeKey(cancellation) == "System.Threading.CancellationToken"
            && method.ReturnType is INamedTypeSymbol { TypeArguments: [ITypeSymbol result] } task && task.OriginalDefinition.ToDisplayString() == "System.Threading.Tasks.Task<TResult>" && TypeKey(result) == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobDescriptor";

        private static bool IsExactCleanupContract(IMethodSymbol method, ITypeSymbol receiverType)
        {
            bool synchronous = method.Name == "Dispose" && TypeKey(method.ReturnType) == "System.Void";

            bool asynchronous = method.Name == "DisposeAsync" && TypeKey(method.ReturnType) == "System.Threading.Tasks.ValueTask";

            if (method.Parameters.Length != 0 || method.IsStatic || !synchronous && !asynchronous)
            {
                return false;
            }

            string contract = synchronous ? "System.IDisposable" : "System.IAsyncDisposable";

            if (receiverType is not INamedTypeSymbol receiver)
            {
                return false;
            }

            INamedTypeSymbol? contractType = TypeKey(receiver) == contract && receiver.TypeKind == TypeKind.Interface
                ? receiver
                : receiver.AllInterfaces.SingleOrDefault(type => TypeKey(type) == contract);

            IMethodSymbol? slot = contractType?.GetMembers(method.Name).OfType<IMethodSymbol>().SingleOrDefault(static candidate => candidate.Parameters.Length == 0);

            if (slot is null)
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, slot.OriginalDefinition))
            {
                return true;
            }

            return receiver.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition);
        }

        private static bool IsExactWriterCleanup(IMethodSymbol method, ITypeSymbol receiverType)
        {
            if (TypeKey(receiverType) != "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter")
            {
                return false;
            }

            return IsExactCleanupContract(method, receiverType);
        }

        private static IMethodSymbol[] SelectCleanupMembers(ITypeSymbol type, string name)
        {
            for (INamedTypeSymbol? current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                IMethodSymbol[] candidates = current.GetMembers(name).OfType<IMethodSymbol>().Where(static method => method.Parameters.Length == 0).ToArray();

                if (candidates.Length != 0)
                {
                    return candidates;
                }
            }

            return [];
        }

        private static ExpressionStatementSyntax? ExactTerminalStatement(AuthoredMember member, InvocationExpressionSyntax call)
        {
            SyntaxNode expression = CompletionPreservingExpression(call, member.Model);

            if (expression.Parent is AwaitExpressionSyntax awaited)
            {
                expression = awaited;
            }

            return expression.Parent is ExpressionStatementSyntax statement && statement.Expression == expression ? statement : null;
        }

        private bool HasExactTerminalJoin(AuthoredMember member, InvocationExpressionSyntax call, IMethodSymbol method, bool inheritedCompletion)
        {
            if (TypeKey(method.ReturnType) == "System.Void")
            {
                return true;
            }

            return IsAwaitable(method.ReturnType) && CompletionPoint(member, call, inheritedCompletion) is not null;
        }

        private bool HasExactCompletionJoin(AuthoredMember member, InvocationExpressionSyntax call, IMethodSymbol method) => IsAwaitable(method.ReturnType) && CompletionPoint(member, call, false) is AwaitExpressionSyntax awaited && awaited.Parent is ExpressionStatementSyntax;

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

            bool IsSuccessfulPathOwnerCall(InvocationExpressionSyntax call)
            {
                if (variable.Parent?.Parent is not LocalDeclarationStatementSyntax
                    {
                        Parent: BlockSyntax block,
                    } declaration
                    || call.Ancestors().OfType<StatementSyntax>().FirstOrDefault(statement =>
                        statement.Parent == block) is not { } statement
                    || statement is not ExpressionStatementSyntax
                        and not LocalDeclarationStatementSyntax
                    || statement.SpanStart <= declaration.Span.End
                    || call.Ancestors().TakeWhile(ancestor => ancestor != statement).Any(
                        static ancestor => ancestor is ConditionalExpressionSyntax
                            or SwitchExpressionSyntax
                            or AnonymousFunctionExpressionSyntax
                            or LocalFunctionStatementSyntax))
                {
                    return false;
                }

                int declarationIndex = block.Statements.IndexOf(declaration);

                int statementIndex = block.Statements.IndexOf(statement);

                return declarationIndex >= 0
                    && statementIndex > declarationIndex
                    && !block.Statements.Skip(declarationIndex + 1)
                        .Take(statementIndex - declarationIndex - 1)
                        .Any(static between => between is ReturnStatementSyntax
                            or ThrowStatementSyntax
                            or GotoStatementSyntax);
            }

            InvocationExpressionSyntax[] ownerCalls = caller.Syntax.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is MemberAccessExpressionSyntax access
                    && SymbolEqualityComparer.Default.Equals(
                        caller.Model.GetSymbolInfo(access.Expression).Symbol,
                        owner)
                    && IsSuccessfulPathOwnerCall(call))
                .ToArray();

            bool IsNonCancelableCompletionArgument(ArgumentSyntax argument) =>
                caller.Model.GetSymbolInfo(argument.Expression).Symbol is IPropertySymbol
                {
                    Name: "None",
                    ContainingType: { } containingType,
                }
                && TypeKey(containingType) == "System.Threading.CancellationToken";

            bool JoinedCarrierCompletion(InvocationExpressionSyntax call)
            {
                if (caller.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol
                    {
                        Name: "CompleteAsync",
                    } method
                    || !IsAwaitable(method.ReturnType)
                    || CompletionPoint(caller, call, false) is null)
                {
                    return false;
                }

                return method.Parameters.Length == 0
                        && call.ArgumentList.Arguments.Count == 0
                    || method.Parameters is [IParameterSymbol { Type: { } tokenType }]
                        && TypeKey(tokenType) == "System.Threading.CancellationToken"
                        && call.ArgumentList.Arguments is [ArgumentSyntax argument]
                        && IsNonCancelableCompletionArgument(argument);
            }

            AuthoredMember[] completionTargets = ownerCalls.Where(JoinedCarrierCompletion).Select(call => Resolve((IMethodSymbol)caller.Model.GetSymbolInfo(call).Symbol!, caller.Model.Compilation)).OfType<AuthoredMember>().ToArray();

            List<AuthoredMember> disposalTargets = ownerCalls.Where(call => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactCleanupContract(method, result) && HasExactTerminalJoin(caller, call, method, false)).Select(call => Resolve((IMethodSymbol)caller.Model.GetSymbolInfo(call).Symbol!, caller.Model.Compilation)).OfType<AuthoredMember>().ToList();

            List<string?> disposalGroups = ownerCalls.Where(call => caller.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactCleanupContract(method, result)).Select(call => EffectAt(caller, call, operationId, inherited)).ToList();

            if (variable.Parent is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax))
            {
                disposalGroups.Add(EffectAt(caller, declaration, operationId, inherited, DisposalPosition(declaration), declaration));

                bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                string methodName = asynchronous ? "DisposeAsync" : "Dispose";

                if (result is INamedTypeSymbol disposable && disposable.AllInterfaces.SingleOrDefault(type => TypeKey(type) == (asynchronous ? "System.IAsyncDisposable" : "System.IDisposable"))?.GetMembers(methodName).OfType<IMethodSymbol>().SingleOrDefault(static method => method.Parameters.Length == 0) is { } slot && disposable.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation && IsExactCleanupContract(implementation, result) && Resolve(implementation, caller.Model.Compilation) is { } cleanup)
                {
                    disposalTargets.Add(cleanup);
                }
            }

            ObjectCreationExpressionSyntax[] returns = factory.Syntax.DescendantNodes().OfType<ReturnStatementSyntax>().Select(static statement => statement.Expression).OfType<ObjectCreationExpressionSyntax>().ToArray();

            HashSet<int> mappedCreations = [];

            List<(IFieldSymbol Field, AssignmentExpressionSyntax Assignment)> mappedFields = [];

            List<(ILocalSymbol Local, InvocationExpressionSyntax Creation, ExpressionSyntax Assignment)> mappedLocals = [];

            List<string> mappingFailures = [];

            AuthoredMember? mappedConstructor = null;

            bool References(AuthoredMember member, SyntaxNode node, ISymbol symbol) => node.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, symbol));

            bool Written(AuthoredMember member, ISymbol symbol) => member.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => References(member, assignment.Left, symbol));

            bool TryMapWriterCreation(
                ILocalSymbol local,
                out InvocationExpressionSyntax creation,
                out ExpressionSyntax assignment)
            {
                creation = null!;

                assignment = null!;

                if (local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax variable)
                {
                    return false;
                }

                List<(ExpressionSyntax Value, ExpressionSyntax Assignment)> values = [];

                bool nullInitializer = variable.Initializer is not null
                    && (variable.Initializer.Value is DefaultExpressionSyntax
                        || variable.Initializer.Value is LiteralExpressionSyntax literal
                            && literal.IsKind(SyntaxKind.DefaultLiteralExpression)
                        || factory.Model.GetConstantValue(variable.Initializer.Value) is
                        {
                            HasValue: true,
                            Value: null,
                        });

                if (variable.Initializer is not null && !nullInitializer)
                {
                    values.Add((variable.Initializer.Value, variable.Initializer.Value));
                }

                AssignmentExpressionSyntax[] writes = factory.Syntax.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(write => SymbolEqualityComparer.Default.Equals(
                        factory.Model.GetSymbolInfo(write.Left).Symbol,
                        local))
                    .ToArray();

                foreach (AssignmentExpressionSyntax write in writes)
                {
                    if (!write.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        || write.Parent is not ExpressionStatementSyntax)
                    {
                        return false;
                    }

                    values.Add((write.Right, write.Left));
                }

                if (values is not [(ExpressionSyntax value, ExpressionSyntax assigned)])
                {
                    return false;
                }

                InvocationExpressionSyntax[] creations = value.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => factory.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                        && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync")
                    .ToArray();

                if (creations is not [InvocationExpressionSyntax exact]
                    || factory.Model.GetSymbolInfo(exact).Symbol is not IMethodSymbol createMethod
                    || IsAwaitable(createMethod.ReturnType)
                        && CompletionPoint(factory, exact, false) is null)
                {
                    return false;
                }

                creation = exact;

                assignment = assigned;

                return true;
            }

            bool IsNonOwningTextAdapterReference(ILocalSymbol local, ExpressionSyntax reference)
            {
                if (reference.Parent is not ArgumentSyntax argument
                    || argument.Parent?.Parent is not InvocationExpressionSyntax call
                    || factory.Model.GetOperation(argument) is not IArgumentOperation { Parameter: { } parameter }
                    || factory.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
                    || TypeKey(method.ReturnType) != "System.IO.StreamWriter"
                    || Resolve(method, factory.Model.Compilation) is not { } adapter)
                {
                    return false;
                }

                BaseObjectCreationExpressionSyntax[] writers = adapter.Syntax.DescendantNodesAndSelf()
                    .OfType<BaseObjectCreationExpressionSyntax>()
                    .Where(created => adapter.Model.GetTypeInfo(created).Type is { } createdType
                        && TypeKey(createdType) == "System.IO.StreamWriter")
                    .ToArray();

                if (writers is not [BaseObjectCreationExpressionSyntax writer]
                    || adapter.Model.GetOperation(writer) is not IObjectCreationOperation operation
                    || operation.Arguments.SingleOrDefault(static candidate => candidate.Parameter?.Name == "leaveOpen") is not { Value.ConstantValue: { HasValue: true, Value: true } }
                    || operation.Arguments.SingleOrDefault(candidate => candidate.Parameter?.Ordinal == 0)?.Value.Syntax is not ExpressionSyntax stream
                    || !SymbolEqualityComparer.Default.Equals(adapter.Model.GetSymbolInfo(stream).Symbol, parameter))
                {
                    return false;
                }

                return HasOnlyReferences(adapter, parameter, stream)
                    && SymbolEqualityComparer.Default.Equals(factory.Model.GetSymbolInfo(reference).Symbol, local);
            }

            bool IsNullGuardReference(ILocalSymbol local, ExpressionSyntax reference) =>
                reference.AncestorsAndSelf().OfType<IsPatternExpressionSyntax>().Any(test =>
                    test.Expression.Span.Contains(reference.Span)
                    && test.Pattern is UnaryPatternSyntax
                    {
                        Pattern: ConstantPatternSyntax
                        {
                            Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                        },
                    }
                    && SymbolEqualityComparer.Default.Equals(
                        factory.Model.GetSymbolInfo(test.Expression).Symbol,
                        local));

            bool IsExceptionalCleanupReference(ILocalSymbol local, ExpressionSyntax reference)
            {
                if (reference.Parent is not MemberAccessExpressionSyntax access
                    || access.Expression != reference
                    || access.Parent is not InvocationExpressionSyntax cleanup
                    || factory.Model.GetSymbolInfo(cleanup).Symbol is not IMethodSymbol method
                    || !IsExactWriterCleanup(method, local.Type)
                    || !HasExactTerminalJoin(factory, cleanup, method, false)
                    || cleanup.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault() is not { } ownerCatch
                    || cleanup.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault() is not { } isolated
                    || isolated.Block.Span.Contains(cleanup.Span) == false
                    || isolated.Catches.Count == 0
                    || isolated.Catches.Any(static caught => caught.Filter is not null
                        || caught.Block.DescendantNodesAndSelf().Any(node => node is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax)))
                {
                    return false;
                }

                IfStatementSyntax? guard = cleanup.Ancestors()
                    .OfType<IfStatementSyntax>()
                    .FirstOrDefault(candidate => candidate.Statement.Span.Contains(cleanup.Span));

                return guard is not null
                    && guard.Condition.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression =>
                        IsNullGuardReference(local, expression))
                    && ownerCatch.Block.Span.Contains(isolated.Span);
            }

            bool HasOnlySupportedFactoryReferences(
                ILocalSymbol local,
                ExpressionSyntax transfer,
                ExpressionSyntax assignment)
            {
                foreach (ExpressionSyntax reference in SymbolReferences(factory, local))
                {
                    if (reference == transfer
                        || reference == assignment
                        || IsNullGuardReference(local, reference)
                        || IsExceptionalCleanupReference(local, reference)
                        || IsNonOwningTextAdapterReference(local, reference))
                    {
                        continue;
                    }

                    mappingFailures.Add(
                        local.Name
                        + ":unsupported@"
                        + reference.SpanStart
                        + ":"
                        + reference.Parent?.Kind()
                        + ":"
                        + reference.Parent?.Parent);

                    return false;
                }

                return true;
            }

            bool HasExceptionalConstructionCleanup()
            {
                if (mappedLocals.Count < 2)
                {
                    return true;
                }

                CatchClauseSyntax[] catches = factory.Syntax.DescendantNodes()
                    .OfType<CatchClauseSyntax>()
                    .Where(caught => caught.Parent is TryStatementSyntax guarded
                        && mappedLocals.All(mapped => guarded.Block.Span.Contains(mapped.Creation.Span))
                        && returns.All(returned => guarded.Block.Span.Contains(returned.Span)))
                    .ToArray();

                if (catches is not [CatchClauseSyntax cleanupCatch]
                    || cleanupCatch.Filter is not null
                    || cleanupCatch.Block.DescendantNodes().OfType<ReturnStatementSyntax>().Any()
                    || !cleanupCatch.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any())
                {
                    return false;
                }

                return mappedLocals.All(mapped => cleanupCatch.Block.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Count(call => call.Expression is MemberAccessExpressionSyntax access
                        && SymbolEqualityComparer.Default.Equals(
                            factory.Model.GetSymbolInfo(access.Expression).Symbol,
                            mapped.Local)
                        && factory.Model.GetSymbolInfo(call).Symbol is IMethodSymbol cleanup
                        && IsExactWriterCleanup(cleanup, mapped.Local.Type)
                        && IsExceptionalCleanupReference(mapped.Local, access.Expression)) == 1);
            }

            bool mapped = returns.Length == 1 && factory.Model.GetOperation(returns[0]) is IObjectCreationOperation { Constructor: { } constructor } creation && Resolve(constructor, factory.Model.Compilation) is { } constructorBody && SetMappedConstructor(constructorBody) && fields.All(field =>
            {
                AssignmentExpressionSyntax[] writes = constructorBody.Syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment => References(constructorBody, assignment.Left, field)).ToArray();

                if (!field.IsReadOnly || field.IsStatic || field.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null }) || writes.Length != 1 || !writes[0].IsKind(SyntaxKind.SimpleAssignmentExpression) || !IsUnconditionalExecution(constructorBody, writes[0], constructorBody.Syntax.SpanStart) || !SymbolEqualityComparer.Default.Equals(constructorBody.Model.GetSymbolInfo(writes[0].Left).Symbol, field))
                {
                    return false;
                }

                if (constructorBody.Model.GetSymbolInfo(writes[0].Right).Symbol is not IParameterSymbol parameter
                    || Written(constructorBody, parameter)
                    || !HasOnlyReferences(constructorBody, parameter, writes[0].Right)
                    || creation.Arguments.SingleOrDefault(argument => argument.Parameter?.Ordinal == parameter.Ordinal)?.Value.Syntax is not ExpressionSyntax argument
                    || factory.Model.GetSymbolInfo(argument).Symbol is not ILocalSymbol local)
                {
                    mappingFailures.Add(field.Name + ":constructor-transfer");

                    return false;
                }

                if (!TryMapWriterCreation(
                        local,
                        out InvocationExpressionSyntax writerCreation,
                        out ExpressionSyntax assignment))
                {
                    mappingFailures.Add(field.Name + ":writer-creation");

                    return false;
                }

                if (!HasOnlySupportedFactoryReferences(local, argument, assignment))
                {
                    mappingFailures.Add(field.Name + ":writer-reference");

                    return false;
                }

                mappedLocals.Add((local, writerCreation, assignment));

                return mappedCreations.Add(writerCreation.SpanStart)
                    && AddMappedField(field, writes[0]);
            });

            bool exceptionalConstructionCleanup = HasExceptionalConstructionCleanup();

            if (!exceptionalConstructionCleanup)
            {
                mappingFailures.Add("factory:exceptional-cleanup");
            }

            mapped &= exceptionalConstructionCleanup;

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

            bool IsFailureIsolatedCleanup(AuthoredMember member, InvocationExpressionSyntax cleanup)
            {
                if (cleanup.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault() is not { } isolated
                    || !isolated.Block.Span.Contains(cleanup.Span)
                    || isolated.Catches.Count == 0
                    || isolated.Catches.Any(static caught => caught.Filter is not null
                        || caught.Block.DescendantNodesAndSelf().Any(node => node is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax)))
                {
                    return false;
                }

                BlockSyntax? body = member.Syntax switch
                {
                    MethodDeclarationSyntax { Body: { } methodBody } => methodBody,
                    LocalFunctionStatementSyntax { Body: { } localBody } => localBody,
                    _ => null,
                };

                return body is not null
                    && isolated.Parent == body
                    && body.Statements.TakeWhile(statement => statement.SpanStart < isolated.SpanStart)
                        .All(statement => statement is LocalDeclarationStatementSyntax
                            or TryStatementSyntax
                            || IsExactIdempotentDisposalGuard(member, statement));
            }

            bool IsExactIdempotentDisposalGuard(AuthoredMember member, StatementSyntax statement)
            {
                if (statement is not IfStatementSyntax
                    {
                        Else: null,
                        Statement: BlockSyntax
                        {
                            Statements: [ReturnStatementSyntax { Expression: null }],
                        },
                    } guard
                    || guard.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().SingleOrDefault() is not { } exchange
                    || member.Model.GetSymbolInfo(exchange).Symbol is not IMethodSymbol method
                    || Normalize(method) != "System.Threading.Interlocked.Exchange"
                    || exchange.ArgumentList.Arguments.Count != 2
                    || member.Model.GetConstantValue(exchange.ArgumentList.Arguments[1].Expression) is not { HasValue: true, Value: 1 })
                {
                    return false;
                }

                return exchange.ArgumentList.Arguments[0].RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    && member.Model.GetSymbolInfo(exchange.ArgumentList.Arguments[0].Expression).Symbol is IFieldSymbol
                    {
                        Type.SpecialType: SpecialType.System_Int32,
                    };
            }

            bool IsConditionalCompletion(AuthoredMember member, InvocationExpressionSyntax call)
            {
                SyntaxNode expression = CompletionPreservingExpression(call, member.Model);

                if (expression.Parent is AwaitExpressionSyntax awaited)
                {
                    expression = awaited;
                }

                return expression.Parent is ConditionalExpressionSyntax;
            }

            IEnumerable<(AuthoredMember Member, ExpressionSyntax Receiver)> FieldTerminals(
                IFieldSymbol field,
                bool cleanup,
                IEnumerable<AuthoredMember> targets) => targets.SelectMany(member => member.Syntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Expression is MemberAccessExpressionSyntax access
                        && SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(access.Expression).Symbol,
                            field)
                        && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                        && HasExactTerminalJoin(member, call, method, true)
                        && (cleanup
                            ? IsExactWriterCleanup(method, field.Type)
                                && (IsUnconditionalExecution(member, call, member.Syntax.SpanStart)
                                    || IsFailureIsolatedCleanup(member, call))
                            : IsExactWriterCompletion(method)
                                && (IsUnconditionalExecution(member, call, member.Syntax.SpanStart)
                                    || IsConditionalCompletion(member, call))))
                    .Select(call => (member, ((MemberAccessExpressionSyntax)call.Expression).Expression)));

            bool FieldHasTerminal(IFieldSymbol field, bool cleanup, IEnumerable<AuthoredMember> targets) => FieldTerminals(field, cleanup, targets).Any();

            bool AllFieldsTerminate(bool cleanup, IEnumerable<AuthoredMember> targets) =>
                targets.Any(target => fields.All(field => FieldHasTerminal(field, cleanup, [target])));

            bool FieldsHaveOnlyMappedTerminals() => mappedFields.All(mapping => AuthoredMembers.Where(member => SymbolEqualityComparer.Default.Equals(member.Symbol.ContainingType, mapping.Field.ContainingType)).All(member =>
            {
                List<ExpressionSyntax> allowed = [];

                if (mappedConstructor is not null && SymbolEqualityComparer.Default.Equals(member.Symbol, mappedConstructor.Symbol))
                {
                    allowed.Add(mapping.Assignment.Left);
                }

                allowed.AddRange(FieldTerminals(mapping.Field, false, [member]).Select(static terminal => terminal.Receiver));

                allowed.AddRange(FieldTerminals(mapping.Field, true, [member]).Select(static terminal => terminal.Receiver));

                return HasOnlyReferences(member, mapping.Field, [.. allowed]);
            }));

            bool sameDisposalGroup = group is not null
                && disposalGroups.Count > 0
                && disposalGroups.All(disposal => disposal == group);

            bool sameOwnerCallGroup = group is not null
                && ownerCalls.All(call => EffectAt(caller, call, operationId, inherited) == group);

            bool completesFields = AllFieldsTerminate(false, completionTargets);

            bool disposesFields = AllFieldsTerminate(true, disposalTargets);

            bool exclusiveFields = FieldsHaveOnlyMappedTerminals();

            bool valid = mapped
                && sameDisposalGroup
                && sameOwnerCallGroup
                && completesFields
                && disposesFields
                && exclusiveFields;

            if (!valid)
            {
                diagnostics.Add(new(
                    "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE",
                    operationId + "/site@" + Location(factoryCall),
                    "Field-backed publication requires exact constructor ownership and completion/disposal under its caller's one retained group. "
                    + $"mapping={mapped}({string.Join(",", mappingFailures)}); disposalGroup={sameDisposalGroup}; ownerGroup={sameOwnerCallGroup}; completion={completesFields}; disposal={disposesFields}; exclusive={exclusiveFields}."));
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

        private static bool IsAssignedOnEverySuccessfulConstruction(
            AuthoredMember constructor,
            AssignmentExpressionSyntax assignment)
        {
            if (assignment.Ancestors()
                .TakeWhile(ancestor => ancestor != constructor.Syntax)
                .Any(static ancestor => ancestor is StatementSyntax and not (BlockSyntax or ExpressionStatementSyntax or LocalDeclarationStatementSyntax) || ancestor is ConditionalExpressionSyntax or SwitchExpressionSyntax or AnonymousFunctionExpressionSyntax))
            {
                return false;
            }

            return !constructor.Syntax.DescendantNodes(candidate => candidate == constructor.Syntax || candidate is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .Any(candidate => candidate.Span.End < assignment.SpanStart && candidate is ReturnStatementSyntax or GotoStatementSyntax);
        }

        private string? EffectAt(AuthoredMember member, SyntaxNode site, string operationId, string? inherited, int? position = null, SyntaxNode? disposing = null)
        {
            string? local = FindRetainedAdmission(member, site, "TryBeginExternalEffectGroup", null, position, disposing);

            return local is null ? LiveInheritedAdmission(member, site, inherited, position) : RootOperation(operationId) + "@effect:" + local;
        }

        private string? WorkAt(AuthoredMember member, SyntaxNode site, string operationId, GrimoireWorkKind? kind, string? inherited, int? position = null, SyntaxNode? disposing = null)
        {
            string? local = kind is null ? null : FindRetainedAdmission(member, site, "TryAcquireWorkLease", kind, position, disposing);

            return local is null ? LiveInheritedAdmission(member, site, inherited, position) : RootOperation(operationId) + "@work:" + local;
        }

        private string? LiveInheritedAdmission(AuthoredMember member, SyntaxNode site, string? inherited, int? atPosition)
        {
            if (inherited is null)
            {
                return null;
            }

            string origin = inherited[(inherited.LastIndexOf("@", StringComparison.Ordinal) + 1)..];

            origin = origin[(origin.IndexOf(':') + 1)..];

            return AdmissionEndedBefore(member, origin, atPosition ?? site.SpanStart, []) ? null : inherited;
        }

        private bool AdmissionEndedBefore(AuthoredMember member, string origin, int position, HashSet<GraphMemberIdentity> path)
        {
            GraphMemberIdentity identity = MemberIdentity(member);

            if (!path.Add(identity))
            {
                return true;
            }

            bool Matches(ExpressionSyntax expression) => AdmissionOrigins(member, expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Contains(origin);

            bool Carries(ExpressionSyntax expression) => Matches(expression) && MayCarryAdmissionHandle(member, expression);

            try
            {
                foreach (SyntaxNode node in member.Syntax.DescendantNodesAndSelf(node => node == member.Syntax || node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
                {
                    if (node is InvocationExpressionSyntax
                        && member.Model.GetOperation(node) is INameOfOperation)
                    {
                        continue;
                    }

                    // Returned or heap-stored handles leave the supported local/formal environment.
                    // Stop inheriting their authority instead of treating the escape as a new handle.
                    if (node.Span.End < position && (node is ReturnStatementSyntax { Expression: { } returned } && Carries(returned)
                        || node is ArrowExpressionClauseSyntax arrow && Carries(arrow.Expression)
                        || node is AssignmentExpressionSyntax assignment && member.Model.GetSymbolInfo(assignment.Left).Symbol is not ILocalSymbol and not IParameterSymbol && Carries(assignment.Right)))
                    {
                        return true;
                    }

                    if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" or "DisposeAsync" } access } disposal && disposal.Span.End < position && Carries(access.Expression)
                        || node is UsingStatementSyntax { Expression: { } resource } statement && statement.Span.End < position && Carries(resource)
                        || node is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax) && DisposalPosition(declaration) < position && declaration.Variables.Any(variable => variable.Initializer is not null && Carries(variable.Initializer.Value)))
                    {
                        return true;
                    }

                    if (node is InvocationExpressionSyntax call && call.Span.End < position && Location(call) != origin && AdmissionReceiver(member, call) is { } receiver && Carries(receiver) && (member.Model.GetSymbolInfo(call).Symbol is not IMethodSymbol receiverMethod || !PreservesAdmissionReceiver(receiverMethod) && !IsCompletionCombinator(receiverMethod)))
                    {
                        return true;
                    }

                    if (node is InvocationExpressionSyntax inspection
                        && inspection.Span.End < position
                        && member.Model.GetSymbolInfo(inspection).Symbol is IMethodSymbol inspector
                        && Normalize(inspector) == "System.ArgumentNullException.ThrowIfNull")
                    {
                        continue;
                    }

                    if (node is InvocationExpressionSyntax argumentCall && argumentCall.Span.End < position && Location(argumentCall) != origin && (member.Model.GetSymbolInfo(argumentCall).Symbol is not IMethodSymbol argumentMethod || !IsCompletionCombinator(argumentMethod)) && AdmissionArguments(member, argumentCall).Any(argument => argument.Expression is not DeclarationExpressionSyntax && Carries(argument.Expression)))
                    {
                        if (AdmissionArguments(member, argumentCall).Any(argument => Carries(argument.Expression) && argument.RefKind is RefKind.Ref or RefKind.Out) || member.Model.GetSymbolInfo(argumentCall).Symbol is not IMethodSymbol method || Resolve(method, member.Model.Compilation) is not { } target || AdmissionEndedBefore(BindAdmissionArguments(member, argumentCall, target), origin, int.MaxValue, path))
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

            bool DirectCreationInitializer(VariableDeclaratorSyntax variable, InvocationExpressionSyntax create)
            {
                if (variable.Initializer is null || member.Model.GetSymbolInfo(create).Symbol is not IMethodSymbol factory)
                {
                    return false;
                }

                if (create.Ancestors()
                    .TakeWhile(ancestor => ancestor != variable)
                    .Any(static ancestor => ancestor is ConditionalExpressionSyntax
                        or SwitchExpressionSyntax
                        || ancestor is BinaryExpressionSyntax binary
                            && binary.IsKind(SyntaxKind.CoalesceExpression)))
                {
                    return false;
                }

                SyntaxNode expression = CompletionPreservingExpression(create, member.Model);

                if (IsAwaitable(factory.ReturnType))
                {
                    if (expression.Parent is not AwaitExpressionSyntax awaited)
                    {
                        return false;
                    }

                    expression = awaited;
                }

                return expression == variable.Initializer.Value;
            }

            bool TryCreatedWriter(LocalDeclarationStatementSyntax declaration, out ILocalSymbol writer, out InvocationExpressionSyntax create)
            {
                writer = null!;

                create = null!;

                if (declaration.UsingKeyword.RawKind != 0 || declaration.Declaration.Variables is not [VariableDeclaratorSyntax variable] || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol local)
                {
                    return false;
                }

                InvocationExpressionSyntax[] creations = variable.Initializer?.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(call => member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync").ToArray() ?? [];

                if (creations is not [InvocationExpressionSyntax exact] || !DirectCreationInitializer(variable, exact))
                {
                    return false;
                }

                writer = local;

                create = exact;

                return true;
            }

            bool DirectReceiver(InvocationExpressionSyntax call, ISymbol writer) => call.Expression is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(access.Expression).Symbol, writer);

            bool SupportedRootStatement(StatementSyntax statement) => !statement.Ancestors().TakeWhile(ancestor => ancestor != member.Syntax).Any(static ancestor => ancestor is StatementSyntax and not BlockSyntax || ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

            bool ExactCompletion(InvocationExpressionSyntax call) => call.ArgumentList.Arguments.Count == 0 && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactWriterCompletion(method) && HasExactCompletionJoin(member, call, method) && ExactTerminalStatement(member, call) is not null;

            bool ExactCleanup(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax access && member.Model.GetTypeInfo(access.Expression).Type is { } receiverType && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactWriterCleanup(method, receiverType) && HasExactTerminalJoin(member, call, method, false) && ExactTerminalStatement(member, call) is not null;

            bool ValidProtectedTry(TryStatementSyntax statement, ILocalSymbol protectedWriter)
            {
                if (statement.Catches.Count != 0 || statement.Finally?.Block.Statements is not [ExpressionStatementSyntax cleanupStatement] || cleanupStatement.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray() is not [InvocationExpressionSyntax cleanup] || !DirectReceiver(cleanup, protectedWriter) || !ExactCleanup(cleanup))
                {
                    return false;
                }

                for (int index = 0; index < statement.Block.Statements.Count; index++)
                {
                    StatementSyntax current = statement.Block.Statements[index];

                    if (current is ExpressionStatementSyntax terminal && terminal.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray() is [InvocationExpressionSyntax completion] && ExactCompletion(completion) && completion.Expression is MemberAccessExpressionSyntax completionAccess && member.Model.GetSymbolInfo(completionAccess.Expression).Symbol is ILocalSymbol)
                    {
                        continue;
                    }

                    if (current is LocalDeclarationStatementSyntax nestedDeclaration && index + 1 < statement.Block.Statements.Count && statement.Block.Statements[index + 1] is TryStatementSyntax nestedTry && TryCreatedWriter(nestedDeclaration, out ILocalSymbol nestedWriter, out _) && ValidProtectedTry(nestedTry, nestedWriter))
                    {
                        index++;

                        continue;
                    }

                    return false;
                }

                return true;
            }

            bool ExactRootProtectedShape(LocalDeclarationStatementSyntax declaration, ILocalSymbol writer, TryStatementSyntax protector)
            {
                TryStatementSyntax root = protector;

                while (root.Parent is BlockSyntax containingBody && containingBody.Parent is TryStatementSyntax containingTry && containingTry.Block == containingBody)
                {
                    root = containingTry;
                }

                if (root.Parent is not BlockSyntax rootBlock)
                {
                    return false;
                }

                int rootIndex = rootBlock.Statements.IndexOf(root);

                if (rootIndex < 1 || rootBlock.Statements[rootIndex - 1] is not LocalDeclarationStatementSyntax rootDeclaration || !TryCreatedWriter(rootDeclaration, out ILocalSymbol rootWriter, out _) || !SupportedRootStatement(rootDeclaration))
                {
                    return false;
                }

                return ValidProtectedTry(root, rootWriter) && declaration.Parent is BlockSyntax declarationBlock && declarationBlock.Statements.IndexOf(declaration) is int declarationIndex && declarationIndex >= 0 && declarationIndex + 1 < declarationBlock.Statements.Count && declarationBlock.Statements[declarationIndex + 1] == protector && ValidProtectedTry(protector, writer);
            }

            bool ExactCatchCleanupRethrowShape(LocalDeclarationStatementSyntax declaration, ILocalSymbol writer, InvocationExpressionSyntax completion, InvocationExpressionSyntax[] cleanups)
            {
                if (!SupportedRootStatement(declaration) || declaration.Parent is not BlockSyntax block)
                {
                    return false;
                }

                int index = block.Statements.IndexOf(declaration);

                if (index < 0 || index + 2 >= block.Statements.Count || block.Statements[index + 1] is not TryStatementSyntax { Finally: null, Catches: [CatchClauseSyntax caught] } guarded || block.Statements[index + 2] is not ExpressionStatementSyntax normalCleanupStatement || guarded.Block.Statements is not [ExpressionStatementSyntax completionStatement] || caught.Filter is not null || caught.Block.Statements is not [ExpressionStatementSyntax exceptionalCleanupStatement, ThrowStatementSyntax { Expression: null }])
                {
                    return false;
                }

                if (completionStatement.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray() is not [InvocationExpressionSyntax exactCompletion] || exactCompletion != completion || normalCleanupStatement.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray() is not [InvocationExpressionSyntax normalCleanup] || exceptionalCleanupStatement.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray() is not [InvocationExpressionSyntax exceptionalCleanup])
                {
                    return false;
                }

                return cleanups.Length == 2 && cleanups.Contains(normalCleanup) && cleanups.Contains(exceptionalCleanup) && DirectReceiver(normalCleanup, writer) && DirectReceiver(exceptionalCleanup, writer) && ExactCleanup(normalCleanup) && ExactCleanup(exceptionalCleanup);
            }

            foreach (InvocationExpressionSyntax create in calls.Where(call => fieldPublications?.Contains(call.SpanStart) != true && selection.Span.Contains(call.Span) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && Normalize(method) == "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync"))
            {
                VariableDeclaratorSyntax? variable = create.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();

                ILocalSymbol? writer = variable is null ? null : member.Model.GetDeclaredSymbol(variable) as ILocalSymbol;

                string? creationGroup = EffectAt(member, create, operationId, inherited);

                InvocationExpressionSyntax[] completions = calls.Where(call => writer is not null && DirectReceiver(call, writer) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactWriterCompletion(method)).ToArray();

                InvocationExpressionSyntax[] disposals = calls.Where(call => writer is not null && DirectReceiver(call, writer) && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactWriterCleanup(method, writer.Type)).ToArray();

                List<ExpressionSyntax> allowedReferences = [];

                allowedReferences.AddRange(completions.Select(static call => ((MemberAccessExpressionSyntax)call.Expression).Expression));

                allowedReferences.AddRange(disposals.Select(static call => ((MemberAccessExpressionSyntax)call.Expression).Expression));

                bool exactOwnership = writer is not null && variable is not null && DirectCreationInitializer(variable, create) && HasOnlyReferences(member, writer, [.. allowedReferences]);

                bool exactCompletion = completions is [InvocationExpressionSyntax completion] && ExactCompletion(completion) && EffectAt(member, completion, operationId, inherited) == creationGroup;

                bool valid = false;

                if (variable?.Parent is VariableDeclarationSyntax declaration && (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } || declaration.Parent is UsingStatementSyntax))
                {
                    bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                    string cleanupName = asynchronous ? "DisposeAsync" : "Dispose";

                    IMethodSymbol[] selectedCleanups = writer is null ? [] : SelectCleanupMembers(writer.Type, cleanupName);

                    ExpressionStatementSyntax? completionStatement = completions.Length == 1 ? ExactTerminalStatement(member, completions[0]) : null;

                    bool lexical = declaration.Parent switch
                    {
                        LocalDeclarationStatementSyntax local when local.Parent is BlockSyntax block => completionStatement?.Parent == block && completionStatement.SpanStart > local.Span.End && block.Statements.Skip(block.Statements.IndexOf(local) + 1).All(statement => statement is ExpressionStatementSyntax or LocalDeclarationStatementSyntax || statement == block.Statements[^1] && statement is ReturnStatementSyntax),
                        UsingStatementSyntax statement when statement.Statement is BlockSyntax block => completionStatement?.Parent == block && block.Statements.All(nested => nested is ExpressionStatementSyntax or LocalDeclarationStatementSyntax || nested == block.Statements[^1] && nested is ReturnStatementSyntax),
                        _ => false,
                    };

                    valid = exactOwnership && exactCompletion && disposals.Length == 0 && declaration.Variables.Count == 1 && selectedCleanups is [IMethodSymbol selected] && IsExactWriterCleanup(selected, writer!.Type) && lexical && completions is [InvocationExpressionSyntax completed] && IsUnconditionalExecution(member, completed, create.Span.End) && declaration.Parent is StatementSyntax usingStatement && SupportedRootStatement(usingStatement) && EffectAt(member, declaration, operationId, inherited, DisposalPosition(declaration), declaration) == creationGroup;
                }
                else if (variable?.Parent?.Parent is LocalDeclarationStatementSyntax local && writer is not null && local.Parent is BlockSyntax block)
                {
                    int declarationIndex = block.Statements.IndexOf(local);

                    TryStatementSyntax? protector = declarationIndex >= 0 && declarationIndex + 1 < block.Statements.Count ? block.Statements[declarationIndex + 1] as TryStatementSyntax : null;

                    bool sameCleanupGroup = disposals.All(cleanup => EffectAt(member, cleanup, operationId, inherited) == creationGroup);

                    valid = creationGroup is not null && exactOwnership && exactCompletion && sameCleanupGroup && (disposals is [InvocationExpressionSyntax cleanup] && ExactCleanup(cleanup) && protector is not null && protector.Block.Span.Contains(completions[0].Span) && ExactRootProtectedShape(local, writer, protector) || completions is [InvocationExpressionSyntax completed] && ExactCatchCleanupRethrowShape(local, writer, completed, disposals));
                }

                if (!valid || creationGroup is null)
                {
                    diagnostics.Add(new("HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE", operationId + "/site@" + Location(create), "A local writer requires one completion-owned creation, exclusive references, and exact joined completion plus exhaustive cleanup under the same retained group."));
                }
            }
        }

        private string? FindRetainedAdmission(AuthoredMember member, SyntaxNode site, string admissionName, GrimoireWorkKind? kind, int? atPosition = null, SyntaxNode? disposing = null)
        {
            int position = atPosition ?? site.SpanStart;

            GraphMemberIdentity memberKey = MemberIdentity(member);

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

                BlockSyntax? block;

                int admittedAt;

                if (TryLoopAdmissionRegion(admission, site, position, out BlockSyntax? loopBlock, out int loopAdmission))
                {
                    block = loopBlock;

                    admittedAt = loopAdmission;
                }
                else
                {
                    IfStatementSyntax guard = admission.Ancestors().OfType<IfStatementSyntax>().First(candidate => candidate.Condition.Span.Contains(admission.Span));

                    bool trueMeansAdmitted = ProvesCallTrue(guard.Condition, admission);

                    StatementSyntax? successful = trueMeansAdmitted ? guard.Statement : guard.Else?.Statement;

                    BlockSyntax? successBranch = successful is BlockSyntax branch && branch.Span.Contains(position) ? branch : null;

                    if (successBranch is null && (trueMeansAdmitted || !FailureTerminates(guard)))
                    {
                        continue;
                    }

                    block = successBranch ?? guard.Parent as BlockSyntax;

                    admittedAt = successBranch?.OpenBraceToken.Span.End ?? guard.Span.End;
                }

                if (block is null || !site.Ancestors().Contains(block) || admittedAt > position || admission.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault() != site.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault())
                {
                    continue;
                }

                SingleVariableDesignationSyntax? designation = admission.ArgumentList.DescendantNodes().OfType<SingleVariableDesignationSyntax>().LastOrDefault();

                ISymbol? group = designation is null ? member.Model.GetSymbolInfo(admission.ArgumentList.Arguments.Last().Expression).Symbol : member.Model.GetDeclaredSymbol(designation);

                if (group is null)
                {
                    continue;
                }

                // A may-alias set can invalidate an instance, but cannot prove that a using owns it.
                bool ReferencesGroup(ExpressionSyntax expression)
                {
                    IOperation? operation = member.Model.GetOperation(expression);

                    while (operation is IConversionOperation conversion)
                    {
                        operation = conversion.Operand;
                    }

                    return operation switch
                    {
                        ILocalReferenceOperation local => SymbolEqualityComparer.Default.Equals(local.Local, group),
                        IParameterReferenceOperation parameter => SymbolEqualityComparer.Default.Equals(parameter.Parameter, group),
                        _ => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(expression).Symbol, group),
                    };
                }

                bool IsNonThrowingPrelude(StatementSyntax statement) =>
                    statement is EmptyStatementSyntax
                    || statement is LocalDeclarationStatementSyntax local
                        && local.Declaration.Variables.All(variable =>
                            variable.Initializer is null
                            || member.Model.GetConstantValue(variable.Initializer.Value).HasValue
                            || variable.Initializer.Value.IsKind(SyntaxKind.DefaultLiteralExpression)
                            || variable.Initializer.Value is DefaultExpressionSyntax);

                bool DominatesWithImmediateOwnership(
                    LocalDeclarationStatementSyntax declaration)
                {
                    if (declaration.UsingKeyword.RawKind == 0
                        || declaration.Declaration.Variables is not [VariableDeclaratorSyntax variable]
                        || variable.Initializer is null
                        || !ReferencesGroup(variable.Initializer.Value)
                        || declaration.SpanStart <= admittedAt
                        || declaration.Span.End >= position
                        || declaration.Parent is not BlockSyntax declarationBlock
                        || !site.AncestorsAndSelf().Contains(declarationBlock)
                        || declarationBlock != block
                            && !declarationBlock.Ancestors().Contains(block))
                    {
                        return false;
                    }

                    StatementSyntax pathStatement = declaration;

                    BlockSyntax pathBlock = declarationBlock;

                    while (true)
                    {
                        if (pathBlock.Statements
                            .TakeWhile(statement => statement != pathStatement)
                            .Any(statement => statement.Span.End > admittedAt
                                && !IsNonThrowingPrelude(statement)))
                        {
                            return false;
                        }

                        if (pathBlock == block)
                        {
                            return true;
                        }

                        if (pathBlock.Parent is BlockSyntax parentBlock)
                        {
                            pathStatement = pathBlock;

                            pathBlock = parentBlock;

                            continue;
                        }

                        if (pathBlock.Parent is TryStatementSyntax protector
                            && protector.Block == pathBlock
                            && protector.Parent is BlockSyntax protectedParent)
                        {
                            pathStatement = protector;

                            pathBlock = protectedParent;

                            continue;
                        }

                        return false;
                    }
                }

                bool DominatesWithExactManualOwnership(
                    LocalDeclarationStatementSyntax declaration)
                {
                    if (declaration.UsingKeyword.RawKind != 0
                        || declaration.Declaration.Variables is not
                            [VariableDeclaratorSyntax variable]
                        || variable.Initializer is null
                        || !ReferencesGroup(variable.Initializer.Value)
                        || member.Model.GetDeclaredSymbol(variable) is not
                            ILocalSymbol lease
                        || declaration.SpanStart <= admittedAt
                        || declaration.Span.End >= position
                        || declaration.Parent != block)
                    {
                        return false;
                    }

                    int declarationIndex = block.Statements.IndexOf(declaration);

                    if (declarationIndex < 0
                        || declarationIndex + 1 >= block.Statements.Count
                        || block.Statements[declarationIndex + 1] is not
                            TryStatementSyntax
                            {
                                Finally: { } finalizer,
                            } protector
                        || !protector.Block.Span.Contains(site.Span))
                    {
                        return false;
                    }

                    InvocationExpressionSyntax[] cleanups = finalizer.Block
                        .DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Where(call => AdmissionReceiver(member, call) is { } receiver
                            && SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(receiver).Symbol,
                                lease)
                            && member.Model.GetSymbolInfo(call).Symbol is
                                IMethodSymbol method
                            && method.Name is "Dispose" or "DisposeAsync")
                        .ToArray();

                    if (cleanups is not [InvocationExpressionSyntax cleanup]
                        || member.Model.GetSymbolInfo(cleanup).Symbol is not
                            IMethodSymbol cleanupMethod)
                    {
                        return false;
                    }

                    if (cleanupMethod.Name == "Dispose")
                    {
                        return TypeKey(cleanupMethod.ReturnType) == "System.Void"
                            && finalizer.Block.Statements is
                                [ExpressionStatementSyntax statement]
                            && statement.Expression == cleanup;
                    }

                    if (cleanup.Parent is not EqualsValueClauseSyntax
                        {
                            Parent: VariableDeclaratorSyntax disposalVariable,
                        }
                        || disposalVariable.Parent?.Parent is not
                            LocalDeclarationStatementSyntax disposalDeclaration
                        || member.Model.GetDeclaredSymbol(disposalVariable) is not
                            ILocalSymbol disposal
                        || finalizer.Block.Statements is not
                            [LocalDeclarationStatementSyntax exactDeclaration,
                                IfStatementSyntax completion]
                        || exactDeclaration != disposalDeclaration
                        || completion.Else is not null
                        || completion.Condition is not PrefixUnaryExpressionSyntax
                            {
                                RawKind: (int)SyntaxKind.LogicalNotExpression,
                                Operand: MemberAccessExpressionSyntax
                                {
                                    Expression: { } completionReceiver,
                                    Name.Identifier.ValueText: "IsCompletedSuccessfully",
                                },
                            }
                        || !SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(completionReceiver).Symbol,
                            disposal))
                    {
                        return false;
                    }

                    ExpressionStatementSyntax? terminal = completion.Statement switch
                    {
                        ExpressionStatementSyntax expressionStatement =>
                            expressionStatement,
                        BlockSyntax
                        {
                            Statements: [ExpressionStatementSyntax expressionStatement],
                        } => expressionStatement,
                        _ => null,
                    };

                    if (terminal?.Expression is not InvocationExpressionSyntax result
                        || member.Model.GetSymbolInfo(result).Symbol is not
                            IMethodSymbol resultMethod
                        || Normalize(resultMethod) is not
                            ("System.Runtime.CompilerServices.TaskAwaiter.GetResult"
                                or "System.Runtime.CompilerServices.TaskAwaiter`1.GetResult")
                        || result.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .SingleOrDefault(call =>
                                member.Model.GetSymbolInfo(call).Symbol is
                                    IMethodSymbol method
                                && Normalize(method) is
                                    "System.Threading.Tasks.ValueTask.AsTask"
                                        or "System.Threading.Tasks.ValueTask`1.AsTask")
                            is not { } asTask
                        || AdmissionReceiver(member, asTask) is not { } taskReceiver
                        || !SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(taskReceiver).Symbol,
                            disposal))
                    {
                        return false;
                    }

                    return true;
                }

                bool retained = member.Syntax.DescendantNodes()
                        .OfType<LocalDeclarationStatementSyntax>()
                        .Any(declaration =>
                            DominatesWithImmediateOwnership(declaration)
                            || admissionName == "TryAcquireWorkLease"
                                && DominatesWithExactManualOwnership(declaration))
                    || site.Ancestors().OfType<UsingStatementSyntax>().Any(statement => statement.Expression is not null && ReferencesGroup(statement.Expression) || statement.Declaration?.Variables.Any(variable => variable.Initializer is not null && ReferencesGroup(variable.Initializer.Value)) == true);

                if (retained)
                {
                    bool disposed = AdmissionEndedBefore(member, Location(admission), position, []);

                    if (disposed)
                    {
                        continue;
                    }

                    return Location(admission);
                }
            }

            if (admissionName == "TryBeginExternalEffectGroup"
                && FindManuallyRetainedEffectAdmission(member, site, position) is { } manualEffect)
            {
                return manualEffect;
            }

            if (admissionName != "TryAcquireWorkLease" || kind is null)
            {
                return null;
            }

            bool IsNonThrowingTransferredPrelude(StatementSyntax statement) =>
                statement is EmptyStatementSyntax
                || statement is LocalDeclarationStatementSyntax declaration
                    && declaration.Declaration.Variables.All(variable =>
                        variable.Initializer is null
                        || member.Model.GetConstantValue(variable.Initializer.Value).HasValue
                        || variable.Initializer.Value.IsKind(
                            SyntaxKind.DefaultLiteralExpression)
                        || variable.Initializer.Value is DefaultExpressionSyntax);

            bool RunsOnEveryFinallyPath(
                FinallyClauseSyntax finalizer,
                InvocationExpressionSyntax cleanup)
            {
                if (cleanup.Ancestors().OfType<ExpressionStatementSyntax>()
                        .FirstOrDefault() is not { } cleanupStatement
                    || cleanupStatement.Parent is not BlockSyntax cleanupBlock)
                {
                    return false;
                }

                StatementSyntax path = cleanupStatement;

                BlockSyntax pathBlock = cleanupBlock;

                while (true)
                {
                    if (pathBlock.Statements
                        .TakeWhile(statement => statement != path)
                        .Any(statement => !IsNonThrowingTransferredPrelude(statement)))
                    {
                        return false;
                    }

                    if (pathBlock == finalizer.Block)
                    {
                        return true;
                    }

                    if (pathBlock.Parent is FinallyClauseSyntax nestedFinalizer
                        && nestedFinalizer.Parent is TryStatementSyntax nestedProtector
                        && nestedProtector.Parent is BlockSyntax parentBlock)
                    {
                        path = nestedProtector;

                        pathBlock = parentBlock;

                        continue;
                    }

                    return false;
                }
            }

            foreach (LocalDeclarationStatementSyntax declaration in member.Syntax
                .DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>())
            {
                if (declaration.Declaration.Variables is not [VariableDeclaratorSyntax variable]
                    || variable.Initializer is null
                    || member.Model.GetDeclaredSymbol(variable) is not ILocalSymbol lease
                    || ResolveReturnedAdmissionProofs(member, variable.Initializer.Value) is not
                        [ReturnedAdmissionProof proof]
                    || proof.WorkKind != kind.ToString()
                    || declaration.Span.End >= position
                    || declaration.Parent is not BlockSyntax declarationBlock)
                {
                    continue;
                }

                bool usingOwned = declaration.UsingKeyword.RawKind != 0
                    && site.AncestorsAndSelf().Contains(declarationBlock);

                bool manuallyOwned = false;

                if (declaration.UsingKeyword.RawKind == 0)
                {
                    int declarationIndex = declarationBlock.Statements.IndexOf(declaration);

                    StatementSyntax[] following = declarationIndex < 0
                        ? []
                        : declarationBlock.Statements
                            .Skip(declarationIndex + 1)
                            .ToArray();

                    int protectorIndex = Array.FindIndex(
                        following,
                        static statement => statement is TryStatementSyntax);

                    if (protectorIndex >= 0
                        && following.Take(protectorIndex)
                            .All(IsNonThrowingTransferredPrelude)
                        && following[protectorIndex] is TryStatementSyntax
                        {
                            Finally: { } finalizer,
                        } protector
                        && protector.Block.Span.Contains(site.Span))
                    {
                        InvocationExpressionSyntax[] cleanups = finalizer.Block
                            .DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .Where(call => AdmissionReceiver(member, call) is { } receiver
                                && SymbolEqualityComparer.Default.Equals(
                                    member.Model.GetSymbolInfo(receiver).Symbol,
                                    lease)
                                && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                                && method.Name is "Dispose" or "DisposeAsync")
                            .ToArray();

                        manuallyOwned = cleanups is [InvocationExpressionSyntax cleanup]
                            && member.Model.GetSymbolInfo(cleanup).Symbol is IMethodSymbol cleanupMethod
                            && (cleanupMethod.Name == "Dispose"
                                && TypeKey(cleanupMethod.ReturnType) == "System.Void"
                                || cleanupMethod.Name == "DisposeAsync"
                                && CompletionPoint(member, cleanup, false) is not null)
                            && RunsOnEveryFinallyPath(finalizer, cleanup);
                    }
                }

                if ((!usingOwned && !manuallyOwned)
                    || AdmissionEndedBefore(member, proof.Origin, position, []))
                {
                    continue;
                }

                return proof.Origin;
            }

            return null;
        }

        private string? FindManuallyRetainedEffectAdmission(
            AuthoredMember member,
            SyntaxNode site,
            int position)
        {
            static bool IsNullInitialization(
                SemanticModel model,
                EqualsValueClauseSyntax? initializer) =>
                initializer is null
                || initializer.Value.IsKind(SyntaxKind.DefaultLiteralExpression)
                || initializer.Value is DefaultExpressionSyntax
                || model.GetConstantValue(initializer.Value) is
                {
                    HasValue: true,
                    Value: null,
                };

            bool IsExactNonNullGuard(
                ExpressionSyntax condition,
                ISymbol carrier) =>
                condition is IsPatternExpressionSyntax
                {
                    Expression: { } tested,
                    Pattern: UnaryPatternSyntax
                    {
                        Pattern: ConstantPatternSyntax
                        {
                            Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                        },
                    },
                }
                && SymbolEqualityComparer.Default.Equals(
                    member.Model.GetSymbolInfo(tested).Symbol,
                    carrier);

            bool IsProvenNonNullAdmissionGuard(IfStatementSyntax guard)
            {
                if (guard.Else is not null
                    || guard.Condition is not IsPatternExpressionSyntax
                    {
                        Expression: { } tested,
                        Pattern: UnaryPatternSyntax
                        {
                            Pattern: ConstantPatternSyntax
                            {
                                Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                            },
                        },
                    })
                {
                    return false;
                }

                return AdmissionOrigins(
                        member,
                        tested,
                        new HashSet<ISymbol>(SymbolEqualityComparer.Default))
                    .Count != 0;
            }

            static bool SharesConditionalBranch(
                IfStatementSyntax guard,
                SyntaxNode first,
                SyntaxNode second) =>
                guard.Statement.Span.Contains(first.Span)
                    && guard.Statement.Span.Contains(second.Span)
                || guard.Else?.Statement.Span.Contains(first.Span) == true
                    && guard.Else.Statement.Span.Contains(second.Span);

            bool IsNonThrowingPrelude(StatementSyntax statement) =>
                statement is EmptyStatementSyntax
                || statement is LocalDeclarationStatementSyntax declaration
                    && declaration.Declaration.Variables.All(variable =>
                        IsNullInitialization(member.Model, variable.Initializer));

            bool RunsOnEveryFinallyPath(
                FinallyClauseSyntax finalizer,
                InvocationExpressionSyntax cleanup,
                ISymbol carrier)
            {
                if (cleanup.Ancestors().OfType<ExpressionStatementSyntax>()
                        .FirstOrDefault() is not { } cleanupStatement)
                {
                    return false;
                }

                StatementSyntax path;
                BlockSyntax pathBlock;

                if (cleanupStatement.Parent is BlockSyntax cleanupBlock)
                {
                    path = cleanupStatement;
                    pathBlock = cleanupBlock;
                }
                else if (cleanupStatement.Parent is IfStatementSyntax guard
                    && guard.Statement == cleanupStatement
                    && IsExactNonNullGuard(guard.Condition, carrier)
                    && guard.Parent is BlockSyntax guardedParent)
                {
                    path = guard;
                    pathBlock = guardedParent;
                }
                else
                {
                    return false;
                }

                while (true)
                {
                    if (pathBlock.Statements
                        .TakeWhile(statement => statement != path)
                        .Any(statement => !IsNonThrowingPrelude(statement)))
                    {
                        return false;
                    }

                    if (pathBlock == finalizer.Block)
                    {
                        return true;
                    }

                    if (pathBlock.Parent is IfStatementSyntax guard
                        && guard.Statement == pathBlock
                        && IsExactNonNullGuard(guard.Condition, carrier)
                        && guard.Parent is BlockSyntax guardedParent)
                    {
                        path = guard;
                        pathBlock = guardedParent;

                        continue;
                    }

                    if (pathBlock.Parent is TryStatementSyntax caught
                        && caught.Block == pathBlock
                        && caught.Finally is null
                        && caught.Catches.Count != 0
                        && caught.Parent is BlockSyntax caughtParent)
                    {
                        path = caught;
                        pathBlock = caughtParent;

                        continue;
                    }

                    if (pathBlock.Parent is TryStatementSyntax protectedTry
                        && protectedTry.Block == pathBlock
                        && protectedTry.Parent is BlockSyntax protectedParent)
                    {
                        path = protectedTry;
                        pathBlock = protectedParent;

                        continue;
                    }

                    if (pathBlock.Parent is FinallyClauseSyntax nestedFinalizer
                        && nestedFinalizer.Parent is TryStatementSyntax nestedProtector
                        && nestedProtector.Parent is BlockSyntax parentBlock)
                    {
                        path = nestedProtector;
                        pathBlock = parentBlock;

                        continue;
                    }

                    return false;
                }
            }

            foreach (InvocationExpressionSyntax admission in member.Syntax
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => call.SpanStart < position
                    && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && Normalize(method)
                        == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup"))
            {
                if (!IsGuardedAdmission(admission)
                    || admission.ArgumentList.Arguments.LastOrDefault()?.Expression is not { } outExpression)
                {
                    continue;
                }

                SingleVariableDesignationSyntax? designation = outExpression
                    .DescendantNodesAndSelf()
                    .OfType<SingleVariableDesignationSyntax>()
                    .SingleOrDefault();

                ISymbol? admitted = designation is null
                    ? member.Model.GetSymbolInfo(outExpression).Symbol
                    : member.Model.GetDeclaredSymbol(designation);

                TryStatementSyntax? protector = admission.Ancestors()
                    .OfType<TryStatementSyntax>()
                    .FirstOrDefault(candidate => candidate.Block.Span.Contains(site.Span)
                        && candidate.Finally is not null);

                if (admitted is null || protector?.Finally is not { } finalizer)
                {
                    continue;
                }

                IfStatementSyntax[] conditionalAdmission = admission.Ancestors()
                    .OfType<IfStatementSyntax>()
                    .TakeWhile(guard => protector.Block.Span.Contains(guard.Span))
                    .Where(guard => guard.Statement.Span.Contains(admission.Span)
                        && !guard.Condition.Span.Contains(admission.Span)
                        && !guard.Statement.Span.Contains(site.Span))
                    .ToArray();

                if (conditionalAdmission.Any(guard => !IsProvenNonNullAdmissionGuard(guard)))
                {
                    continue;
                }

                AssignmentExpressionSyntax[] transfers = member.Syntax
                    .DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && assignment.SpanStart > admission.Span.End
                        && assignment.Span.End < position
                        && SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(assignment.Right).Symbol,
                            admitted))
                    .ToArray();

                ISymbol carrier = admitted;

                if (transfers is [AssignmentExpressionSyntax singleTransfer])
                {
                    if (singleTransfer.Ancestors()
                        .TakeWhile(ancestor => ancestor != protector.Block)
                        .Any(ancestor => ancestor is IfStatementSyntax guard
                                && !SharesConditionalBranch(
                                    guard,
                                    singleTransfer,
                                    site)
                                && !IsProvenNonNullAdmissionGuard(guard)
                            || ancestor is ConditionalExpressionSyntax
                            or SwitchExpressionSyntax
                            or SwitchStatementSyntax
                            or AnonymousFunctionExpressionSyntax
                            or LocalFunctionStatementSyntax)
                        || member.Model.GetSymbolInfo(singleTransfer.Left).Symbol is not ILocalSymbol transferred)
                    {
                        continue;
                    }

                    carrier = transferred;
                }
                else if (transfers.Length != 0)
                {
                    continue;
                }

                VariableDeclaratorSyntax? carrierDeclaration = carrier.DeclaringSyntaxReferences
                    .Select(static reference => reference.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .SingleOrDefault();

                if (carrierDeclaration is null
                    || !SymbolEqualityComparer.Default.Equals(carrier, admitted)
                        && !IsNullInitialization(member.Model, carrierDeclaration.Initializer))
                {
                    continue;
                }

                AssignmentExpressionSyntax[] carrierWrites = member.Syntax
                    .DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => SymbolEqualityComparer.Default.Equals(
                        member.Model.GetSymbolInfo(assignment.Left).Symbol,
                        carrier))
                    .ToArray();

                bool exactWrites = SymbolEqualityComparer.Default.Equals(carrier, admitted)
                    ? carrierWrites.Length == 0
                    : carrierWrites is [AssignmentExpressionSyntax only]
                        && transfers.Length == 1
                        && only == transfers[0];

                if (!exactWrites
                    || AdmissionEndedBefore(
                        member,
                        Location(admission),
                        position,
                        []))
                {
                    continue;
                }

                bool consumedBeforeSite = member.Syntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => call != admission
                        && call.SpanStart > admission.Span.End
                        && call.Span.End < position)
                    .Any(call => AdmissionReceiver(member, call) is { } receiver
                            && (SymbolEqualityComparer.Default.Equals(
                                    member.Model.GetSymbolInfo(receiver).Symbol,
                                    admitted)
                                || SymbolEqualityComparer.Default.Equals(
                                    member.Model.GetSymbolInfo(receiver).Symbol,
                                    carrier))
                        || AdmissionArguments(member, call).Any(argument =>
                            SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(argument.Expression).Symbol,
                                admitted)
                            || SymbolEqualityComparer.Default.Equals(
                                member.Model.GetSymbolInfo(argument.Expression).Symbol,
                                carrier)));

                if (consumedBeforeSite)
                {
                    continue;
                }

                InvocationExpressionSyntax[] cleanups = finalizer.Block
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => AdmissionReceiver(member, call) is { } receiver
                        && SymbolEqualityComparer.Default.Equals(
                            member.Model.GetSymbolInfo(receiver).Symbol,
                            carrier)
                        && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                        && method.Name is "Dispose" or "DisposeAsync")
                    .ToArray();

                if (cleanups is not [InvocationExpressionSyntax cleanup]
                    || member.Model.GetSymbolInfo(cleanup).Symbol is not IMethodSymbol cleanupMethod
                    || cleanupMethod.Name == "DisposeAsync"
                        && CompletionPoint(member, cleanup, false) is null
                    || cleanupMethod.Name == "Dispose"
                        && TypeKey(cleanupMethod.ReturnType) != "System.Void"
                    || !RunsOnEveryFinallyPath(finalizer, cleanup, carrier))
                {
                    continue;
                }

                return Location(admission);
            }

            return null;
        }

        private HostedProducerSiteKind? Classify(
            IMethodSymbol method,
            string callee,
            SyntaxNode node,
            AuthoredMember member)
        {
            SemanticModel model = member.Model;

            if (callee.StartsWith("<ambiguous:", StringComparison.Ordinal))
            {
                diagnostics.Add(new("HOSTED_CALL_TARGET_UNRESOLVED", Location(node), callee));

                return null;
            }

            if (IsBootstrapConsoleRedirection(member)
                && TypeKey(method.ContainingType) == "System.Console"
                && method.Name is "SetOut" or "SetError")
            {
                return HostedProducerSiteKind.FileSystemEffect;
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

            if ((callee is "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" or "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease")
                && (IsGuardedAdmission(node)
                    || callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup"
                    && node is InvocationExpressionSyntax conditionalRecovery
                    && IsConditionalRecoveryEffectAdmission(model, conditionalRecovery)))
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

            if (IsReviewedInMemoryStreamReaderMember(
                method,
                node,
                model,
                member))
            {
                return null;
            }

            if (TryClassifyExternalProducer(method, out HostedProducerSiteKind externalKind))
            {
                return externalKind;
            }

            return null;
        }

        private static bool IsBootstrapConsoleRedirection(AuthoredMember member) =>
            TypeKey(member.Symbol.ContainingType)
                == "RetroDownfall.Arcanum.Cli.Commands.ServeCommand"
            && member.Symbol.Name == "RedirectConsoleToBootstrapLog";

        private static bool TryClassifyExternalProducer(
            IMethodSymbol method,
            out HostedProducerSiteKind kind)
        {
            string type = TypeKey(method.ContainingType);

            if (IsDatabaseOperation(method))
            {
                kind = HostedProducerSiteKind.DatabaseAccess;

                return true;
            }

            if (type is "System.Net.Http.HttpClient"
                    or "System.Net.Sockets.Socket"
                    or "System.Diagnostics.Process"
                && method.Name is not ".ctor")
            {
                kind = HostedProducerSiteKind.ProviderCall;

                return true;
            }

            if (type == "Microsoft.AspNetCore.DataProtection.IDataProtector"
                && method.Name is "Protect" or "Unprotect"
                || type == "System.Security.Principal.WindowsIdentity"
                    && method.Name == "GetCurrent"
                || type == "System.IO.FileSystemAclExtensions"
                    && method.Name.StartsWith("Get", StringComparison.Ordinal)
                || type.StartsWith(
                        "System.Security.AccessControl.",
                        StringComparison.Ordinal)
                    && method.Name.StartsWith("Get", StringComparison.Ordinal))
            {
                kind = HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            if (type == "System.IO.RandomAccess")
            {
                kind = method.Name is "Write" or "WriteAsync" or "FlushToDisk"
                    ? HostedProducerSiteKind.FileSystemEffect
                    : HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            if (type is "System.IO.Stream" or "System.IO.TextWriter")
            {
                kind = method.Name.StartsWith("Read", StringComparison.Ordinal)
                    ? HostedProducerSiteKind.FileSystemRead
                    : HostedProducerSiteKind.FileSystemEffect;

                return true;
            }

            if (type == "System.IO.StreamReader")
            {
                kind = HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            if (type == "System.Security.Cryptography.SHA256"
                && method.Name == "HashDataAsync"
                || type == "System.Text.Json.JsonSerializer"
                    && method.Parameters.Any(static parameter =>
                        TypeKey(parameter.Type) == "System.IO.Stream")
                    && method.Name.StartsWith("Deserialize", StringComparison.Ordinal))
            {
                kind = HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            if (type == "System.Text.Json.JsonSerializer"
                && method.Parameters.Any(static parameter =>
                    TypeKey(parameter.Type) == "System.IO.Stream")
                && method.Name.StartsWith("Serialize", StringComparison.Ordinal)
                || type == "Serilog.FileLoggerConfigurationExtensions"
                    && method.Name == "File")
            {
                kind = HostedProducerSiteKind.FileSystemEffect;

                return true;
            }

            kind = default;

            return false;
        }

        private static bool TryClassifyExternalProducer(
            IPropertySymbol property,
            bool mutation,
            out HostedProducerSiteKind kind)
        {
            string type = TypeKey(property.ContainingType);

            if (IsDatabaseOperation(property, mutation))
            {
                kind = HostedProducerSiteKind.DatabaseAccess;

                return true;
            }

            if (type == "System.IO.FileSystemWatcher"
                && property.Name == "EnableRaisingEvents")
            {
                kind = HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            if (type is "System.IO.FileSystemInfo" or "System.IO.DriveInfo"
                || type == "System.Diagnostics.Process"
                    && property.Name is "ExitCode" or "HasExited" or "StandardError" or "StandardOutput"
                || type.StartsWith("System.Security.AccessControl.", StringComparison.Ordinal)
                    || type == "System.Security.Principal.WindowsIdentity")
            {
                if (mutation
                    && type is "System.IO.FileSystemInfo" or "System.IO.DriveInfo")
                {
                    kind = default;

                    return false;
                }

                kind = type == "System.Diagnostics.Process"
                    ? HostedProducerSiteKind.ProviderCall
                    : HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            if (type == "System.IO.Stream")
            {
                kind = mutation
                    ? HostedProducerSiteKind.FileSystemEffect
                    : HostedProducerSiteKind.FileSystemRead;

                return true;
            }

            kind = default;

            return false;
        }

        private static bool IsDatabaseOperation(IMethodSymbol method)
        {
            string type = TypeKey(method.ContainingType);

            return type == "Microsoft.Data.Sqlite.SqliteCommand"
                    && method.Name.StartsWith("Execute", StringComparison.Ordinal)
                || type == "Microsoft.Data.Sqlite.SqliteConnection"
                    && method.Name is "BeginTransaction" or "ClearAllPools" or "ClearPool" or "Open" or "OpenAsync"
                || type == "Microsoft.EntityFrameworkCore.DbContext"
                    && method.Name.StartsWith("SaveChanges", StringComparison.Ordinal)
                || type == "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"
                    && method.Name.StartsWith("BeginTransaction", StringComparison.Ordinal)
                || type == "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"
                    && method.Name.StartsWith("OpenConnection", StringComparison.Ordinal)
                || type == "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"
                    && method.Name is "Commit" or "CommitAsync" or "Rollback" or "RollbackAsync"
                || type == "System.Data.Common.DbCommand"
                    && method.Name.StartsWith("Execute", StringComparison.Ordinal)
                || type == "System.Data.Common.DbConnection"
                    && method.Name is "BeginTransaction" or "BeginTransactionAsync" or "Close" or "CloseAsync" or "Open" or "OpenAsync"
                || type == "System.Data.Common.DbDataReader"
                    && method.Name is "CloseAsync" or "Read" or "ReadAsync" or "NextResult" or "NextResultAsync"
                || type == "System.Data.Common.DbTransaction"
                    && method.Name is "Commit" or "CommitAsync" or "Rollback" or "RollbackAsync"
                || type == "SQLitePCL.raw"
                    && method.Name.StartsWith("sqlite3_backup_", StringComparison.Ordinal);
        }

        private static bool IsDatabaseOperation(
            IPropertySymbol property,
            bool mutation) => false;

        private static bool IsReviewedDatabaseSupportMember(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            string type = TypeKey(definition.ContainingType);

            return type == "Microsoft.Data.Sqlite.SqliteCommand"
                    && definition.Name is "CreateParameter" or "Dispose" or "DisposeAsync"
                || type == "Microsoft.Data.Sqlite.SqliteConnection"
                    && definition.Name is ".ctor" or "CreateCommand" or "Dispose" or "DisposeAsync"
                || type == "Microsoft.Data.Sqlite.SqliteConnectionStringBuilder"
                    && definition.Name is ".ctor" or "ToString"
                || type == "Microsoft.Data.Sqlite.SqliteDataReader"
                    && (definition.Name.StartsWith("Get", StringComparison.Ordinal)
                        || definition.Name is "Dispose" or "DisposeAsync" or "IsDBNull")
                || type == "Microsoft.Data.Sqlite.SqliteException"
                    && definition.Name == "ThrowExceptionForRC"
                || type == "Microsoft.Data.Sqlite.SqliteParameterCollection"
                    && definition.Name is "Add" or "AddWithValue"
                || type == "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker"
                    && definition.Name == "Entries"
                || type == "Microsoft.EntityFrameworkCore.DbContext"
                    && definition.Name is "Dispose" or "DisposeAsync" or "Set"
                || type == "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder`1"
                    && definition.Name is ".ctor" or "UseModel"
                || type == "Microsoft.EntityFrameworkCore.DbSet`1"
                    && definition.Name is "Add" or "AddAsync" or "Attach" or "Remove" or "Update"
                || type == "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"
                    && definition.Name == "GetDbConnection"
                || type == "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions"
                    && definition.Name == "UseSqlite"
                || type == "Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions"
                    && definition.Name == "GetDbTransaction"
                || type == "System.Data.Common.DbCommand"
                    && definition.Name is "CreateParameter" or "Dispose" or "DisposeAsync"
                || type == "System.Data.Common.DbConnection"
                    && definition.Name is "CreateCommand" or "Dispose" or "DisposeAsync"
                || type == "System.Data.Common.DbConnectionStringBuilder"
                    && definition.Name == "ToString"
                || type == "System.Data.Common.DbDataReader"
                    && (definition.Name.StartsWith("Get", StringComparison.Ordinal)
                        || definition.Name is "Dispose" or "DisposeAsync" or "IsDBNull")
                || type == "System.Data.Common.DbParameterCollection"
                    && definition.Name == "Add"
                || type == "System.Data.Common.DbTransaction"
                    && definition.Name is "Dispose" or "DisposeAsync"
                || type == "SQLitePCL.raw"
                    && definition.Name == "sqlite3_errcode";
        }

        private static bool IsReviewedDatabaseSupportMember(
            IPropertySymbol property,
            bool mutation)
        {
            string type = TypeKey(property.ContainingType);

            return type == "Microsoft.Data.Sqlite.SqliteCommand"
                    && property.Name is "CommandText" or "Connection" or "Parameters" or "Transaction"
                || type == "Microsoft.Data.Sqlite.SqliteConnection"
                    && property.Name is "DataSource" or "Handle" or "State"
                || type == "Microsoft.Data.Sqlite.SqliteConnectionStringBuilder"
                    && property.Name is "DataSource" or "Mode" or "Password" or "Pooling"
                || type == "Microsoft.Data.Sqlite.SqliteDataReader"
                    && property.Name == "FieldCount"
                || type == "Microsoft.Data.Sqlite.SqliteException"
                    && property.Name == "SqliteErrorCode"
                || type == "Microsoft.Data.Sqlite.SqliteParameter"
                    && property.Name is "ParameterName" or "Value"
                || type == "Microsoft.Data.Sqlite.SqliteTransaction"
                    && property.Name == "Connection"
                || type == "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry"
                    && property.Name is "Entity" or "State"
                || type == "Microsoft.EntityFrameworkCore.DbContext"
                    && property.Name is "ChangeTracker" or "Database"
                || type == "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder`1"
                    && property.Name == "Options"
                || type == "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"
                    && property.Name == "CurrentTransaction"
                || type == "System.Data.Common.DbCommand"
                    && property.Name is "CommandText" or "Parameters" or "Transaction"
                || type == "System.Data.Common.DbConnection"
                    && property.Name == "State"
                || type == "System.Data.Common.DbDataReader"
                    && property.Name == "FieldCount"
                || type == "System.Data.Common.DbParameter"
                    && property.Name is "DbType" or "ParameterName" or "Value"
                || type == "SQLitePCL.sqlite3_backup"
                    && property.Name == "IsInvalid";
        }

        private static bool IsConditionalRecoveryEffectAdmission(
            SemanticModel model,
            InvocationExpressionSyntax admission)
        {
            IfStatementSyntax? gate = admission.Ancestors()
                .OfType<IfStatementSyntax>()
                .FirstOrDefault(candidate => candidate.Condition.Span.Contains(admission.Span));

            if (gate?.Else?.Statement is not { } admittedBranch
                || gate.Condition is not BinaryExpressionSyntax condition
                || !condition.IsKind(SyntaxKind.LogicalAndExpression)
                || !TryMatchRecoveryDisposition(
                    model,
                    condition.Left,
                    "OrdinaryExternalEffect",
                    negated: false,
                    out _)
                || condition.Right is not PrefixUnaryExpressionSyntax refusal
                || !refusal.IsKind(SyntaxKind.LogicalNotExpression)
                || refusal.Operand.Span != admission.Span)
            {
                return false;
            }

            return admittedBranch.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(call => model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && method.Name == "SettleDiscoveredRuntimeAsync"
                    && TypeKey(method.ContainingType)
                        == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler");
        }

        private static bool IsPureFileSystemMetadataProperty(IPropertySymbol property) => TypeKey(property.ContainingType) == "System.IO.FileInfo" && property.Name is "Name" or "FullName" or "Extension" or "DirectoryName" or "Directory" || TypeKey(property.ContainingType) == "System.IO.FileStream" && property.Name is "Name" or "Position" or "SafeFileHandle";

        private TraversalEvidence AddSite(HostedProducerSiteKind kind, string callee, AuthoredMember member, string rootType, string operationId, SyntaxNode node, string? effectGroup = null, string? workAdmitted = null)
        {
            if (activeBoundedDataProbe is { } lazyStorage)
            {
                boundedLazyDataEvidence[lazyStorage].Add(
                    kind + ":" + callee + "@" + Location(node));
            }

            string rootOperation = RootOperation(operationId);

            string memberIdentity = StableMemberIdentity(member);

            string syntaxFingerprint = Fingerprint(node);

            int syntaxOrdinal = member.Syntax
                .DescendantNodesAndSelf()
                .Where(candidate => candidate.RawKind == node.RawKind && candidate.SpanStart < node.SpanStart && SyntaxFactory.AreEquivalent(candidate, node))
                .Count();

            string capsuleId = CapsuleId(member.Syntax.SyntaxTree.FilePath, memberIdentity, kind, callee, syntaxOrdinal, syntaxFingerprint);

            HostedProducerOperationEntry? ordinary = declaredOperations.FirstOrDefault(entry => entry.Authority == HostedProducerAuthorityKind.OrdinaryHostedWork && rootOperation == entry.OperationId);

            if (RequiresExternalEffect(kind, callee)
                && member.RecoveryContext is
                {
                    Disposition: RecoveryDisposition.OrdinaryDbOnly,
                } recovery)
            {
                diagnostics.Add(new(
                    "HOSTED_RECOVERY_DB_EFFECT",
                    operationId + "/site@" + Location(node),
                    recovery.Kind
                        + " V"
                        + recovery.CheckpointVersion
                        + "; a DB-only recovery tuple reached "
                        + callee
                        + "."));
            }

            if (ordinary is not null && kind != HostedProducerSiteKind.EffectFrontier)
            {
                if (workAdmitted is null)
                {
                    diagnostics.Add(new("HOSTED_SITE_WORK_FRONTIER_MISSING", operationId + "/site@" + Location(node), callee));
                }

                if (RequiresExternalEffect(kind, callee) && effectGroup is null)
                {
                    diagnostics.Add(new("HOSTED_SITE_EFFECT_FRONTIER_MISSING", operationId + "/site@" + Location(node), callee));
                }
            }

            if (kind == HostedProducerSiteKind.EffectFrontier && callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal))
            {
                workAdmitted = rootOperation + "@work:" + Location(node);

                admissionCapsules[workAdmitted] = capsuleId;
            }

            operationId = rootOperation;

            if (workAdmitted is not null)
            {
                operationId += "/work@" + Uri.EscapeDataString(workAdmitted);
            }

            if (kind == HostedProducerSiteKind.EffectFrontier && callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal))
            {
                effectGroup = rootOperation + "@effect:" + Location(node);

                admissionCapsules[effectGroup] = capsuleId;
            }

            if (effectGroup is not null)
            {
                operationId += "/effect@" + Uri.EscapeDataString(effectGroup);
            }

            if (callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal) && node is InvocationExpressionSyntax invocation && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } expression && member.Model.GetSymbolInfo(expression).Symbol is IFieldSymbol workKind)
            {
                operationId += "/workKind=" + workKind.Name;
            }

            GrimoireWorkKind? admittedWorkKind = null;

            if (callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal) && node is InvocationExpressionSyntax workAdmission && workAdmission.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } workKindExpression && member.Model.GetSymbolInfo(workKindExpression).Symbol is IFieldSymbol admittedKind && Enum.TryParse(admittedKind.Name, out GrimoireWorkKind parsedKind))
            {
                admittedWorkKind = parsedKind;
            }

            string? workFrontierCapsuleId = workAdmitted is not null && admissionCapsules.TryGetValue(workAdmitted, out string? workCapsule)
                ? workCapsule
                : null;

            string? effectFrontierCapsuleId = effectGroup is not null && admissionCapsules.TryGetValue(effectGroup, out string? effectCapsule)
                ? effectCapsule
                : null;

            HostedProducerSite site = new(
                rootType,
                operationId + "/site@" + Location(node),
                member.Syntax.SyntaxTree.FilePath,
                TypeKey(member.Symbol.ContainingType),
                member.Symbol.Name,
                kind,
                callee,
                rootOperation,
                memberIdentity,
                syntaxOrdinal,
                syntaxFingerprint,
                workFrontierCapsuleId,
                effectFrontierCapsuleId,
                admittedWorkKind);

            sites.TryAdd(StableSiteIdentity(site), site);

            return TraversalEvidence.Evidence;
        }

        private static string StableMemberIdentity(AuthoredMember member)
        {
            AnonymousFunctionExpressionSyntax? lambda = member.Syntax.AncestorsAndSelf().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();

            SyntaxNode? ownerSyntax = lambda?.Ancestors().FirstOrDefault(static ancestor => ancestor is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax);

            if (lambda is not null && ownerSyntax is not null && member.Model.GetDeclaredSymbol(ownerSyntax) is IMethodSymbol owner)
            {
                string lambdaFingerprint = Fingerprint(lambda);

                int lambdaOrdinal = ownerSyntax.DescendantNodes()
                    .OfType<AnonymousFunctionExpressionSyntax>()
                    .Where(candidate => candidate.SpanStart < lambda.SpanStart && SyntaxFactory.AreEquivalent(candidate, lambda))
                    .Count();

                return MethodKey(owner) + "/lambda#" + lambdaOrdinal.ToString(CultureInfo.InvariantCulture) + "~" + lambdaFingerprint;
            }

            if (member.Symbol.GetDocumentationCommentId() is { Length: > 0 } documentationIdentity)
            {
                return documentationIdentity;
            }

            return MethodKey(member.Symbol);
        }
    }

    private static bool IsLifecycle(IMethodSymbol method) => method.Name is "StartAsync" or "ExecuteAsync" or "StopAsync" && method.Parameters.Length == 1 && method.Parameters[0].Type.ToDisplayString() == "System.Threading.CancellationToken" && method.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task";

    private static bool RequiresExternalEffect(HostedProducerSiteKind kind, string callee) => kind is HostedProducerSiteKind.ProviderCall or HostedProducerSiteKind.FileSystemEffect;

    private static bool IsGuardedAdmission(SyntaxNode node)
    {
        IfStatementSyntax? guard = node.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault(candidate => candidate.Condition.Span.Contains(node.Span));

        if (guard is null)
        {
            return false;
        }

        if (ProvesCallTrue(guard.Condition, node))
        {
            return guard.Statement is BlockSyntax;
        }

        return ProvesConditionFalseMeansCallTrue(guard.Condition, node) && (guard.Else?.Statement is BlockSyntax || FailureTerminates(guard));
    }

    private static bool ProvesCallTrue(ExpressionSyntax condition, SyntaxNode call)
    {
        return condition switch
        {
            ParenthesizedExpressionSyntax parentheses => ProvesCallTrue(parentheses.Expression, call),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) && binary.Left.Span.Contains(call.Span) => ProvesCallTrue(binary.Left, call),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) && binary.Right.Span.Contains(call.Span) => ProvesCallTrue(binary.Right, call),
            _ => condition == call,
        };
    }

    private static bool ProvesConditionFalseMeansCallTrue(ExpressionSyntax condition, SyntaxNode call)
    {
        return condition switch
        {
            ParenthesizedExpressionSyntax parentheses => ProvesConditionFalseMeansCallTrue(parentheses.Expression, call),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) && binary.Left.Span.Contains(call.Span) => ProvesConditionFalseMeansCallTrue(binary.Left, call),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) && binary.Right.Span.Contains(call.Span) => ProvesConditionFalseMeansCallTrue(binary.Right, call),
            PrefixUnaryExpressionSyntax negation when negation.IsKind(SyntaxKind.LogicalNotExpression) => ProvesCallTrue(negation.Operand, call),
            _ => false,
        };
    }

    private static bool TryLoopAdmissionRegion(InvocationExpressionSyntax admission, SyntaxNode site, int position, out BlockSyntax? region, out int admittedAt)
    {
        region = null;

        admittedAt = 0;

        IfStatementSyntax? guard = admission.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault(candidate => candidate.Condition.Span.Contains(admission.Span) && ProvesCallTrue(candidate.Condition, admission));

        WhileStatementSyntax? loop = guard?.Ancestors().OfType<WhileStatementSyntax>().FirstOrDefault();

        if (guard is null || loop?.Condition is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.TrueLiteralExpression } || loop.Parent is not BlockSyntax containing || position <= loop.Span.End || !site.Ancestors().Contains(containing))
        {
            return false;
        }

        BreakStatementSyntax[] exits = loop.Statement.DescendantNodes().OfType<BreakStatementSyntax>().Where(exit => exit.Ancestors().OfType<WhileStatementSyntax>().FirstOrDefault() == loop).ToArray();

        if (exits is not [BreakStatementSyntax exit] || !guard.Statement.Span.Contains(exit.Span))
        {
            return false;
        }

        region = containing;

        admittedAt = loop.Span.End;

        return true;
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

        Compare(sites.Select(StableSiteIdentity), discoveredSites.Items.Select(StableSiteIdentity), "HOSTED_SITE", diagnostics);

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
                foreach (string identity in new[] { site.RootType, site.OperationId, site.SourcePath, site.EnclosingType, site.Callee })
                {
                    CheckIdentity(identity, diagnostics);
                }

                CheckIdentity(site.Member.Length == 0 ? site.MemberIdentity : site.Member, diagnostics);

                if (!site.OperationId.StartsWith(operation.OperationId + "/", StringComparison.Ordinal) && site.OperationId != operation.OperationId || site.RootType != catalog.FirstOrDefault(service => service.Operations.Contains(operation))?.ServiceType && !nonHostedCatalog.Any(chain => chain.ChainId == operation.OperationId && chain.EnclosingType == site.RootType))
                {
                    diagnostics.Add(new("HOSTED_SITE_ROOT_MISMATCH", SiteIdentity(site), "The site must belong to this exact operation and owner root."));
                }

                if (operation.Authority == HostedProducerAuthorityKind.EffectFree && site.Kind != HostedProducerSiteKind.EffectFrontier)
                {
                    diagnostics.Add(new("HOSTED_SITE_AUTHORITY_INVALID", SiteIdentity(site), "Effect-free authority cannot contain a sensitive producer site."));
                }

                if (ordinary && site.Kind != HostedProducerSiteKind.EffectFrontier && !operation.Sites.Any(frontier => frontier.Kind == HostedProducerSiteKind.EffectFrontier && FrontierIdentity(site, "work") is { } lease && lease == FrontierIdentity(frontier, "work") && frontier.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal) && (frontier.AdmittedWorkKind == operation.WorkKind || frontier.OperationId.Contains("/workKind=" + operation.WorkKind, StringComparison.Ordinal))))
                {
                    diagnostics.Add(new("HOSTED_SITE_WORK_FRONTIER_MISSING", SiteIdentity(site), "Ordinary work requires admission for its declared work kind before scopes and effects."));
                }

                if (ordinary && RequiresExternalEffect(site.Kind, site.Callee) && !operation.Sites.Any(frontier => frontier.Kind == HostedProducerSiteKind.EffectFrontier && EffectIdentity(site) is { } group && group == EffectIdentity(frontier) && frontier.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal)))
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

    internal static string StableSiteIdentity(HostedProducerSite site)
    {
        if (site.AuthorityOperationId.Length == 0 || site.MemberIdentity.Length == 0 || site.SyntaxOrdinal < 0 || site.SyntaxFingerprint.Length == 0)
        {
            return SiteIdentity(site);
        }

        return string.Join('|', site.RootType, site.AuthorityOperationId, site.CapsuleId, site.WorkFrontierCapsuleId ?? "-", site.EffectFrontierCapsuleId ?? "-", site.AdmittedWorkKind?.ToString() ?? "-");
    }

    internal static string CapsuleId(HostedProducerSite site) => CapsuleId(site.SourcePath, site.MemberIdentity, site.Kind, site.Callee, site.SyntaxOrdinal, site.SyntaxFingerprint);

    private static string CapsuleId(string sourcePath, string memberIdentity, HostedProducerSiteKind kind, string callee, int syntaxOrdinal, string syntaxFingerprint)
    {
        string evidence = string.Join('\n', sourcePath, memberIdentity, kind.ToString(), callee, syntaxOrdinal.ToString(CultureInfo.InvariantCulture), syntaxFingerprint);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
    }

    internal static string Fingerprint(SyntaxNode node)
    {
        StringBuilder tokens = new();

        foreach (SyntaxToken token in node.DescendantTokens())
        {
            tokens.Append(token.RawKind.ToString(CultureInfo.InvariantCulture)).Append(':').Append(token.ValueText.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(token.ValueText).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokens.ToString())));
    }

    private static string? EffectIdentity(HostedProducerSite site) => FrontierIdentity(site, "effect");

    private static string? FrontierIdentity(HostedProducerSite site, string kind)
    {
        string? stable = kind == "work"
            ? site.WorkFrontierCapsuleId
            : site.EffectFrontierCapsuleId;

        if (stable is not null)
        {
            return stable;
        }

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
