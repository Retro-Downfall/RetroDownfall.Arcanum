using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The EfQueries fixtures. Executing an Entity Framework query is a database access wherever it is written: enumerating a DbSet or an IQueryable
// built from one (foreach, await foreach, a spread, an enumerating framework member), a LINQ execution operator over it
// (Enumerable or Queryable), an EF execution operator (ToListAsync, FirstAsync, Load, ExecuteDelete, ...), Find, and
// ExecuteSql. Each is a DatabaseAccess site that requires the Work frontier live at it, recognized by the exact framework
// identity of the operator and by the operand's provenance from a DbSet, never by name alone. A query value that is not
// consumed where it is built cannot be followed to its execution, so it is refused: that is what keeps an
// IEnumerable-typed parameter or return, which the authored-enumerables premise would otherwise stop at, from hiding one.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string QueryEnumerate = "System.Collections.Generic.IEnumerable`1.GetEnumerator";

    private const string QueryEnumerateAsync = "System.Collections.Generic.IAsyncEnumerable`1.GetAsyncEnumerator";

    private HostedProducerDiscovery<HostedProducerSite> QueryDiscover(string admitted, string early = "", string helpers = "", string prefix = "", params MetadataReference[] references) =>
        DiscoverSqliteIndexerProbe(QuerySource(admitted, early, helpers, prefix), references);

    // The strict regime every fixture has by default (false), and the regime the shipped tree is judged under (true). The
    // production regime stops the authored-enumerables analysis at a sequence-typed value, so the EF query analysis is the
    // only thing that follows a query to its execution there. The regimes are chosen by DiscoverUnderRegime, which lives
    // with the closed-world fixtures: no other fixture requests the closed-world facts itself.
    private static readonly bool[] QueryModes = [false, true];

    // Runs the body under each regime and reports every regime that fails, so one failing regime does not hide the other.
    private static void EachQueryMode(Action<bool> body)
    {
        List<string> failures = [];

        foreach (bool production in QueryModes)
        {
            try
            {
                body(production);
            }
            catch (Xunit.Sdk.XunitException exception)
            {
                failures.Add("[" + (production ? "production" : "strict") + "] " + exception.Message);
            }
        }

        Assert.True(failures.Count == 0, string.Join(global::System.Environment.NewLine, failures));
    }

    private HostedProducerDiscovery<HostedProducerSite> QueryDiscoverUnder(bool production, string admitted, string early = "", string helpers = "", string prefix = "")
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSqliteIndexerProbe(QuerySource(admitted, early, helpers, prefix), [], production);

        // A premise the fixture violates would grant nothing, and the run would silently be the strict one.
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");

        return result;
    }

    private static string QuerySource(string admitted, string early = "", string helpers = "", string prefix = "")
    {
        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: HookRows,
            onConfiguring: null,
            early: early,
            pipelinePrefix: string.Empty,
            contextExpression: "db",
            helpers: helpers);

        const string pipelineCall = "await BlobPipeline.RunAsync(db, token); }";

        Assert.Contains(pipelineCall, source, StringComparison.Ordinal);

        return prefix + source.Replace(pipelineCall, admitted + "}", StringComparison.Ordinal);
    }

    private static HostedProducerSite[] QuerySites(HostedProducerDiscovery<HostedProducerSite> result, string callee) =>
        [.. result.Items.Where(site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == callee)];

    private static bool QueryRefused(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.Contains("EF query", StringComparison.Ordinal));

    // ---- Every query-executing operation over a DbSet is a database site under the Work frontier live at it.

    [Theory]
    [InlineData("foreach", "foreach (BlobRow r in db.Rows) { _ = r; }", QueryEnumerate)]
    [InlineData("foreach-set", "foreach (BlobRow r in db.Set<BlobRow>()) { _ = r; }", QueryEnumerate)]
    [InlineData("foreach-where", "foreach (BlobRow r in System.Linq.Queryable.Where(db.Rows, r => r.Id > 1)) { _ = r; }", QueryEnumerate)]
    [InlineData("await-foreach", "await foreach (BlobRow r in Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows)) { _ = r; }", QueryEnumerateAsync)]
    [InlineData("dbset-as-async", "await foreach (BlobRow r in db.Rows.AsAsyncEnumerable()) { _ = r; }", QueryEnumerateAsync)]
    [InlineData("spread", "BlobRow[] all = [.. db.Rows]; _ = all;", QueryEnumerate)]
    [InlineData("enumerable-tolist", "_ = System.Linq.Enumerable.ToList(db.Rows);", "System.Linq.Enumerable.ToList")]
    [InlineData("enumerable-toarray", "_ = System.Linq.Enumerable.ToArray(db.Rows);", "System.Linq.Enumerable.ToArray")]
    [InlineData("enumerable-first", "_ = System.Linq.Enumerable.First(db.Rows);", "System.Linq.Enumerable.First")]
    [InlineData("enumerable-any", "_ = System.Linq.Enumerable.Any(db.Rows);", "System.Linq.Enumerable.Any")]
    [InlineData("enumerable-count", "_ = System.Linq.Enumerable.Count(db.Rows);", "System.Linq.Enumerable.Count")]
    [InlineData("enumerable-select-tolist", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(db.Rows, r => r.Id));", "System.Linq.Enumerable.ToList")]
    [InlineData("queryable-first", "_ = System.Linq.Queryable.First(db.Rows);", "System.Linq.Queryable.First")]
    [InlineData("queryable-count", "_ = System.Linq.Queryable.Count(db.Rows);", "System.Linq.Queryable.Count")]
    [InlineData("queryable-any", "_ = System.Linq.Queryable.Any(db.Rows);", "System.Linq.Queryable.Any")]
    [InlineData("queryable-single-or-default", "_ = System.Linq.Queryable.SingleOrDefault(System.Linq.Queryable.Where(db.Rows, r => r.Id == 1));", "System.Linq.Queryable.SingleOrDefault")]
    [InlineData("tolistasync", "_ = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Rows, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync")]
    [InlineData("firstasync", "_ = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(db.Rows, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync")]
    [InlineData("anyasync", "_ = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Rows, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync")]
    [InlineData("countasync", "_ = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Rows, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync")]
    [InlineData("load", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Load(db.Rows);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Load")]
    [InlineData("loadasync", "await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LoadAsync(db.Rows, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LoadAsync")]
    [InlineData("foreachasync", "await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ForEachAsync(db.Rows, r => { _ = r; }, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ForEachAsync")]
    [InlineData("as-no-tracking", "_ = System.Linq.Enumerable.ToList(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Rows));", "System.Linq.Enumerable.ToList")]
    [InlineData("find", "_ = db.Rows.Find(1);", "Microsoft.EntityFrameworkCore.DbSet`1.Find")]
    [InlineData("find-async", "_ = await db.Rows.FindAsync(1);", "Microsoft.EntityFrameworkCore.DbSet`1.FindAsync")]
    [InlineData("context-find", "_ = db.Find<BlobRow>(1);", "Microsoft.EntityFrameworkCore.DbContext.Find")]
    [InlineData("execute-sql", "_ = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(db.Database, \"x\");", "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw")]
    [InlineData("list-ctor", "_ = new System.Collections.Generic.List<BlobRow>(db.Rows);", "System.Collections.Generic.List`1..ctor")]
    [InlineData("list-add-range", "System.Collections.Generic.List<BlobRow> all = new(); all.AddRange(db.Rows);", "System.Collections.Generic.List`1.AddRange")]
    [InlineData("sql-query", "_ = System.Linq.Enumerable.ToList(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SqlQueryRaw<int>(db.Database, \"select 1\"));", "System.Linq.Enumerable.ToList")]
    [InlineData("from-sql", "_ = System.Linq.Enumerable.ToList(Microsoft.EntityFrameworkCore.RelationalQueryableExtensions.FromSqlRaw(db.Rows, \"select 1\"));", "System.Linq.Enumerable.ToList")]
    [InlineData("execute-delete", "_ = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDelete(db.Rows);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDelete")]
    [InlineData("execute-delete-async", "_ = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(db.Rows, token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync")]
    [InlineData("local-chain", "System.Linq.IQueryable<BlobRow> q = System.Linq.Queryable.Where(db.Rows, r => r.Id > 1); _ = System.Linq.Enumerable.ToList(q);", "System.Linq.Enumerable.ToList")]
    [InlineData("local-foreach", "var q = db.Set<BlobRow>(); foreach (BlobRow r in q) { _ = r; }", QueryEnumerate)]
    public void AnEfQueryExecutionIsADatabaseSiteUnderTheFrontierLiveAtIt(string shape, string statement, string callee)
    {
        output.WriteLine("QUERY " + shape);

        HostedProducerDiscovery<HostedProducerSite> admitted = QueryDiscover(statement);

        HostedProducerSite site = Assert.Single(QuerySites(admitted, callee).DistinctBy(static site => site.SyntaxOrdinal + ":" + site.SyntaxFingerprint));

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.False(QueryRefused(admitted));

        // The same execution outside an admitted region lacks its frontier, and the diagnostic names the site.
        HostedProducerDiscovery<HostedProducerSite> unadmitted = QueryDiscover(string.Empty, early: statement);

        Assert.All(QuerySites(unadmitted, callee), static unadmittedSite => Assert.Null(unadmittedSite.WorkFrontierCapsuleId));

        Assert.Contains(unadmitted.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == callee);
    }

    // ---- The twins that are not database accesses.

    [Theory]
    [InlineData("list-foreach", "System.Collections.Generic.List<BlobRow> l = new(); foreach (BlobRow r in l) { _ = r; }")]
    [InlineData("list-tolist", "System.Collections.Generic.List<BlobRow> l = new(); _ = System.Linq.Enumerable.ToList(l); _ = System.Linq.Enumerable.First(l); _ = System.Linq.Enumerable.Any(l);")]
    [InlineData("array-linq", "BlobRow[] l = []; _ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(l, r => r.Id));")]
    [InlineData("dbset-add", "db.Rows.Add(new BlobRow()); db.Rows.Remove(new BlobRow()); db.Rows.Update(new BlobRow()); db.Rows.Attach(new BlobRow());")]
    [InlineData("set-add", "db.Set<BlobRow>().Add(new BlobRow());")]
    [InlineData("local-add", "var set = db.Set<BlobRow>(); set.Add(new BlobRow()); set.Remove(new BlobRow());")]
    [InlineData("discarded", "_ = db.Rows; _ = db.Set<BlobRow>();")]
    public void ANonQueryingUseOfADbSetOrAnInMemorySequenceIsNotADatabaseAccess(string shape, string statement)
    {
        output.WriteLine("QUERY " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = QueryDiscover(statement);

        Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess
            && !site.Callee.StartsWith("System.Data.", StringComparison.Ordinal));

        Assert.False(QueryRefused(result));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void AnInMemoryQueryableIsNotADatabaseAccessAndStaysUnreviewed()
    {
        // A Queryable operator is reviewed only over a query EF proves it built; over an in-memory queryable it keeps
        // the refusal every unreviewed framework operator gets, and is no database site.
        HostedProducerDiscovery<HostedProducerSite> result = QueryDiscover("System.Collections.Generic.List<BlobRow> l = new(); _ = System.Linq.Queryable.First(System.Linq.Queryable.AsQueryable(l));");

        Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == "System.Linq.Queryable.First");
    }

    [Fact]
    public void ALookalikeDbSetFromAnotherAssemblyIsNotAnEfQuery()
    {
        const string lookalikeSource = "namespace Microsoft.EntityFrameworkCore { public class DbSet<T> : System.Collections.Generic.IEnumerable<T> where T : class { public System.Collections.Generic.IEnumerator<T> GetEnumerator() => throw new System.NotSupportedException(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } }";

        PortableExecutableReference lookalike = EmitSqliteIndexerLookalike("Microsoft.EntityFrameworkCore.Lookalike", lookalikeSource, "fake");

        HostedProducerDiscovery<HostedProducerSite> result = QueryDiscover(
            "foreach (BlobRow r in HookFake.Rows) { _ = r; }",
            helpers: "internal static class HookFake { internal static fake::Microsoft.EntityFrameworkCore.DbSet<BlobRow> Rows { get; set; } = null!; }",
            prefix: "extern alias fake;\n",
            references: lookalike);

        // It is no EF query: no database site, and the enumeration is judged as the unproven value it is.
        Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == QueryEnumerate);
    }

    // ---- A query value that is not consumed where it is built is refused.

    [Theory]
    [InlineData("argument", "QueryHelpers.Walk(db.Rows);")]
    [InlineData("argument-set", "QueryHelpers.Walk(db.Set<BlobRow>());")]
    [InlineData("return", "foreach (BlobRow r in QueryHelpers.Provide(db)) { _ = r; }")]
    [InlineData("static-field", "QueryHelpers.Stored = db.Rows;")]
    [InlineData("collection-element", "object[] holders = [db.Rows]; _ = holders;")]
    [InlineData("lambda-capture", "System.Func<System.Collections.Generic.IEnumerable<BlobRow>> f = () => db.Rows; foreach (BlobRow r in f()) { _ = r; }")]
    [InlineData("tuple", "var pair = (db.Rows, 1); _ = pair;")]
    [InlineData("conditional", "var chosen = BlobInput.Options is null ? db.Rows : db.Set<BlobRow>(); _ = chosen;")]
    [InlineData("local-then-argument", "var q = db.Rows; QueryHelpers.Walk(q);")]
    [InlineData("local-reassigned", "System.Collections.Generic.IEnumerable<BlobRow> q = db.Rows; q = QueryHelpers.Other; foreach (BlobRow r in q) { _ = r; }")]
    public void AnEfQueryValueThatEscapesWhereItIsBuiltIsRefused(string shape, string statement)
    {
        output.WriteLine("QUERY " + shape);

        const string helpers = "internal static class QueryHelpers { internal static void Walk(System.Collections.Generic.IEnumerable<BlobRow> rows) { foreach (BlobRow r in rows) { _ = r; } } "
            + "internal static System.Collections.Generic.IEnumerable<BlobRow> Provide(BlobContext c) => c.Rows; "
            + "internal static System.Collections.Generic.IEnumerable<BlobRow> Stored { get; set; } = null!; "
            + "internal static System.Collections.Generic.IEnumerable<BlobRow> Other { get; set; } = null!; }";

        Assert.True(QueryRefused(QueryDiscover(statement, helpers: helpers)), shape + " must be refused.");
    }

    [Fact]
    public void ADbSetPropertyThatReturnsSetOfTIsTheFrameworkIdiomNotAnEscape()
    {
        // The context's own `Rows => Set<BlobRow>()` hands the set to the read that consumes it; the read is judged where
        // it is consumed. This is exactly the shape the shipped context declares.
        HostedProducerDiscovery<HostedProducerSite> result = QueryDiscover("db.Rows.Add(new BlobRow());");

        Assert.False(QueryRefused(result));
    }

    // ---- A builder over an EF query is an EF query whatever static type it returns.

    [Theory]
    [InlineData("ordered", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.AsEnumerable(db.Rows), r => r.Id));", "System.Linq.Enumerable.ToList")]
    [InlineData("then-by", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.ThenBy(System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.AsEnumerable(db.Rows), r => r.Id), r => r.Id));", "System.Linq.Enumerable.ToList")]
    [InlineData("ordered-queryable", "_ = System.Linq.Enumerable.ToList(System.Linq.Queryable.OrderBy(db.Rows, r => r.Id));", "System.Linq.Enumerable.ToList")]
    [InlineData("include-tolist", "_ = System.Linq.Enumerable.ToList(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.Rows, r => r.Id));", "System.Linq.Enumerable.ToList")]
    [InlineData("include-count", "_ = System.Linq.Enumerable.Count(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.Rows, r => r.Id));", "System.Linq.Enumerable.Count")]
    [InlineData("include-foreach", "foreach (BlobRow r in Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.Rows, r => r.Id)) { _ = r; }", QueryEnumerate)]
    [InlineData("include-tolistasync", "_ = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.Rows, r => r.Id), token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync")]
    [InlineData("ordered-local-foreach", "var ordered = System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.AsEnumerable(db.Rows), r => r.Id); foreach (BlobRow r in ordered) { _ = r; }", QueryEnumerate)]
    [InlineData("grouped", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.GroupBy(db.Rows, r => r.Id));", "System.Linq.Enumerable.ToList")]
    public void ABuilderOverAnEfQueryIsAQueryWhateverStaticTypeItReturns(string shape, string statement, string callee)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> admitted = QueryDiscoverUnder(production, statement);

            HostedProducerSite site = Assert.Single(QuerySites(admitted, callee).DistinctBy(static site => site.SyntaxOrdinal + ":" + site.SyntaxFingerprint));

            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.False(QueryRefused(admitted));

            Assert.DoesNotContain(admitted.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_UNCLASSIFIED");

            HostedProducerDiscovery<HostedProducerSite> unadmitted = QueryDiscoverUnder(production, string.Empty, early: statement);

            Assert.All(QuerySites(unadmitted, callee), static unadmittedSite => Assert.Null(unadmittedSite.WorkFrontierCapsuleId));

            Assert.Contains(unadmitted.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == callee);
        });
    }

    [Theory]
    [InlineData("ordered-in-memory", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(new BlobRow[0], r => r.Id));")]
    [InlineData("ordered-then-by-in-memory", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.ThenBy(System.Linq.Enumerable.OrderBy(new BlobRow[0], r => r.Id), r => r.Id));")]
    [InlineData("grouped-in-memory", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.GroupBy(new BlobRow[0], r => r.Id));")]
    public void ABuilderOverAnInMemorySequenceIsNoDatabaseAccess(string shape, string statement)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = QueryDiscoverUnder(production, statement);

            Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && !site.Callee.StartsWith("System.Data.", StringComparison.Ordinal));

            Assert.False(QueryRefused(result));
        });
    }

    // ---- The operators are the same whether they are called as static methods or as extension methods.

    private const string QueryExtensionUsings = "using System.Linq;\nusing Microsoft.EntityFrameworkCore;\n";

    [Theory]
    [InlineData("where-order-tolist", "_ = db.Rows.Where(r => r.Id > 1).OrderBy(r => r.Id).ToList();", "System.Linq.Enumerable.ToList")]
    [InlineData("queryable-where-first", "_ = db.Rows.Where(r => r.Id > 1).First();", "System.Linq.Queryable.First")]
    [InlineData("include-no-tracking", "_ = db.Rows.Include(r => r.Id).AsNoTracking().ToList();", "System.Linq.Enumerable.ToList")]
    [InlineData("count-async", "_ = await db.Rows.Where(r => r.Id > 1).CountAsync(token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync")]
    [InlineData("to-list-async", "_ = await db.Rows.AsNoTracking().ToListAsync(token);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync")]
    [InlineData("as-enumerable-order", "_ = db.Rows.AsEnumerable().OrderBy(r => r.Id).ToList();", "System.Linq.Enumerable.ToList")]
    [InlineData("foreach-where", "foreach (BlobRow r in db.Rows.Where(r => r.Id > 1)) { _ = r; }", QueryEnumerate)]
    [InlineData("string-join", "_ = string.Join(\",\", db.Rows.Select(r => r.Id));", "System.String.Join")]
    [InlineData("hash-set", "_ = new System.Collections.Generic.HashSet<int>(db.Rows.Select(r => r.Id));", "System.Collections.Generic.HashSet`1..ctor")]
    public void TheOperatorsOfAnEfQueryAreTheSameAsExtensionMethods(string shape, string statement, string callee)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> admitted = QueryDiscoverUnder(production, statement, prefix: QueryExtensionUsings);

            HostedProducerSite site = Assert.Single(QuerySites(admitted, callee).DistinctBy(static site => site.SyntaxOrdinal + ":" + site.SyntaxFingerprint));

            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.False(QueryRefused(admitted));

            HostedProducerDiscovery<HostedProducerSite> unadmitted = QueryDiscoverUnder(production, string.Empty, early: statement, prefix: QueryExtensionUsings);

            Assert.Contains(unadmitted.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == callee);
        });
    }

    [Theory]
    [InlineData("order-then-consume", "var ordered = db.Rows.AsEnumerable().OrderBy(r => r.Id); {gap} foreach (BlobRow r in ordered) { _ = r; }", QueryEnumerate)]
    [InlineData("group-then-consume", "var grouped = db.Rows.GroupBy(r => r.Id); {gap} _ = grouped.ToList();", "System.Linq.Enumerable.ToList")]
    [InlineData("async-then-consume", "var rows = db.Rows.AsAsyncEnumerable(); {gap} await foreach (BlobRow r in rows) { _ = r; }", QueryEnumerateAsync)]
    public void AQueryBuiltWithExtensionMethodsIsJudgedWhereItIsConsumed(string shape, string statement, string consumer)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> late = QueryDiscoverUnder(production, statement.Replace("{gap}", "lease.Dispose();", StringComparison.Ordinal), prefix: QueryExtensionUsings);

            Assert.Contains(late.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == consumer);

            HostedProducerDiscovery<HostedProducerSite> held = QueryDiscoverUnder(production, statement.Replace("{gap}", string.Empty, StringComparison.Ordinal), prefix: QueryExtensionUsings);

            Assert.NotEmpty(QuerySites(held, consumer));

            Assert.All(QuerySites(held, consumer), static site => Assert.NotNull(site.WorkFrontierCapsuleId));

            Assert.False(QueryRefused(held));
        });
    }

    // ---- A lazy framework operator over an EF query runs nothing where it wraps the query.

    [Theory]
    [InlineData("count-by", "var wrapped = System.Linq.Enumerable.CountBy(db.Rows, r => r.Id); {gap} _ = System.Linq.Enumerable.ToList(wrapped);", "System.Linq.Enumerable.CountBy", "System.Linq.Enumerable.ToList")]
    [InlineData("aggregate-by", "var wrapped = System.Linq.Enumerable.AggregateBy(db.Rows, r => r.Id, 0, (acc, r) => acc + 1); {gap} _ = System.Linq.Enumerable.ToList(wrapped);", "System.Linq.Enumerable.AggregateBy", "System.Linq.Enumerable.ToList")]
    [InlineData("left-join", "var wrapped = System.Linq.Enumerable.LeftJoin(db.Rows, new BlobRow[0], r => r.Id, r => r.Id, (a, b) => a); {gap} _ = System.Linq.Enumerable.ToList(wrapped);", "System.Linq.Enumerable.LeftJoin", "System.Linq.Enumerable.ToList")]
    [InlineData("right-join", "var wrapped = System.Linq.Enumerable.RightJoin(new BlobRow[0], db.Rows, r => r.Id, r => r.Id, (a, b) => b); {gap} _ = System.Linq.Enumerable.ToList(wrapped);", "System.Linq.Enumerable.RightJoin", "System.Linq.Enumerable.ToList")]
    [InlineData("index", "var wrapped = System.Linq.Enumerable.Index(db.Rows); {gap} _ = System.Linq.Enumerable.ToList(wrapped);", "System.Linq.Enumerable.Index", "System.Linq.Enumerable.ToList")]
    [InlineData("shuffle", "var wrapped = System.Linq.Enumerable.Shuffle(db.Rows); {gap} _ = System.Linq.Enumerable.ToList(wrapped);", "System.Linq.Enumerable.Shuffle", "System.Linq.Enumerable.ToList")]
    [InlineData("count-by-foreach", "var wrapped = System.Linq.Enumerable.CountBy(db.Rows, r => r.Id); {gap} foreach (var pair in wrapped) { _ = pair; }", "System.Linq.Enumerable.CountBy", QueryEnumerate)]
    [InlineData("async-where", "var wrapped = System.Linq.AsyncEnumerable.Where(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows), r => r.Id > 0); {gap} await foreach (BlobRow r in wrapped) { _ = r; }", "System.Linq.AsyncEnumerable.Where", QueryEnumerateAsync)]
    [InlineData("async-select-tolist", "var wrapped = System.Linq.AsyncEnumerable.Select(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows), r => r.Id); {gap} _ = await System.Linq.AsyncEnumerable.ToListAsync(wrapped, token);", "System.Linq.AsyncEnumerable.Select", "System.Linq.AsyncEnumerable.ToListAsync")]
    public void ALazyOperatorOverAnEfQueryRunsNothingWhereItWrapsSoTheExecutionIsJudgedWhereTheResultIsConsumed(string shape, string statement, string wrapper, string consumer)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            // The result is consumed after the lease has ended: the consumer is the access, and it lacks the frontier.
            HostedProducerDiscovery<HostedProducerSite> late = QueryDiscoverUnder(production, statement.Replace("{gap}", "lease.Dispose();", StringComparison.Ordinal));

            Assert.Empty(QuerySites(late, wrapper));

            HostedProducerSite[] consumed = QuerySites(late, consumer);

            Assert.NotEmpty(consumed);

            Assert.All(consumed, static site => Assert.Null(site.WorkFrontierCapsuleId));

            Assert.Contains(late.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == consumer);

            // The twin consumes it under the lease it was built under.
            HostedProducerDiscovery<HostedProducerSite> held = QueryDiscoverUnder(production, statement.Replace("{gap}", string.Empty, StringComparison.Ordinal));

            Assert.Empty(QuerySites(held, wrapper));

            Assert.NotEmpty(QuerySites(held, consumer));

            Assert.All(QuerySites(held, consumer), static site => Assert.NotNull(site.WorkFrontierCapsuleId));

            Assert.False(QueryRefused(held));
        });
    }

    private const string QueryEnumerateConfigured = "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1.GetAsyncEnumerator";

    [Theory]
    [InlineData("configure-await", "await foreach (BlobRow r in System.Threading.Tasks.TaskAsyncEnumerableExtensions.ConfigureAwait(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows), false)) { {body} }", QueryEnumerateConfigured)]
    [InlineData("with-cancellation", "await foreach (BlobRow r in System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows), token)) { {body} }", QueryEnumerateConfigured)]
    [InlineData("plain", "await foreach (BlobRow r in Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows)) { {body} }", QueryEnumerateAsync)]
    [InlineData("async-where", "await foreach (BlobRow r in System.Linq.AsyncEnumerable.Where(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows), r => r.Id > 0)) { {body} }", QueryEnumerateAsync)]
    public void ALoopOverAWrappedAsyncQueryHoldsItsReaderAcrossTheBackEdge(string shape, string template, string callee)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            // The body ends the lease while the reader is still open: the back edge has no frontier.
            HostedProducerDiscovery<HostedProducerSite> broken = QueryDiscoverUnder(production, template.Replace("{body}", "lease.Dispose();", StringComparison.Ordinal));

            Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == callee);

            HostedProducerDiscovery<HostedProducerSite> held = QueryDiscoverUnder(production, template.Replace("{body}", "_ = r;", StringComparison.Ordinal));

            HostedProducerSite site = Assert.Single(QuerySites(held, callee).DistinctBy(static site => site.SyntaxOrdinal + ":" + site.SyntaxFingerprint));

            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.False(QueryRefused(held));
        });
    }

    // ---- A framework member that stores, defers or hands back a query cannot be followed to its execution.

    [Theory]
    [InlineData("list-add", "var holder = new System.Collections.Generic.List<System.Linq.IQueryable<BlobRow>>(); holder.Add(db.Rows); lease.Dispose(); _ = System.Linq.Enumerable.ToList(holder[0]);", "System.Collections.Generic.List`1.Add")]
    [InlineData("task-from-result", "var t = System.Threading.Tasks.Task.FromResult<System.Linq.IQueryable<BlobRow>>(db.Rows); lease.Dispose(); _ = System.Linq.Enumerable.ToList(await t);", "System.Threading.Tasks.Task.FromResult")]
    [InlineData("repeat", "var rep = System.Linq.Enumerable.Repeat<System.Linq.IQueryable<BlobRow>>(db.Rows, 1); lease.Dispose(); _ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.SelectMany(rep, x => x));", "System.Linq.Enumerable.Repeat")]
    [InlineData("append-element", "var all = System.Linq.Enumerable.Append<System.Linq.IQueryable<BlobRow>>(new System.Collections.Generic.List<System.Linq.IQueryable<BlobRow>>(), db.Rows); lease.Dispose(); foreach (var q in all) { _ = System.Linq.Enumerable.ToList(q); }", "System.Linq.Enumerable.Append")]
    [InlineData("contains-element", "_ = System.Linq.Enumerable.Contains(new System.Collections.Generic.List<System.Linq.IQueryable<BlobRow>>(), db.Rows);", "System.Linq.Enumerable.Contains")]
    [InlineData("get-enumerator", "var e = System.Linq.Enumerable.AsEnumerable(db.Rows).GetEnumerator(); lease.Dispose(); while (e.MoveNext()) { _ = e.Current; }", "System.Collections.Generic.IEnumerable`1.GetEnumerator")]
    [InlineData("as-parallel", "var p = System.Linq.ParallelEnumerable.AsParallel(db.Rows); lease.Dispose(); _ = System.Linq.ParallelEnumerable.ToList(p);", "System.Linq.ParallelEnumerable.AsParallel")]
    [InlineData("try-get-count", "_ = System.Linq.Enumerable.TryGetNonEnumeratedCount(db.Rows, out int n);", "System.Linq.Enumerable.TryGetNonEnumeratedCount")]
    [InlineData("to-query-string", "_ = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToQueryString(db.Rows);", "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToQueryString")]
    [InlineData("to-blocking-enumerable", "var b = System.Threading.Tasks.TaskAsyncEnumerableExtensions.ToBlockingEnumerable(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsAsyncEnumerable(db.Rows)); lease.Dispose(); _ = System.Linq.Enumerable.ToList(b);", "System.Threading.Tasks.TaskAsyncEnumerableExtensions.ToBlockingEnumerable")]
    public void AFrameworkMemberThatStoresOrDefersAnEfQueryIsRefusedWhereTheQueryIsBuilt(string shape, string statement, string wrapper)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = QueryDiscoverUnder(production, statement);

            Assert.True(QueryRefused(result), shape + " must be refused where the query is built (" + (production ? "production" : "strict") + ").");

            // The wrapper is no database access: the execution it defers is not where it is called.
            Assert.DoesNotContain(result.Items, site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == wrapper);
        });
    }

    // ---- A query reassigned from itself is the common conditional-filter idiom.

    [Theory]
    [InlineData("self-reassigned", "System.Linq.IQueryable<BlobRow> q = db.Rows; q = System.Linq.Queryable.Where(q, r => r.Id > 1); _ = System.Linq.Enumerable.ToList(q);")]
    [InlineData("self-reassigned-in-a-branch", "System.Linq.IQueryable<BlobRow> q = db.Rows; if (token.IsCancellationRequested) { q = System.Linq.Queryable.Where(q, r => r.Id > 1); } _ = System.Linq.Enumerable.ToList(q);")]
    [InlineData("self-reassigned-in-a-loop", "System.Linq.IQueryable<BlobRow> q = db.Rows; for (int i = 0; i < 3; i++) { q = System.Linq.Queryable.Take(q, i + 1); } _ = System.Linq.Enumerable.ToList(q);")]
    [InlineData("mutually-reassigned", "System.Linq.IQueryable<BlobRow> a = db.Rows; System.Linq.IQueryable<BlobRow> b = db.Rows; a = System.Linq.Queryable.Where(b, r => r.Id > 1); b = System.Linq.Queryable.Where(a, r => r.Id > 2); _ = System.Linq.Enumerable.ToList(a);")]
    [InlineData("self-reassigned-by-extension", "System.Linq.IQueryable<BlobRow> q = db.Rows; q = System.Linq.Queryable.OrderBy(q, r => r.Id); _ = System.Linq.Enumerable.ToList(q);")]
    public void AQueryReassignedFromItselfIsStillAQueryAndNeverLoopsTheAnalysis(string shape, string statement)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("QUERY " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> admitted = QueryDiscoverUnder(production, statement);

            HostedProducerSite site = Assert.Single(QuerySites(admitted, "System.Linq.Enumerable.ToList").DistinctBy(static site => site.SyntaxOrdinal + ":" + site.SyntaxFingerprint));

            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.False(QueryRefused(admitted));
        });
    }

    [Fact]
    public void AReassignedLocalWhoseSeedIsNotAnEfQueryIsNotAnEfQuery()
    {
        // The twin: the same cycle seeded from an in-memory sequence has no database behind it, and still terminates.
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = QueryDiscoverUnder(production, "System.Linq.IQueryable<BlobRow> q = System.Linq.Queryable.AsQueryable(new BlobRow[0]); q = System.Linq.Queryable.Where(q, r => r.Id > 1); _ = System.Linq.Enumerable.ToList(q);");

            Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && !site.Callee.StartsWith("System.Data.", StringComparison.Ordinal));

            Assert.False(QueryRefused(result));
        });
    }

    // ---- The interceptors an executed query raises.

    [Fact]
    public void AnExecutedQueryRaisesTheConnectionAndCommandCallbacksOfTheContext()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite + ".AddInterceptors(new ConnectionInterceptorProbe(), new CommandInterceptorProbe(), new SaveInterceptorProbe())",
            string.Empty,
            contextMembers: HookRows,
            helpers: InterceptorTypes,
            admitted: "_ = System.Linq.Enumerable.ToList(db.Rows);");

        Assert.True(CallbackAdmitted(result, "ConnectionOpened"));

        Assert.True(CallbackAdmitted(result, "ReaderExecuting"));

        Assert.False(CallbackTraversed(result, "SavingChanges"));
    }
}
