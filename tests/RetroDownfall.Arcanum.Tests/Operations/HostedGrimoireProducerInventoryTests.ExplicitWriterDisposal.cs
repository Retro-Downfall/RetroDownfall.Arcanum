using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitWriterDisposal_preserves_logical_calls_and_concrete_field_cleanup(bool twoFields)
    {
        string fields = twoFields
            ? "private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter second;"
            : "";

        string assign = twoFields ? "second = new KnownWriter();" : "";

        string dispose = twoFields ? "await second.DisposeAsync();" : "";

        string helper = "sealed class ExactWriterCarrier : IAsyncDisposable { private readonly RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter first; "
            + fields + " internal ExactWriterCarrier() { first = new KnownWriter(); " + assign
            + " } public async ValueTask DisposeAsync() { await first.DisposeAsync(); " + dispose + " } }";

        HostedProducerDiscovery<HostedProducerSite> result = ExplicitWriterDisposalDiscover(
            R2Admission + "await using var carrier = new ExactWriterCarrier(); await Task.CompletedTask;", helper);

        HostedProducerSite[] logical = [.. result.Items.Where(static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync")];

        Assert.Equal(twoFields ? 2 : 1, logical.Length);

        Assert.All(logical, static site =>
        {
            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.NotNull(site.EffectFrontierCapsuleId);
        });

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mixed")]
    [InlineData("detached")]
    [InlineData("expired")]
    [InlineData("without-effect")]
    public void ExplicitWriterDisposal_does_not_turn_incomplete_cleanup_into_authority(string shape)
    {
        string value = shape switch
        {
            "unknown" => "WriterInput.UnknownWriter()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new KnownWriter() : WriterInput.UnknownWriter()",
            _ => "new KnownWriter()",
        };

        string cleanup = shape == "detached" ? "_ = writer.DisposeAsync();" : "await writer.DisposeAsync();";

        string body = R2Admission + (shape == "expired" ? "held.Dispose(); lease.Dispose(); " : shape == "without-effect" ? "held.Dispose(); " : "")
            + "await ExactWriterConsumer.Use(" + value + ");";

        string helper = "static class ExactWriterConsumer { internal static async Task Use(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer) { " + cleanup + " } }";

        HostedProducerDiscovery<HostedProducerSite> result = ExplicitWriterDisposalDiscover(body, helper);

        if (shape is "unknown" or "mixed")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

            return;
        }

        HostedProducerSite[] physical = [.. result.Items.Where(static site => site.Callee == "System.IO.File.Delete")];

        Assert.NotEmpty(physical);

        Assert.All(physical, site =>
        {
            Assert.Equal(shape == "without-effect", site.WorkFrontierCapsuleId is not null);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        if (shape == "detached") Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitWriterDisposal_does_not_label_a_foreign_source_lookalike_as_the_first_party_writer(bool forgedAssemblyName)
    {
        string source = R2Source(R2Admission + "await ForeignWriterConsumer.Use(new ForeignWriter());", """
            namespace RetroDownfall.Arcanum.Core.Storage
            {
                public class EncryptedBlobWriter : System.IO.MemoryStream {}
            }
            sealed class ForeignWriter : RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter
            {
                public override ValueTask DisposeAsync()
                {
                    System.IO.File.Delete("foreign-writer-cleanup");
                    return ValueTask.CompletedTask;
                }
            }
            static class ForeignWriterConsumer
            {
                internal static async Task Use(RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter writer)
                {
                    await writer.DisposeAsync();
                }
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        if (forgedAssemblyName) compilation = compilation.WithAssemblyName("RetroDownfall.Arcanum.Core");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation, HostedGrimoireProducerInventory.ProductionCompilations.Single(static value => value.AssemblyName == "RetroDownfall.Arcanum.Core")],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.DoesNotContain(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void ExplicitWriterDisposal_actual_batch_preserves_physical_rows_and_records_each_logical_call()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary,
            ["RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExecuteAsync"]);

        Assert.True(result.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));

        HostedProducerSite[] logical = [.. result.Items.Where(static site =>
            site.Callee == "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter.DisposeAsync")];

        Assert.Equal(4, logical.Length);

        Assert.All(logical, static site =>
        {
            Assert.EndsWith("/Intelligence/BatchProcessingService.cs", site.SourcePath, StringComparison.Ordinal);

            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.NotNull(site.EffectFrontierCapsuleId);
        });

        string[] reviewed = File.ReadAllLines(HostedGrimoireProducerInventory.CapsuleManifestPath);

        string physical = HostedGrimoireProducerInventory.RenderCapsuleManifest(result.Items.Except(logical));

        foreach (string row in physical.Split('\n').Skip(2).Where(static row => row.Length != 0)) Assert.Contains(row, reviewed);

        output.WriteLine("EXPLICIT_WRITER_LOGICAL_BEGIN");

        output.WriteLine(HostedGrimoireProducerInventory.RenderCapsuleManifest(logical));

        output.WriteLine("EXPLICIT_WRITER_LOGICAL_END");

        output.WriteLine("EXPLICIT_WRITER_PHYSICAL_COUNT=" + result.Items.Except(logical).Count());
    }

    private static HostedProducerDiscovery<HostedProducerSite> ExplicitWriterDisposalDiscover(string body, string helper)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source(body, HostedContinuationWriterOrigins + helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation, HostedGrimoireProducerInventory.ProductionCompilations.Single(static value => value.AssemblyName == "RetroDownfall.Arcanum.Core")],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }
}
