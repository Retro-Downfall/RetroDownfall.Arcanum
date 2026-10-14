using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// EF hands its events to the interceptors registered on a context, at the operations that raise them: a connection
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
        string? onConfiguring = null)
    {
        return HookDiscover(
            SqliteIndexerUseSqlite + interceptors,
            early,
            contextMembers: contextMembers,
            onConfiguring: onConfiguring,
            registration: registration,
            helpers: InterceptorTypes + helpers,
            admitted: admitted);
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
