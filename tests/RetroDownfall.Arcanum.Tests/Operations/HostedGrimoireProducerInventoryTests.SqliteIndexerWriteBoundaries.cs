using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The widened SQLite indexer certificate (DbDataReader.this[]) rests on three facts about the
// container-built context: the value the pipeline reads is the instance the container built, that
// instance received exactly the options its registration configured, and nothing replaces its
// connection afterwards. These theories pin the routes by which a fixture can break each fact
// without tripping the checks that already exist: writes through a tuple, ref, out, local function
// or lambda; a base constructor that ignores its options; and references to a connection
// replacement that are not invocations. Every refusal has an exact positive twin so a negative
// cannot pass because the plumbing around it refuses on its own. Source analysis only; nothing
// here executes EF, SQL or a provider.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string SqliteIndexerContextHeader = "public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(options)";

    private const string SqliteIndexerWriteHelpers = """
        internal static class BlobSwap { internal static void Swap(ref BlobContext context) { context = new BlobContext(BlobInput.Options); } }
        internal static class BlobOut { internal static void Out(out BlobContext context) { context = new BlobContext(BlobInput.Options); } }
        internal static class ProviderOut { internal static void Out(out IServiceProvider provider) { provider = new FakeProvider(); } }
        internal static class ScopeInput { internal static Microsoft.Extensions.DependencyInjection.IServiceScope Value { get; set; } = null!; }
        public sealed class BlobHolder(BlobContext db) { private readonly BlobContext _db = db; public Task RunAsync(CancellationToken token) => BlobPipeline.RunAsync(_db, token); }
        public sealed class FakeScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory { public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => ScopeInput.Value; }
        public sealed class FakeProvider : IServiceProvider { public object? GetService(Type serviceType) => new BlobContext(BlobInput.Options); }
        """;

    [Theory]
    [InlineData("control-exact", true)]
    [InlineData("field-readonly", true)]
    [InlineData("nameof-parameter", true)]
    [InlineData("ctor-param-exact", true)]
    [InlineData("pc-sp-exact", true)]
    [InlineData("pc-scopes-exact", true)]
    [InlineData("factory-sp-exact", true)]
    [InlineData("pc-reassigned", false)]
    [InlineData("pc-local-function", false)]
    [InlineData("pc-lambda", false)]
    [InlineData("pc-ref-swap", false)]
    [InlineData("pc-out-swap", false)]
    [InlineData("pc-deconstruct-swap", false)]
    [InlineData("pc-nested-deconstruct-swap", false)]
    [InlineData("pc-coalesce-assign", false)]
    [InlineData("pc-ref-local-alias", false)]
    [InlineData("pc-written-elsewhere", false)]
    [InlineData("ctor-param-reassigned", false)]
    [InlineData("pc-sp-reassigned", false)]
    [InlineData("pc-sp-deconstruct", false)]
    [InlineData("pc-sp-out", false)]
    [InlineData("pc-scopes-reassigned", false)]
    [InlineData("pc-scopes-deconstruct", false)]
    [InlineData("factory-sp-reassigned", false)]
    [InlineData("factory-sp-deconstruct", false)]
    [InlineData("factory-sp-out", false)]
    public void SqliteBlobIndexerRefusesEveryWriteToAnInjectedDependency(string shape, bool database)
    {
        string early = shape switch
        {
            "nameof-parameter" => "_ = nameof(db); ",
            "pc-reassigned" => "db = new BlobContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>().Options); ",
            "pc-local-function" => "void Swap() { db = new BlobContext(BlobInput.Options); } Swap(); ",
            "pc-lambda" => "System.Action swap = () => db = new BlobContext(BlobInput.Options); swap(); ",
            "pc-ref-swap" => "BlobSwap.Swap(ref db); ",
            "pc-out-swap" => "BlobOut.Out(out db); ",
            "pc-deconstruct-swap" => "(db, _) = (new BlobContext(BlobInput.Options), 0); ",
            "pc-nested-deconstruct-swap" => "((db, _), _) = ((new BlobContext(BlobInput.Options), 0), 0); ",
            "pc-coalesce-assign" => "db ??= new BlobContext(BlobInput.Options); ",
            "pc-ref-local-alias" => "ref BlobContext alias = ref db; alias = new BlobContext(BlobInput.Options); ",
            "pc-sp-reassigned" => "sp = new FakeProvider(); ",
            "pc-sp-deconstruct" => "(sp, _) = (new FakeProvider(), 0); ",
            "pc-sp-out" => "ProviderOut.Out(out sp); ",
            "pc-scopes-exact" => "await using AsyncServiceScope scope = scopes.CreateAsyncScope(); ",
            "pc-scopes-reassigned" => "scopes = new FakeScopes(); await using AsyncServiceScope scope = scopes.CreateAsyncScope(); ",
            "pc-scopes-deconstruct" => "(scopes, _) = (new FakeScopes(), 0); await using AsyncServiceScope scope = scopes.CreateAsyncScope(); ",
            _ => string.Empty,
        };

        string registration = shape switch
        {
            "factory-sp-exact" => "services.AddScoped<BlobHolder>(sp => new BlobHolder(sp.GetRequiredService<BlobContext>())); ",
            "factory-sp-reassigned" => "services.AddScoped<BlobHolder>(sp => { sp = new FakeProvider(); return new BlobHolder(sp.GetRequiredService<BlobContext>()); }); ",
            "factory-sp-deconstruct" => "services.AddScoped<BlobHolder>(sp => { (sp, _) = (new FakeProvider(), 0); return new BlobHolder(sp.GetRequiredService<BlobContext>()); }); ",
            "factory-sp-out" => "services.AddScoped<BlobHolder>(sp => { ProviderOut.Out(out sp); return new BlobHolder(sp.GetRequiredService<BlobContext>()); }); ",
            _ => string.Empty,
        };

        string contextExpression = shape switch
        {
            "field-readonly" => "_db",
            "pc-sp-exact" or "pc-sp-reassigned" or "pc-sp-deconstruct" or "pc-sp-out" => "sp.GetRequiredService<BlobContext>()",
            "pc-scopes-exact" or "pc-scopes-reassigned" or "pc-scopes-deconstruct" => "scope.ServiceProvider.GetRequiredService<BlobContext>()",
            _ => "db",
        };

        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite,
            registration,
            contextBases: string.Empty,
            contextMembers: string.Empty,
            onConfiguring: null,
            early: early,
            pipelinePrefix: string.Empty,
            contextExpression: contextExpression,
            helpers: SqliteIndexerWriteHelpers);

        const string worker = "public class Worker(BlobContext db) : IHostedService";

        if (shape.StartsWith("factory-sp-", StringComparison.Ordinal))
        {
            source = source
                .Replace(worker, "public class Worker(BlobContext db, BlobHolder holder) : IHostedService", StringComparison.Ordinal)
                .Replace("await BlobPipeline.RunAsync(db, token); }", "await holder.RunAsync(token); }", StringComparison.Ordinal);
        }
        else if (shape.StartsWith("pc-scopes-", StringComparison.Ordinal))
        {
            source = source.Replace(worker, "public class Worker(BlobContext db, Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes) : IHostedService", StringComparison.Ordinal);
        }
        else if (shape.StartsWith("pc-sp-", StringComparison.Ordinal))
        {
            source = source.Replace(worker, "public class Worker(BlobContext db, IServiceProvider sp) : IHostedService", StringComparison.Ordinal);
        }
        else if (shape == "field-readonly")
        {
            source = source.Replace("public async Task StartAsync(CancellationToken token) {", "private readonly BlobContext _db = db; public async Task StartAsync(CancellationToken token) {", StringComparison.Ordinal);
        }
        else if (shape == "pc-written-elsewhere")
        {
            source = source.Replace("public Task StopAsync(CancellationToken token) => Task.CompletedTask;", "public Task StopAsync(CancellationToken token) { db = new BlobContext(BlobInput.Options); return Task.CompletedTask; }", StringComparison.Ordinal);
        }
        else if (shape is "ctor-param-exact" or "ctor-param-reassigned")
        {
            string constructorBody = shape == "ctor-param-exact"
                ? "this.db = db;"
                : "db = new BlobContext(BlobInput.Options); this.db = db;";

            source = source
                .Replace(worker, "public class Worker : IHostedService", StringComparison.Ordinal)
                .Replace("public async Task StartAsync(CancellationToken token) {", "private readonly BlobContext db; public Worker(BlobContext db) { " + constructorBody + " } public async Task StartAsync(CancellationToken token) {", StringComparison.Ordinal);
        }

        AssertSqliteIndexerCertificate(source, shape, database);
    }

    [Theory]
    [InlineData("primary-exact", true)]
    [InlineData("explicit-exact", true)]
    [InlineData("authored-base-exact", true)]
    [InlineData("base-ctor-options", false)]
    [InlineData("base-ctor-options-wrapped", false)]
    [InlineData("base-ctor-options-assigned", false)]
    [InlineData("explicit-no-initializer", false)]
    [InlineData("explicit-parameterless-base", false)]
    [InlineData("explicit-this-chain", false)]
    [InlineData("second-constructor", false)]
    [InlineData("authored-base-ignores-options", false)]
    [InlineData("authored-base-wrapped", false)]
    [InlineData("generic-options-of-another-context", false)]
    public void SqliteBlobIndexerRequiresTheContextToForwardItsInjectedOptions(string shape, bool database)
    {
        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: shape switch
            {
                "explicit-exact" => "public BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : base(options) { }",
                "explicit-no-initializer" => "public BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) { }",
                "explicit-parameterless-base" => "public BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : base() { }",
                "explicit-this-chain" => "public BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : this(options, 0) { } private BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options, int extra) : base(BlobInput.Options) { }",
                "second-constructor" => "public BlobContext() : this(BlobInput.Options) { }",
                _ => string.Empty,
            },
            onConfiguring: null,
            early: string.Empty,
            pipelinePrefix: string.Empty,
            contextExpression: "db",
            helpers: (shape == "generic-options-of-another-context" ? string.Empty : SqliteIndexerWriteHelpers) + """
                public abstract class BlobBaseExact(Microsoft.EntityFrameworkCore.DbContextOptions options) : Microsoft.EntityFrameworkCore.DbContext(options) { }
                public abstract class BlobBaseIgnores(Microsoft.EntityFrameworkCore.DbContextOptions options) : Microsoft.EntityFrameworkCore.DbContext(BlobInput.Options) { }
                public abstract class BlobBaseWrapped(Microsoft.EntityFrameworkCore.DbContextOptions options) : Microsoft.EntityFrameworkCore.DbContext(BlobOptionsSwap.Swap(options)) { }
                internal static class BlobOptionsSwap { internal static Microsoft.EntityFrameworkCore.DbContextOptions Swap(Microsoft.EntityFrameworkCore.DbContextOptions options) => new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<OtherContext>().Options; }
                public sealed class OtherContext(Microsoft.EntityFrameworkCore.DbContextOptions<OtherContext> options) : Microsoft.EntityFrameworkCore.DbContext(options) { }
                """);

        string header = shape switch
        {
            "explicit-exact" or "explicit-no-initializer" or "explicit-parameterless-base" or "explicit-this-chain" =>
                "public sealed class BlobContext : Microsoft.EntityFrameworkCore.DbContext",
            "base-ctor-options" => SqliteIndexerContextHeader.Replace("DbContext(options)", "DbContext(BlobInput.Options)", StringComparison.Ordinal),
            "base-ctor-options-wrapped" => SqliteIndexerContextHeader.Replace("DbContext(options)", "DbContext(BlobOptionsSwap.Swap(options))", StringComparison.Ordinal),
            "base-ctor-options-assigned" => SqliteIndexerContextHeader.Replace("DbContext(options)", "DbContext(options = BlobInput.Options)", StringComparison.Ordinal),
            "authored-base-exact" => SqliteIndexerContextHeader.Replace("Microsoft.EntityFrameworkCore.DbContext(options)", "BlobBaseExact(options)", StringComparison.Ordinal),
            "authored-base-ignores-options" => SqliteIndexerContextHeader.Replace("Microsoft.EntityFrameworkCore.DbContext(options)", "BlobBaseIgnores(options)", StringComparison.Ordinal),
            "authored-base-wrapped" => SqliteIndexerContextHeader.Replace("Microsoft.EntityFrameworkCore.DbContext(options)", "BlobBaseWrapped(options)", StringComparison.Ordinal),
            "generic-options-of-another-context" => SqliteIndexerContextHeader.Replace("DbContextOptions<BlobContext> options", "DbContextOptions<OtherContext> options", StringComparison.Ordinal),
            _ => SqliteIndexerContextHeader,
        };

        if (shape != "primary-exact" && shape != "second-constructor")
        {
            Assert.Contains(SqliteIndexerContextHeader, source, StringComparison.Ordinal);

            source = source.Replace(SqliteIndexerContextHeader, header, StringComparison.Ordinal);
        }

        AssertSqliteIndexerCertificate(source, shape, database);
    }

    [Theory]
    [InlineData("no-replacement", true)]
    [InlineData("setdbconnection-invocation", false)]
    [InlineData("setdbconnection-method-group", false)]
    [InlineData("setdbconnection-unreached-method-group", false)]
    [InlineData("setconnectionstring-method-group", false)]
    [InlineData("relational-connection-set", false)]
    [InlineData("relational-connection-concrete-set", false)]
    [InlineData("infrastructure-instance-read", false)]
    public void SqliteBlobIndexerRefusesEveryReferenceToAConnectionReplacement(string shape, bool database)
    {
        string prefix = shape switch
        {
            "setdbconnection-invocation" => "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SetDbConnection(db.Database, BlobInput.Connection, false); ",
            "setdbconnection-method-group" => "System.Action<Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade, System.Data.Common.DbConnection?, bool> swap = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SetDbConnection; swap(db.Database, BlobInput.Connection, false); ",
            "setconnectionstring-method-group" => "System.Action<Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade, string?> swap = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SetConnectionString; swap(db.Database, \"Data Source=:memory:\"); ",
            "relational-connection-set" => "IServiceProvider services = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)db).Instance; Microsoft.EntityFrameworkCore.Storage.IRelationalConnection rc = (Microsoft.EntityFrameworkCore.Storage.IRelationalConnection)services.GetService(typeof(Microsoft.EntityFrameworkCore.Storage.IRelationalConnection))!; rc.DbConnection = BlobInput.Connection; ",
            "relational-connection-concrete-set" => "IServiceProvider services = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)db).Instance; Microsoft.EntityFrameworkCore.Storage.RelationalConnection rc = (Microsoft.EntityFrameworkCore.Storage.RelationalConnection)services.GetService(typeof(Microsoft.EntityFrameworkCore.Storage.IRelationalConnection))!; rc.DbConnection = BlobInput.Connection; ",
            "infrastructure-instance-read" => "_ = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)db).Instance; ",
            _ => string.Empty,
        };

        string helpers = SqliteIndexerWriteHelpers
            + "internal static class UnreachedSwap { internal static System.Action<Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade, System.Data.Common.DbConnection?, bool> Swap = "
            + (shape == "setdbconnection-unreached-method-group" ? "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SetDbConnection" : "null!")
            + "; }";

        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: string.Empty,
            onConfiguring: null,
            early: string.Empty,
            pipelinePrefix: prefix,
            contextExpression: "db",
            helpers: helpers);

        AssertSqliteIndexerCertificate(source, shape, database);
    }

    [Theory]
    [InlineData("dbset-escape")]
    [InlineData("queryable-escape")]
    [InlineData("facade-escape")]
    [InlineData("set-method-escape")]
    public void SqliteBlobIndexerOnConfiguringStaysUnadmittedWhenAServiceValueOutlivesAdmission(string shape)
    {
        string capture = shape switch
        {
            "dbset-escape" => "rows = db.Rows; ",
            "facade-escape" => "facade = db.Database; ",
            "set-method-escape" => "rows = db.Set<BlobRow>(); ",
            _ => "rows = System.Linq.Queryable.Where(db.Rows, static row => row.Id > 0); ",
        };

        // EF runs OnConfiguring at the first use of the context's services, which is not the read
        // of a DbSet or the facade: it is whatever later consumes it, and that can be after the
        // admission that held at the read has ended.
        string after = shape == "facade-escape"
            ? "_ = facade.CanConnect(); "
            : "_ = System.Linq.Enumerable.ToList(rows); ";

        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: "public Microsoft.EntityFrameworkCore.DbSet<BlobRow> Rows { get; set; } = null!;",
            onConfiguring: "System.IO.File.Delete(\"on-configuring\");",
            early: string.Empty,
            pipelinePrefix: string.Empty,
            contextExpression: "db",
            helpers: string.Empty);

        const string start = "public async Task StartAsync(CancellationToken token) { _ = db; ";

        Assert.Contains(start + R2Admission + "await BlobPipeline.RunAsync(db, token); }", source, StringComparison.Ordinal);

        source = source.Replace(
            start + R2Admission + "await BlobPipeline.RunAsync(db, token); }",
            start + "System.Linq.IQueryable<BlobRow> rows = null!; Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade facade = null!; await Admitted(); " + after + "return; async Task Admitted() { " + R2Admission + capture + "await Task.Yield(); } }",
            StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSqliteIndexerProbe(source);

        HostedProducerSite[] deletes = result.Items.Where(static site => site.Callee == "System.IO.File.Delete").ToArray();

        Assert.NotEmpty(deletes);

        Assert.True(
            deletes.Any(static site => site.WorkFrontierCapsuleId is null)
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"),
            "OnConfiguring effect certified as admitted although its first service use happens after admission ended");
    }

    private void AssertSqliteIndexerCertificate(string source, string shape, bool database)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSqliteIndexerProbe(source);

        output.WriteLine("SQLITE-INDEXER shape=" + shape + " reached=" + IsSqliteIndexerReached(result) + " database=" + IsSqliteIndexerClassified(result));

        Assert.True(IsSqliteIndexerReached(result));

        Assert.Equal(database, IsSqliteIndexerClassified(result));

        // A refused certificate must be the indexer's own refusal, not an unrelated one.
        Assert.Equal(!database, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == SqliteIndexerCallee));
    }
}
