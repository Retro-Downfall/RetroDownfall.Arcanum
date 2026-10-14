using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// Executing an Entity Framework query is a database access wherever it is written: enumerating a DbSet or an IQueryable
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
