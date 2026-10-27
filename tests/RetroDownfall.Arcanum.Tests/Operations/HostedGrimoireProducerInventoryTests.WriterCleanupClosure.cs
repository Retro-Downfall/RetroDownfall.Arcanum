using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    [InlineData("mixed")]
    [InlineData("expired")]
    [InlineData("unknown-cleanup-0")]
    [InlineData("unknown-cleanup-1")]
    [InlineData("unknown-cleanup-2")]
    [InlineData("unknown-cleanup-3")]
    public void HostedContinuationActualBatchWriterSourceRetainsExactFactoryAndAllFourCleanupEffects(string shape)
    {
        string relative = "src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs";

        CompilationUnitSyntax original = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), relative))).GetCompilationUnitRoot();

        ClassDeclarationSyntax owner = original.DescendantNodes().OfType<ClassDeclarationSyntax>().First(static type => type.Identifier.ValueText == "BatchProcessingService");

        ClassDeclarationSyntax nested = owner.Members.OfType<ClassDeclarationSyntax>().Single(static type => type.Identifier.ValueText == "BatchJsonlWriters");

        MemberDeclarationSyntax exercise = SyntaxFactory.ParseMemberDeclaration("internal static async Task ExerciseWriterAsync(RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore store, CancellationToken token) { await using BatchJsonlWriters writers = await BatchJsonlWriters.CreateAsync(store, \"output.stage\", \"error.stage\", Guid.Empty, token); await writers.WriteOutputLineAsync(\"output\", token); await writers.WriteErrorLineAsync(\"error\", token); await writers.CompleteAsync(CancellationToken.None); }")!;

        ClassDeclarationSyntax selected = owner.WithParameterList(null).WithBaseList(null).WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>([nested, exercise]));

        int unknownCleanup = shape.StartsWith("unknown-cleanup-", StringComparison.Ordinal)
            ? int.Parse(shape["unknown-cleanup-".Length..], global::System.Globalization.CultureInfo.InvariantCulture)
            : -1;

        if (unknownCleanup >= 0)
        {
            InvocationExpressionSyntax[] originalCleanups = [.. selected.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static call =>
                call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DisposeAsync" } access
                    && access.Expression.ToString() is "encryptedError" or "encryptedOutput" or "_encryptedOutput" or "_encryptedError")];

            ExpressionSyntax receiver = ((MemberAccessExpressionSyntax)originalCleanups[unknownCleanup].Expression).Expression;

            selected = selected.ReplaceNode(receiver, SyntaxFactory.ParseExpression("global::WriterInput.UnknownWriter()").WithTriviaFrom(receiver));

            Assert.Single(selected.DescendantNodes().OfType<InvocationExpressionSyntax>(), static call =>
                call.Expression.ToString() == "global::WriterInput.UnknownWriter");
        }

        CompilationUnitSyntax isolated = original.ReplaceNode(owner, selected).WithUsings(SyntaxFactory.List(original.Usings
            .Where(static directive => directive.Name?.ToString() is "RetroDownfall.Arcanum.Core.Storage" or "System.Diagnostics.CodeAnalysis")));

        SyntaxTree actual = CSharpSyntaxTree.Create(isolated, path: relative);

        SyntaxTree globals = CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", path: "src/Fixture.GlobalUsings.g.cs");

        string store = shape switch
        {
            "unknown" => "WriterInput.Take()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new KnownWriterStore() : WriterInput.Take()",
            _ => "new KnownWriterStore()",
        };

        string body = R2Admission + (shape == "expired" ? "held.Dispose(); lease.Dispose(); " : "")
            + "await RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExerciseWriterAsync(" + store + ", token); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, HostedContinuationWriterOrigins), "RetroDownfall.Arcanum.Api").AddSyntaxTrees(actual, globals);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        SemanticModel actualModel = compilation.GetSemanticModel(actual);

        InvocationExpressionSyntax[] cleanups = [.. actual.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DisposeAsync" } access
                && actualModel.GetTypeInfo(access.Expression).Type?.ToDisplayString() == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter")];

        Assert.Equal(4, cleanups.Length);

        if (shape is "unknown" or "mixed")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.IO.Stream.DisposeAsync", StringComparison.Ordinal));

            return;
        }

        if (unknownCleanup >= 0)
        {
            string coordinate = "/dispose@" + relative + ":" + cleanups[unknownCleanup].SpanStart;

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));

            return;
        }

        foreach (InvocationExpressionSyntax cleanup in cleanups)
        {
            string coordinate = "/dispose@" + relative + ":" + cleanup.SpanStart;

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
        }

        // Sites intentionally deduplicate repeated callers under the same Work/effect
        // pair. The four exact refusal controls above/below pin each cleanup position;
        // the complete actual source must retain the physical effect under that pair.
        HostedProducerSite[] effects = [.. result.Items.Where(static site => site.Callee == "System.IO.File.Delete")];

        Assert.NotEmpty(effects);

        Assert.All(effects, site =>
        {
            Assert.Equal(shape == "known", site.WorkFrontierCapsuleId is not null);

            Assert.Equal(shape == "known", site.EffectFrontierCapsuleId is not null);
        });

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.IO.Stream.DisposeAsync", StringComparison.Ordinal));
    }

    private const string HostedContinuationWriterOrigins = """
        internal static class WriterInput
        {
            internal static extern RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore Take();
            internal static extern RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter UnknownWriter();
        }
        internal sealed class KnownWriterStore : RetroDownfall.Arcanum.Core.Storage.IEncryptedBlobStore
        {
            public Task<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter> CreateWriterAsync(string path, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobPurpose purpose, ReadOnlyMemory<byte> metadata = default, CancellationToken ct = default)
                => Task.FromResult<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter>(new KnownWriter());
            public Task<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobDescriptor> WriteAsync(string path, System.IO.Stream plaintext, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobPurpose purpose, ReadOnlyMemory<byte> metadata = default, long? length = null, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<System.IO.Stream> OpenReadAsync(string path, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobPurpose purpose, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobPlaintextInspection> InspectAuthenticatedAsync(string path, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobPurpose purpose, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobDescriptor> InspectAsync(string path, RetroDownfall.Arcanum.Core.Storage.EncryptedBlobPurpose purpose, bool verifyAllChunks, CancellationToken ct = default) => throw new NotSupportedException();
            public bool HasEnvelope(string path) => throw new NotSupportedException();
        }
        internal sealed class KnownWriter : RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) { }
            public override Task<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobDescriptor> CompleteAsync(CancellationToken ct = default)
                => Task.FromResult<RetroDownfall.Arcanum.Core.Storage.EncryptedBlobDescriptor>(null!);
            public override ValueTask DisposeAsync()
            {
                System.IO.File.Delete("actual-writer-cleanup");
                return ValueTask.CompletedTask;
            }
        }
        """;

    [Theory]
    [InlineData("explicit", true)]
    [InlineData("using", true)]
    [InlineData("unknown", false)]
    [InlineData("mixed", false)]
    [InlineData("expired", false)]
    public void HostedContinuationReferenceCleanupFactoryKeepsItsActualReceiverSource(string shape, bool owned)
    {
        string factory = shape switch
        {
            "unknown" => "StreamFactoryInput.Take()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new KnownStreamFactory() : StreamFactoryInput.Take()",
            _ => "new KnownStreamFactory()",
        };

        string cleanup = shape == "using"
            ? "await using System.IO.Stream stream = await factory.Create();"
            : "System.IO.Stream? stream = null; try { stream = await factory.Create(); } finally { if (stream is not null) await stream.DisposeAsync(); }";

        string helper = "interface IStreamFactory { Task<System.IO.Stream> Create(); } "
            + "internal static class StreamFactoryInput { internal static extern IStreamFactory Take(); } "
            + "sealed class KnownStreamFactory : IStreamFactory { public Task<System.IO.Stream> Create() => Task.FromResult<System.IO.Stream>(new CleanupStream()); } "
            + "sealed class CleanupStream : System.IO.MemoryStream { public override ValueTask DisposeAsync() { System.IO.File.Delete(\"cleanup-factory\"); return ValueTask.CompletedTask; } } "
            + "static class StreamFactoryConsumer { internal static async Task Use(IStreamFactory factory) { " + cleanup + " } }";

        string body = R2Admission + (shape == "expired" ? "held.Dispose(); lease.Dispose(); " : "")
            + "await StreamFactoryConsumer.Use(" + factory + "); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (shape is "unknown" or "mixed")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

            return;
        }

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(owned, effect.WorkFrontierCapsuleId is not null);

        Assert.Equal(owned, effect.EffectFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("inherited", true)]
    [InlineData("interface", true)]
    [InlineData("hidden", false)]
    [InlineData("unknown", false)]
    [InlineData("mixed", false)]
    [InlineData("expired", false)]
    public void HostedContinuationReferenceExplicitStreamCleanupUsesTheCompilerBoundSlot(string shape, bool owned)
    {
        string value = shape switch
        {
            "unknown" => "AbstractWriterInput.Take()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new ConcreteWriter() : AbstractWriterInput.Take()",
            _ => "new ConcreteWriter()",
        };

        string hidden = shape == "hidden" ? "public new virtual ValueTask DisposeAsync() { System.IO.File.Delete(\"hidden-base-slot\"); return ValueTask.CompletedTask; }" : "";

        string parameter = shape == "interface" ? "IAsyncDisposable" : "AbstractWriter";

        string helper = "abstract class AbstractWriter : System.IO.MemoryStream { " + hidden + " } "
            + "sealed class ConcreteWriter : AbstractWriter { public override ValueTask DisposeAsync() { System.IO.File.Delete(\"derived-cleanup\"); return ValueTask.CompletedTask; } } "
            + "static class AbstractWriterInput { internal static extern AbstractWriter Take(); } "
            + "static class ExplicitWriterConsumer { internal static async Task Use(" + parameter + " writer) { await writer.DisposeAsync(); } }";

        string body = R2Admission + (shape == "expired" ? "held.Dispose(); lease.Dispose(); " : "")
            + "await ExplicitWriterConsumer.Use(" + value + "); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (shape == "hidden")
        {
            // An authored hidden slot can already be traversed normally. The guard
            // requires its real effect or an explicit refusal, never silent cleanup.
            Assert.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code is "HOSTED_DISPOSAL_TARGET_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED")
                || result.Items.Any(static site => site.Callee == "System.IO.File.Delete"
                    && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null));

            return;
        }

        if (shape is "unknown" or "mixed")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_DISPOSAL_TARGET_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED");

            return;
        }

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(owned, effect.WorkFrontierCapsuleId is not null);

        Assert.Equal(owned, effect.EffectFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualBatchProductionSubsetRetainsPhysicalWriterCleanup()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, ["RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExecuteAsync"]);

        Assert.NotEmpty(result.Items);

        Assert.All(result.Items, static site => Assert.Equal("BatchProcessingService", site.RootType));

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && HostedContinuationTerminalAnchor(diagnostic).Contains("/BatchProcessingService.cs:", StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.SourcePath.EndsWith("/Storage/EncryptedBlobStore.cs", StringComparison.Ordinal)
            && site.EnclosingType.Contains("StreamingEncryptedBlobWriter", StringComparison.Ordinal)
            && site.Callee == "System.IO.File.Delete" && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);
    }
}
