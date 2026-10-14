using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// A handler subscribed to DbConnection.StateChange is authored code nothing calls: the connection raises the event when it
// opens, closes or changes state. While every handler in the analysed source can be named the subscription is reviewed
// wiring, and the handlers are entered, under the frontiers live there, at the operations that change a connection's state:
// an authored Open or Close, and every EF operation that raises the connection contract. A handler that cannot be named
// leaves the subscription refused and every such operation refused with it. The data EF and the framework hand an event
// callback is read through getters of EF's own types, which run no authored code. Source analysis only; nothing here executes
// EF, SQL or a provider.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string ConnectionStateTypes = """
        public static class StateInput
        {
            public static void Handler(object? sender, System.Data.StateChangeEventArgs change) { System.IO.File.Delete("state-change"); }

            public static System.Data.StateChangeEventHandler? Opaque { get; set; }
        }

        public sealed class StateWiringInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { connection.StateChange += StateInput.Handler; }
        }
        """;

    private HostedProducerDiscovery<HostedProducerSite> StateDiscover(string admitted, string early = "", string options = "", string helpers = "", string prefix = "", params MetadataReference[] references)
    {
        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite + options,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: HookRows,
            onConfiguring: null,
            early: early,
            pipelinePrefix: string.Empty,
            contextExpression: "db",
            helpers: ConnectionStateTypes + helpers);

        const string pipelineCall = "await BlobPipeline.RunAsync(db, token); }";

        Assert.Contains(pipelineCall, source, StringComparison.Ordinal);

        return DiscoverSqliteIndexerProbe(prefix + source.Replace(pipelineCall, admitted + "}", StringComparison.Ordinal), references);
    }

    private static bool StateHandlerTraversed(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Items.Any(static site => site.Callee == "System.IO.File.Delete" && site.Member == "Handler");

    private static bool StateHandlerAdmitted(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Items.Any(static site => site.Callee == "System.IO.File.Delete" && site.Member == "Handler" && site.WorkFrontierCapsuleId is not null);

    private const string StateConnection = "System.Data.Common.DbConnection c = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); c.StateChange += StateInput.Handler; ";

    // ---- The handlers run where the connection changes state.

    [Theory]
    [InlineData("open", StateConnection + "c.Open();", true)]
    [InlineData("open-async", StateConnection + "await c.OpenAsync(token);", true)]
    [InlineData("close", StateConnection + "c.Close();", true)]
    [InlineData("close-async", StateConnection + "await c.CloseAsync();", true)]
    [InlineData("a-call-that-changes-no-state", StateConnection + "_ = c.CreateCommand();", false)]
    public void AHandlerOfConnectionStateChangeIsEnteredWhereTheConnectionChangesState(string shape, string admitted, bool traversed)
    {
        output.WriteLine("STATE " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(admitted);

        Assert.Equal(traversed, StateHandlerTraversed(result));

        // The operation is inside the admitted region, so the handler runs under the Work frontier held there.
        Assert.Equal(traversed, StateHandlerAdmitted(result));
    }

    [Fact]
    public void AHandlerOfConnectionStateChangeRunsUnderTheFrontiersLiveAtTheOperation()
    {
        // The same open before the work lease: the operation lacks its frontier, and so does the handler it runs.
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(string.Empty, early: StateConnection + "c.Open(); ");

        Assert.True(StateHandlerTraversed(result));

        Assert.False(StateHandlerAdmitted(result));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void ALambdaHandlerOfConnectionStateChangeIsEnteredToo()
    {
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            "System.Data.Common.DbConnection c = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); c.StateChange += (_, _) => { System.IO.File.Delete(\"lambda-state\"); }; c.Open();");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.WorkFrontierCapsuleId is not null);
    }

    [Fact]
    public void ARemovedSubscriptionIsReviewedWiringToo()
    {
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            "System.Data.Common.DbConnection c = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); c.StateChange += StateInput.Handler; c.StateChange -= StateInput.Handler; c.Open();");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Detail.Contains("StateChange", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEfOperationThatRaisesTheConnectionContractEntersTheHandlersToo()
    {
        // The interceptor subscribes the handler to the connection EF hands it, and the save is what opens that connection.
        HostedProducerDiscovery<HostedProducerSite> save = StateDiscover("db.SaveChanges();", options: ".AddInterceptors(new StateWiringInterceptor())");

        Assert.True(StateHandlerAdmitted(save));

        // The twin: a read of a DbSet opens no connection, so no handler runs at it.
        HostedProducerDiscovery<HostedProducerSite> read = StateDiscover("_ = db.Set<BlobRow>();", options: ".AddInterceptors(new StateWiringInterceptor())");

        Assert.False(StateHandlerTraversed(read));
    }

    // ---- A handler that cannot be named leaves the operations and the subscription refused.

    [Fact]
    public void AHandlerThatCannotBeNamedRefusesTheSubscriptionAndEveryOperationThatChangesState()
    {
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            "System.Data.Common.DbConnection c = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); c.StateChange += StateInput.Opaque; c.Open();");

        // The subscription is an external event whose handler is unknown, as it was before the lane existed.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.Contains("StateChange", StringComparison.Ordinal));

        // And the open cannot say what code it runs.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.Contains("handler of DbConnection.StateChange cannot be named", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreachedOpaqueSubscriptionStillOpensTheRegistryForEveryReachedOperation()
    {
        // The opaque subscription is in a helper no root reaches; the registry is whole-tree, so the reached open is
        // refused all the same, because that handler could be on this connection.
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            StateConnection + "c.Open();",
            helpers: "public static class StateUnreached { public static void Wire(System.Data.Common.DbConnection c) { c.StateChange += StateInput.Opaque; } }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.Contains("handler of DbConnection.StateChange cannot be named", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEventNamedStateChangeThatIsNotTheFrameworkEventIsNotAHandlerRegistry()
    {
        // An authored event with the same name is its own event, handled by the authored-event rules.
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            StateConnection + "c.Open(); StateOwner owner = new(); owner.StateChange += StateInput.Opaque;",
            helpers: "public sealed class StateOwner { public event System.Data.StateChangeEventHandler? StateChange { add { } remove { } } }");

        // The framework subscription stays named, so the open still enters its handler; the lookalike did not open the registry.
        Assert.True(StateHandlerAdmitted(result));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Detail.Contains("handler of DbConnection.StateChange cannot be named", StringComparison.Ordinal));
    }

    // ---- The data an event hands a callback.

    private const string EventDataInterceptor = """
        public sealed class EventDataInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionFailed(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionErrorEventData eventData)
            {
                _ = eventData.Exception;
                _ = eventData.Context;
                _ = eventData.IsAsync;
            }
        }
        """;

    private static bool EventDataRead(HostedProducerDiscovery<HostedProducerSite> result, string member) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.Contains(member, StringComparison.Ordinal));

    [Fact]
    public void AGetterOfTheDataEfRaisedAnEventWithIsReviewedWhileNoSourceMakesEventData()
    {
        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            "db.SaveChanges();",
            options: ".AddInterceptors(new EventDataInterceptor())",
            helpers: EventDataInterceptor);

        Assert.False(EventDataRead(result, "ConnectionErrorEventData.Exception"));

        Assert.False(EventDataRead(result, "DbContextEventData.Context"));

        Assert.False(EventDataRead(result, "ConnectionEventData.IsAsync"));

        // The twin: once analysed source spells an event-data type it could hand a callback an override, so the same
        // reads are no longer judged to return what EF stored.
        HostedProducerDiscovery<HostedProducerSite> refused = StateDiscover(
            "db.SaveChanges();",
            options: ".AddInterceptors(new EventDataInterceptor())",
            helpers: EventDataInterceptor + " public static class StateEventDataMaker { public static object Make() => new Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData(null!, null!, null); }");

        Assert.True(EventDataRead(refused, "ConnectionErrorEventData.Exception"));

        Assert.True(EventDataRead(refused, "DbContextEventData.Context"));
    }

    [Fact]
    public void AGetterOfALookalikeEventDataTypeFromAnotherAssemblyIsNotReviewed()
    {
        const string lookalikeSource = "namespace Microsoft.EntityFrameworkCore.Diagnostics { public class EventData { } public class DbContextEventData : EventData { public Microsoft.EntityFrameworkCore.DbContext? Context => null; } }";

        PortableExecutableReference lookalike = EmitSqliteIndexerLookalike("Microsoft.EntityFrameworkCore.Lookalike", lookalikeSource, "fake");

        HostedProducerDiscovery<HostedProducerSite> result = StateDiscover(
            "_ = StateLookalike.Value.Context;",
            helpers: "public static class StateLookalike { public static fake::Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData Value { get; set; } = null!; }",
            prefix: "extern alias fake;\n",
            references: lookalike);

        Assert.True(EventDataRead(result, "DbContextEventData.Context"));
    }

    [Fact]
    public void TheLoggerFactoryAnEventContextHoldsIsReadAsAFrameworkServiceAndAnythingElseIsNot()
    {
        const string logging = """
            public sealed class LoggingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
            {
                public override void ConnectionFailed(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionErrorEventData eventData) { _ = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(eventData.Context!); }
            }

            public sealed class ServiceInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
            {
                public override void ConnectionFailed(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionErrorEventData eventData) { _ = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<System.IServiceProvider>(eventData.Context!); }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> reviewed = StateDiscover("db.SaveChanges();", options: ".AddInterceptors(new LoggingInterceptor())", helpers: logging);

        Assert.False(EventDataRead(reviewed, "AccessorExtensions.GetService"));

        HostedProducerDiscovery<HostedProducerSite> refused = StateDiscover("db.SaveChanges();", options: ".AddInterceptors(new ServiceInterceptor())", helpers: logging);

        Assert.True(EventDataRead(refused, "AccessorExtensions.GetService"));
    }

    [Fact]
    public void ARemovalFromAWeakTableIsReviewedAndItsClearIsNot()
    {
        const string table = "public static class WeakTable { public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> Items = new(); public static void Drop(object key) { _ = Items.Remove(key); } public static void Wipe() { Items.Clear(); } }";

        HostedProducerDiscovery<HostedProducerSite> removal = StateDiscover("WeakTable.Drop(db);", helpers: table);

        Assert.False(EventDataRead(removal, "ConditionalWeakTable`2.Remove"));

        HostedProducerDiscovery<HostedProducerSite> clear = StateDiscover("WeakTable.Wipe();", helpers: table);

        Assert.True(EventDataRead(clear, "ConditionalWeakTable`2.Clear"));
    }

    // ---- The shipped universe.

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_subscribes_exactly_one_named_handler_to_connection_state_changes()
    {
        HostedGrimoireProducerInventory.ConnectionStateSubscription subscription = Assert.Single(
            HostedGrimoireProducerInventory.ProbeConnectionStateSubscriptions(HostedGrimoireProducerInventory.ProductionCompilations));

        // The enrolment interceptor releases a physical connection that is closed from outside EF, and nothing else
        // attaches to the event, so the registry the traversal reads is closed.
        Assert.Equal("CovenantConnectionEnrolmentInterceptor.cs", Path.GetFileName(subscription.Tree.FilePath));

        Assert.Equal("OnPhysicalStateChanged", subscription.Assignment.Right.ToString());
    }
}
