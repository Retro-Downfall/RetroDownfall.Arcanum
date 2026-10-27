using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualAsyncDigestRetainsItsIteratorAndTransactionWork()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Identity.Contains("/CampaignRollupStore.Read.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && (diagnostic.Detail.StartsWith("System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1.", StringComparison.Ordinal)
                    || diagnostic.Detail == "System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation")
            || diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Identity.Contains("/CampaignRollupStore.Read.cs:", StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.MemberIdentity.Contains(".ReadEntryChunksAsync(", StringComparison.Ordinal)
            && site.Callee == "System.Data.Common.DbCommand.ExecuteScalarAsync" && site.WorkFrontierCapsuleId is not null);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualCommandCleanupHasExactProviderAndConnectionOrigins()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && HostedContinuationTerminalAnchor(diagnostic).Contains("/CampaignRollupSourceStorage.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Data.Common.DbCommand.Connection")];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.SourcePath.EndsWith("/CampaignRollupSourceStorage.cs", StringComparison.Ordinal)
            && site.Kind == HostedProducerSiteKind.DatabaseAccess);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualWriterCleanupRetainsConstructionAndOwnedFieldEffects()
    {
        HostedProducerInventoryDiagnostic[] failures = [.. HostedGrimoireProducerInventory.ProductionSiteDiscovery.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && HostedContinuationTerminalAnchor(diagnostic).Contains("/BatchProcessingService.cs:", StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualChatLeaseCleanupHasExactClientOwnership()
    {
        HostedProducerInventoryDiagnostic[] failures = [.. HostedGrimoireProducerInventory.ProductionSiteDiscovery.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && HostedContinuationTerminalAnchor(diagnostic).Contains("/ChatClientFactory.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Net.Http.HttpMessageInvoker.Dispose")];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualSdkPolicySetupRetainsIndependentSendObligations()
    {
        string[] setup = ["System.ClientModel.Primitives.ClientPipelineOptions.AddPolicy", "System.ClientModel.Primitives.PipelinePolicy..ctor", "OpenAI.Chat.ChatClient..ctor"];

        HostedProducerInventoryDiagnostic[] failures = [.. HostedGrimoireProducerInventory.ProductionSiteDiscovery.Diagnostics.Where(diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && setup.Contains(diagnostic.Detail, StringComparer.Ordinal))];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualProductionSemanticProofHasNoUnresolvedFamily()
    {
        HostedProducerInventoryDiagnostic[] diagnostics = [.. HostedGrimoireProducerInventory.ProductionSiteDiscovery.Diagnostics];

        Assert.True(diagnostics.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(diagnostics));
    }

    [Theory]
    [InlineData("joined", true)]
    [InlineData("configured", true)]
    [InlineData("unconsumed", false)]
    [InlineData("returned", false)]
    [InlineData("stored", false)]
    [InlineData("fire-and-forget", false)]
    [InlineData("unknown-consumer", false)]
    [InlineData("expired", false)]
    [InlineData("work-only-expired", false)]
    [InlineData("captures-admission", false)]
    public void HostedContinuationReferenceAsyncConsumerMustJoinWithinTheOriginalAdmission(string shape, bool owned)
    {
        string call = shape switch
        {
            "unconsumed" => "_ = LazyDigestFixture.Values();",
            "returned" => "_ = LazyDigestFixture.Forward(LazyDigestFixture.Values());",
            "stored" => "LazyDigestFixture.Store(LazyDigestFixture.Values());",
            "fire-and-forget" => "_ = LazyDigestFixture.Consume(LazyDigestFixture.Values(), token);",
            "unknown-consumer" => "await LazyDigestFixture.UnknownConsume(LazyDigestFixture.Values(), token);",
            "expired" => "held.Dispose(); lease.Dispose(); await LazyDigestFixture.Consume(LazyDigestFixture.Values(), token);",
            "work-only-expired" => "lease.Dispose(); await LazyDigestFixture.Consume(LazyDigestFixture.Values(), token);",
            "captures-admission" => "await LazyDigestFixture.Consume(LazyDigestFixture.Values(), token, lease);",
            _ => "await LazyDigestFixture.Consume(LazyDigestFixture.Values(), token);",
        };

        string operand = shape == "configured" ? "source.WithCancellation(ct).ConfigureAwait(false)" : "source";

        string helper = "internal static class LazyDigestFixture { "
            + "internal static System.Collections.Generic.IAsyncEnumerable<int>? Saved; "
            + "internal static async System.Collections.Generic.IAsyncEnumerable<int> Values() { await Task.CompletedTask; System.IO.File.Delete(\"lazy-consumer-effect\"); yield return 1; } "
            + "internal static System.Collections.Generic.IAsyncEnumerable<int> Forward(System.Collections.Generic.IAsyncEnumerable<int> source) => source; "
            + "internal static void Store(System.Collections.Generic.IAsyncEnumerable<int> source) { Saved = source; } "
            + "internal static extern Task UnknownConsume(System.Collections.Generic.IAsyncEnumerable<int> source, CancellationToken ct); "
            + "internal static async Task Consume(System.Collections.Generic.IAsyncEnumerable<int> source, CancellationToken ct, IDisposable? held = null) { "
            + "held?.Dispose(); await foreach (int value in " + operand + ") { _ = value; } } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + call + " await Task.CompletedTask;", helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(owned, effect.WorkFrontierCapsuleId is not null);

        if (shape != "work-only-expired") Assert.Equal(owned, effect.EffectFrontierCapsuleId is not null);

        if (shape == "work-only-expired") Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");

        if (!owned) Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        if (owned) Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.Contains("ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    [InlineData("authored")]
    [InlineData("stored-unknown")]
    public void HostedContinuationReferenceAsyncWrappersMustKeepTheirActualEnumeratorOrigin(string shape)
    {
        string operand = shape switch
        {
            "unknown" => "AsyncWrapperFixture.Unknown()",
            "authored" => "new DangerousAsyncSequence()",
            "stored-unknown" => "UnknownConfigured.Value",
            _ => "AsyncWrapperFixture.Values()",
        };

        string helper = "internal static class AsyncWrapperFixture { internal static extern System.Collections.Generic.IAsyncEnumerable<int> Unknown(); "
            + "internal static async System.Collections.Generic.IAsyncEnumerable<int> Values() { await Task.CompletedTask; System.IO.File.Delete(\"wrapper-iterator-effect\"); yield return 1; } } "
            + "internal sealed class DangerousAsyncSequence : System.Collections.Generic.IAsyncEnumerable<int>, System.Collections.Generic.IAsyncEnumerator<int> { "
            + "public int Current => 0; public System.Collections.Generic.IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken token = default) { System.IO.File.Delete(\"wrapper-get-enumerator\"); return this; } "
            + "public ValueTask<bool> MoveNextAsync() { System.IO.File.Delete(\"wrapper-move-next\"); return new(false); } public ValueTask DisposeAsync() { System.IO.File.Delete(\"wrapper-dispose\"); return default; } } "
            + "internal static class UnknownConfigured { internal static System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<int> Value; internal static void Set(System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<int> value) { Value = value; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission
            + "await foreach (int value in " + operand + ".WithCancellation(token).ConfigureAwait(false)) { _ = value; }", helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (shape == "known")
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && (diagnostic.Detail.Contains("ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal) || diagnostic.Detail.Contains("TaskAsyncEnumerableExtensions.WithCancellation", StringComparison.Ordinal)));
        }
        else
        {
            Assert.True(result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
                || result.Diagnostics.Any(static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                    && (diagnostic.Detail.Contains("ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal)
                        || diagnostic.Detail.Contains("TaskAsyncEnumerableExtensions.WithCancellation", StringComparison.Ordinal))));
        }
    }

    [Fact]
    public void HostedContinuationReferenceForeignAsyncWrapperCannotAcquireFrameworkSemantics()
    {
        const string foreignSource = "namespace System.Runtime.CompilerServices { public readonly struct ConfiguredCancelableAsyncEnumerable<T> { public System.Collections.Generic.IAsyncEnumerator<T> GetAsyncEnumerator() => throw new System.NotSupportedException(); } } "
            + "namespace System.Threading.Tasks { public static class TaskAsyncEnumerableExtensions { public static System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<T> WithCancellation<T>(System.Collections.Generic.IAsyncEnumerable<T> source, System.Threading.CancellationToken token) => default; } }";

        CSharpCompilation foreign = CompileWithProductionReferencePack(foreignSource, "RetroDownfall.Arcanum.Api").WithAssemblyName("Unreviewed.AsyncWrapper");

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        MetadataReference reference = MetadataReference.CreateFromImage(bytes.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"]));

        string source = "extern alias counterfeit; " + R2Source(R2Admission
            + "await foreach (int value in counterfeit::System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation((System.Collections.Generic.IAsyncEnumerable<int>)null!, token)) { _ = value; }");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api").AddReferences(reference);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.Contains("ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal));
    }

    [Fact]
    public void HostedContinuationActualDigestSourceRetainsNativeSqlIteratorWork()
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src/RetroDownfall.Arcanum.Core/Storage/DerivedArtifactContentDigest.cs");

        SyntaxTree actual = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: "src/RetroDownfall.Arcanum.Core/Storage/DerivedArtifactContentDigest.cs");

        const string helper = "internal static class ActualDigestSqlSource { internal static async System.Collections.Generic.IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks() { "
            + "await using Microsoft.Data.Sqlite.SqliteCommand command = new(); _ = await command.ExecuteScalarAsync(); yield return Array.Empty<byte>(); } }";

        string source = R2Source(R2Admission + "_ = await RetroDownfall.Arcanum.Core.Storage.DerivedArtifactContentDigest.ForChunksAsync(ActualDigestSqlSource.Chunks(), token); await Task.CompletedTask;", helper);

        SyntaxTree globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", path: "src/Fixture.GlobalUsings.g.cs");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api").AddSyntaxTrees(actual, globals);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite sql = Assert.Single(result.Items, static site => site.Callee == "System.Data.Common.DbCommand.ExecuteScalarAsync");

        Assert.NotNull(sql.WorkFrontierCapsuleId);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && (diagnostic.Detail.Contains("ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal) || diagnostic.Detail.Contains("TaskAsyncEnumerableExtensions.WithCancellation", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualDigestProductionSubsetRetainsRealSqlWork()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, ["Loremaster"]);

        Assert.NotEmpty(result.Items);

        Assert.All(result.Items, static site => Assert.Equal("Loremaster", site.RootType));

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" && diagnostic.Identity.Contains("/CampaignRollupStore.Read.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && (diagnostic.Detail.Contains("ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal)
                || diagnostic.Detail == "System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation")
            || diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Identity.Contains("/CampaignRollupStore.Read.cs:", StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.MemberIdentity.Contains(".ReadEntryChunksAsync(", StringComparison.Ordinal)
            && site.Callee == "System.Data.Common.DbCommand.ExecuteScalarAsync" && site.WorkFrontierCapsuleId is not null);
    }

    private static string HostedContinuationTerminalAnchor(HostedProducerInventoryDiagnostic diagnostic) =>
        diagnostic.Identity[(diagnostic.Identity.LastIndexOf('@') + 1)..];
}
