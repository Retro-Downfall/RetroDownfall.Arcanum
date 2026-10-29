using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void SchemaReadiness_ExactConnectionStringGetterIsClosed()
    {
        var discovery = TimeoutDiscovery(R2Admission + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); _ = connection.ConnectionString; await Task.CompletedTask;");

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.ConnectionString");
    }

    [Fact]
    public void SchemaReadiness_ConnectionStringSetterRemainsUnclassified()
    {
        var discovery = TimeoutDiscovery(R2Admission + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); connection.ConnectionString = \"Data Source=private\"; await Task.CompletedTask;");

        Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.ConnectionString setter");
    }

    [Fact]
    public void SchemaReadiness_OverriddenConnectionStringGetterRefusesTheClosedProviderPremise()
    {
        var discovery = TimeoutDiscovery(R2Admission + "Microsoft.Data.Sqlite.SqliteConnection connection = new CustomConnection(); _ = connection.ConnectionString; await Task.CompletedTask;", "sealed class CustomConnection : Microsoft.Data.Sqlite.SqliteConnection { public override string ConnectionString { get { System.IO.File.Delete(\"override-effect\"); return \"\"; } set { } } }");

        Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaReadiness_Int32MaxKeepsIteratorAndPredicateEffects(bool admitted)
    {
        var discovery = TimeoutDiscovery((admitted ? R2Admission : "") + "_ = System.Linq.Enumerable.Max(System.Linq.Enumerable.DefaultIfEmpty(System.Linq.Enumerable.Where(Values.Read(), value => System.IO.File.Exists(\"predicate\")), 0)); await Task.CompletedTask;", "static class Values { public static System.Collections.Generic.IEnumerable<int> Read() { _ = System.IO.File.ReadAllText(\"iterator\"); yield return 1; } }");

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Linq.Enumerable.Max");

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.ReadAllText");

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.All(discovery.Items.Where(static site => site.Callee is "System.IO.File.ReadAllText" or "System.IO.File.Exists"),
            site => Assert.Equal(admitted, site.WorkFrontierCapsuleId is not null));
    }

    [Theory]
    [InlineData("System.Linq.Enumerable.Max(new int?[] { 1 })")]
    [InlineData("System.Linq.Enumerable.Max(new long[] { 1 })")]
    [InlineData("System.Linq.Enumerable.Max(new int[] { 1 }, System.Collections.Generic.Comparer<int>.Default)")]
    public void SchemaReadiness_OtherMaxOverloadsStayConservative(string call)
    {
        var discovery = TimeoutDiscovery("_ = " + call + "; await Task.CompletedTask;");

        Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Linq.Enumerable.Max");
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void SchemaReadiness_ActualProductionSitesUseOnlyTheReviewedSignatures()
    {
        CSharpCompilation compilation = HostedGrimoireProducerInventory.ProductionCompilations.Single(static candidate => candidate.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        SyntaxTree connection = compilation.SyntaxTrees.Single(static tree => tree.FilePath.EndsWith("/GrimoireSchemaQuiescentExecution.cs", StringComparison.Ordinal));

        MemberAccessExpressionSyntax[] reads = [.. connection.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(static node => node.Name.Identifier.ValueText == "ConnectionString")];

        Assert.Equal(2, reads.Length);

        Assert.All(reads, node => Assert.True(HostedGrimoireProducerInventory.ProbeSchemaReadinessSupport(compilation, node, false)));

        Assert.All(reads, node => Assert.False(HostedGrimoireProducerInventory.ProbeSchemaReadinessSupport(compilation, node, true)));

        SyntaxTree drain = compilation.SyntaxTrees.Single(static tree => tree.FilePath.EndsWith("/GrimoireSchemaReadinessDrain.cs", StringComparison.Ordinal));

        InvocationExpressionSyntax maximum = drain.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single(static node => node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Max" });

        Assert.True(HostedGrimoireProducerInventory.ProbeSchemaReadinessSupport(compilation, maximum, false));
    }
}
