using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The DbProviderDispatch fixtures. The reviewed database support members and the database sites name framework members by identity, but most of
// them are virtual, abstract or non-virtual wrappers around a virtual slot of a type authored code can extend
// (DbConnection.CreateCommand calls CreateDbCommand, DbCommand.ExecuteReader calls ExecuteDbDataReader). A grant
// by name is therefore valid only where the receiver is closed: a sealed or non-extensible static type, a provenance
// that proves the exact framework runtime type, or the DbProviderTypesClosedByUse premise. Anything else is
// refused, so an authored subclass behind an abstract static type can no longer hide its overrides.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string DispatchInputs = """
        internal sealed class DispatchRow { public int Id { get; set; } }

        internal static class DispatchInput
        {
            internal static System.Data.Common.DbConnection Connection { get; set; } = null!;

            internal static System.Data.Common.DbCommand Command { get; set; } = null!;

            internal static System.Data.Common.DbDataReader Reader { get; set; } = null!;

            internal static System.Data.Common.DbTransaction Transaction { get; set; } = null!;

            internal static System.Data.Common.DbParameterCollection Parameters { get; set; } = null!;

            internal static System.Data.Common.DbParameter Parameter { get; set; } = null!;

            internal static System.Data.Common.DbConnectionStringBuilder Builder { get; set; } = null!;

            internal static Microsoft.Data.Sqlite.SqliteParameter SqliteParameter { get; set; } = null!;

            internal static Microsoft.Data.Sqlite.SqliteDataReader SqliteReader { get; set; } = null!;

            internal static Microsoft.Data.Sqlite.SqliteParameterCollection SqliteParameters { get; set; } = null!;

            internal static Microsoft.EntityFrameworkCore.DbContext Context { get; set; } = null!;

            internal static Microsoft.EntityFrameworkCore.DbSet<DispatchRow> Rows { get; set; } = null!;

            internal static Microsoft.EntityFrameworkCore.DbContextOptions<DispatchContext> Options { get; set; } = null!;
        }

        internal sealed class DispatchContext(Microsoft.EntityFrameworkCore.DbContextOptions<DispatchContext> options) : Microsoft.EntityFrameworkCore.DbContext(options)
        {
            public Microsoft.EntityFrameworkCore.DbSet<DispatchRow> Rows => Set<DispatchRow>();
        }
        """;

    // An authored provider type whose overrides run when the framework wrapper calls the virtual slot.
    private const string DispatchAuthoredProviders = """
        internal sealed class EvilCommand : System.Data.Common.DbCommand
        {
            protected override System.Data.Common.DbParameter CreateDbParameter() { System.IO.File.Delete("evil-create-parameter"); return null!; }

            protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior) { System.IO.File.Delete("evil-execute-reader"); return null!; }

            public override int ExecuteNonQuery() { System.IO.File.Delete("evil-execute"); return 0; }

            public override object? ExecuteScalar() => null;

            public override void Cancel() { }

            public override void Prepare() { }

            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;

            public override int CommandTimeout { get; set; }

            public override System.Data.CommandType CommandType { get; set; }

            public override bool DesignTimeVisible { get; set; }

            public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }

            protected override System.Data.Common.DbConnection? DbConnection { get; set; }

            protected override System.Data.Common.DbParameterCollection DbParameterCollection => null!;

            protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        }

        internal sealed class EvilConnection : System.Data.Common.DbConnection
        {
            protected override System.Data.Common.DbCommand CreateDbCommand() { System.IO.File.Delete("evil-create-command"); return null!; }

            protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => null!;

            public override void ChangeDatabase(string databaseName) { }

            public override void Close() { }

            public override void Open() { System.IO.File.Delete("evil-open"); }

            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ConnectionString { get; set; } = string.Empty;

            public override string Database => string.Empty;

            public override string DataSource => string.Empty;

            public override string ServerVersion => string.Empty;

            public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;
        }
        """;

    private HostedProducerDiscovery<HostedProducerSite> DispatchDiscover(string body, string extra = "", bool production = false)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + body + " await Task.CompletedTask;", DispatchInputs + "\n" + extra), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine("DISPATCH diag " + diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail);
        }

        return result;
    }

    private static bool DispatchRefused(HostedProducerDiscovery<HostedProducerSite> result, string callee) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith(callee, StringComparison.Ordinal));

    private static bool DispatchSite(HostedProducerDiscovery<HostedProducerSite> result, string callee) =>
        result.Items.Any(site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == callee);

    // Each row names the abstract framework member an authored subclass can intercept, the receiver the call goes
    // through, and whether the member is a database site (it is reported as one whatever the receiver is).
    [Theory]
    [InlineData("connection-create-command", "_ = DispatchInput.Connection.CreateCommand();", "System.Data.Common.DbConnection.CreateCommand", false)]
    [InlineData("connection-state", "_ = DispatchInput.Connection.State;", "System.Data.Common.DbConnection.State", false)]
    [InlineData("command-create-parameter", "_ = DispatchInput.Command.CreateParameter();", "System.Data.Common.DbCommand.CreateParameter", false)]
    [InlineData("command-commandtext", "_ = DispatchInput.Command.CommandText;", "System.Data.Common.DbCommand.CommandText", false)]
    [InlineData("command-parameters", "_ = DispatchInput.Command.Parameters;", "System.Data.Common.DbCommand.Parameters", false)]
    [InlineData("command-transaction", "DispatchInput.Command.Transaction = DispatchInput.Transaction;", "System.Data.Common.DbCommand.Transaction", false)]
    [InlineData("reader-get-string", "_ = DispatchInput.Reader.GetString(0);", "System.Data.Common.DbDataReader.GetString", false)]
    [InlineData("reader-get-int32", "_ = DispatchInput.Reader.GetInt32(0);", "System.Data.Common.DbDataReader.GetInt32", false)]
    [InlineData("reader-isdbnull", "_ = DispatchInput.Reader.IsDBNull(0);", "System.Data.Common.DbDataReader.IsDBNull", false)]
    [InlineData("reader-fieldcount", "_ = DispatchInput.Reader.FieldCount;", "System.Data.Common.DbDataReader.FieldCount", false)]
    [InlineData("parameters-add", "_ = DispatchInput.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter());", "System.Data.Common.DbParameterCollection.Add", false)]
    [InlineData("parameters-clear", "DispatchInput.Parameters.Clear();", "System.Data.Common.DbParameterCollection.Clear", false)]
    [InlineData("parameter-value", "_ = DispatchInput.Parameter.Value;", "System.Data.Common.DbParameter.Value", false)]
    [InlineData("parameter-name", "_ = DispatchInput.Parameter.ParameterName;", "System.Data.Common.DbParameter.ParameterName", false)]
    [InlineData("transaction-connection", "_ = DispatchInput.Transaction.Connection;", "System.Data.Common.DbTransaction.Connection", false)]
    [InlineData("builder-to-string", "_ = DispatchInput.Builder.ToString();", "System.Data.Common.DbConnectionStringBuilder.ToString", false)]
    [InlineData("sqlite-parameter-value", "_ = DispatchInput.SqliteParameter.Value;", "Microsoft.Data.Sqlite.SqliteParameter.Value", false)]
    [InlineData("command-execute-non-query", "_ = DispatchInput.Command.ExecuteNonQuery();", "System.Data.Common.DbCommand.ExecuteNonQuery", true)]
    [InlineData("command-execute-reader", "_ = DispatchInput.Command.ExecuteReader();", "System.Data.Common.DbCommand.ExecuteReader", true)]
    [InlineData("connection-open", "DispatchInput.Connection.Open();", "System.Data.Common.DbConnection.Open", true)]
    [InlineData("connection-begin-transaction", "_ = DispatchInput.Connection.BeginTransaction();", "System.Data.Common.DbConnection.BeginTransaction", true)]
    [InlineData("reader-read", "_ = DispatchInput.Reader.Read();", "System.Data.Common.DbDataReader.Read", true)]
    [InlineData("transaction-commit", "DispatchInput.Transaction.Commit();", "System.Data.Common.DbTransaction.Commit", true)]
    public void AReviewedDatabaseMemberOnAnOpaqueExtensibleReceiverIsRefused(string name, string statement, string callee, bool site)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement);

        output.WriteLine("DISPATCH " + name);

        // The call is judged where it is written, whatever authored subclass the receiver may hold.
        Assert.True(DispatchRefused(result, callee), name + " must be refused for an opaque receiver.");

        // A database site is a real access either way, so it is still reported as one.
        Assert.Equal(site, DispatchSite(result, callee));
    }

    // The receivers that stay closed. An exact framework allocation is its own runtime type, however the local is
    // declared; a static type no authored code can extend is the runtime type; a sealed context is exactly the
    // type its member lookup bound.
    [Theory]
    [InlineData("connection-create-command", "System.Data.Common.DbConnection r = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); _ = r.CreateCommand();", "System.Data.Common.DbConnection.CreateCommand")]
    [InlineData("connection-state", "System.Data.Common.DbConnection r = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); _ = r.State;", "System.Data.Common.DbConnection.State")]
    [InlineData("command-create-parameter", "System.Data.Common.DbCommand r = new Microsoft.Data.Sqlite.SqliteCommand(); _ = r.CreateParameter();", "System.Data.Common.DbCommand.CreateParameter")]
    [InlineData("command-commandtext", "System.Data.Common.DbCommand r = new Microsoft.Data.Sqlite.SqliteCommand(); _ = r.CommandText;", "System.Data.Common.DbCommand.CommandText")]
    [InlineData("command-parameters", "System.Data.Common.DbCommand r = new Microsoft.Data.Sqlite.SqliteCommand(); _ = r.Parameters;", "System.Data.Common.DbCommand.Parameters")]
    [InlineData("command-execute-non-query", "System.Data.Common.DbCommand r = new Microsoft.Data.Sqlite.SqliteCommand(); _ = r.ExecuteNonQuery();", "System.Data.Common.DbCommand.ExecuteNonQuery")]
    [InlineData("connection-open", "System.Data.Common.DbConnection r = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\"); r.Open();", "System.Data.Common.DbConnection.Open")]
    [InlineData("reader-get-string", "_ = DispatchInput.SqliteReader.GetString(0);", "Microsoft.Data.Sqlite.SqliteDataReader.GetString")]
    [InlineData("reader-fieldcount", "_ = DispatchInput.SqliteReader.FieldCount;", "Microsoft.Data.Sqlite.SqliteDataReader.FieldCount")]
    [InlineData("parameters-add", "_ = DispatchInput.SqliteParameters.AddWithValue(\"@a\", 1);", "Microsoft.Data.Sqlite.SqliteParameterCollection.AddWithValue")]
    [InlineData("parameter-value", "Microsoft.Data.Sqlite.SqliteParameter r = new(); _ = r.Value;", "Microsoft.Data.Sqlite.SqliteParameter.Value")]
    [InlineData("builder-to-string", "System.Data.Common.DbConnectionStringBuilder r = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(); _ = r.ToString();", "System.Data.Common.DbConnectionStringBuilder.ToString")]
    [InlineData("context-database", "DispatchContext r = new(DispatchInput.Options); _ = r.Database; _ = r.ChangeTracker; _ = r.Set<DispatchRow>();", "Microsoft.EntityFrameworkCore.DbContext.Set")]
    [InlineData("dbset-add", "DispatchContext r = new(DispatchInput.Options); _ = r.Rows.Add(new DispatchRow()); _ = r.Set<DispatchRow>().Remove(new DispatchRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Add")]
    public void AReviewedDatabaseMemberOnAClosedReceiverStaysClassified(string name, string statement, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement);

        output.WriteLine("DISPATCH " + name);

        Assert.False(DispatchRefused(result, callee), name + " must stay classified for a closed receiver.");
    }

    // The same members through a receiver that can hold an authored subclass, or through one that is exactly an
    // authored subclass, are refused: an override behind the wrapper is code no traversal reaches.
    [Theory]
    [InlineData("authored-command-create-parameter", "System.Data.Common.DbCommand r = new EvilCommand(); _ = r.CreateParameter();", "System.Data.Common.DbCommand.CreateParameter")]
    [InlineData("authored-command-execute-reader", "System.Data.Common.DbCommand r = new EvilCommand(); _ = r.ExecuteReader();", "System.Data.Common.DbCommand.ExecuteReader")]
    [InlineData("authored-connection-create-command", "System.Data.Common.DbConnection r = new EvilConnection(); _ = r.CreateCommand();", "System.Data.Common.DbConnection.CreateCommand")]
    [InlineData("authored-connection-open", "System.Data.Common.DbConnection r = new EvilConnection(); r.Open();", "System.Data.Common.DbConnection.Open")]
    [InlineData("unsealed-context-set", "Microsoft.EntityFrameworkCore.DbContext r = DispatchInput.Context; _ = r.Set<DispatchRow>();", "Microsoft.EntityFrameworkCore.DbContext.Set")]
    [InlineData("opaque-dbset-add", "_ = DispatchInput.Rows.Add(new DispatchRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Add")]
    public void AnAuthoredSubclassOrAnUnsealedContextBehindAReviewedMemberIsRefused(string name, string statement, string callee)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement, DispatchAuthoredProviders);

        output.WriteLine("DISPATCH " + name);

        Assert.True(DispatchRefused(result, callee), name + " must be refused.");
    }

    // The reviewer's probe: the override is behind a non-virtual wrapper, and the static type says nothing.
    [Fact]
    public void AnOverrideOfTheCreateDbCommandSlotBehindAnAbstractConnectionIsNeitherSilentNorAccepted()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover("System.Data.Common.DbConnection c = new EvilConnection(); _ = c.CreateCommand();", DispatchAuthoredProviders);

        Assert.True(DispatchRefused(result, "System.Data.Common.DbConnection.CreateCommand"));
    }

    // Disposal is not part of the by-name grant: the cleanup resolution proves the exact cleanup target of each resource,
    // so an override of Dispose behind an opaque or authored receiver is resolved or refused there, never skipped.
    [Theory]
    [InlineData("opaque-command-dispose", "System.Data.Common.DbCommand r = DispatchInput.Command; r.Dispose();", false)]
    [InlineData("opaque-connection-dispose", "System.Data.Common.DbConnection r = DispatchInput.Connection; r.Dispose();", false)]
    [InlineData("opaque-reader-dispose", "System.Data.Common.DbDataReader r = DispatchInput.Reader; r.Dispose();", false)]
    [InlineData("opaque-transaction-dispose", "System.Data.Common.DbTransaction r = DispatchInput.Transaction; r.Dispose();", false)]
    [InlineData("opaque-reader-dispose-async", "System.Data.Common.DbDataReader r = DispatchInput.Reader; await r.DisposeAsync();", false)]
    [InlineData("opaque-await-using", "await using System.Data.Common.DbCommand r = DispatchInput.Command;", false)]
    [InlineData("exact-await-using", "await using System.Data.Common.DbCommand r = new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\").CreateCommand();", true)]
    [InlineData("exact-reader-type-dispose", "DispatchInput.SqliteReader.Dispose();", true)]
    public void ADisposalBehindAProviderTypedReceiverIsResolvedOrRefused(string shape, string statement, bool resolved)
    {
        output.WriteLine("DISPATCH " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement);

        bool refused = result.Diagnostics.Any(static diagnostic => diagnostic.Code is "HOSTED_DISPOSAL_TARGET_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED");

        Assert.Equal(!resolved, refused);
    }

    // ---- A local is a closed receiver only when every write to it is a plain assignment the analysis can read.

    private const string DispatchWriters = """
        internal static class DispatchWriters
        {
            internal static void Replace(out System.Data.Common.DbConnection connection) { connection = new EvilConnection(); }
        }
        """;

    private const string DispatchSqlite = "new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\")";

    [Theory]
    [InlineData("plain-assignment", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; c = new EvilConnection(); _ = c.CreateCommand();")]
    [InlineData("tuple-deconstruction", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; (c, _) = (new EvilConnection(), 0); _ = c.CreateCommand();")]
    [InlineData("nested-tuple-deconstruction", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; int n; ((c, n), _) = ((new EvilConnection(), 0), 0); _ = c.CreateCommand();")]
    [InlineData("ref-local-alias", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; ref System.Data.Common.DbConnection r = ref c; r = new EvilConnection(); _ = c.CreateCommand();")]
    [InlineData("out-argument", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; DispatchWriters.Replace(out c); _ = c.CreateCommand();")]
    [InlineData("null-coalescing-assignment", "System.Data.Common.DbConnection? c = null; c ??= new EvilConnection(); _ = c.CreateCommand();")]
    public void ALocalWrittenByAnythingButAReadablePlainAssignmentIsNotAClosedReceiver(string shape, string statement)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("DISPATCH " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement, DispatchAuthoredProviders + DispatchWriters, production);

            Assert.True(DispatchRefused(result, "System.Data.Common.DbConnection.CreateCommand"), shape + " must be refused (" + (production ? "production" : "strict") + ").");
        });
    }

    [Theory]
    [InlineData("reassigned-closed", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; c = " + DispatchSqlite + "; _ = c.CreateCommand();")]
    [InlineData("branch-assigned-closed", "System.Data.Common.DbConnection c = " + DispatchSqlite + "; if (System.DateTime.UtcNow.Ticks > 0) { c = " + DispatchSqlite + "; } _ = c.CreateCommand();")]
    public void ALocalOnlyEverAssignedClosedValuesStaysAClosedReceiver(string shape, string statement)
    {
        output.WriteLine("DISPATCH " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement, DispatchAuthoredProviders + DispatchWriters);

        Assert.False(DispatchRefused(result, "System.Data.Common.DbConnection.CreateCommand"));
    }

    // ---- A provider-typed value handed to a reviewed member is judged by what it is, not by what it is declared as.

    private const string DispatchSqliteTypes = """
        internal sealed class EvilSqliteParameter : Microsoft.Data.Sqlite.SqliteParameter
        {
            public override string ParameterName { get => "@a"; set { System.IO.File.Delete("evil-sqlite-parameter"); } }
        }

        internal sealed class EvilSqliteConnection : Microsoft.Data.Sqlite.SqliteConnection
        {
            public override System.Data.ConnectionState State { get { System.IO.File.Delete("evil-sqlite-state"); return System.Data.ConnectionState.Open; } }
        }
        """;

    private const string DispatchSqliteCommand = "using var conn = " + DispatchSqlite + "; using var cmd = conn.CreateCommand(); ";

    [Theory]
    [InlineData("direct-argument", DispatchSqliteCommand + "cmd.Parameters.Add(new EvilSqliteParameter()); _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Add")]
    [InlineData("parameter-typed-local", DispatchSqliteCommand + "Microsoft.Data.Sqlite.SqliteParameter p = new EvilSqliteParameter(); cmd.Parameters.Add(p); _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Add")]
    [InlineData("object-typed-local", DispatchSqliteCommand + "object p = new EvilSqliteParameter(); cmd.Parameters.Add(p); _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Add")]
    [InlineData("connection-typed-local", DispatchSqliteCommand + "Microsoft.Data.Sqlite.SqliteConnection c = new EvilSqliteConnection(); cmd.Connection = c; _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteCommand.Connection")]
    public void AnAuthoredProviderSubclassHandedToAReviewedMemberThroughATypedLocalIsRefused(string shape, string statement, string callee)
    {
        EachQueryMode(production =>
        {
            output.WriteLine("DISPATCH " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement, DispatchSqliteTypes, production);

            Assert.True(DispatchRefused(result, callee), shape + " must be refused (" + (production ? "production" : "strict") + ").");
        });
    }

    [Theory]
    [InlineData("framework-parameter-local", DispatchSqliteCommand + "Microsoft.Data.Sqlite.SqliteParameter p = new Microsoft.Data.Sqlite.SqliteParameter(); cmd.Parameters.Add(p); _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Add")]
    [InlineData("framework-created-parameter", DispatchSqliteCommand + "Microsoft.Data.Sqlite.SqliteParameter p = cmd.CreateParameter(); cmd.Parameters.Add(p); _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Add")]
    [InlineData("framework-connection-local", DispatchSqliteCommand + "Microsoft.Data.Sqlite.SqliteConnection c = " + DispatchSqlite + "; cmd.Connection = c; _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteCommand.Connection")]
    [InlineData("string-argument", DispatchSqliteCommand + "string name = \"@a\"; cmd.Parameters.AddWithValue(name, 1); _ = cmd.ExecuteNonQuery();", "Microsoft.Data.Sqlite.SqliteParameterCollection.AddWithValue")]
    public void AFrameworkProviderValueHandedToAReviewedMemberStaysClassifiedWhileAnAuthoredSubclassExists(string shape, string statement, string callee)
    {
        output.WriteLine("DISPATCH " + shape);

        // The authored subclasses are in the tree, so an argument declared as their base could hold one; these cannot.
        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(statement, DispatchSqliteTypes);

        Assert.False(DispatchRefused(result, callee), shape + " must stay classified.");
    }

    [Fact]
    public void AnAuthoredSubclassArgumentIsRefusedBecauseTheFrameworkMemberWouldCallItsOverrides()
    {
        // The receiver is closed, but the framework collection reads the authored parameter's virtual members.
        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(
            "_ = DispatchInput.SqliteParameters.Add(new EvilParameter());",
            "internal sealed class EvilParameter : Microsoft.Data.Sqlite.SqliteParameter { public override string ParameterName { get => \"@a\"; set { System.IO.File.Delete(\"evil-parameter\"); } } }");

        Assert.True(DispatchRefused(result, "Microsoft.Data.Sqlite.SqliteParameterCollection.Add"));
    }
}
