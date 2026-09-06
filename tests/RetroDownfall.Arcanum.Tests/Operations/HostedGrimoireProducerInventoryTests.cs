using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Infrastructure.Data;

using System.Collections.Immutable;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class HostedGrimoireProducerInventoryTests(ITestOutputHelper output)
{
    private static string R2Source(string body, string extra = "") => RegistrationSource("services.AddHostedService<Worker>();").Replace("public Task StartAsync(CancellationToken token) => Task.CompletedTask;", "public async Task StartAsync(CancellationToken token) { " + body + " }", StringComparison.Ordinal) + AdmissionTypes + extra;

    private static string R2Admission => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " if (!lease.TryBeginExternalEffectGroup(out var group)) return; using var held = group; ";

    private static HostedProducerDiscovery<HostedProducerSite> R2Discover(string source, params HostedProducerOperationEntry[] roots)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", roots.Length == 0 ? [OrdinaryRoot()] : roots)], []);
    }

    [Theory]
    [InlineData("await pending;", true)]
    [InlineData("await pending.ConfigureAwait(false);", true)]
    [InlineData("await (DateTime.UtcNow.Ticks < 0 ? pending : Task.CompletedTask);", false)]
    [InlineData("await Task.WhenAny(pending, Task.CompletedTask);", false)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) await pending;", false)]
    public void R3TaskLocalRequiresExactUnconditionalJoin(string join, bool owned)
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "var pending = Task.Run(() => System.IO.File.Delete(\"path\")); " + join));

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("held.Release();", false)]
    [InlineData("held.ReleaseAndUse();", false)]
    [InlineData("held.Keep();", true)]
    [InlineData("held.ReleaseOther(other);", true)]
    [InlineData("other.ReleaseOther(held);", false)]
    [InlineData("Helper.Release(other); Helper.Release(held);", false)]
    [InlineData("Helper.Keep(held); Helper.Release(other);", true)]
    [InlineData("Helper.Release(condition ? held : other);", false)]
    [InlineData("Helper.Release(other ?? held);", false)]
    [InlineData("Helper.Release(condition switch { true => held, _ => other });", false)]
    [InlineData("Helper.Release(new[] { held, other }[condition ? 0 : 1]);", false)]
    [InlineData("Helper.Release((held, other).Item1);", false)]
    [InlineData("GC.KeepAlive(condition ? held : other);", false)]
    [InlineData("GC.KeepAlive(from item in new[] { held, other } select item);", false)]
    [InlineData("held?.Release();", false)]
    [InlineData("new Box(held).Release();", false)]
    [InlineData("held.GetHashCode();", false)]
    public void R4AdmissionOriginsIncludeReceiversAndCompositeArguments(string operation, bool retained)
    {
        string helpers = "static class Helper { public static void Release(this IDisposable handle) => handle.Dispose(); public static void ReleaseAndUse(this IDisposable handle) { handle.Dispose(); System.IO.File.Delete(\"inside\"); } public static void Keep(this IDisposable handle) {} public static void ReleaseOther(this IDisposable handle, IDisposable other) => other.Dispose(); } class Box(IDisposable handle) { public void Release() => handle.Dispose(); }";

        string source = R2Source(R2Admission + "IDisposable other=null!; bool condition=DateTime.UtcNow.Ticks<0; " + operation + " System.IO.File.Delete(\"after\");", helpers);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(operation.StartsWith("GC.KeepAlive(from", StringComparison.Ordinal) ? "using System.Linq;" + source : source);

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Fact]
    public void R4OpaqueCallbackCaptureRemainsAnExplicitOwnershipRefusal()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "GC.KeepAlive((Action)(() => held.Dispose()));"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    private static string R4CarrierSource(string constructor, string completion = "await first.CompleteAsync(); await second.CompleteAsync();", string disposal = "first.Dispose(); second.Dispose();", bool readOnly = true, string beforeReturn = "", string arguments = "a,b") => R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writers=Carrier.Create(store); await writers.CompleteAsync();", R2Blobs + "class Carrier : IDisposable { private " + (readOnly ? "readonly " : "") + "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first,second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { " + constructor + " } public static Carrier Create(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store) { var a=store.CreateWriterAsync(); var b=store.CreateWriterAsync(); " + beforeReturn + " return new Carrier(" + arguments + "); } public async Task CompleteAsync() { " + completion + " } public void Dispose() { " + disposal + " } }");

    [Theory]
    [InlineData("stable", true)]
    [InlineData("overwrite", false)]
    [InlineData("conditional", false)]
    [InlineData("mutable", false)]
    [InlineData("parameter-write", false)]
    [InlineData("local-write", false)]
    [InlineData("named-arguments", true)]
    [InlineData("ref-field", false)]
    public void R4CarrierMappingRequiresOneStableWriterPerReadonlyField(string shape, bool complete)
    {
        string constructor = shape switch { "overwrite" => "first=a; second=b; second=a;", "conditional" => "first=a; if (DateTime.UtcNow.Ticks<0) second=b;", "parameter-write" => "b=a; first=a; second=b;", "ref-field" => "first=a; second=b; System.Threading.Interlocked.Exchange(ref second,a);", _ => "first=a; second=b;" };

        string source = R4CarrierSource(constructor, readOnly: shape != "mutable", beforeReturn: shape == "local-write" ? "b=a;" : "", arguments: shape == "named-arguments" ? "b:b,a:a" : "a,b");

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("await first.CompleteAsync(); await second.CompleteAsync();", true)]
    [InlineData("try { await first.CompleteAsync(); } catch { await second.CompleteAsync(); }", false)]
    [InlineData("try { await first.CompleteAsync(); } finally { await second.CompleteAsync(); }", false)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    [InlineData("switch (DateTime.UtcNow.Ticks) { case < 0: await first.CompleteAsync(); await second.CompleteAsync(); break; default: break; }", false)]
    [InlineData("for (var index = 0; index < 1; index++) { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    [InlineData("await first.CompleteAsync(); goto done; await second.CompleteAsync(); done:;", false)]
    [InlineData("lock (this) { first.CompleteAsync().GetAwaiter().GetResult(); second.CompleteAsync().GetAwaiter().GetResult(); }", false)]
    [InlineData("using (var resource = new System.IO.MemoryStream()) { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    [InlineData("checked { await first.CompleteAsync(); await second.CompleteAsync(); }", false)]
    public void R4CarrierTerminalsRequireOneSupportedExecutablePath(string terminals, bool complete)
    {
        foreach (bool disposal in new[] { false, true })
        {
            string cleanup = terminals.Replace("await ", "", StringComparison.Ordinal).Replace(".GetAwaiter().GetResult()", "", StringComparison.Ordinal).Replace("CompleteAsync", "Dispose", StringComparison.Ordinal);

            HostedProducerDiscovery<HostedProducerSite> result = R2Discover(disposal ? R4CarrierSource("first=a; second=b;", disposal: cleanup) : R4CarrierSource("first=a; second=b;", completion: terminals));

            Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
        }
    }

    [Theory]
    [InlineData("stable", true)]
    [InlineData("factory-authored-dispose", false)]
    [InlineData("factory-authored-store", false)]
    [InlineData("factory-opaque", false)]
    [InlineData("constructor-authored-dispose", false)]
    [InlineData("constructor-authored-store", false)]
    [InlineData("constructor-opaque", false)]
    [InlineData("completion-authored-store", false)]
    [InlineData("completion-opaque", false)]
    [InlineData("disposal-authored-store", false)]
    [InlineData("disposal-opaque", false)]
    public void R5CarrierOwnershipAllowsOnlyTheExactDirectTransferAndTerminals(string shape, bool complete)
    {
        const string helper = "static class WriterSink { public static RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter Saved=null!; public static void Dispose(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) => writer.Dispose(); public static void Store(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { Saved=writer; } }";

        string constructor = shape switch { "constructor-authored-dispose" => "WriterSink.Dispose(a); first=a; second=b;", "constructor-authored-store" => "WriterSink.Store(a); first=a; second=b;", "constructor-opaque" => "GC.KeepAlive(a); first=a; second=b;", _ => "first=a; second=b;" };

        string beforeReturn = shape switch { "factory-authored-dispose" => "WriterSink.Dispose(a);", "factory-authored-store" => "WriterSink.Store(a);", "factory-opaque" => "GC.KeepAlive(a);", _ => "" };

        string completion = shape switch { "completion-authored-store" => "await first.CompleteAsync(); WriterSink.Store(first); await second.CompleteAsync();", "completion-opaque" => "await first.CompleteAsync(); GC.KeepAlive(first); await second.CompleteAsync();", _ => "await first.CompleteAsync(); await second.CompleteAsync();" };

        string disposal = shape switch { "disposal-authored-store" => "first.Dispose(); WriterSink.Store(first); second.Dispose();", "disposal-opaque" => "first.Dispose(); GC.KeepAlive(first); second.Dispose();", _ => "first.Dispose(); second.Dispose();" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R4CarrierSource(constructor, completion, disposal, beforeReturn: beforeReturn) + helper);

        Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R5AdmissionCachesSeparateDistinctTreesWithTheSamePathAndSpan(bool firstAdmitted)
    {
        static string Source(string worker, bool admitted)
        {
            string gate = admitted ? "TryBeginExternalEffectGroup" : "TryBeginExternalEffectGrouq";

            string helper = "static class SharedHelper { public static void Run(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) { if (!lease." + gate + "(out var group)) return; using var held=group; System.IO.File.Delete(\"path\"); } } static class SimilarGate { public static bool TryBeginExternalEffectGrouq(this RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, out IDisposable group) { group=null!; return true; } }";

            return R2Source(AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " SharedHelper.Run(lease);", helper).Replace("Worker", worker, StringComparison.Ordinal);
        }

        CSharpCompilation first = Compile(Source("WorkerA", firstAdmitted)).WithAssemblyName("CacheContextA");

        CSharpCompilation second = Compile(Source("WorkerB", !firstAdmitted)).WithAssemblyName("CacheContextB");

        SyntaxTree firstTree = first.SyntaxTrees.Single();

        SyntaxTree secondTree = second.SyntaxTrees.Single();

        Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax firstHelper = firstTree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Run");

        Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax secondHelper = secondTree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Run");

        Assert.NotSame(firstTree, secondTree);

        Assert.Equal(firstTree.FilePath, secondTree.FilePath);

        Assert.Equal(firstHelper.Span, secondHelper.Span);

        HostedProducerOperationEntry FirstRoot(string worker) => new(worker + ".StartAsync", "src/Fixture.cs", worker, "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([first, second], new(["WorkerA", "WorkerB"], []), [new("WorkerA", [FirstRoot("WorkerA")]), new("WorkerB", [FirstRoot("WorkerB")])], []);

        string admitted = firstAdmitted ? "WorkerA.StartAsync" : "WorkerB.StartAsync";

        string unadmitted = firstAdmitted ? "WorkerB.StartAsync" : "WorkerA.StartAsync";

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Identity.StartsWith(admitted, StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Identity.StartsWith(unadmitted, StringComparison.Ordinal));
    }

    [Fact]
    public void R7GraphIdentitySeparatesAliasedSamePathSameSpanHelpers()
    {
        static CSharpCompilation Helper(string assembly, string operation) => Compile("namespace RetroDownfall.Collision { public static class Shared { public static void Run() { System.IO.File." + operation + "(\"path\"); } } }").WithAssemblyName(assembly);

        CSharpCompilation first = Helper("Collision.First", "Exists");

        CSharpCompilation second = Helper("Collision.Second", "Delete");

        MetadataReference firstReference = first.ToMetadataReference(ImmutableArray.Create("first"));

        MetadataReference secondReference = second.ToMetadataReference(ImmutableArray.Create("second"));

        string source = "extern alias first; extern alias second; " + FixtureSource("first::RetroDownfall.Collision.Shared.Run(); second::RetroDownfall.Collision.Shared.Run();");

        CSharpCompilation producer = Compile(source).AddReferences(firstReference, secondReference).WithAssemblyName("Collision.Producer");

        Assert.Empty(first.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        MethodDeclarationSyntax firstRun = first.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        MethodDeclarationSyntax secondRun = second.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        Assert.Equal(firstRun.Span, secondRun.Span);

        Assert.Equal(firstRun.SyntaxTree.FilePath, secondRun.SyntaxTree.FilePath);

        Assert.Equal(first.GetSemanticModel(firstRun.SyntaxTree).GetDeclaredSymbol(firstRun)!.GetDocumentationCommentId(), second.GetSemanticModel(secondRun.SyntaxTree).GetDeclaredSymbol(secondRun)!.GetDocumentationCommentId());

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([first, second, producer], new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void R7LifecycleMembershipCannotHideSameKeyNonLifecycleCaller()
    {
        const string prefix = "using Microsoft.Extensions.Hosting; using System.Threading; using System.Threading.Tasks; namespace RetroDownfall.Collision { public static class Shared { public static void Caller() { ";

        string lifecycleSource = prefix + "Lifecycle.Run(); } } public static class Lifecycle { public static void Run() { System.IO.File.Exists(\"path\"); } } public class Worker : IHostedService { public Task StartAsync(CancellationToken token) { Shared.Caller(); return Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; } }";

        string nonLifecycleSource = prefix + "HostedJob.Run(); } } public static class HostedJob { public static void Run() { System.IO.File.Delete(\"path\"); } } }";

        CSharpCompilation lifecycle = Compile(lifecycleSource).WithAssemblyName("Collision.Lifecycle");

        CSharpCompilation nonLifecycle = Compile(nonLifecycleSource).WithAssemblyName("Collision.NonLifecycle");

        Assert.Empty(lifecycle.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(nonLifecycle.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        MethodDeclarationSyntax lifecycleCaller = lifecycle.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Caller");

        MethodDeclarationSyntax nonLifecycleCaller = nonLifecycle.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Caller");

        Assert.Equal(lifecycleCaller.Span, nonLifecycleCaller.Span);

        Assert.Equal(lifecycle.GetSemanticModel(lifecycleCaller.SyntaxTree).GetDeclaredSymbol(lifecycleCaller)!.GetDocumentationCommentId(), nonLifecycle.GetSemanticModel(nonLifecycleCaller.SyntaxTree).GetDeclaredSymbol(nonLifecycleCaller)!.GetDocumentationCommentId());

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([lifecycle, nonLifecycle], new(["Worker", "HostedJob"], []), [], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith("RetroDownfall.Collision.HostedJob.Run/", StringComparison.Ordinal));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_EXTERNAL_OPERATION_UNCATALOGUED" && diagnostic.Detail == "RetroDownfall.Collision.HostedJob.Run");
    }

    [Fact]
    public void R7AmbiguousFirstPartySameKeyCallEmitsUnresolved()
    {
        const string helper = "namespace RetroDownfall.Collision { public static class Shared { public static void Run() { System.IO.File.Exists(\"path\"); } } }";

        CSharpCompilation first = Compile(helper).WithAssemblyName("Collision.Twin");

        CSharpCompilation second = Compile(helper).WithAssemblyName("Collision.Twin");

        using MemoryStream image = new();

        Assert.True(first.Emit(image).Success);

        MetadataReference reference = MetadataReference.CreateFromImage(image.ToArray(), MetadataReferenceProperties.Assembly.WithAliases(ImmutableArray.Create("twin")));

        CSharpCompilation producer = Compile("extern alias twin; " + FixtureSource("twin::RetroDownfall.Collision.Shared.Run();")).AddReferences(reference).WithAssemblyName("Collision.Producer");

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([first, second, producer], new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "RetroDownfall.Collision.Shared.Run");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R8AliasedInterfaceBindingsFollowTheirExactCompilationInEveryCacheOrder(bool reverse)
    {
        static CSharpCompilation Service(string assembly, string operation) => Compile("using Microsoft.Extensions.DependencyInjection; namespace RetroDownfall.Collision { public interface IWorker { void Run(); } public sealed class Worker : IWorker { public void Run() { System.IO.File." + operation + "(\"path\"); } } public static class Composition { public static void Configure(IServiceCollection services) { services.AddSingleton<IWorker, Worker>(); } } }").WithAssemblyName(assembly);

        CSharpCompilation first = Service("Collision.FirstService", "Exists");

        CSharpCompilation second = Service("Collision.SecondService", "Delete");

        MetadataReference firstReference = first.ToMetadataReference(ImmutableArray.Create("first"));

        MetadataReference secondReference = second.ToMetadataReference(ImmutableArray.Create("second"));

        string body = "first::RetroDownfall.Collision.IWorker firstWorker=null!; second::RetroDownfall.Collision.IWorker secondWorker=null!; firstWorker.Run(); secondWorker.Run(); firstWorker.Run();";

        CSharpCompilation producer = Compile("extern alias first; extern alias second; " + FixtureSource(body)).AddReferences(firstReference, secondReference).WithAssemblyName("Collision.Producer");

        Assert.Empty(first.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation[] compilations = reverse ? [second, first, producer] : [first, second, producer];

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(compilations, new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Equal(2, result.Items.Count(static site => site.Callee == "System.IO.File.Exists"));

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R8UnmappableInterfaceBindingIsUnresolvedAndNeverCached(bool reverse)
    {
        static CSharpCompilation Service(string operation) => Compile("using Microsoft.Extensions.DependencyInjection; namespace RetroDownfall.Collision { public interface IWorker { void Run(); } public sealed class Worker : IWorker { public void Run() { System.IO.File." + operation + "(\"path\"); } } public static class Composition { public static void Configure(IServiceCollection services) { services.AddSingleton<IWorker, Worker>(); } } }").WithAssemblyName("Collision.TwinService");

        CSharpCompilation first = Service("Exists");

        CSharpCompilation second = Service("Delete");

        using MemoryStream image = new();

        Assert.True(first.Emit(image).Success);

        MetadataReference reference = MetadataReference.CreateFromImage(image.ToArray(), MetadataReferenceProperties.Assembly.WithAliases(ImmutableArray.Create("twin")));

        string body = "twin::RetroDownfall.Collision.IWorker worker=null!; worker.Run(); worker.Run();";

        CSharpCompilation producer = Compile("extern alias twin; " + FixtureSource(body)).AddReferences(reference).WithAssemblyName("Collision.Producer");

        Assert.Empty(first.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(second.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Empty(producer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation[] compilations = reverse ? [second, first, producer] : [first, second, producer];

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(compilations, new(["Worker"], []), [new("Worker", [new("Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, null, [])])], []);

        Assert.Equal(2, result.Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "RetroDownfall.Collision.IWorker.Run"));

        Assert.DoesNotContain(result.Items, static site => site.Callee is "System.IO.File.Exists" or "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("IDisposable alias; alias = group; alias.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Use(held);", false)]
    [InlineData("Helper.Release(held); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Forward(held); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Escape(ref group); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Replace(out group); System.IO.File.Delete(\"path\");", false)]
    [InlineData("var alias = Helper.Return(held); alias.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Store(held); Helper.Saved.Dispose(); System.IO.File.Delete(\"path\");", false)]
    [InlineData("Helper.Keep(held);", true)]
    [InlineData("IDisposable alias; alias = group; Helper.Keep(alias);", true)]
    public void R3AdmissionIdentitySurvivesAssignmentsAndFormalParameters(string body, bool retained)
    {
        string helper = "static class Helper { public static IDisposable Saved=null!; public static IDisposable Return(IDisposable handle) => handle; public static void Store(IDisposable handle) { Saved=handle; } public static void Use(IDisposable handle) { handle.Dispose(); System.IO.File.Delete(\"path\"); } public static void Release(IDisposable handle) { handle.Dispose(); } public static void Forward(IDisposable handle) { IDisposable alias; alias=handle; Release(alias); } public static void Escape(ref IDisposable handle) { handle = null!; } public static void Replace(out IDisposable handle) { handle = null!; } public static void Keep(IDisposable handle) { System.IO.File.Delete(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + body, helper));

        Assert.Equal(retained, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("both", false)]
    [InlineData("overload", false)]
    [InlineData("conditional", false)]
    [InlineData("unreachable", false)]
    [InlineData("detached", false)]
    [InlineData("joined", true)]
    public void R3EachWriterRequiresTheActuallyInvokedCarrierTerminals(string shape, bool complete)
    {
        string completion = shape switch { "both" or "detached" => "first.CompleteAsync(); second.CompleteAsync();", "joined" => "await first.CompleteAsync(); await second.CompleteAsync();", "overload" => "first.CompleteAsync();", "conditional" => "first.CompleteAsync(); if (DateTime.UtcNow.Ticks < 0) second.CompleteAsync();", _ => "first.CompleteAsync(); return; second.CompleteAsync();" };

        string disposal = shape switch { "both" or "detached" or "joined" => "first.Dispose(); second.Dispose();", "overload" => "first.Dispose();", "conditional" => "first.Dispose(); if (DateTime.UtcNow.Ticks < 0) second.Dispose();", _ => "first.Dispose(); return; second.Dispose();" };

        string helper = "class Carrier : IDisposable { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first, second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { first=a; second=b; } public static Carrier Create(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store) { var a=store.CreateWriterAsync(); var b=store.CreateWriterAsync(); return new Carrier(a,b); } public void CompleteAsync() { " + completion + " } public void CompleteAsync(bool unused) => second.CompleteAsync(); public void Dispose() { " + disposal + " } public void Dispose(bool unused) => second.Dispose(); }";

        bool asynchronous = shape is "detached" or "joined";

        if (asynchronous)
        {
            helper = helper.Replace("public void CompleteAsync()", "public async Task CompleteAsync()", StringComparison.Ordinal).Replace("public void CompleteAsync(bool unused) => second.CompleteAsync();", "public Task CompleteAsync(bool unused) => second.CompleteAsync();", StringComparison.Ordinal);
        }

        string blobs = R2Blobs;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writers=Carrier.Create(store); " + (asynchronous ? "await " : "") + "writers.CompleteAsync();", blobs + helper));

        Assert.Equal(complete, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Interlocked.Exchange(ref background, Task.CompletedTask);", false)]
    [InlineData("Helper.Replace(ref background);", false)]
    [InlineData("Helper.Set(out background);", false)]
    [InlineData("ref Task? alias = ref background; alias = Task.CompletedTask;", false)]
    [InlineData("int ignored; (background, ignored) = (Task.CompletedTask, 0);", false)]
    public void R3HostHandoffJoinsTheExactDispatchedTask(string mutation, bool owned)
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token)); " + mutation, "static class Helper { public static void Replace(ref Task? task) { task=Task.CompletedTask; } public static void Set(out Task? task) { task=Task.CompletedTask; } }")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { if (background is not null) await background; }", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source, startup, runtime);

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(owned ? 1 : 2, result.Items.Count(static site => site.Callee == "System.IO.File.Delete"));
    }

    [Theory]
    [InlineData("_ = Task.Run(() => System.IO.File.Delete(\"path\"));", false)]
    [InlineData("_ = Task.Factory.StartNew(() => System.IO.File.Delete(\"path\"));", false)]
    [InlineData("await Task.Run(() => System.IO.File.Delete(\"path\"));", true)]
    [InlineData("var pending = Task.Run(() => System.IO.File.Delete(\"path\")); await pending;", true)]
    [InlineData("var pending = Task.Run(() => System.IO.File.Delete(\"path\")); held.Dispose(); await pending;", false)]
    [InlineData("var pending = Task.Run(() => System.IO.File.Delete(\"path\")); Helper.MayThrow(); await pending;", false)]
    [InlineData("Helper.Detach();", false)]
    [InlineData("await Helper.Join();", true)]
    [InlineData("Action callback = () => System.IO.File.Delete(\"path\"); callback();", true)]
    [InlineData("Action callback = () => System.IO.File.Delete(\"path\"); callback = () => {}; callback();", false)]
    [InlineData("Action callback = Helper.Factory(); callback();", false)]
    [InlineData("Action callback = async () => { await Task.Yield(); System.IO.File.Delete(\"path\"); }; callback();", false)]
    [InlineData("Func<int> callback = () => { System.IO.File.Delete(\"path\"); return 1; }; _ = callback();", true)]
    public void R2CallbacksRequireCompletionOwnership(string body, bool owned)
    {
        string helpers = "static class Helper { public static Action Factory() => () => System.IO.File.Delete(\"path\"); public static void MayThrow() {} public static void Detach() { _ = Task.Run(() => System.IO.File.Delete(\"path\")); } public static Task Join() => Task.Run(() => System.IO.File.Delete(\"path\")); }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + body, helpers));

        Assert.Equal(owned, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        if (owned)
        {
            Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
        }
    }

    [Theory]
    [InlineData("new Helper().Value = 1;", false)]
    [InlineData("new Helper().Value = 1;", true)]
    [InlineData("_ = new Helper { Initial = 1 };", true)]
    [InlineData("using (new Helper()) { }", true)]
    [InlineData("using (new Helper()) { held.Dispose(); }", false)]
    [InlineData("await using (new Helper()) { }", true)]
    [InlineData("await using (new Helper()) { held.Dispose(); }", false)]
    public void R2ExecutedAccessorsAndExpressionDisposalAreInventoried(string body, bool protectedSite)
    {
        string helpers = "class Helper : IDisposable, IAsyncDisposable { public int Value { get { System.IO.File.Exists(\"read\"); return 0; } set { System.IO.File.Delete(\"write\"); } } public int Initial { init { System.IO.File.Delete(\"init\"); } } public void Dispose() { System.IO.File.Delete(\"dispose\"); } public ValueTask DisposeAsync() { System.IO.File.Delete(\"async\"); return ValueTask.CompletedTask; } }";

        string admission = protectedSite || body.Contains("held.Dispose", StringComparison.Ordinal) ? R2Admission : "";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(admission + body, helpers));

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Equal(protectedSite, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void R2LogicalNegationReadsButDoesNotSetAProperty()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "_ = !new Helper().Flag;", "class Helper { public bool Flag { get => System.IO.File.Exists(\"read\"); set => System.IO.File.Delete(\"write\"); } }"));

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    private const string R2Blobs = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter : IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} } }";

    private static string R6BlobTypes(string completion, string disposal = "ValueTask")
    {
        string completionBody = completion switch
        {
            "void" => "public void CompleteAsync() {} public void CompleteAsync(bool unused) {}",
            "ValueTask" => "public ValueTask<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => ValueTask.FromResult(new EncryptedBlobDescriptor()); public ValueTask<EncryptedBlobDescriptor> CompleteAsync(bool unused) => ValueTask.FromResult(new EncryptedBlobDescriptor());",
            _ => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public Task<EncryptedBlobDescriptor> CompleteAsync(bool unused) => Task.FromResult(new EncryptedBlobDescriptor());",
        };

        string disposalBody = disposal == "Task"
            ? "public Task WriteAsync() => Task.CompletedTask; public void Dispose() {} public Task DisposeAsync() => Task.CompletedTask;"
            : "public Task WriteAsync() => Task.CompletedTask; public void Dispose() {} public ValueTask DisposeAsync() => ValueTask.CompletedTask;";

        return "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter : IDisposable" + (disposal == "ValueTask" ? ", IAsyncDisposable" : "") + " { " + completionBody + disposalBody + " } }";
    }

    [Theory]
    [InlineData("straight", "void", "ValueTask", false)]
    [InlineData("await-task", "Task", "ValueTask", true)]
    [InlineData("wrong-value-task-completion", "ValueTask", "ValueTask", false)]
    [InlineData("completion-configure-await", "Task", "ValueTask", true)]
    [InlineData("using", "Task", "ValueTask", true)]
    [InlineData("await-using", "Task", "ValueTask", true)]
    [InlineData("try-finally-task", "Task", "ValueTask", true)]
    [InlineData("try-finally-dispose-task", "Task", "Task", false)]
    [InlineData("try-finally-dispose-value-task", "Task", "ValueTask", true)]
    [InlineData("catch-cleanup-rethrow", "Task", "ValueTask", true)]
    [InlineData("catch-cleanup-fallthrough", "Task", "ValueTask", false)]
    [InlineData("conditional-completion", "Task", "ValueTask", false)]
    [InlineData("loop-before-completion", "Task", "ValueTask", false)]
    [InlineData("unreachable-completion", "Task", "ValueTask", false)]
    [InlineData("dispose-before-completion", "Task", "ValueTask", false)]
    [InlineData("discarded-completion-task", "Task", "ValueTask", false)]
    [InlineData("discarded-completion-value-task", "ValueTask", "ValueTask", false)]
    [InlineData("discarded-disposal-task", "Task", "Task", false)]
    [InlineData("discarded-disposal-value-task", "Task", "ValueTask", false)]
    [InlineData("duplicate-completion", "Task", "ValueTask", false)]
    [InlineData("duplicate-disposal", "Task", "ValueTask", false)]
    [InlineData("wrong-completion-overload", "Task", "ValueTask", false)]
    [InlineData("explicit-completion-argument", "Task", "ValueTask", false)]
    [InlineData("wrong-disposal-overload", "Task", "ValueTask", false)]
    [InlineData("completion-after-group-loss", "Task", "ValueTask", false)]
    [InlineData("disposal-after-group-loss", "Task", "ValueTask", false)]
    [InlineData("cross-writer-completion", "Task", "ValueTask", false)]
    [InlineData("cross-writer-disposal", "Task", "ValueTask", false)]
    [InlineData("conditional-create", "Task", "ValueTask", false)]
    [InlineData("unsupported-create-path", "Task", "ValueTask", false)]
    public void R6LocalWriterPublicationRequiresOneExactSupportedLifetime(string shape, string completion, string disposal, bool valid)
    {
        string one = shape switch
        {
            "straight" => "var writer=store.CreateWriterAsync(); writer.CompleteAsync(); writer.Dispose();",
            "await-task" or "wrong-value-task-completion" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "completion-configure-await" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync().ConfigureAwait(false);",
            "using" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "await-using" => "await using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "try-finally-dispose-task" or "try-finally-dispose-value-task" => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } finally { await writer.DisposeAsync(); }",
            "catch-cleanup-rethrow" => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } catch { await writer.DisposeAsync(); throw; } writer.Dispose();",
            "catch-cleanup-fallthrough" => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } catch { await writer.DisposeAsync(); } writer.Dispose();",
            "conditional-completion" => "using var writer=store.CreateWriterAsync(); if (DateTime.UtcNow.Ticks<0) await writer.CompleteAsync();",
            "loop-before-completion" => "using var writer=store.CreateWriterAsync(); while (DateTime.UtcNow.Ticks<0) { } await writer.CompleteAsync();",
            "unreachable-completion" => "using var writer=store.CreateWriterAsync(); return; await writer.CompleteAsync();",
            "dispose-before-completion" => "var writer=store.CreateWriterAsync(); writer.Dispose(); await writer.CompleteAsync();",
            "discarded-completion-task" or "discarded-completion-value-task" => "using var writer=store.CreateWriterAsync(); _=writer.CompleteAsync();",
            "discarded-disposal-task" or "discarded-disposal-value-task" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); _=writer.DisposeAsync();",
            "duplicate-completion" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); await writer.CompleteAsync();",
            "duplicate-disposal" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); writer.Dispose(); writer.Dispose();",
            "wrong-completion-overload" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(true);",
            "explicit-completion-argument" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(CancellationToken.None);",
            "wrong-disposal-overload" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); writer.Dispose(true);",
            "completion-after-group-loss" => "using var writer=store.CreateWriterAsync(); held.Dispose(); await writer.CompleteAsync();",
            "disposal-after-group-loss" => "var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); held.Dispose(); writer.Dispose();",
            "conditional-create" => "using var writer=DateTime.UtcNow.Ticks<0 ? store.CreateWriterAsync() : store.CreateWriterAsync(); await writer.CompleteAsync();",
            "unsupported-create-path" => "if (DateTime.UtcNow.Ticks<0) { using var writer=store.CreateWriterAsync(); await writer.CompleteAsync(); }",
            _ => "",
        };

        if (shape == "try-finally-task")
        {
            one = "var first=store.CreateWriterAsync(); try { var second=store.CreateWriterAsync(); try { await first.CompleteAsync(); await second.CompleteAsync(); } finally { await second.DisposeAsync(); } } finally { await first.DisposeAsync(); }";
        }
        else if (shape == "cross-writer-completion")
        {
            one = "var first=store.CreateWriterAsync(); var second=store.CreateWriterAsync(); await first.CompleteAsync(); await first.CompleteAsync(); first.Dispose(); second.Dispose();";
        }
        else if (shape == "cross-writer-disposal")
        {
            one = "var first=store.CreateWriterAsync(); try { var second=store.CreateWriterAsync(); try { await first.CompleteAsync(); await second.CompleteAsync(); } finally { first.Dispose(); } } finally { first.Dispose(); }";
        }

        string helpers = R6BlobTypes(completion, disposal);

        if (shape == "wrong-disposal-overload")
        {
            helpers = helpers.Replace("public void Dispose() {}", "public void Dispose() {} public void Dispose(bool unused) {}", StringComparison.Ordinal);
        }

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; " + one, helpers));

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("exact", "sync", true)]
    [InlineData("exact", "async", true)]
    [InlineData("optional-overload", "sync", false)]
    [InlineData("wrong-task-result", "sync", false)]
    [InlineData("custom-awaitable", "sync", false)]
    [InlineData("exact", "optional-overload", false)]
    [InlineData("exact", "custom-awaitable", false)]
    public void R7WriterTerminalsUseClosedProductionContracts(string completion, string cleanup, bool valid)
    {
        string completionMember = completion switch
        {
            "optional-overload" => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default, bool unrelated=false) => Task.FromResult(new EncryptedBlobDescriptor());",
            "wrong-task-result" => "public Task<int> CompleteAsync(CancellationToken token=default) => Task.FromResult(0);",
            "custom-awaitable" => "public CustomAwaitable CompleteAsync(CancellationToken token=default) => new();",
            _ => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor());",
        };

        string interfaces = cleanup == "async" ? ", IAsyncDisposable" : "";

        string cleanupMember = cleanup switch
        {
            "async" => "public ValueTask DisposeAsync() => ValueTask.CompletedTask;",
            "optional-overload" => "public ValueTask DisposeAsync(bool unrelated=false) => ValueTask.CompletedTask;",
            "custom-awaitable" => "public CustomAwaitable DisposeAsync() => new();",
            _ => "",
        };

        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public sealed class CustomAwaitable { public Awaiter GetAwaiter() => new(); public sealed class Awaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void OnCompleted(Action continuation) {} public void GetResult() {} } } public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter : IDisposable" + interfaces + " { " + completionMember + " public void Dispose() {} " + cleanupMember + " } }";

        string body = cleanup switch
        {
            "sync" => "using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            "async" => "await using var writer=store.CreateWriterAsync(); await writer.CompleteAsync();",
            _ => "var writer=store.CreateWriterAsync(); try { await writer.CompleteAsync(); } finally { await writer.DisposeAsync(); }",
        };

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; " + body, helpers));

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("production-sync", true)]
    [InlineData("production-async", true)]
    [InlineData("optional-completion-overload", false)]
    [InlineData("wrong-completion-result", false)]
    [InlineData("custom-completion-awaitable", false)]
    [InlineData("wrong-cleanup", false)]
    [InlineData("discarded-completion", false)]
    [InlineData("explicit-none-completion", false)]
    [InlineData("cancelable-completion", false)]
    public void R8CarrierFieldsUseTheSameExactCompletionJoinAndStreamCleanupContracts(string shape, bool valid)
    {
        string completion = shape switch
        {
            "optional-completion-overload" => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default, bool unrelated=false) => Task.FromResult(new EncryptedBlobDescriptor());",
            "wrong-completion-result" => "public Task<int> CompleteAsync(CancellationToken token=default) => Task.FromResult(0);",
            "custom-completion-awaitable" => "public CustomAwaitable CompleteAsync(CancellationToken token=default) => new();",
            _ => "public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor());",
        };

        bool inheritedStreamCleanup = shape is "production-sync" or "production-async" or "discarded-completion" or "explicit-none-completion" or "cancelable-completion";

        string writerBase = inheritedStreamCleanup ? " : System.IO.MemoryStream" : "";

        string writerCleanup = shape == "wrong-cleanup" ? "public Task DisposeAsync() => Task.CompletedTask; public void Dispose() {}" : inheritedStreamCleanup ? "" : "public void Dispose() {}";

        string completionCalls = shape switch
        {
            "discarded-completion" => "_ = first.CompleteAsync(); _ = second.CompleteAsync();",
            "explicit-none-completion" => "await first.CompleteAsync(CancellationToken.None); await second.CompleteAsync(CancellationToken.None);",
            "cancelable-completion" => "var token=new CancellationToken(true); await first.CompleteAsync(token); await second.CompleteAsync(token);",
            _ => "await first.CompleteAsync(); await second.CompleteAsync();",
        };

        bool asynchronousCleanup = shape is "production-async" or "wrong-cleanup";

        string carrierContract = asynchronousCleanup ? "IAsyncDisposable" : "IDisposable";

        string carrierCleanup = asynchronousCleanup
            ? "public async ValueTask DisposeAsync() { await first.DisposeAsync(); await second.DisposeAsync(); }"
            : "public void Dispose() { first.Dispose(); second.Dispose(); }";

        string caller = asynchronousCleanup
            ? "await using var writers=Carrier.Create(store); await writers.CompleteAsync();"
            : "using var writers=Carrier.Create(store); await writers.CompleteAsync();";

        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public sealed class CustomAwaitable { public Awaiter GetAwaiter() => new(); public sealed class Awaiter : System.Runtime.CompilerServices.INotifyCompletion { public bool IsCompleted => true; public void OnCompleted(Action continuation) {} public void GetResult() {} } } public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); } public class EncryptedBlobWriter" + writerBase + " { " + completion + writerCleanup + " } } class Carrier : " + carrierContract + " { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first,second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter second) { this.first=first; this.second=second; } public static Carrier Create(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store) { var first=store.CreateWriterAsync(); var second=store.CreateWriterAsync(); return new Carrier(first,second); } public async Task CompleteAsync() { " + completionCalls + " } " + carrierCleanup + " }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; " + caller, helpers));

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("authored-store")]
    [InlineData("authored-dispose")]
    [InlineData("opaque")]
    [InlineData("alias")]
    [InlineData("field")]
    [InlineData("callback")]
    [InlineData("return")]
    [InlineData("local-function")]
    [InlineData("lambda")]
    [InlineData("detached-write")]
    public void R6LocalWriterPublicationRejectsEveryOwnershipEscape(string shape)
    {
        string use = shape switch
        {
            "authored-store" => "WriterSink.Store(writer);",
            "authored-dispose" => "WriterSink.Dispose(writer);",
            "opaque" => "GC.KeepAlive(writer);",
            "alias" => "var alias=writer; GC.KeepAlive(alias);",
            "field" => "WriterSink.Saved=writer;",
            "callback" => "Action callback=()=>GC.KeepAlive(writer); GC.KeepAlive(callback);",
            "local-function" => "void CompleteLater() => writer.CompleteAsync(); GC.KeepAlive((Action)CompleteLater);",
            "lambda" => "Action completeLater=()=>writer.CompleteAsync(); GC.KeepAlive(completeLater);",
            "detached-write" => "_=writer.WriteAsync();",
            _ => "_ = WriterSink.Return(writer);",
        };

        string helpers = R6BlobTypes("Task") + " static class WriterSink { public static RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter Saved=null!; public static void Store(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { Saved=writer; } public static void Dispose(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) => writer.Dispose(); public static RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter Return(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) => writer; }";

        string body = R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writer=store.CreateWriterAsync(); " + use + " await writer.CompleteAsync();";

        Assert.Contains(R2Discover(R2Source(body, helpers)).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Theory]
    [InlineData("Task", true)]
    [InlineData("ValueTask", true)]
    [InlineData("Task", false)]
    [InlineData("ValueTask", false)]
    public void R6WriterCreationMustCompleteThroughAnExactAwaitBeforeOwnershipStarts(string awaitable, bool joined)
    {
        string completed = awaitable == "Task" ? "Task.FromResult(new EncryptedBlobWriter())" : "ValueTask.FromResult(new EncryptedBlobWriter())";

        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public sealed class EncryptedBlobDescriptor {} public interface IEncryptedBlobStore { " + awaitable + "<EncryptedBlobWriter> CreateWriterAsync(); } public class Store : IEncryptedBlobStore { public " + awaitable + "<EncryptedBlobWriter> CreateWriterAsync() => " + completed + "; } public class EncryptedBlobWriter : IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} } }";

        string creation = joined
            ? "await store.CreateWriterAsync().ConfigureAwait(false)"
            : "store.CreateWriterAsync().GetAwaiter().GetResult()";

        string body = R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=new RetroDownfall.Arcanum.Core.Storage.Store(); using var writer=" + creation + "; await writer.CompleteAsync();";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helpers));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Fact]
    public void R2UnknownExpressionDisposalFailsClosed()
    {
        Assert.Contains(R2Discover(R2Source(R2Admission + "IDisposable unknown = null!; using (unknown) { } ")).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void R2OutDelegateIsNotExecutedByItsProducer()
    {
        Assert.DoesNotContain(R2Discover(R2Source(R2Admission + "var callbacks = new System.Collections.Concurrent.ConcurrentDictionary<string, Action>(); _ = callbacks.TryGetValue(\"key\", out var unused);")).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("await Task.Run(() => { held.Dispose(); System.IO.File.Delete(\"path\"); });")]
    [InlineData("await Task.Run(() => { using (group) { } System.IO.File.Delete(\"path\"); });")]
    public void R2CapturedAdmissionDisposalEndsInheritedAuthority(string body)
    {
        Assert.Contains(R2Discover(R2Source(R2Admission + body)).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void R2DiscardedAsyncHelperCannotInheritCallerLifetime()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "_ = Helper.Async();", "static class Helper { public static async Task Async() { await Task.Yield(); System.IO.File.Delete(\"path\"); } }"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R2CarrierProofNeverCoversAnOrphanWriter(bool orphan)
    {
        string helper = "class Carrier : IDisposable { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first, second; private Carrier(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter a, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter b) { first=a; second=b; } public static Carrier Create(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store) { var a=store.CreateWriterAsync(); var b=store.CreateWriterAsync(); " + (orphan ? "var orphan=store.CreateWriterAsync();" : "") + " return new Carrier(a,b); } public async Task CompleteAsync() { await first.CompleteAsync(); await second.CompleteAsync(); } public void Dispose() { first.Dispose(); second.Dispose(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; using var writers=Carrier.Create(store); await writers.CompleteAsync();", R2Blobs + helper));

        Assert.Equal(orphan ? 1 : 0, result.Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    [Theory]
    [InlineData("using (group) { } using var resurrected = group;", false)]
    [InlineData("{ using var alias = group; } using var resurrected = group;", false)]
    [InlineData("var alias = group; alias.Dispose(); using var resurrected = group;", false)]
    [InlineData("using var retained = group;", true)]
    public void R2DisposedAdmissionAliasesCannotBeResurrected(string lifetime, bool valid)
    {
        string body = AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " if (!lease.TryBeginExternalEffectGroup(out var group)) return; " + lifetime + " System.IO.File.Delete(\"path\");";

        Assert.Equal(valid, !R2Discover(R2Source(body)).Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R2EveryLocalWriterNeedsItsOwnTerminalAndExceptionCleanup(bool completeBoth)
    {
        string body = R2Admission + "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store=null!; var first=store.CreateWriterAsync(); try { var second=store.CreateWriterAsync(); try { await first.CompleteAsync(); " + (completeBoth ? "await second.CompleteAsync();" : "") + " } finally { second.Dispose(); } } finally { first.Dispose(); }";

        Assert.Equal(completeBoth ? 0 : 1, R2Discover(R2Source(body, R2Blobs)).Diagnostics.Count(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));
    }

    private const string AdmissionTypes = """
        namespace RetroDownfall.Arcanum.Infrastructure.Data
        {
            public enum GrimoireWorkKind { WorkspaceIndexing = 4 }
            public interface IGrimoireWorkLease : System.IDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }
            public interface IGrimoireConnectionAdmissionGate { bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease); }
        }
        """;

    private const string AcquireWork = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) return Task.CompletedTask; using var lease = work;";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R2DeclaredAuthorityDoesNotOverrideTheCallingAuthority(bool admitted)
    {
        string source = R2Source((admitted ? R2Admission : "") + "await ContinueAsync(token);").Replace("public Task StopAsync", "private Task ContinueAsync(CancellationToken token) { System.IO.File.Delete(\"path\"); return Task.CompletedTask; } public Task StopAsync", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync", Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.ContinueAsync: readiness" };

        HostedProducerDiscovery<HostedProducerSite> discovery = R2Discover(source, OrdinaryRoot(), startup);

        Assert.Equal(2, discovery.Items.Count(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Equal(admitted, !discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith("Worker.StartAsync/call@", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("if (background is not null) await background.WaitAsync(token);", true)]
    [InlineData("if (DateTime.UtcNow.Ticks < 0) await background!;", false)]
    public void R2HostHandoffRequiresAnExactTaskFieldAndStopJoin(string stopBody, bool joined)
    {
        string source = R2Source("background = Task.Run(() => ContinueAsync(token));")
            .Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "private Task? background; private async Task ContinueAsync(CancellationToken token) { " + R2Admission + " System.IO.File.Delete(\"path\"); } public async Task StopAsync(CancellationToken token) { " + stopBody + " }", StringComparison.Ordinal);

        HostedProducerOperationEntry startup = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: readiness" };

        HostedProducerOperationEntry runtime = OrdinaryRoot("Worker.ContinueAsync") with { Member = "ContinueAsync" };

        HostedProducerDiscovery<HostedProducerSite> discovery = R2Discover(source, startup, runtime);

        Assert.Equal(joined, !discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(joined ? 1 : 2, discovery.Items.Count(static site => site.Callee == "System.IO.File.Delete"));

        if (joined)
        {
            Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
        }
    }

    [Fact]
    public void UncalledLocalFunctionDoesNotInheritLexicalAuthority()
    {
        Assert.DoesNotContain(Discover(FixtureSource("void Cold() { System.IO.File.Delete(\"path\"); }")).Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void GeneratorOutputSuppliesSymbolsWithoutInventingAuthoredProducerSites()
    {
        CSharpCompilation compilation = Compile(FixtureSource("Generated.Read();")).AddSyntaxTrees(CSharpSyntaxTree.ParseText("static class Generated { public static void Read() { System.IO.File.Exists(\"generated\"); } }", path: "ExampleGenerator/Generated.g.cs"));

        Assert.Empty(HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [], []).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyJoinedCallbacksExecuteUnderTheirCallers(bool joined)
    {
        string body = joined ? "_ = Task.Run(() => System.IO.File.Delete(\"path\"));" : "Action cold = () => System.IO.File.Delete(\"path\");";

        Assert.Equal(joined, Discover(FixtureSource(body)).Items.Any(static site => site.Callee == "System.IO.File.Delete"));
    }

    [Fact]
    public void AmbiguousSensitiveInterfaceSlotsFailClosed()
    {
        string helpers = "namespace RetroDownfall.Arcanum.Core.Storage { public interface IEncryptedBlobStore { void OpenReadAsync(); } public interface ISessionAttachmentStore { void OpenReadAsync(); } } class Both : RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore, RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore { public void OpenReadAsync() {} }";

        Assert.Contains(Discover(FixtureSource("new Both().OpenReadAsync();", helpers)).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void NestedTypesAndUnqualifiedGettersKeepTheirExactIdentity()
    {
        string source = FixtureSource("_ = Bytes;")
            .Replace("public Task StartAsync", "private long Bytes => Outer.Helper.Read(); public Task StartAsync", StringComparison.Ordinal)
            + "static class Outer { public static class Helper { public static long Read() => new System.IO.FileInfo(\"path\").Length; } }";

        Assert.Contains(Discover(source).Items, static site => site.Callee == "System.IO.FileInfo.Length" && site.EnclosingType == "Outer.Helper");
    }

    [Fact]
    public void ImplicitStreamDisposalAfterGroupLossFailsTheSharedFrontierCheck()
    {
        string body = AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; using var writer = new System.IO.StreamWriter(System.IO.Stream.Null); held.Dispose();";

        Assert.Contains(DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot()).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.StreamWriter.Dispose");
    }

    [Fact]
    public void InheritedBlobWriterDisposalIsStillAnExplicitPublicationEffect()
    {
        Assert.Contains(Discover(FixtureSource("RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer = null!; _ = writer.DisposeAsync();")).Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync" && site.Kind == HostedProducerSiteKind.FileSystemEffect);
    }

    [Fact]
    public void ValidatorRejectsPublicationEndpointsWithDifferentGroupIdentities()
    {
        HostedProducerSite create = new("Worker", "Worker.StartAsync/effect@one/site@src/Fixture.cs:10", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemEffect, "RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore.CreateWriterAsync");

        HostedProducerSite complete = create with { OperationId = "Worker.StartAsync/effect@two/site@src/Fixture.cs:20", Callee = "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.CompleteAsync" };

        HostedProducerSite dispose = create with { OperationId = "Worker.StartAsync/effect@two/site@src/Fixture.cs:30", Callee = "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose" };

        Assert.Contains(HostedGrimoireProducerInventory.Validate([new("Worker", [OrdinaryRoot() with { Sites = [create, complete, dispose] }])], [], new(["Worker"], []), new([create, complete, dispose], [])).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE");
    }

    [Theory]
    [InlineData("work", "HOSTED_SITE_WORK_FRONTIER_MISSING")]
    [InlineData("effect-free", "HOSTED_SITE_AUTHORITY_INVALID")]
    [InlineData("root", "HOSTED_SITE_ROOT_MISMATCH")]
    public void ValidatorDoesNotTrustUnrelatedFrontiersOrAuthorityLabels(string mutation, string expected)
    {
        HostedProducerSite read = new("Worker", "Worker.StartAsync/work@other/site@src/Fixture.cs:20", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemRead, "System.IO.File.Exists");

        HostedProducerSite frontier = read with { OperationId = "Worker.StartAsync/work@one/workKind=WorkspaceIndexing/site@src/Fixture.cs:10", Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease" };

        HostedProducerOperationEntry operation = OrdinaryRoot();

        if (mutation == "effect-free") operation = operation with { Authority = HostedProducerAuthorityKind.EffectFree, WorkKind = null, Proof = "Worker.StartAsync: claimed no work" };

        if (mutation == "root") read = read with { OperationId = "Other.StartAsync/work@one/site@src/Fixture.cs:20" };

        Assert.Contains(HostedGrimoireProducerInventory.Validate([new("Worker", [operation with { Sites = [read, frontier] }])], [], new(["Worker"], []), new([read, frontier], [])).Diagnostics, diagnostic => diagnostic.Code == expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PositiveAdmissionGuardProtectsOnlyItsSuccessfulBranch(bool success)
    {
        string body = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) { using var lease = work; " + (success ? "System.IO.File.Exists(\"path\");" : "") + " } else { " + (success ? "" : "System.IO.File.Exists(\"path\");") + " }";

        Assert.Equal(success, !DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot()).Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Fact]
    public void ProductionCompilationsResolveGeneratedJsonSymbols()
    {
        foreach (CSharpCompilation compilation in HostedGrimoireProducerInventory.ProductionCompilations)
        {
            INamedTypeSymbol[] contexts = compilation.SyntaxTrees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().Select(declaration => compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration))).OfType<INamedTypeSymbol>().Where(static type => type.BaseType?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext").ToArray();

            Assert.NotEmpty(contexts);

            Assert.All(contexts, static context => Assert.NotEmpty(context.GetMembers("Default")));

            Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Theory]
    [InlineData("sp => new Helper(sp.GetRequiredService<Dependency>())", true)]
    [InlineData("sp => DateTime.Now.Ticks > 0 ? new Helper(null!) : new Helper(null!)", true)]
    [InlineData("sp => DateTime.Now.Ticks > 0 ? new Helper(null!) : new Other()", false)]
    [InlineData("sp => Make(sp)", true)]
    [InlineData("Make", true)]
    [InlineData("factory", true)]
    [InlineData("sp => { IHelper item = new Helper(null!); return item; }", true)]
    [InlineData("sp => Unknown(sp)", false)]
    public void FactoryBindingFollowsReturnedImplementation(string factory, bool resolved)
    {
        string helpers = "interface IHelper { void Run(); } class Dependency {} class Helper(Dependency dependency) : IHelper { public void Run() { System.IO.File.Delete(\"path\"); } } class Other : IHelper { public void Run() { System.IO.File.Exists(\"other\"); } }";

        string source = FixtureSource("IHelper helper = null!; helper.Run();", helpers)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); Func<IServiceProvider,IHelper> factory = Make; services.AddSingleton<IHelper>(" + factory + ");", StringComparison.Ordinal)
            .Replace("public static void Configure", "static IHelper Make(IServiceProvider sp) => new Helper(sp.GetRequiredService<Dependency>()); static IHelper Unknown(IServiceProvider sp) => throw new NotImplementedException(); public static void Configure", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = Discover(source);

        Assert.Equal(resolved, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Equal(!resolved, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IHelper.Run"));
    }

    [Theory]
    [InlineData("bound", true)]
    [InlineData("missing", false)]
    [InlineData("empty", false)]
    [InlineData("drifted", false)]
    public void ExactAggregateBoundaryExecutesItsBoundSourceContract(string shape, bool proven)
    {
        const string boundary = "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService";

        string helpers = "namespace RetroDownfall.Arcanum.Api.Intelligence { public interface IBatchRecoveryService { void ReconcileStrandedAsync(); } public class Recovery : IBatchRecoveryService { public void ReconcileStrandedAsync() { " + (shape == "empty" ? "" : "System.IO.File.Delete(\"stranded\");") + " } } public class Unrelated { public void ReconcileStrandedAsync() { System.IO.File.Delete(\"decoy\"); } } }";

        string binding = shape == "missing" ? "" : "services.AddSingleton<" + boundary + ", RetroDownfall.Arcanum.Api.Intelligence." + (shape == "drifted" ? "Unrelated" : "Recovery") + ">();";

        string source = FixtureSource(boundary + " recovery = null!; recovery.ReconcileStrandedAsync();", helpers).Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>();" + binding, StringComparison.Ordinal);

        HostedProducerOperationEntry root = OrdinaryRoot() with { Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: awaited recovery" };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, root);

        Assert.Equal(proven, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_AGGREGATE_PROOF_MISSING"));

        if (proven)
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "RetroDownfall.Arcanum.Api.Intelligence.Recovery" && site.OperationId.Contains("/call@", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("different", false)]
    [InlineData("implicit-after-loss", false)]
    [InlineData("explicit-after-loss", false)]
    [InlineData("explicit", false)]
    public void BlobPublicationRequiresOneContinuousGroupThroughDisposal(string shape, bool valid)
    {
        string declarations = """
            namespace RetroDownfall.Arcanum.Core.Storage
            {
                public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); }
                public sealed class EncryptedBlobDescriptor {}
                public class EncryptedBlobWriter : System.IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} }
            }
            """;

        string create = shape.StartsWith("explicit", StringComparison.Ordinal) ? "var writer = store.CreateWriterAsync();" : "using var writer = store.CreateWriterAsync();";

        string switchGroup = shape == "different" ? "held.Dispose(); if (!lease.TryBeginExternalEffectGroup(out var second)) return; using var next = second;" : "";

        string loss = shape.EndsWith("after-loss", StringComparison.Ordinal) ? "held.Dispose();" : "";

        string dispose = shape.StartsWith("explicit", StringComparison.Ordinal) ? "writer.Dispose();" : "";

        string body = R2Admission + " RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store = null!; " + create + switchGroup + " await writer.CompleteAsync(); " + loss + dispose;

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(R2Source(body, declarations), OrdinaryRoot());

        Assert.Equal(valid, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));

        if (valid)
        {
            Assert.Contains(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose");
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BatchWriterFieldsRemainInTheCallersContinuousPublicationRegion(bool loseGroup, bool valid)
    {
        string helpers = AdmissionTypes + """
            namespace RetroDownfall.Arcanum.Core.Storage
            {
                public interface IEncryptedBlobStore { EncryptedBlobWriter CreateWriterAsync(); }
                public sealed class EncryptedBlobDescriptor {}
                public class EncryptedBlobWriter : System.IDisposable { public Task<EncryptedBlobDescriptor> CompleteAsync(CancellationToken token=default) => Task.FromResult(new EncryptedBlobDescriptor()); public void Dispose() {} }
            }
            sealed class BatchJsonlWriters : System.IDisposable
            {
                private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter _writer;
                private BatchJsonlWriters(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { _writer = writer; }
                public static BatchJsonlWriters CreateAsync(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store)
                {
                    var writer = store.CreateWriterAsync();
                    return new BatchJsonlWriters(writer);
                }
                public async Task CompleteAsync() { await _writer.CompleteAsync(); }
                public void Dispose() { _writer.Dispose(); }
            }
            """;

        string body = AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store = null!; using var writers = BatchJsonlWriters.CreateAsync(store); await writers.CompleteAsync(); " + (loseGroup ? "held.Dispose();" : "");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, helpers), OrdinaryRoot());

        Assert.Equal(valid, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE"));

        Assert.Contains(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.Dispose");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PropertyReadsAndExpressionGettersRequireRetainedAuthority(bool throughGetter, bool admitted)
    {
        string guard = admitted ? AcquireWork : "";

        string read = throughGetter ? "_ = Helper.Bytes;" : "_ = new System.IO.FileInfo(\"path\").Length;";

        string source = FixtureSource(guard + read, AdmissionTypes + "static class Helper { public static long Bytes => new System.IO.FileInfo(\"path\").Length; }");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, OrdinaryRoot());

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.FileInfo.Length");

        Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && d.Detail == "System.IO.FileInfo.Length");

        Assert.Equal(admitted, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && d.Detail == "System.IO.FileInfo.Length"));
    }

    [Fact]
    public void ValidatorAllowsWorkAdmittedFilesystemReadsWithoutEffectGroup()
    {
        HostedProducerSite read = new("Worker", "Worker.StartAsync", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.FileSystemRead, "System.IO.FileInfo.Length");

        HostedProducerSite lease = read with { Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease", OperationId = "Worker.StartAsync/workKind=WorkspaceIndexing" };

        HostedProducerOperationEntry operation = OrdinaryRoot() with { Sites = [read, lease] };

        Assert.DoesNotContain(HostedGrimoireProducerInventory.Validate([new("Worker", [operation])], [], new(["Worker"], []), new([read, lease], [])).Diagnostics, static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Fact]
    public void DeepSiblingCallAfterGroupDisposalIsDiscoveredAndRejected()
    {
        string helpers = """
            static class Outer
            {
                public static void Run(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease)
                {
                    if (!lease.TryBeginExternalEffectGroup(out var group)) return;
                    using (group) { Middle.Run(); }
                    Middle.Run();
                }
            }
            static class Middle { public static void Run() { Leaf.Run(); } }
            static class Leaf { public static void Run() { System.IO.File.Delete("path"); } }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(AcquireWork + " Outer.Run(lease);", AdmissionTypes + helpers), OrdinaryRoot());

        Assert.Equal(2, result.Items.Count(static site => site.Callee == "System.IO.File.Delete"));

        Assert.Single(result.Diagnostics, static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && d.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void DiamondAndRecursiveHelpersKeepEveryDistinctNoncyclicCallEdge()
    {
        string helpers = "static class Left { public static void Run() { Leaf.Run(); } } static class Right { public static void Run() { Leaf.Run(); } } static class Leaf { public static void Run() { Left.Run(); System.IO.File.Exists(\"path\"); } }";

        HostedProducerSite[] sites = Discover(FixtureSource("Left.Run(); Right.Run();", helpers)).Items.Where(static site => site.Callee == "System.IO.File.Exists").ToArray();

        Assert.Equal(2, sites.Length);

        Assert.Equal(2, sites.Select(static site => site.OperationId).Distinct().Count());
    }

    [Fact]
    public void AggregateBoundaryTableContainsExactlyTheSixApprovedContracts()
    {
        Assert.Equal(new[] {
            "RetroDownfall.Arcanum.Infrastructure.InstallationReset.IInstallationResetStartupRecovery.RecoverBeforeBootstrapAsync",
            "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.IGrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync",
            "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseBootstrapper.EnsureInitializedAsync",
            "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler.ReconcileAsync",
            "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexProcessor.ProcessAsync",
            "RetroDownfall.Arcanum.Api.Intelligence.IBatchRecoveryService.ReconcileStrandedAsync"
        }.Order(StringComparer.Ordinal), HostedGrimoireProducerInventory.AggregateBoundaries.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SuccessfulElseRetainsBothWorkAndEffectOwnership()
    {
        string body = "bool deferred = false; RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate = null!; if (!gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var work)) { deferred = true; } else { using var lease = work; if (!lease.TryBeginExternalEffectGroup(out var group)) { deferred = true; } else { using var held = group; System.IO.File.Delete(\"path\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(FixtureSource(body, AdmissionTypes), OrdinaryRoot());

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static d => d.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    private static HostedProducerOperationEntry OrdinaryRoot(string identity = "Worker.StartAsync") => new(identity, "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

    private static string CallRoot(string source, string callee, int occurrence = 0)
    {
        CSharpCompilation compilation = Compile(source);

        SyntaxTree tree = compilation.SyntaxTrees.Single();

        Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax call = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().Where(call => compilation.GetSemanticModel(tree).GetSymbolInfo(call).Symbol is IMethodSymbol symbol && symbol.ContainingType.ToDisplayString() + "." + symbol.Name == callee).ElementAt(occurrence);

        return "Worker.StartAsync::call:" + callee + "#" + occurrence + "@" + call.Span.Start + ":" + call.Span.Length;
    }

    [Fact]
    public void ExactSourceAnchorRejectsInsertedSiblingRatherThanRetargeting()
    {
        string source = FixtureSource("System.IO.File.Exists(\"one\"); System.IO.File.Exists(\"two\");");

        HostedProducerOperationEntry root = OrdinaryRoot(CallRoot(source, "System.IO.File.Exists", 1));

        Assert.DoesNotContain(DiscoverWithRoots(source, root).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");

        Assert.Contains(DiscoverWithRoots(source.Replace("System.IO.File.Exists(\"one\");", "System.IO.File.Exists(\"inserted\"); System.IO.File.Exists(\"one\");", StringComparison.Ordinal), root).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_UNRESOLVED");
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverWithRoots(string source, params HostedProducerOperationEntry[] roots)
    {
        CSharpCompilation compilation = Compile(source);

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", roots)], []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactCallSelectorsDoNotCrossScanMixedAuthorityBranches(bool protectedRuntime)
    {
        string admission = protectedRuntime ? AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group;" : "";

        string source = FixtureSource("if (DateTime.Now.Ticks > 0) { System.IO.File.Exists(\"startup\"); } else { " + admission + " System.IO.File.Delete(\"runtime\"); }", AdmissionTypes);

        HostedProducerOperationEntry startup = new(CallRoot(source, "System.IO.File.Exists"), "src/Fixture.cs", "Worker", "StartAsync", HostedProducerAuthorityKind.PreReadinessStartup, null, "Worker.StartAsync: awaited startup branch", []);

        HostedProducerOperationEntry runtime = OrdinaryRoot(CallRoot(source, "System.IO.File.Delete"));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverWithRoots(source, startup, runtime);

        Assert.Single(result.Items, site => site.Callee == "System.IO.File.Exists" && site.OperationId.StartsWith(startup.OperationId, StringComparison.Ordinal));

        Assert.Single(result.Items, site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith(runtime.OperationId, StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, site => site.Callee == "System.IO.File.Delete" && site.OperationId.StartsWith(startup.OperationId, StringComparison.Ordinal));

        Assert.Equal(protectedRuntime, !result.Diagnostics.Any(static d => d.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"));
    }

    [Fact]
    public void SelectedOperationRoundTripsItsExactRetainedFrontiers()
    {
        string source = FixtureSource(AcquireWork + " if (!lease.TryBeginExternalEffectGroup(out var group)) return Task.CompletedTask; using var held = group; System.IO.File.Delete(\"path\");", AdmissionTypes);

        HostedProducerOperationEntry root = OrdinaryRoot(CallRoot(source, "System.IO.File.Delete"));

        HostedProducerDiscovery<HostedProducerSite> discovery = DiscoverWithRoots(source, root);

        HostedProducerSite[] selected = discovery.Items.Where(site => site.OperationId.StartsWith(root.OperationId, StringComparison.Ordinal)).ToArray();

        Assert.Contains(selected, static site => site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal));

        Assert.Contains(selected, static site => site.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal));

        Assert.Empty(HostedGrimoireProducerInventory.Validate([new("Worker", [root with { Sites = selected }])], [], new(["Worker"], []), new(selected, discovery.Diagnostics)).Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingAuthorityClaimsAreRejected(bool wholeMember)
    {
        string source = FixtureSource("System.IO.File.Delete(\"path\");");

        HostedProducerOperationEntry ordinary = OrdinaryRoot(CallRoot(source, "System.IO.File.Delete"));

        HostedProducerOperationEntry startup = ordinary with { OperationId = wholeMember ? "Worker.StartAsync" : ordinary.OperationId, Authority = HostedProducerAuthorityKind.PreReadinessStartup, WorkKind = null, Proof = "Worker.StartAsync: startup" };

        Assert.Contains(DiscoverWithRoots(source, ordinary, startup).Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ROOT_OVERLAP");
    }

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
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        HostedProducerInventoryValidation validation =
            HostedGrimoireProducerInventory.ValidateProductionTree();

        output.WriteLine($"Cold/in-process-first inventory: {stopwatch.Elapsed.TotalSeconds:F3}s");

        stopwatch.Restart();

        Assert.Same(validation, HostedGrimoireProducerInventory.ValidateProductionTree());

        output.WriteLine($"Warm inventory: {stopwatch.Elapsed.TotalMilliseconds:F3}ms");

        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code.StartsWith("HOSTED_SERVICE_", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDiscoveredProducerSiteIsCataloguedExactlyOnce()
    {
        HostedProducerInventoryValidation validation =
            HostedGrimoireProducerInventory.ValidateProductionTree();

        string[][] contextualSites = validation.Diagnostics.Where(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCATALOGUED").Select(static diagnostic => diagnostic.Identity.Split('|')).ToArray();

        string PhysicalSite(string[] fields) => fields[1][fields[1].LastIndexOf("/site@", StringComparison.Ordinal)..] + "|" + string.Join("|", fields.Skip(2));

        output.WriteLine($"Uncatalogued contextual sites: {contextualSites.Length}; unique physical sites: {contextualSites.Select(PhysicalSite).Distinct(StringComparer.Ordinal).Count()}; physical sites per exact operation root: {contextualSites.Select(fields => fields[0] + "|" + fields[1].Split('/')[0] + "|" + PhysicalSite(fields)).Distinct(StringComparer.Ordinal).Count()}");

        foreach (IGrouping<string, HostedProducerInventoryDiagnostic> group in validation.Diagnostics.GroupBy(static diagnostic => diagnostic.Code))
        {
            output.WriteLine($"{group.Key}: {group.Count()}");

            if (group.Key is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED")
            {
                IGrouping<string, HostedProducerInventoryDiagnostic>[] physical = group.GroupBy(static diagnostic => diagnostic.Identity[(diagnostic.Identity.LastIndexOf('@') + 1)..] + " | " + diagnostic.Detail.Split(';')[0], StringComparer.Ordinal).ToArray();

                output.WriteLine($"{group.Key} unique physical anchors: {physical.Length}");

                foreach (IGrouping<string, HostedProducerInventoryDiagnostic> anchor in physical)
                {
                    output.WriteLine($"PHYSICAL {group.Key} | {anchor.Count()} contexts | {anchor.Key}");
                }
            }

            foreach (HostedProducerInventoryDiagnostic diagnostic in group.Take(30))
            {
                output.WriteLine($"{diagnostic.Identity}: {diagnostic.Detail}");
            }
        }

        Assert.True(validation.IsValid, "Every discovery, ownership, root, and site diagnostic must be closed by executable evidence before the production inventory is GREEN.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryScopeRequiresItsDeclaredWorkFrontier(bool frontier)
    {
        HostedProducerSite scope = new("Worker", "Worker.StartAsync/work@lease/site@src/Fixture.cs:20", "src/Fixture.cs", "Worker", "StartAsync", HostedProducerSiteKind.ScopeCreation, "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope");

        HostedProducerSite lease = scope with { Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease", OperationId = "Worker.StartAsync/work@lease/workKind=WorkspaceIndexing/site@src/Fixture.cs:10" };

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
