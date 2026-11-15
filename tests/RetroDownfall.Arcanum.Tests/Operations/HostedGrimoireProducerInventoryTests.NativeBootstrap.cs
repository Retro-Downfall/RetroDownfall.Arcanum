using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void CompletedProductionNativeBootstrapRetainsItsGeneratedImportImplementations()
    {
        CSharpCompilation compilation = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol owner = Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetTypeByMetadataName("RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime"));

        foreach (string name in new[] { "GetNativeImageInfo", "GetModuleFileName" })
        {
            IMethodSymbol definition = Assert.Single(owner.GetMembers(name).OfType<IMethodSymbol>());

            IMethodSymbol implementation = Assert.IsAssignableFrom<IMethodSymbol>(definition.PartialImplementationPart);

            Assert.True(SymbolEqualityComparer.Default.Equals(definition, implementation.PartialDefinitionPart));

            Assert.True(SymbolEqualityComparer.Default.Equals(owner, implementation.ContainingType));

            Assert.IsType<MethodDeclarationSyntax>(Assert.Single(implementation.DeclaringSyntaxReferences).GetSyntax());
        }

        Assert.True(HostedGrimoireProducerInventory.ProbeCompletedProductionNativeBootstrap(),
            "The exact actual bootstrap includes its compiler-linked generated native import implementations.");
    }

    private const string NativeBootstrapFixtureDirectory = "src/RetroDownfall.Arcanum.Infrastructure/Data/";

    private static string NativeBootstrapActualSource(string name) => File.ReadAllText(
        Path.Combine(TestRepositoryPaths.RepositoryRoot(), NativeBootstrapFixtureDirectory, name));

    private static CSharpCompilation NativeBootstrapFixture(string shape = "actual", bool designTime = false)
    {
        string runtime = NativeBootstrapActualSource("SqliteNativeRuntime.cs");

        string contract = NativeBootstrapActualSource("ISqliteNativeRuntime.cs");

        string exception = NativeBootstrapActualSource("SqliteNativeRuntimeUnavailableException.cs");

        MethodDeclarationSyntax factoryMethod = CSharpSyntaxTree.ParseText(NativeBootstrapActualSource("ArcanumDbContextFactory.cs"))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "InitializeDesignTimeNativeRuntime");

        string factory = "using System.Diagnostics.CodeAnalysis; namespace RetroDownfall.Arcanum.Infrastructure.Data; "
            + "public sealed class ArcanumDbContextFactory { " + factoryMethod.ToFullString()
            + " internal static void Exercise() { _ = InitializeDesignTimeNativeRuntime(); } }";

        string extra = string.Empty;

        switch (shape)
        {
            case "discard-shadow":
                runtime = "using static NativeBootstrapDiscardShadow;\n" + runtime;
                extra = "internal static class NativeBootstrapDiscardShadow { internal static bool _ { set { File.Delete(\"native-model-effect\"); } } }";
                break;
            case "factory-effect":
                runtime = runtime.ReplaceSdkFixtureText("IntPtr handle = NativeLibrary.Load(assetPath);",
                    "File.Delete(\"native-model-effect\"); IntPtr handle = NativeLibrary.Load(assetPath);", StringComparison.Ordinal);
                break;
            case "resolver-effect":
                runtime = runtime.ReplaceSdkFixtureText("if (libraryName != \"sqlcipher\"",
                    "File.Delete(\"native-model-effect\"); if (libraryName != \"sqlcipher\"", StringComparison.Ordinal);
                break;
            case "validation-effect":
                runtime = runtime.ReplaceSdkFixtureText("string? loadedPath;",
                    "File.Delete(\"native-model-effect\"); string? loadedPath;", StringComparison.Ordinal);
                break;
            case "exception-effect":
                exception = exception.ReplaceSdkFixtureText("RuntimeIdentifier = runtimeIdentifier;",
                    "File.Delete(\"native-model-effect\"); RuntimeIdentifier = runtimeIdentifier;", StringComparison.Ordinal);
                break;
            case "extra-write":
                runtime = runtime.ReplaceSdkFixtureText("_nativeHandle = handle;",
                    "_nativeHandle = handle; _nativeHandle = IntPtr.Zero;", StringComparison.Ordinal);
                break;
            case "ref-escape":
                runtime = runtime.ReplaceSdkFixtureText("_nativeHandle = handle;",
                    "_nativeHandle = handle; Rebind(ref _nativeHandle);", StringComparison.Ordinal)
                    .ReplaceSdkFixtureText("private static string ExpectedAssetFileName()",
                        "private static void Rebind(ref IntPtr value) { value = IntPtr.Zero; } private static string ExpectedAssetFileName()", StringComparison.Ordinal);
                break;
            case "implicit-in-escape":
                runtime = runtime.ReplaceSdkFixtureText("_nativeHandle = handle;",
                    "_nativeHandle = handle; Rebind(_nativeHandle);", StringComparison.Ordinal)
                    .ReplaceSdkFixtureText("private static string ExpectedAssetFileName()",
                        "private static void Rebind(in IntPtr value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = IntPtr.Zero; } private static string ExpectedAssetFileName()", StringComparison.Ordinal);
                break;
            case "free-published":
                runtime = runtime.ReplaceSdkFixtureText("_nativeHandle = handle;",
                    "_nativeHandle = handle; NativeLibrary.Free(handle);", StringComparison.Ordinal);
                break;
            case "zero-fallback":
                runtime = runtime.ReplaceSdkFixtureText("return _nativeHandle;", "return IntPtr.Zero;", StringComparison.Ordinal);
                break;
            case "publication-only":
                runtime = runtime.ReplaceSdkFixtureText("LazyThreadSafetyMode.ExecutionAndPublication",
                    "LazyThreadSafetyMode.PublicationOnly", StringComparison.Ordinal);
                break;
            case "no-lock":
                runtime = runtime.ReplaceSdkFixtureText("lock (InitializationGate)", "if (true)", StringComparison.Ordinal);
                break;
            case "different-resolver":
                runtime = runtime.ReplaceSdkFixtureText(".Assembly, ResolveImport);", ".Assembly, OtherImport);", StringComparison.Ordinal)
                    .ReplaceSdkFixtureText("private static string ExpectedAssetFileName()",
                        "private static IntPtr OtherImport(string library, System.Reflection.Assembly assembly, System.Runtime.InteropServices.DllImportSearchPath? search) => IntPtr.Zero; private static string ExpectedAssetFileName()", StringComparison.Ordinal);
                break;
            case "other-native-import":
                runtime = runtime.ReplaceSdkFixtureText("EntryPoint = \"dladdr\"", "EntryPoint = \"forged_dladdr\"", StringComparison.Ordinal);
                break;
            case "extra-partial":
                extra = "namespace RetroDownfall.Arcanum.Infrastructure.Data { internal sealed partial class SqliteNativeRuntime { private static void Rebind() { _nativeHandle = IntPtr.Zero; } } }";
                break;
            case "trusted-alias-shadow":
                runtime = "using TypeInitializationException = System.InvalidOperationException;\n" + runtime;
                break;
            case "foreign-framework-symbol":
                runtime = "using NativeLibrary = NativeBootstrapCounterfeit;\n" + runtime;
                extra = """
                    internal static class NativeBootstrapCounterfeit
                    {
                        internal static IntPtr Load(string path) { File.Delete("native-model-effect"); return new IntPtr(1); }
                        internal static void Free(IntPtr handle) { }
                        internal static IntPtr GetExport(IntPtr handle, string name) => handle;
                        internal static void SetDllImportResolver(System.Reflection.Assembly assembly, System.Runtime.InteropServices.DllImportResolver resolver) { }
                    }
                    """;
                break;
            case "foreign-provider-symbol":
                runtime = "using SQLite3Provider_sqlcipher = NativeBootstrapCounterfeitProvider;\nusing raw = NativeBootstrapCounterfeitRaw;\n" + runtime;
                extra = """
                    internal sealed class NativeBootstrapCounterfeitProvider { }
                    internal static class NativeBootstrapCounterfeitRaw
                    {
                        internal static void SetProvider(object provider) { File.Delete("native-model-effect"); }
                        internal static void FreezeProvider(bool value) { }
                    }
                    """;
                break;
            case "factory-nested-shadow":
                factory = factory.ReplaceSdkFixtureText("public sealed class ArcanumDbContextFactory {",
                    "public sealed class ArcanumDbContextFactory { private static class Path { internal static string GetDirectoryName(string value) { File.Delete(\"native-model-effect\"); return \"/untrusted\"; } }", StringComparison.Ordinal);
                break;
            case "arbitrary-design-directory":
                factory = factory.ReplaceSdkFixtureText("InitializeForDesignTime(assemblyDirectory)",
                    "InitializeForDesignTime(Path.GetTempPath())", StringComparison.Ordinal);
                break;
            case "additional-design-caller":
                extra = "namespace RetroDownfall.Arcanum.Infrastructure.Data { internal static class NativeBootstrapRedirect { internal static void Run() => SqliteNativeRuntime.Instance.InitializeForDesignTime(\"/untrusted\"); } }";
                break;
            case "design-method-group-escape":
                extra = "namespace RetroDownfall.Arcanum.Infrastructure.Data { internal static class NativeBootstrapRedirect { internal static Action<string> Take() => SqliteNativeRuntime.Instance.InitializeForDesignTime; } }";
                break;
        }

        string runtimeName = "SqliteNativeRuntime";

        if (shape == "foreign-owner")
        {
            runtime = runtime.Replace("SqliteNativeRuntime", "ForeignNativeRuntime", StringComparison.Ordinal);
            contract = contract.Replace("SqliteNativeRuntime", "ForeignNativeRuntime", StringComparison.Ordinal);
            exception = exception.Replace("SqliteNativeRuntime", "ForeignNativeRuntime", StringComparison.Ordinal);
            factory = factory.Replace("SqliteNativeRuntime", "ForeignNativeRuntime", StringComparison.Ordinal);
            runtimeName = "ForeignNativeRuntime";
        }

        string call = designTime ? "ArcanumDbContextFactory.Exercise();" : runtimeName + ".Instance.Initialize();";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("RetroDownfall.Arcanum.Infrastructure.Data." + call + " await Task.CompletedTask;"),
            "RetroDownfall.Arcanum.Infrastructure")
            .WithAssemblyName(shape == "foreign-assembly" ? "Foreign.NativeOwner" : "RetroDownfall.Arcanum.Infrastructure")
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
            .AddSyntaxTrees(
                CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; [assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"RetroDownfall.Arcanum.Api\")]", path: "src/NativeGlobals.cs"),
                CSharpSyntaxTree.ParseText(runtime, path: NativeBootstrapFixtureDirectory + "SqliteNativeRuntime.cs"),
                CSharpSyntaxTree.ParseText(contract, path: NativeBootstrapFixtureDirectory + "ISqliteNativeRuntime.cs"),
                CSharpSyntaxTree.ParseText(exception, path: NativeBootstrapFixtureDirectory + "SqliteNativeRuntimeUnavailableException.cs"),
                CSharpSyntaxTree.ParseText(factory, path: NativeBootstrapFixtureDirectory + "ArcanumDbContextFactory.cs"),
                CSharpSyntaxTree.ParseText(extra, path: "src/NativeMutation.cs"));

        // The authored-file fixture intentionally excludes generated implementations; ProductionCompilations includes generator output.
        // These exact LibraryImport declarations require generator output to emit an assembly.
        // Missing generated out-parameter implementation also leaves the one image field unassigned.
        // This fixture proves source analysis only: no fake implementations or native execution.
        Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.True(errors.Length == 3, string.Join(System.Environment.NewLine, errors.Select(static error => error.ToString())));

        foreach (Diagnostic error in errors)
        {
            Assert.Equal(NativeBootstrapFixtureDirectory + "SqliteNativeRuntime.cs", error.Location.SourceTree!.FilePath);

            if (error.Id == "CS0170")
            {
                Assert.Equal("image.FileName", error.Location.SourceTree.GetText().ToString(error.Location.SourceSpan));

                Assert.Equal("ValidateNativeImage", error.Location.SourceTree.GetRoot().FindNode(error.Location.SourceSpan)
                    .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText);

                continue;
            }

            Assert.Equal("CS8795", error.Id);

            MethodDeclarationSyntax method = error.Location.SourceTree.GetRoot().FindNode(error.Location.SourceSpan)
                .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
            Assert.Contains(method.Identifier.ValueText, new[] { "GetNativeImageInfo", "GetModuleFileName" });
        }

        return compilation;
    }

    [Fact]
    public void A_friend_assembly_cannot_rebind_the_reviewed_native_asset_directory()
    {
        CSharpCompilation infrastructure = NativeBootstrapFixture();

        CSharpCompilation api = CSharpCompilation.Create("RetroDownfall.Arcanum.Api",
            [CSharpSyntaxTree.ParseText("internal static class AdditionalNativeCaller { internal static void Run() => RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime.Instance.InitializeForDesignTime(\"/untrusted\"); }", path: "src/NativeFriend.cs")],
            infrastructure.References.Append(infrastructure.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(api.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [infrastructure, api], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverNativeBootstrap(CSharpCompilation compilation) =>
        HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_native_bootstrap_is_process_owned_and_reaches_no_Grimoire_work(bool designTime)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNativeBootstrap(NativeBootstrapFixture(designTime: designTime));

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("discard-shadow")]
    [InlineData("factory-effect")]
    [InlineData("resolver-effect")]
    [InlineData("validation-effect")]
    [InlineData("exception-effect")]
    [InlineData("extra-write")]
    [InlineData("ref-escape")]
    [InlineData("implicit-in-escape")]
    [InlineData("free-published")]
    [InlineData("zero-fallback")]
    [InlineData("publication-only")]
    [InlineData("no-lock")]
    [InlineData("different-resolver")]
    [InlineData("other-native-import")]
    [InlineData("extra-partial")]
    [InlineData("trusted-alias-shadow")]
    [InlineData("foreign-framework-symbol")]
    [InlineData("foreign-provider-symbol")]
    [InlineData("foreign-owner")]
    [InlineData("foreign-assembly")]
    [InlineData("factory-nested-shadow")]
    [InlineData("arbitrary-design-directory")]
    [InlineData("additional-design-caller")]
    [InlineData("design-method-group-escape")]
    public void Native_bootstrap_review_is_invalidated_by_changed_effects_identity_or_lifetime(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNativeBootstrap(NativeBootstrapFixture(shape));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_CALL_TARGET_UNRESOLVED");
    }
}
