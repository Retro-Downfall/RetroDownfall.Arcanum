using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("j-exact", true)]
    [InlineData("j-nested-sqlite-action", false)]
    [InlineData("j-iface-interceptor", false)]
    [InlineData("j-explicit-interceptor", false)]
    [InlineData("j-params-escape", false)]
    [InlineData("j-array-escape", false)]
    [InlineData("j-lambda-escape", false)]
    [InlineData("j-tuple-escape", false)]
    [InlineData("j-list-cast", false)]
    [InlineData("j-list-escape", false)]
    [InlineData("j-indexer-set", false)]
    [InlineData("j-custom-provider", false)]
    [InlineData("j-usesqlite-connection", false)]
    [InlineData("j-usesqlite-derived", false)]
    [InlineData("j-setdbconnection", false)]
    [InlineData("j-factory-activation", false)]
    [InlineData("j-activator", false)]
    [InlineData("j-onconfiguring-early-use", false)]
    [InlineData("j-onconfiguring-database-early", false)]
    [InlineData("j-helper-connection-custom", false)]
    [InlineData("j-helper-reader-custom", false)]
    [InlineData("j-helper-connection-exact", true)]
    [InlineData("j-scope-exact", true)]
    [InlineData("j-factory-sp-field", true)]
    [InlineData("j-factory-sp-reassigned", false)]
    [InlineData("j-scope-fake-factory", false)]
    [InlineData("j-scope-factory-sp", false)] // conservative: provider stored in an authored holder property
    [InlineData("j-scope-custom-scopefactory-descriptor", false)]
    public void WidenedSqliteBlobIndexerRefusesEveryRouteToAnUnprovenFinalProvider(string shape, bool database)
    {
        string useSqlite = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, \"Data Source=:memory:\")";

        string options = shape switch
        {
            "j-nested-sqlite-action" => "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, \"Data Source=:memory:\", sqlite => ((Microsoft.EntityFrameworkCore.Infrastructure.IRelationalDbContextOptionsBuilderInfrastructure)sqlite).OptionsBuilder.UseInternalServiceProvider(BlobInput.Provider))",
            "j-iface-interceptor" => useSqlite + ".AddInterceptors(new SwapInterceptor())",
            "j-explicit-interceptor" => useSqlite + ".AddInterceptors(new ExplicitSwapInterceptor())",
            "j-usesqlite-connection" => "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, BlobInput.Connection)",
            "j-usesqlite-derived" => "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, new BlobDerivedConnection())",
            _ => useSqlite,
        };

        string callbackTail = shape switch
        {
            "j-params-escape" => "BlobInput.MutateMany(options);",
            "j-array-escape" => "BlobInput.MutateArray(new object[] { options });",
            "j-lambda-escape" => "BlobInput.Defer(() => options);",
            "j-tuple-escape" => "BlobInput.MutateTuple((options, 0));",
            _ => "",
        };

        string registration = "services.AddDbContext<BlobContext>((sp, options) => { " + options + "; " + callbackTail + " }); ";

        registration += shape switch
        {
            "j-list-cast" => "((System.Collections.Generic.IList<ServiceDescriptor>)services).Add(ServiceDescriptor.Scoped<BlobContext>(sp => new BlobContext(BlobInput.Options))); ",
            "j-list-escape" => "BlobInput.MutateList(services); ",
            "j-indexer-set" => "services[services.Count - 1] = ServiceDescriptor.Scoped<BlobContext>(sp => new BlobContext(BlobInput.Options)); ",
            _ => "",
        };

        registration += shape switch
        {
            "j-factory-sp-field" => "services.AddScoped<BlobHolder>(sp => new BlobHolder(sp.GetRequiredService<BlobContext>())); ",
            "j-factory-sp-reassigned" => "services.AddScoped<BlobHolder>(sp => { sp = BlobInput.Provider; return new BlobHolder(sp.GetRequiredService<BlobContext>()); }); ",
            _ => "",
        };

        registration += shape == "j-scope-custom-scopefactory-descriptor"
            ? "services.AddSingleton<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory, FakeScopes>(); "
            : shape == "j-scope-factory-sp"
                ? "services.AddSingleton<ProviderHolder>(sp => new ProviderHolder(sp)); "
                : "";

        registration += shape == "j-factory-activation"
            ? "services.AddHostedService<Worker>(sp => new Worker(new BlobContext(BlobInput.Options)));"
            : shape == "j-activator"
                ? "services.AddHostedService<Worker>(sp => Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Worker>(sp, new BlobContext(BlobInput.Options)));"
                : "services.AddHostedService<Worker>();";

        string prefix = shape == "j-setdbconnection"
            ? "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SetDbConnection(db.Database, BlobInput.Connection); "
            : "";

        string connectionSource = shape is "j-helper-connection-custom" or "j-helper-connection-exact"
            ? "BlobConnections.Open(db)"
            : "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database)";

        string readerSource = shape == "j-helper-reader-custom"
            ? "await BlobConnections.ReadAsync(command, token)"
            : "await command.ExecuteReaderAsync(token).ConfigureAwait(false)";

        string body = prefix
            + "System.Data.Common.DbConnection connection = " + connectionSource + "; "
            + "await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(token); "
            + "await using System.Data.Common.DbCommand command = connection.CreateCommand(); command.Transaction = transaction; "
            + "System.Data.Common.DbDataReader reader = " + readerSource + "; "
            + "_ = reader[0]; await reader.DisposeAsync();";

        string blobContext = shape.StartsWith("j-onconfiguring-", StringComparison.Ordinal)
            ? "public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(options) { protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { System.IO.File.Delete(\"on-configuring\"); } }"
            : "public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(options) { }";

        string helpers = $$"""
            {{blobContext}}
            public sealed class BlobRow { public int Id { get; set; } }
            internal sealed class BlobDerivedConnection : Microsoft.Data.Sqlite.SqliteConnection
            {
                protected override System.Data.Common.DbCommand CreateDbCommand() { System.IO.File.Delete("command-override"); return base.CreateDbCommand(); }
            }

            public sealed class SwapInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor
            {
                public System.Data.Common.DbConnection ConnectionCreated(Microsoft.EntityFrameworkCore.Diagnostics.ConnectionCreatedEventData eventData, System.Data.Common.DbConnection result) => BlobInput.Connection;
            }

            public sealed class ExplicitSwapInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor
            {
                System.Data.Common.DbConnection Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor.ConnectionCreated(Microsoft.EntityFrameworkCore.Diagnostics.ConnectionCreatedEventData eventData, System.Data.Common.DbConnection result) => BlobInput.Connection;
            }

            public sealed class BlobHolder(BlobContext db) { private readonly BlobContext _db = db; public Task RunAsync(CancellationToken token) => BlobPipeline.RunAsync(_db, token); }
            public sealed class ProviderHolder(IServiceProvider provider) { public IServiceProvider Provider { get; } = provider; }
            public sealed class FakeScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
            {
                public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => BlobInput.Scope;
            }

            public sealed class FakeProvider : IServiceProvider
            {
                public object? GetService(Type serviceType) => new BlobContext(BlobInput.Options);
            }

            internal static class BlobInput
            {
                internal static Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> Options { get; set; } = null!;
                internal static IServiceProvider Provider { get; set; } = null!;
                internal static Microsoft.Extensions.DependencyInjection.IServiceScope Scope { get; set; } = null!;
                internal static System.Data.Common.DbConnection Connection { get; set; } = null!;
                internal static object? Sink { get; set; }
                internal static void MutateMany(params object[] items) { Sink = items; }
                internal static void MutateArray(object[] items) { Sink = items; }
                internal static void Defer(Func<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder> builder) { Sink = builder; }
                internal static void MutateTuple((Microsoft.EntityFrameworkCore.DbContextOptionsBuilder, int) pair) { Sink = pair; }
                internal static void MutateList(System.Collections.Generic.IList<ServiceDescriptor> list) { Sink = list; }
            }

            internal static class BlobConnections
            {
                internal static System.Data.Common.DbConnection Open(BlobContext db) => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database);
                internal static Task<System.Data.Common.DbDataReader> ReadAsync(System.Data.Common.DbCommand command, CancellationToken token) => command.ExecuteReaderAsync(token);
            }

            internal static class BlobPipeline
            {
                internal static async Task RunAsync(BlobContext db, CancellationToken token) { {{body}} }
            }
            """;

        string context = shape == "j-custom-provider"
            ? "new FakeProvider().GetRequiredService<BlobContext>()"
            : shape is "j-helper-connection-custom" or "j-helper-reader-custom"
                ? "new BlobContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>().Options)"
                : shape.StartsWith("j-scope-", StringComparison.Ordinal)
                    ? "scope.ServiceProvider.GetRequiredService<BlobContext>()"
                    : shape.StartsWith("j-factory-sp-", StringComparison.Ordinal)
                        ? "holder.Db"
                        : "db";

        string scopeSetup = shape switch
        {
            "j-scope-exact" or "j-scope-custom-scopefactory-descriptor" => "await using AsyncServiceScope scope = scopes.CreateAsyncScope(); ",
            "j-scope-fake-factory" => "await using AsyncServiceScope scope = new FakeScopes().CreateAsyncScope(); ",
            "j-scope-factory-sp" => "await using AsyncServiceScope scope = holder.Provider.CreateAsyncScope(); ",
            _ => "",
        };

        // First use of the context's services (EF runs OnConfiguring here) happens before
        // the work/effect admission is held.
        string early = shape switch
        {
            "j-onconfiguring-early-use" => "_ = db.Set<BlobRow>(); ",
            "j-onconfiguring-database-early" => "_ = db.Database; ",
            _ => "",
        };

        string source = RegistrationSource(registration)
            .Replace("public class Worker : IHostedService", shape.StartsWith("j-factory-sp-", StringComparison.Ordinal)
                ? "public class Worker(BlobContext db, BlobHolder holder) : IHostedService"
                : shape.StartsWith("j-scope-", StringComparison.Ordinal)
                ? "public class Worker(BlobContext db, Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes" + (shape == "j-scope-factory-sp" ? ", ProviderHolder holder" : "") + ") : IHostedService"
                : "public class Worker(BlobContext db) : IHostedService", StringComparison.Ordinal)
            .Replace("static Worker Factory(IServiceProvider provider) => new Worker();", "", StringComparison.Ordinal)
            .Replace("public Task StartAsync(CancellationToken token) => Task.CompletedTask;", "public async Task StartAsync(CancellationToken token) { _ = db; " + early + R2Admission + scopeSetup + (shape.StartsWith("j-factory-sp-", StringComparison.Ordinal) ? "await holder.RunAsync(token); }" : "await BlobPipeline.RunAsync(" + context + ", token); }"), StringComparison.Ordinal)
            + AdmissionTypes + helpers;

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        const string callee = "System.Data.Common.DbDataReader.this[]";

        bool reached = result.Items.Any(site => site.Callee == callee)
            || result.Diagnostics.Any(diagnostic => diagnostic.Detail == callee);

        bool classified = result.Items.Any(site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == callee);

        output.WriteLine("SQLITE-INDEXER shape=" + shape + " reached=" + reached + " database=" + classified);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine("SQLITE-INDEXER diag " + diagnostic.Code + " " + diagnostic.Detail);
        }

        Assert.True(reached);

        Assert.Equal(database, classified);

        // A refused certificate is the indexer's own refusal, not an unrelated one.
        Assert.Equal(!database, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee));

        if (shape.StartsWith("j-onconfiguring-", StringComparison.Ordinal))
        {
            HostedProducerSite[] deletes = result.Items.Where(static site => site.Callee == "System.IO.File.Delete").ToArray();

            foreach (HostedProducerSite delete in deletes)
            {
                output.WriteLine("SQLITE-INDEXER delete work=" + (delete.WorkFrontierCapsuleId is not null) + " effect=" + (delete.EffectFrontierCapsuleId is not null) + " op=" + delete.OperationId);
            }

            // The OnConfiguring effect really runs at the early, unadmitted first use. A sound
            // inventory must not present it only as an admitted site.
            Assert.True(deletes.Any(static site => site.WorkFrontierCapsuleId is null)
                || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"),
                "OnConfiguring effect certified as admitted although its first use is unadmitted");
        }
    }
}
