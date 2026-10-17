using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The EfInterceptors fixtures. EF hands its events to the interceptors registered on a context, at the operations that raise them: a connection
// callback when the connection is created, opened or closed, a command callback when a command executes, a transaction
// callback when a transaction starts, commits or rolls back, a SaveChanges callback around the save. Each authored
// callback is traversed at the EF operation that raises its contract, under the frontiers live at that operation, using
// the interceptor set the registration closes; an interceptor set that cannot be closed is refused. These fixtures pin
// that contract. Source analysis only; nothing here executes EF, SQL or a provider.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string InterceptorTypes = """
        public sealed class ConnectionInterceptorProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { System.IO.File.Delete("connection-opened"); }

            public override async System.Threading.Tasks.Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData, System.Threading.CancellationToken cancellationToken = default) { System.IO.File.Delete("connection-opened-async"); await System.Threading.Tasks.Task.CompletedTask; }

            public override System.Data.Common.DbConnection ConnectionCreated(Microsoft.EntityFrameworkCore.Diagnostics.ConnectionCreatedEventData eventData, System.Data.Common.DbConnection result) { System.IO.File.Delete("connection-created"); return result; }
        }

        public sealed class CommandInterceptorProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
        {
            public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result) { System.IO.File.Delete("reader-executing"); return result; }
        }

        public sealed class SaveInterceptorProbe : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
        {
            public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result) { System.IO.File.Delete("saving-changes"); return result; }
        }

        public static class InterceptorInput
        {
            public static ConnectionInterceptorProbe Value { get; set; } = null!;
        }

        public static class InterceptorSingletons
        {
            public static readonly ConnectionInterceptorProbe Connection = new();
        }

        public static class InterceptorRegistrationHelper
        {
            public static void Add(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { builder.AddInterceptors(new ConnectionInterceptorProbe()); }
        }
        """;

    private HostedProducerDiscovery<HostedProducerSite> InterceptorDiscover(
        string interceptors,
        string admitted,
        string early = "",
        string registration = "",
        string helpers = "",
        string contextMembers = "",
        string? onConfiguring = null,
        bool production = false)
    {
        return HookDiscover(
            SqliteIndexerUseSqlite + interceptors,
            early,
            contextMembers: contextMembers,
            onConfiguring: onConfiguring,
            registration: registration,
            helpers: InterceptorTypes + helpers,
            admitted: admitted,
            production: production);
    }

    private static bool CallbackTraversed(HostedProducerDiscovery<HostedProducerSite> result, string callback) =>
        result.Items.Any(site => site.Callee == "System.IO.File.Delete" && site.Member == callback);

    private static bool CallbackAdmitted(HostedProducerDiscovery<HostedProducerSite> result, string callback) =>
        result.Items.Any(site => site.Callee == "System.IO.File.Delete" && site.Member == callback && site.WorkFrontierCapsuleId is not null);

    // The matrix: which contract's callbacks an EF operation raises.
    [Theory]
    [InlineData("db.SaveChanges();", "ConnectionOpened", true)]
    [InlineData("db.SaveChanges();", "ReaderExecuting", true)]
    [InlineData("db.SaveChanges();", "SavingChanges", true)]
    [InlineData("db.SaveChanges();", "ConnectionCreated", true)]
    [InlineData("_ = db.Database.BeginTransaction();", "ConnectionOpened", true)]
    [InlineData("_ = db.Database.BeginTransaction();", "ReaderExecuting", false)]
    [InlineData("_ = db.Database.BeginTransaction();", "SavingChanges", false)]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.OpenConnection(db.Database);", "ConnectionOpened", true)]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.OpenConnection(db.Database);", "ReaderExecuting", false)]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.OpenConnection(db.Database);", "SavingChanges", false)]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.CloseConnection(db.Database);", "ConnectionOpened", true)]
    [InlineData("_ = db.Set<BlobRow>();", "ConnectionOpened", false)]
    [InlineData("_ = db.Set<BlobRow>();", "ReaderExecuting", false)]
    [InlineData("_ = db.Set<BlobRow>();", "SavingChanges", false)]
    public void AnAuthoredInterceptorCallbackIsTraversedOnlyAtTheEfOperationsThatRaiseItsContract(string statement, string callback, bool traversed)
    {
        output.WriteLine("INTERCEPTOR " + statement + " " + callback);

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".AddInterceptors(new ConnectionInterceptorProbe(), new CommandInterceptorProbe(), new SaveInterceptorProbe())",
            statement);

        Assert.Equal(traversed, CallbackTraversed(result, callback));

        // The operation is inside the admitted region, so the callback runs under the Work frontier held there.
        Assert.Equal(traversed, CallbackAdmitted(result, callback));
    }

    [Fact]
    public void AnAsyncInterceptorCallbackIsTraversedAtTheAwaitedEfOperation()
    {
        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".AddInterceptors(new ConnectionInterceptorProbe())",
            "await db.SaveChangesAsync(token);");

        Assert.True(CallbackTraversed(result, "ConnectionOpenedAsync"));

        Assert.True(CallbackAdmitted(result, "ConnectionOpenedAsync"));
    }

    [Fact]
    public void AnInterceptorCallbackOutsideTheAdmittedRegionIsTheMissingFrontierNotAHiddenOne()
    {
        // The same save before the work lease: the operation lacks its frontier, and so does the callback it raises.
        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".AddInterceptors(new ConnectionInterceptorProbe())",
            string.Empty,
            early: "db.SaveChanges(); ");

        Assert.True(CallbackTraversed(result, "ConnectionOpened"));

        Assert.False(CallbackAdmitted(result, "ConnectionOpened"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void ACommitRaisesTheTransactionCallbacksOfTheContextThatBeganTheTransaction()
    {
        const string interceptor = "public sealed class CommitInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor { public override System.Data.Common.DbTransaction TransactionStarted(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, System.Data.Common.DbTransaction result) { return result; } public override void TransactionCommitted(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData) { System.IO.File.Delete(\"transaction-committed\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".AddInterceptors(new CommitInterceptor())",
            "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = db.Database.BeginTransaction(); transaction.Commit();",
            helpers: interceptor);

        Assert.True(CallbackAdmitted(result, "TransactionCommitted"));

        // A save that never started a transaction is no commit: the same interceptor is not raised by a plain read.
        HostedProducerDiscovery<HostedProducerSite> none = InterceptorDiscover(".AddInterceptors(new CommitInterceptor())", "_ = db.Set<BlobRow>();", helpers: interceptor);

        Assert.False(CallbackTraversed(none, "TransactionCommitted"));
    }

    private const string AsyncCommitInterceptor = "public sealed class AsyncCommitInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor { public override System.Threading.Tasks.Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, System.Threading.CancellationToken cancellationToken = default) { System.IO.File.Delete(\"transaction-committed-async\"); return System.Threading.Tasks.Task.CompletedTask; } }"
        + "public static class TransactionInput { public static Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Value { get; set; } = null!; public static Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Facade { get; set; } = null!; }";

    [Theory]
    [InlineData("awaited-local", "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false); await transaction.CommitAsync(token);", true)]
    [InlineData("var-local", "var transaction = await db.Database.BeginTransactionAsync(token); await transaction.CommitAsync(token);", true)]
    [InlineData("facade-local", "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade facade = db.Database; await using var transaction = await facade.BeginTransactionAsync(token); await transaction.CommitAsync(token);", true)]
    [InlineData("declared-in-a-lambda", "await ((System.Func<System.Threading.Tasks.Task>)(async () => { await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false); await transaction.CommitAsync(token).ConfigureAwait(false); }))();", true)]
    [InlineData("reassigned-to-an-opaque-value", "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token); transaction = TransactionInput.Value; await transaction.CommitAsync(token);", false)]
    [InlineData("captured-and-rewritten", "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token); System.Action change = () => transaction = TransactionInput.Value; change(); await transaction.CommitAsync(token);", false)]
    [InlineData("declared-in-a-lambda-and-rewritten-by-a-nested-one", "await ((System.Func<System.Threading.Tasks.Task>)(async () => { Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false); System.Action change = () => transaction = TransactionInput.Value; change(); await transaction.CommitAsync(token).ConfigureAwait(false); }))();", false)]
    [InlineData("facade-reassigned-to-an-opaque-value", "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade facade = db.Database; facade = TransactionInput.Facade; var transaction = await facade.BeginTransactionAsync(token); await transaction.CommitAsync(token);", false)]
    public void ALocalHoldingATransactionOrFacadeIsFollowedToTheContextThatBeganIt(string shape, string statement, bool followed)
    {
        output.WriteLine("TRANSACTION " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".AddInterceptors(new AsyncCommitInterceptor())",
            statement,
            helpers: AsyncCommitInterceptor);

        // The commit raises the callbacks of the context that began the transaction only when that context can be named;
        // otherwise the interceptors EF would raise are unknown and the commit is refused. (Beginning the transaction
        // raises the transaction contract on its own, so the refusal of the commit is what tells the shapes apart.)
        if (followed)
        {
            Assert.True(CallbackAdmitted(result, "TransactionCommittedAsync"));
        }

        Assert.Equal(!followed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.CommitAsync", StringComparison.Ordinal)));
    }

    // ---- The interceptor set a registration closes.

    [Theory]
    [InlineData("helper", "; InterceptorRegistrationHelper.Add(options)", true)]
    [InlineData("array", ".AddInterceptors(new CommandInterceptorProbe(), new ConnectionInterceptorProbe())", true)]
    [InlineData("singleton", ".AddInterceptors(InterceptorSingletons.Connection)", true)]
    [InlineData("two-calls", ".AddInterceptors(new CommandInterceptorProbe()); options.AddInterceptors(new ConnectionInterceptorProbe())", true)]
    public void TheRegistrationClosesTheInterceptorSet(string shape, string options, bool traversed)
    {
        output.WriteLine("INTERCEPTOR " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(options, "db.SaveChanges();");

        Assert.Equal(traversed, CallbackAdmitted(result, "ConnectionOpened"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("opaque-value", ".AddInterceptors(InterceptorInput.Value)", "")]
    [InlineData("opaque-array", ".AddInterceptors(new ConnectionInterceptorProbe[] { InterceptorInput.Value })", "")]
    [InlineData("descriptor", "", "services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor, ConnectionInterceptorProbe>(); ")]
    public void AnInterceptorSetThatCannotBeClosedIsRefusedAtTheEfOperation(string shape, string options, string registration)
    {
        output.WriteLine("INTERCEPTOR " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(options, "db.SaveChanges();", registration: registration);

        // The save is still the database access it is, and the interceptors it would raise are refused as unknown.
        Assert.Contains(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == "Microsoft.EntityFrameworkCore.DbContext.SaveChanges");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInterceptorRegisteredOnAnotherContextIsNotRaisedByThisOne()
    {
        const string other = "public sealed class OtherInterceptorProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor { public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { System.IO.File.Delete(\"other-opened\"); } } "
            + "public sealed class OtherContext(Microsoft.EntityFrameworkCore.DbContextOptions<OtherContext> options) : Microsoft.EntityFrameworkCore.DbContext(options) { }";

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".AddInterceptors(new ConnectionInterceptorProbe())",
            "db.SaveChanges();",
            registration: "services.AddDbContext<OtherContext>((sp, other) => { Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(other, \"Data Source=:memory:\").AddInterceptors(new OtherInterceptorProbe()); }); ",
            helpers: other);

        Assert.True(CallbackAdmitted(result, "ConnectionOpened"));

        Assert.DoesNotContain(result.Items, static site => site.EnclosingType == "OtherInterceptorProbe");
    }

    [Fact]
    public void AnInterceptorTheContextsOnConfiguringAddsIsRaisedWhenThatHookCanRun()
    {
        const string onConfiguring = "builder.AddInterceptors(new ConnectionInterceptorProbe());";

        // The container-built instance is configured by the registration and returns before a guard; with no guard the
        // hook always runs and adds its interceptor to the set.
        HostedProducerDiscovery<HostedProducerSite> unguarded = InterceptorDiscover(string.Empty, "db.SaveChanges();", onConfiguring: onConfiguring);

        Assert.True(CallbackAdmitted(unguarded, "ConnectionOpened"));

        HostedProducerDiscovery<HostedProducerSite> guarded = InterceptorDiscover(string.Empty, "db.SaveChanges();", onConfiguring: "if (builder.IsConfigured) { return; } " + onConfiguring);

        Assert.False(CallbackTraversed(guarded, "ConnectionOpened"));

        Assert.DoesNotContain(guarded.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
    }

    // ---- A context the analysis allocated takes its interceptors from the options it was given.

    [Fact]
    public void ACreatedContextTakesItsInterceptorsFromTheOptionsChainItWasBuiltFrom()
    {
        const string created = "BlobContext created = new(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\").AddInterceptors(new ConnectionInterceptorProbe()).Options); created.SaveChanges();";

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, created);

        Assert.True(CallbackAdmitted(result, "ConnectionOpened"));
    }

    [Fact]
    public void ACreatedContextWithOptionsNobodyCanFollowIsRefusedAtTheEfOperation()
    {
        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, "BlobContext created = new(BlobInput.Options); created.SaveChanges();");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
    }

    [Fact]
    public void AGuardedFallbackOnlyAddsItsInterceptorToAnUnconfiguredCreatedContext()
    {
        const string onConfiguring = "if (builder.IsConfigured) { return; } builder.AddInterceptors(new ConnectionInterceptorProbe());";

        string configured = "BlobContext created = new(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\").Options); created.SaveChanges();";

        string unconfigured = "BlobContext created = new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>().Options); created.SaveChanges();";

        Assert.False(CallbackTraversed(InterceptorDiscover(string.Empty, configured, onConfiguring: onConfiguring), "ConnectionOpened"));

        Assert.True(CallbackAdmitted(InterceptorDiscover(string.Empty, unconfigured, onConfiguring: onConfiguring), "ConnectionOpened"));
    }

    // ---- The context a query is built from is every context any of its operands is built from.

    private const string CreatedUseSqlite = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\")";

    private const string AnotherContextWithProbe = "var other = new BlobContext(" + CreatedUseSqlite + ".AddInterceptors(new ConnectionInterceptorProbe()).Options); ";

    private const string OtherContextTypes = """
        public sealed class OtherCtx(Microsoft.EntityFrameworkCore.DbContextOptions<OtherCtx> options) : Microsoft.EntityFrameworkCore.DbContext(options)
        {
        }

        public static class OtherHolder
        {
            public static OtherCtx Make() => new OtherCtx(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<OtherCtx>(), "Data Source=:memory:").AddInterceptors(new ConnectionInterceptorProbe()).Options);
        }
        """;

    private const string SetHolderTypes = """
        public static class SetHolder
        {
            public static Microsoft.EntityFrameworkCore.DbSet<BlobRow> Rows { get; set; } = null!;
        }
        """;

    [Theory]
    [InlineData("second-operand", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Concat(db.Rows, other.Rows));")]
    [InlineData("first-operand", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Concat(other.Rows, db.Rows));")]
    [InlineData("zip-second-operand", "_ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Zip(db.Rows, other.Rows, (a, b) => a));")]
    [InlineData("local-holding-two-contexts", "var q = db.Rows; if (token.IsCancellationRequested) { q = other.Rows; } _ = System.Linq.Enumerable.ToList(q);")]
    [InlineData("builder-over-a-local", "var q = System.Linq.Queryable.Where(other.Rows, r => r.Id > 1); _ = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Concat(db.Rows, q));")]
    [InlineData("foreach-over-a-union", "foreach (BlobRow r in System.Linq.Enumerable.Concat(db.Rows, other.Rows)) { _ = r; }")]
    public void EveryContextAQueryIsBuiltFromRaisesItsCallbacks(string shape, string statement)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("INTERCEPTOR " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, AnotherContextWithProbe + statement, contextMembers: HookRows, production: production);

            Assert.True(CallbackAdmitted(result, "ConnectionOpened"));
        });
    }

    [Fact]
    public void AQueryOverTheRegisteredContextAloneRaisesNothingOfAnotherContext()
    {
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, AnotherContextWithProbe + "_ = System.Linq.Enumerable.ToList(db.Rows);", contextMembers: HookRows, production: production);

            Assert.False(CallbackTraversed(result, "ConnectionOpened"));
        });
    }

    [Fact]
    public void ASetPropertyWhoseGetterReadsAnotherContextIsRefused()
    {
        EachQueryMode(production =>
        {
            // The getter hands the read a set of a context the read does not name: the read could not be judged by its owner.
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                string.Empty,
                "_ = System.Linq.Enumerable.ToList(db.Rows);",
                contextMembers: "public Microsoft.EntityFrameworkCore.DbSet<BlobRow> Rows => OtherHolder.Make().Set<BlobRow>(); ",
                helpers: OtherContextTypes,
                production: production);

            Assert.True(QueryRefused(result), "The set property must be refused (" + (production ? "production" : "strict") + ").");

            // The twin: the context's own `Set<T>()` is the framework idiom, and is not refused.
            HostedProducerDiscovery<HostedProducerSite> idiom = InterceptorDiscover(string.Empty, "_ = System.Linq.Enumerable.ToList(db.Rows);", contextMembers: HookRows, helpers: OtherContextTypes, production: production);

            Assert.False(QueryRefused(idiom));
        });
    }

    [Fact]
    public void ADbSetOfAHolderThatIsNoContextIsRefusedBecauseItsContextCannotBeFollowed()
    {
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new ConnectionInterceptorProbe())",
                "_ = System.Linq.Enumerable.ToList(SetHolder.Rows);",
                contextMembers: HookRows,
                helpers: SetHolderTypes,
                production: production);

            // Strict mode stops at the unproven sequence; under the production regime only the EF analysis can.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Detail.StartsWith("System.Linq.Enumerable.ToList", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void AnImpliedSetOfASealedContextIsJudgedByThatContextAlone()
    {
        const string registration = "services.AddDbContext<OtherCtx>((sp, other) => { Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(other, \"Data Source=:memory:\").AddInterceptors(InterceptorInput.Value); }); ";

        EachQueryMode(production =>
        {
            // Another context in the program has an interceptor set nobody can close; the sealed context does not own it.
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new ConnectionInterceptorProbe())",
                "_ = db.CountRows();",
                registration: registration,
                helpers: OtherContextTypes,
                contextMembers: HookRows + "public int CountRows() => System.Linq.Enumerable.Count(Set<BlobRow>()); ",
                production: production);

            Assert.True(CallbackAdmitted(result, "ConnectionOpened"));

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Detail.Contains("OtherCtx", StringComparison.Ordinal));
        });
    }

    // ---- An interceptor set is closed only by values the analysis can name.

    private const string UntrustedSingletonTypes = """
        public sealed class BenignInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor { }

        public static class StaticConstructorFieldSingletons
        {
            public static readonly Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor Connection = new BenignInterceptor();

            static StaticConstructorFieldSingletons() { Connection = new ConnectionInterceptorProbe(); }
        }

        public static class StaticConstructorPropertySingletons
        {
            public static Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor Connection { get; } = new BenignInterceptor();

            static StaticConstructorPropertySingletons() { Connection = new ConnectionInterceptorProbe(); }
        }

        public static class PlainFieldSingletons
        {
            public static readonly Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor Connection = new ConnectionInterceptorProbe();
        }
        """;

    [Theory]
    [InlineData("list-argument", ".AddInterceptors(new System.Collections.Generic.List<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor> { new ConnectionInterceptorProbe() })")]
    [InlineData("empty-list-argument", ".AddInterceptors(new System.Collections.Generic.List<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>())")]
    [InlineData("enumerable-argument", ".AddInterceptors((System.Collections.Generic.IEnumerable<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>)new Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] { new ConnectionInterceptorProbe() })")]
    [InlineData("static-constructor-field", ".AddInterceptors(StaticConstructorFieldSingletons.Connection)")]
    [InlineData("static-constructor-property", ".AddInterceptors(StaticConstructorPropertySingletons.Connection)")]
    public void AnInterceptorSetBuiltFromAnythingButNamedInterceptorsIsRefusedAtTheEfOperation(string shape, string options)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("INTERCEPTOR " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(options, "db.SaveChanges();", helpers: UntrustedSingletonTypes, production: production);

            Assert.Contains(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == "Microsoft.EntityFrameworkCore.DbContext.SaveChanges");

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));

            Assert.False(CallbackTraversed(result, "ConnectionOpened"));
        });
    }

    [Fact]
    public void AStaticSingletonNoStaticConstructorCanReassignStaysTrusted()
    {
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(".AddInterceptors(PlainFieldSingletons.Connection)", "db.SaveChanges();", helpers: UntrustedSingletonTypes, production: production);

            Assert.True(CallbackAdmitted(result, "ConnectionOpened"));

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
        });
    }

    private const string CreatedUseSqliteOptions = CreatedUseSqlite + ".Options";

    private const string CreatedWithProbeOptions = CreatedUseSqlite + ".AddInterceptors(new ConnectionInterceptorProbe()).Options";

    [Theory]
    [InlineData("copy-of-a-followed-chain", "var inner = " + CreatedWithProbeOptions + "; var created = new BlobContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(inner).Options); created.SaveChanges();", true)]
    [InlineData("copy-then-more", "var inner = " + CreatedUseSqliteOptions + "; var created = new BlobContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(inner).AddInterceptors(new ConnectionInterceptorProbe()).Options); created.SaveChanges();", true)]
    [InlineData("copy-of-opaque-options", "var created = new BlobContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(BlobInput.Options).Options); created.SaveChanges();", false)]
    [InlineData("assigned-options", "var opts = " + CreatedUseSqliteOptions + "; opts = " + CreatedWithProbeOptions + "; var created = new BlobContext(opts); created.SaveChanges();", true)]
    [InlineData("deconstructed-options", "var opts = " + CreatedUseSqliteOptions + "; (opts, _) = (" + CreatedWithProbeOptions + ", 0); var created = new BlobContext(opts); created.SaveChanges();", false)]
    [InlineData("ref-aliased-options", "var opts = " + CreatedUseSqliteOptions + "; ref var alias = ref opts; alias = " + CreatedWithProbeOptions + "; var created = new BlobContext(opts); created.SaveChanges();", false)]
    [InlineData("out-written-options", "var opts = " + CreatedUseSqliteOptions + "; InterceptorOutputs.Write(out opts); var created = new BlobContext(opts); created.SaveChanges();", false)]
    public void TheOptionsAContextWasBuiltFromAreFollowedThroughEveryFormThatWritesThem(string shape, string statement, bool followed)
    {
        const string outputs = "public static class InterceptorOutputs { public static void Write(out Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) { options = BlobInput.Options; } }";

        EachQueryMode(production =>
        {
            output.WriteLine("INTERCEPTOR " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, statement, helpers: outputs, production: production);

            Assert.Equal(followed, CallbackAdmitted(result, "ConnectionOpened"));

            Assert.Equal(!followed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal)));
        });
    }

    // ---- An interceptor of a contract this analysis does not enter is refused, never ignored.

    private const string UnreviewedContractTypes = """
        public sealed class BindingProbe : Microsoft.EntityFrameworkCore.Diagnostics.IInstantiationBindingInterceptor
        {
            public Microsoft.EntityFrameworkCore.Metadata.InstantiationBinding ModifyBinding(Microsoft.EntityFrameworkCore.Diagnostics.InstantiationBindingInterceptionData interceptionData, Microsoft.EntityFrameworkCore.Metadata.InstantiationBinding binding) { System.IO.File.Delete("binding"); return binding; }
        }

        public sealed class IdentityProbe : Microsoft.EntityFrameworkCore.Diagnostics.IIdentityResolutionInterceptor
        {
            public void UpdateTrackedInstance(Microsoft.EntityFrameworkCore.Diagnostics.IdentityResolutionInterceptionData interceptionData, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry existingEntry, object newEntity) { System.IO.File.Delete("identity"); }
        }
        """;

    [Theory]
    [InlineData("instantiation-binding", ".AddInterceptors(new BindingProbe())", "_ = System.Linq.Enumerable.ToList(db.Rows);", "IInstantiationBindingInterceptor")]
    [InlineData("identity-resolution-query", ".AddInterceptors(new IdentityProbe())", "_ = System.Linq.Enumerable.ToList(db.Rows);", "IIdentityResolutionInterceptor")]
    [InlineData("identity-resolution-save", ".AddInterceptors(new IdentityProbe())", "db.SaveChanges();", "IIdentityResolutionInterceptor")]
    [InlineData("mixed-with-a-reviewed-contract", ".AddInterceptors(new ConnectionInterceptorProbe(), new BindingProbe())", "db.SaveChanges();", "IInstantiationBindingInterceptor")]
    public void AnInterceptorOfAContractTheAnalysisDoesNotEnterIsRefusedAtTheOperation(string shape, string options, string statement, string contract)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("INTERCEPTOR " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(options, statement, contextMembers: HookRows, helpers: UnreviewedContractTypes, production: production);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.Contains(contract, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void AnInstantiationBindingInterceptorIsRefusedAtTheFirstUseThatBuildsTheModel()
    {
        EachQueryMode(production =>
        {
            // The first use of the context's services builds the model, which raises the interceptor; no operation is involved.
            HostedProducerDiscovery<HostedProducerSite> built = InterceptorDiscover(".AddInterceptors(new BindingProbe())", "_ = db.Set<BlobRow>();", contextMembers: HookRows, helpers: UnreviewedContractTypes, production: production);

            Assert.Contains(built.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.Contains("IInstantiationBindingInterceptor", StringComparison.Ordinal)
                && diagnostic.Detail.Contains("builds the model", StringComparison.Ordinal));

            // The twin: a compiled model means EF never builds one, so the interceptor is never raised by a first use.
            HostedProducerDiscovery<HostedProducerSite> supplied = InterceptorDiscover(".UseModel(BlobInput.Model).AddInterceptors(new BindingProbe())", "_ = db.Set<BlobRow>();", contextMembers: HookRows, helpers: UnreviewedContractTypes, production: production);

            Assert.DoesNotContain(supplied.Diagnostics, static diagnostic => diagnostic.Detail.Contains("builds the model", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("attach", "db.Rows.Attach(new BlobRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Attach")]
    [InlineData("add", "db.Rows.Add(new BlobRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Add")]
    [InlineData("update", "db.Rows.Update(new BlobRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Update")]
    [InlineData("remove", "db.Rows.Remove(new BlobRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Remove")]
    [InlineData("entries", "_ = db.ChangeTracker.Entries();", "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.Entries")]
    public void AnOperationThatStartsTrackingIsRefusedWhileAnIdentityResolutionInterceptorExists(string shape, string statement, string callee)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("INTERCEPTOR " + shape + " " + (production ? "production" : "strict"));

            // The interceptor is raised when EF starts tracking an instance whose key is already tracked, which these
            // operations do and the analysis does not enter, so none of them is reviewed while one is authored.
            HostedProducerDiscovery<HostedProducerSite> refused = InterceptorDiscover(".AddInterceptors(new IdentityProbe())", statement, contextMembers: HookRows, helpers: UnreviewedContractTypes, production: production);

            Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith(callee, StringComparison.Ordinal));

            // The twin: with no identity-resolution interceptor authored the same operation stays reviewed.
            HostedProducerDiscovery<HostedProducerSite> reviewed = InterceptorDiscover(".AddInterceptors(new ConnectionInterceptorProbe())", statement, contextMembers: HookRows, production: production);

            Assert.DoesNotContain(reviewed.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith(callee, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void EveryInterceptorContractEfShipsIsEitherEnteredOrRefused()
    {
        // The set of contracts is read from the assemblies, so a contract a later EF version adds cannot be ignored.
        global::System.Reflection.Assembly[] assemblies =
        [
            typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly,
            typeof(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions).Assembly,
            typeof(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions).Assembly,
        ];

        Type marker = typeof(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor);

        string[] contracts =
        [
            .. assemblies.SelectMany(static assembly => assembly.GetExportedTypes())
                .Where(type => type.IsInterface && type != marker && marker.IsAssignableFrom(type))
                .Select(static type => type.FullName!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(contracts);

        foreach (string contract in contracts)
        {
            string verdict = HostedGrimoireProducerInventory.EfInterceptorContractVerdict(contract);

            output.WriteLine("CONTRACT " + contract + " " + verdict);

            Assert.True(verdict is "Entered" or "Refused" or "Marker", contract + " has no reviewed verdict.");
        }

        Assert.Equal("Entered", HostedGrimoireProducerInventory.EfInterceptorContractVerdict("Microsoft.EntityFrameworkCore.Diagnostics.IDbConnectionInterceptor"));

        Assert.Equal("Refused", HostedGrimoireProducerInventory.EfInterceptorContractVerdict("Microsoft.EntityFrameworkCore.Diagnostics.IInstantiationBindingInterceptor"));

        Assert.Equal("Refused", HostedGrimoireProducerInventory.EfInterceptorContractVerdict("Microsoft.EntityFrameworkCore.Diagnostics.IIdentityResolutionInterceptor"));
    }

    // ---- A singleton declared in another analysed compilation is read from that compilation.

    [Fact]
    public void ASingletonDeclaredInAnotherAnalysedCompilationIsReadFromItsOwnCompilation()
    {
        const string librarySource = """
            namespace Lib
            {
                public sealed class LibraryInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
                {
                    public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { System.IO.File.Delete("library-opened"); }
                }

                public static class LibrarySingletons
                {
                    public static readonly Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor Connection = new LibraryInterceptor();
                }
            }
            """;

        CSharpCompilation library = CSharpCompilation.Create(
            "InterceptorLibrary",
            [CSharpSyntaxTree.ParseText(librarySource, path: "src/Library.cs")],
            HostedGrimoireProducerInventory.ProductionCompilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api").References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(library.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite + ".AddInterceptors(Lib.LibrarySingletons.Connection)",
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: string.Empty,
            onConfiguring: null,
            early: string.Empty,
            pipelinePrefix: string.Empty,
            contextExpression: "db",
            helpers: string.Empty);

        const string pipelineCall = "await BlobPipeline.RunAsync(db, token); }";

        Assert.Contains(pipelineCall, source, StringComparison.Ordinal);

        CSharpCompilation consumer = CompileWithProductionReferencePack(source.Replace(pipelineCall, "db.SaveChanges(); }", StringComparison.Ordinal), "RetroDownfall.Arcanum.Api")
            .AddReferences(library.ToMetadataReference());

        Assert.Empty(consumer.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // The declaring tree belongs to the other compilation: reading it through the consumer's would throw.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [library, consumer], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.True(CallbackAdmitted(result, "ConnectionOpened"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
    }

    // ---- A context's disposal raises its closing and disposing callbacks where the context is disposed.

    private const string DisposeProbeTypes = """
        public sealed class DisposeProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionDisposed(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { System.IO.File.Delete("disposed"); }

            public override System.Threading.Tasks.Task ConnectionDisposedAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { System.IO.File.Delete("disposed-async"); return System.Threading.Tasks.Task.CompletedTask; }
        }
        """;

    private const string DisposableContext = "new BlobContext(" + CreatedUseSqlite + ".AddInterceptors(new DisposeProbe()).Options)";

    [Theory]
    [InlineData("explicit", "var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows); {gap} c2.Dispose();", "ConnectionDisposed")]
    [InlineData("explicit-async", "var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows); {gap} await c2.DisposeAsync();", "ConnectionDisposedAsync")]
    [InlineData("using-declaration", "using var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows); {gap}", "ConnectionDisposed")]
    [InlineData("await-using-declaration", "await using var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows); {gap}", "ConnectionDisposedAsync")]
    [InlineData("using-statement", "using (var c2 = " + DisposableContext + ") { _ = System.Linq.Enumerable.ToList(c2.Rows); {gap} }", "ConnectionDisposed")]
    public void ADisposedContextRaisesItsDisposalCallbacksUnderTheFrontiersLiveAtTheDisposal(string shape, string statement, string callback)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("DISPOSAL " + shape + " " + (production ? "production" : "strict"));

            // Disposed after the lease ended: the callback runs without the Work frontier it was admitted under at the query.
            HostedProducerDiscovery<HostedProducerSite> late = InterceptorDiscover(string.Empty, statement.Replace("{gap}", "lease.Dispose();", StringComparison.Ordinal), contextMembers: HookRows, helpers: DisposeProbeTypes, production: production);

            Assert.True(CallbackTraversed(late, callback));

            Assert.False(CallbackAdmitted(late, callback));

            Assert.Contains(late.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");

            // The twin disposes the context while the lease is held.
            HostedProducerDiscovery<HostedProducerSite> held = InterceptorDiscover(string.Empty, statement.Replace("{gap}", string.Empty, StringComparison.Ordinal), contextMembers: HookRows, helpers: DisposeProbeTypes, production: production);

            Assert.True(CallbackAdmitted(held, callback));

            Assert.DoesNotContain(held.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    private const string LeaseStatementPrefix = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate other = null!; if (!other.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out var admittedLease)) { return; } ";

    [Theory]
    [InlineData("using-declaration", "await using (var leased = admittedLease) { using var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows); }", "ConnectionDisposed")]
    [InlineData("await-using-declaration", "await using (var leased = admittedLease) { await using var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows); }", "ConnectionDisposedAsync")]
    [InlineData("await-using-scope", "await using (var leased = admittedLease) { " + ScopedContext + "await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(factory); var scoped = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<BlobContext>(scope.ServiceProvider); _ = System.Linq.Enumerable.ToList(scoped.Rows); }", "ConnectionDisposedAsync")]
    [InlineData("production-loop-shape", "while (true) { if (!other.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? admittedLoop)) { return; } await using (RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease leased = admittedLoop!) { " + ScopedContext + "await using Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope = factory.CreateAsyncScope(); var scoped = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<BlobContext>(scope.ServiceProvider); _ = System.Linq.Enumerable.ToList(scoped.Rows); } break; }", "ConnectionDisposedAsync")]
    public void AResourceDisposedAtTheEndOfTheBlockOfAnAwaitUsingLeaseIsDisposedInsideTheLease(string shape, string statement, string callback)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("DISPOSAL " + shape + " " + (production ? "production" : "strict"));

            // The resource is declared after the lease, so it is disposed first, while the lease is still held.
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(".AddInterceptors(new DisposeProbe())", string.Empty, early: LeaseStatementPrefix + statement, contextMembers: HookRows, helpers: DisposeProbeTypes, production: production);

            Assert.True(CallbackAdmitted(result, callback));

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    // The shape of the shipped retention sweep: a lease acquired in a loop, held by an await using statement, and a container
    // scope declared inside its block, so the scope (and the contexts it built) is disposed before the lease.
    private const string SweepLikeTypes = """
        public interface ISweepService { System.Threading.Tasks.Task ApplyAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, System.Threading.CancellationToken cancellationToken); }

        // The shipped service forwards the handle through an explicit interface implementation that returns the task of the
        // call it forwards to: the returned task is not the handle.
        internal sealed class SweepService : ISweepService
        {
            System.Threading.Tasks.Task ISweepService.ApplyAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, System.Threading.CancellationToken cancellationToken) => ApplyCoreAsync(lease, cancellationToken);

            internal async System.Threading.Tasks.Task ApplyCoreAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, System.Threading.CancellationToken cancellationToken)
            {
                await System.Threading.Tasks.Task.CompletedTask;
            }
        }

        internal sealed class SweepLike(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory, RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate admission)
        {
            internal async System.Threading.Tasks.Task RunOnceAsync(System.Threading.CancellationToken cancellationToken)
            {
                object? continuation = null;

                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!admission.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? admitted))
                        {
                            if (continuation is null)
                            {
                                return;
                            }

                            continue;
                        }

                        await using (RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted!)
                        {
                            if (continuation is null && cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            await using Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                            BlobContext context = scope.ServiceProvider.GetRequiredService<BlobContext>();

                            _ = System.Linq.Enumerable.ToList(context.Rows);

                            await scope.ServiceProvider.GetRequiredService<ISweepService>().ApplyAsync(lease, cancellationToken);
                        }

                        return;
                    }
                }
                finally
                {
                    _ = continuation;
                }
            }
        }
        """;

    [Fact]
    public void AContainerScopeDeclaredInsideAnAwaitUsingLeaseStatementIsDisposedInsideTheLease()
    {
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new DisposeProbe())",
                string.Empty,
                registration: "services.AddScoped<ISweepService, SweepService>(); ",
                early: "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory sweepFactory = null!; RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate sweepGate = null!; await new SweepLike(sweepFactory, sweepGate).RunOnceAsync(token); ",
                contextMembers: HookRows,
                helpers: DisposeProbeTypes + SweepLikeTypes,
                production: production);

            Assert.True(CallbackTraversed(result, "ConnectionDisposedAsync"));

            Assert.True(CallbackAdmitted(result, "ConnectionDisposedAsync"));

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    // The twin: the call the handle is forwarded to returns a task of the handle itself, which carries it out of the lease's
    // block, so nothing proves the lease is still held where the scope is disposed.
    private const string SweepCarryingTypes = """
        public interface ISweepCarrier { System.Threading.Tasks.Task<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease> ApplyAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, System.Threading.CancellationToken cancellationToken); }

        internal sealed class SweepCarrier : ISweepCarrier
        {
            System.Threading.Tasks.Task<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease> ISweepCarrier.ApplyAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, System.Threading.CancellationToken cancellationToken) => ApplyCoreAsync(lease, cancellationToken);

            internal async System.Threading.Tasks.Task<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease> ApplyCoreAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, System.Threading.CancellationToken cancellationToken)
            {
                await System.Threading.Tasks.Task.CompletedTask;

                return lease;
            }
        }

        internal sealed class SweepCarryingLike(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory, RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate admission)
        {
            internal async System.Threading.Tasks.Task RunOnceAsync(System.Threading.CancellationToken cancellationToken)
            {
                if (!admission.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? admitted))
                {
                    return;
                }

                await using (RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease = admitted!)
                {
                    await using Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                    BlobContext context = scope.ServiceProvider.GetRequiredService<BlobContext>();

                    _ = System.Linq.Enumerable.ToList(context.Rows);

                    _ = await scope.ServiceProvider.GetRequiredService<ISweepCarrier>().ApplyAsync(lease, cancellationToken);
                }
            }
        }
        """;

    [Fact]
    public void AHandleCarriedOutThroughTheTaskItIsForwardedToStillEndsTheLeaseForWhatIsDisposedAfterwards()
    {
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new DisposeProbe())",
                string.Empty,
                registration: "services.AddScoped<ISweepCarrier, SweepCarrier>(); ",
                early: "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory sweepFactory = null!; RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate sweepGate = null!; await new SweepCarryingLike(sweepFactory, sweepGate).RunOnceAsync(token); ",
                contextMembers: HookRows,
                helpers: DisposeProbeTypes + SweepCarryingTypes,
                production: production);

            Assert.True(CallbackTraversed(result, "ConnectionDisposedAsync"));

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    [Fact]
    public void AContainerScopeDisposedWhereNoContextIsRegisteredRaisesNothingAndIsNotRefusedForAnAuthoredLogger()
    {
        // The program authors a logger, which EF's events would reach; but no context is registered with the container, so
        // disposing a scope disposes none and no EF event is raised.
        const string logger = "public sealed class ProbeLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider { public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance; public void Dispose() { } }";

        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(
                "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes = null!; await using var scope = scopes.CreateAsyncScope();",
                logger,
                production);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.Contains("logger", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void AnOperationOnAContextRaisesNoDisposalCallbackBecauseNothingHasDisposedIt()
    {
        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                string.Empty,
                "var c2 = " + DisposableContext + "; _ = System.Linq.Enumerable.ToList(c2.Rows);",
                contextMembers: HookRows,
                helpers: DisposeProbeTypes,
                production: production);

            Assert.False(CallbackTraversed(result, "ConnectionDisposed"));

            Assert.False(CallbackTraversed(result, "ConnectionDisposedAsync"));
        });
    }

    private const string ScopedContext = "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory factory = null!; ";

    [Theory]
    [InlineData("await-using-scope", ScopedContext + "await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(factory); var scoped = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<BlobContext>(scope.ServiceProvider); _ = System.Linq.Enumerable.ToList(scoped.Rows); {gap}", "ConnectionDisposedAsync")]
    [InlineData("using-scope", ScopedContext + "using var scope = factory.CreateScope(); var scoped = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<BlobContext>(scope.ServiceProvider); _ = System.Linq.Enumerable.ToList(scoped.Rows); {gap}", "ConnectionDisposed")]
    [InlineData("explicit-scope-dispose", ScopedContext + "var scope = factory.CreateScope(); var scoped = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<BlobContext>(scope.ServiceProvider); _ = System.Linq.Enumerable.ToList(scoped.Rows); {gap} scope.Dispose();", "ConnectionDisposed")]
    public void AContainerScopeDisposalRaisesTheDisposalCallbacksOfTheContainerBuiltContexts(string shape, string statement, string callback)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("DISPOSAL " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> late = InterceptorDiscover(".AddInterceptors(new DisposeProbe())", statement.Replace("{gap}", "lease.Dispose();", StringComparison.Ordinal), contextMembers: HookRows, helpers: DisposeProbeTypes, production: production);

            Assert.True(CallbackTraversed(late, callback));

            Assert.False(CallbackAdmitted(late, callback));

            HostedProducerDiscovery<HostedProducerSite> held = InterceptorDiscover(".AddInterceptors(new DisposeProbe())", statement.Replace("{gap}", string.Empty, StringComparison.Ordinal), contextMembers: HookRows, helpers: DisposeProbeTypes, production: production);

            Assert.True(CallbackAdmitted(held, callback));
        });
    }

    // ---- Loggers.

    [Fact]
    public void AnAuthoredLoggerCanReceiveEfEventsSoTheOperationIsRefused()
    {
        const string logger = "public sealed class ProbeLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider { public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) { System.IO.File.Delete(\"logger-created\"); return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance; } public void Dispose() { } }";

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, "db.SaveChanges();", helpers: logger);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal)
            && diagnostic.Detail.Contains("logger", StringComparison.OrdinalIgnoreCase));

        // The twin: with no authored logger the same save carries no logger refusal.
        HostedProducerDiscovery<HostedProducerSite> none = InterceptorDiscover(string.Empty, "db.SaveChanges();");

        Assert.DoesNotContain(none.Diagnostics, static diagnostic => diagnostic.Detail.Contains("logger", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The context an EF event carries is the context EF was working for.

    private const string ContextReadingInterceptor = """
        public sealed class ContextReadingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { _ = eventData.Context!.Set<BlobRow>(); }
        }
        """;

    // An allocation whose options carry no compiled model: the universe of BlobContext no longer supplies one, so a
    // context nobody can follow is judged to run OnModelCreating unless something else says its first use was already judged.
    private const string UnsuppliedAllocation = "public static class UnsuppliedAllocation { public static BlobContext Make() => new(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\").Options); }";

    private bool ModelHookTraversed(string helpers)
    {
        // The registration supplies the compiled model, so the use of the registered context runs no model hook. Only a
        // use of a context nothing has proven supplied can: here, the context an event carries.
        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".UseModel(BlobInput.Model).AddInterceptors(new ContextReadingInterceptor())",
            "db.SaveChanges();",
            contextMembers: HookModelCreating,
            helpers: ContextReadingInterceptor + UnsuppliedAllocation + helpers);

        return result.Diagnostics.Any(static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal))
            || result.Items.Any(static site => site.Callee == "System.IO.File.Delete" && site.OperationId.Contains("/model@", StringComparison.Ordinal));
    }

    [Fact]
    public void TheContextOfAnEfEventIsNotAFirstUseWhenNoEventDataIsMadeByAnalysedSource()
    {
        // EF raised the event for a context it was working on, whose first use was judged where that work was done.
        Assert.False(ModelHookTraversed(string.Empty));

        // A method that merely shares a spelling with an event-data type is not a spelling of the type.
        Assert.False(ModelHookTraversed("public static class EventDataNames { public static void ConnectionEventData() { } public static void Use() { ConnectionEventData(); } }"));
    }

    [Theory]
    [InlineData("created", "public static class EventDataMaker { public static object Make() => new Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData(null!, null!, null); }")]
    [InlineData("derived", "public sealed class EventDataSubclass : Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData { public EventDataSubclass() : base(null!, null!, null!, null, default, default, default) { } }")]
    [InlineData("cast", "public static class EventDataCast { public static object? Cast(object value) => value as Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData; }")]
    [InlineData("typeof", "public static class EventDataAliasHost { public static System.Type Name => typeof(Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData); }")]
    public void AnAnalysedSourceThatSpellsAnEventDataTypeMakesTheContextReadAFirstUseAgain(string shape, string helpers)
    {
        output.WriteLine("EVENT " + shape);

        // The authored source could hand a context nothing has used to an event-data value, so the read is a use whose
        // hooks are judged.
        Assert.True(ModelHookTraversed(helpers));
    }

    [Fact]
    public void AnAuthoredSubclassOfTheEventDataBaseIsSpelledToo()
    {
        // EventData itself and every subclass outside DbContextEventData are event-data types: an authored subclass
        // overrides the virtual getters a callback reads, so the read is no longer a framework read.
        const string evil = "public sealed class EvilEventData : Microsoft.EntityFrameworkCore.Diagnostics.EventData { public EvilEventData() : base(null!, null!) { } public override Microsoft.Extensions.Logging.LogLevel LogLevel { get { System.IO.File.Delete(\"event-data\"); return default; } } }";

        EachQueryMode(production =>
        {
            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(string.Empty, "Microsoft.EntityFrameworkCore.Diagnostics.EventData data = new EvilEventData(); _ = data.LogLevel;", helpers: evil, production: production);

            bool traversed = result.Items.Any(static site => site.Callee == "System.IO.File.Delete");

            bool refused = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.Contains("EventData.LogLevel", StringComparison.Ordinal));

            Assert.True(traversed || refused, "The authored override must be entered or refused (" + (production ? "production" : "strict") + ").");
        });
    }

    [Fact]
    public void ALookalikeContextPropertyIsNotTheContextOfAnEfEvent()
    {
        const string lookalike = """
            public sealed class LookalikeEventData { public BlobContext Context { get; set; } = null!; }
            public sealed class LookalikeReadingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
            {
                public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { _ = new LookalikeEventData().Context.Set<BlobRow>(); }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".UseModel(BlobInput.Model).AddInterceptors(new LookalikeReadingInterceptor())",
            "db.SaveChanges();",
            contextMembers: HookModelCreating,
            helpers: lookalike + UnsuppliedAllocation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryEfEventDataTypeNameIsKnownToTheSpellingScan()
    {
        // The scan filters on the simple names of the types Entity Framework ships. A new event-data type is picked up
        // from the assemblies, so the set can never lag the framework.
        string[] names = [.. HostedGrimoireProducerInventory.EfEventDataNames];

        Assert.Contains("DbContextEventData", names);

        Assert.Contains("EventData", names);

        Assert.Contains("ConnectionEndEventData", names);

        Assert.Contains("CommandEventData", names);

        Assert.DoesNotContain("DbContext", names);
    }

    // ---- The shipped universe.

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_registrations_supply_the_compiled_model_so_the_model_hooks_cannot_run()
    {
        HostedGrimoireProducerInventory.EfRegistrationProbe probe = HostedGrimoireProducerInventory.ProbeEfRegistration(
            HostedGrimoireProducerInventory.ProductionCompilations, "RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext");

        // Both AddDbContext and AddDbContextPool configure the options through the shared configurator, whose provider
        // setup ends in UseModel(ArcanumDbContextModel.Instance): EF never builds the model, so OnModelCreating and
        // ConfigureConventions cannot run for a container-built context.
        Assert.True(probe.ModelSupplied);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_interceptor_set_is_closed_and_is_exactly_the_reviewed_enrolment_interceptor()
    {
        HostedGrimoireProducerInventory.EfRegistrationProbe probe = HostedGrimoireProducerInventory.ProbeEfRegistration(
            HostedGrimoireProducerInventory.ProductionCompilations, "RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext");

        Assert.True(probe.InterceptorsClosed);

        Assert.Equal(["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantConnectionEnrolmentInterceptor"], probe.Interceptors);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_never_spells_an_EF_event_data_type_outside_a_parameter_declaration()
    {
        // Every event-data value in the shipped source is therefore one Entity Framework raised, whose Context was used
        // where EF's work began.
        Assert.False(HostedGrimoireProducerInventory.FindSpelledEfEventData(HostedGrimoireProducerInventory.ProductionCompilations));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_declares_no_authored_logger_and_never_selects_a_logger_factory()
    {
        // EF reports to the application's loggers (Serilog, a framework provider). No authored type implements a logger
        // contract, and UseLoggerFactory, which is not a reviewed builder setting, is not used anywhere in the shipped source.
        Assert.Null(HostedGrimoireProducerInventory.FindAuthoredLoggerType(HostedGrimoireProducerInventory.ProductionCompilations));

        string root = global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();

        string[] offenders = [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains("UseLoggerFactory", StringComparison.Ordinal))];

        Assert.Empty(offenders);
    }
}
