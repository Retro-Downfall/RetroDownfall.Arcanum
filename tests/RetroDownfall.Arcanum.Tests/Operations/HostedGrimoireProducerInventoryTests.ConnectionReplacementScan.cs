using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The whole-tree connection-replacement scan (H3) reads the bound symbol of every simple name of every tree, so it
// is the most expensive single fact the closed-provider certificate depends on. It is a property of the analysed
// compilations alone, so it is computed once per discovery, by a scan that does not depend on which root worker
// asked first, and every isolated root worker shares that one verdict.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string ConnectionReplacementReviewedSettings = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, \"Data Source=:memory:\").EnableDetailedErrors()";

    // Three hosted services, each of which reads the widened DbDataReader indexer through the same closed
    // container-built context, so every isolated root worker demands the closed-provider certificate.
    private static string ThreeWorkerIndexerSource(string options)
    {
        string source = SqliteIndexerSource(
            options,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: string.Empty,
            onConfiguring: null,
            early: string.Empty,
            pipelinePrefix: string.Empty,
            contextExpression: "db",
            helpers: string.Empty);

        const string header = "public class Worker(BlobContext db) : IHostedService";

        int start = source.IndexOf(header, StringComparison.Ordinal);

        Assert.True(start >= 0);

        int end = source.IndexOf("\n}\n", start, StringComparison.Ordinal) + 3;

        string worker = source[start..end];

        string copies = worker.Replace("class Worker(", "class Worker2(", StringComparison.Ordinal)
            + worker.Replace("class Worker(", "class Worker3(", StringComparison.Ordinal);

        return source.Insert(end, copies)
            .Replace("services.AddHostedService<Worker>();", "services.AddHostedService<Worker>(); services.AddHostedService<Worker2>(); services.AddHostedService<Worker3>();", StringComparison.Ordinal);
    }

    private HostedProducerDiscovery<HostedProducerSite> DiscoverThreeWorkers(string source, int rootWorkers)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerOperationEntry Root(string worker) => OrdinaryRoot() with { OperationId = worker + ".StartAsync", EnclosingType = worker };

        return HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker", "Worker2", "Worker3"], []),
            [new("Worker", [Root("Worker")]), new("Worker2", [Root("Worker2")]), new("Worker3", [Root("Worker3")])],
            [],
            rootWorkers: rootWorkers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConnectionReplacementIsScannedOncePerDiscoveryWhateverTheWorkerCount(int rootWorkers)
    {
        int before = HostedGrimoireProducerInventory.ConnectionReplacementScans;

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverThreeWorkers(ThreeWorkerIndexerSource(ConnectionReplacementReviewedSettings), rootWorkers);

        // The certificate really was demanded by every root: each hosted service's reader indexer is a
        // classified database access, which only the closed-provider certificate grants.
        Assert.Equal(3, result.Items.Count(static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == SqliteIndexerCallee));

        Assert.Equal(before + 1, HostedGrimoireProducerInventory.ConnectionReplacementScans);
    }

    [Fact]
    public void ConnectionReplacementIsScannedAgainByTheNextDiscovery()
    {
        int before = HostedGrimoireProducerInventory.ConnectionReplacementScans;

        string source = ThreeWorkerIndexerSource(ConnectionReplacementReviewedSettings);

        _ = DiscoverThreeWorkers(source, 3);

        _ = DiscoverThreeWorkers(source, 2);

        // The verdict belongs to one discovery's compilations; nothing leaks between discoveries.
        Assert.Equal(before + 2, HostedGrimoireProducerInventory.ConnectionReplacementScans);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, \"Data Source=:memory:\")", true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, \"Data Source=:memory:\")", false)]
    public void ConnectionReplacementVerdictIsTheSameForEveryWorkerCount(string options, bool replacement)
    {
        string source = ThreeWorkerIndexerSource(options);

        if (replacement)
        {
            // A reference to a member that can replace the context's connection, in code no root reaches, refuses
            // the certificate for every worker alike.
            source += " internal static class ConnectionReplacer { internal static void Replace(BlobContext db, System.Data.Common.DbConnection connection) { Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SetDbConnection(db.Database, connection); } }";
        }

        int[] classified = [.. new[] { 1, 2, 3 }.Select(workers =>
            DiscoverThreeWorkers(source, workers).Items.Count(static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == SqliteIndexerCallee))];

        Assert.Equal(replacement ? [0, 0, 0] : [3, 3, 3], classified);
    }
}
