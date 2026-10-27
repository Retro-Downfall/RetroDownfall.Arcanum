using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HostedContinuationActualCore28StrategyBodyRetainsNativeAndInjectedEffects(bool closedDispatch, bool injectedEffect)
    {
        string relative = "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/MemorySearchProjectionBackfill.cs";

        string actual = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), relative));

        if (injectedEffect)
        {
            actual = actual.ReplaceSdkFixtureText("int phase = 0;", "System.IO.File.Delete(\"backfill-body-effect\"); int phase = 0;", StringComparison.Ordinal);
        }

        string contract = "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/IGrimoireSchemaBackfill.cs";

        string others = string.Join(" ", HostedGrimoireProducerInventory.SchemaBackfillStrategies
            .Where(static type => !type.EndsWith(".MemorySearchProjectionBackfill", StringComparison.Ordinal))
            .Select(static type => "internal sealed class " + type.Split('.').Last()
                + " : IGrimoireSchemaBackfill { public string Name => \"fixture\"; public int MaxRowsPerBatch => 1; public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string? cursor, CancellationToken token) => Task.FromResult(new GrimoireSchemaBackfillBatch(null, 0, true)); }"));

        string receiver = closedDispatch ? "RetroDownfall.Arcanum.Infrastructure.Data.Schema.IGrimoireSchemaBackfill" : "RetroDownfall.Arcanum.Infrastructure.Data.Schema.MemorySearchProjectionBackfill";

        string body = R2Admission + receiver + " strategy = new RetroDownfall.Arcanum.Infrastructure.Data.Schema.MemorySearchProjectionBackfill(); await strategy.AdvanceBatchAsync(new Microsoft.Data.Sqlite.SqliteConnection(), null!, null, token); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body), "RetroDownfall.Arcanum.Api").AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(actual, path: relative),
            CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), contract)), path: contract),
            CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", path: "src/Fixture.GlobalUsings.g.cs"),
            CSharpSyntaxTree.ParseText("namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema { " + others
                + " internal sealed class UnconfiguredBackfill : IGrimoireSchemaBackfill { public string Name => \"unconfigured\"; public int MaxRowsPerBatch => 1; public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string? cursor, CancellationToken token) { System.IO.File.Delete(\"unconfigured-body\"); return Task.FromResult(new GrimoireSchemaBackfillBatch(null, 0, true)); } } }", path: "src/Fixture.OtherStrategies.cs"));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = PremiseRequested(compilation);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_STRATEGY_BINDING_UNRESOLVED");

        Assert.Contains(result.Items, static site => site.EnclosingType.EndsWith(".MemorySearchProjectionBackfill", StringComparison.Ordinal)
            && site.Callee.EndsWith(".ExecuteReaderAsync", StringComparison.Ordinal));

        Assert.Contains(result.Items, static site => site.EnclosingType.EndsWith(".MemorySearchProjectionBackfill", StringComparison.Ordinal)
            && site.Callee.EndsWith(".ExecuteNonQueryAsync", StringComparison.Ordinal));

        Assert.Equal(injectedEffect, result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));

        Assert.DoesNotContain(result.Items, static site => site.EnclosingType.EndsWith(".UnconfiguredBackfill", StringComparison.Ordinal));
    }
}
