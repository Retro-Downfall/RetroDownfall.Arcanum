using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Pure-data origins. A value computed from a call that was handed the lease is "derived from the lease" for the
    // purpose of its own lifetime: the checker follows it, and a disposable built from it counts as a possible carrier. That
    // is right for an object that could hold the lease, and wrong for a value that cannot: a closed pure-data type (exact
    // framework scalars, enums, sealed authored records, structs and classes whose every instance field is itself closed,
    // and constructed authored generics and immutable arrays of such) has no place to keep a lease or a group, so passing
    // one onward, however it was produced, neither escapes nor ends the admission. Every shape that is not closed keeps the
    // refusal it had.
    private sealed record PureDataShape(string Declarations, string Construct, string ReturnType, string Key);

    private static PureDataShape PureDataShapeOf(string shape) => shape switch
    {
        "sealed-record" => new("sealed record Paid(string Key, int Count);", "new Paid(key, 1)", "Paid", "paid.Key"),
        "readonly-record-struct" => new("readonly record struct Paid(string Key, System.Guid Id);", "new Paid(key, default)", "Paid", "paid.Key"),
        "nested-closed-record" => new("sealed record Inner(string Key, long Revision); sealed record Paid(Inner Inner, System.DateTimeOffset At);",
            "new Paid(new Inner(key, 1), default)", "Paid", "paid.Inner.Key"),
        "immutable-array-of-closed-record" => new("sealed record Row(string Key); sealed record Paid(System.Collections.Immutable.ImmutableArray<Row> Rows);",
            "new Paid(System.Collections.Immutable.ImmutableArray.Create(new Row(key)))", "Paid", "paid.Rows[0].Key"),
        "nullable-closed-struct" => new("readonly record struct Stamp(long Ticks); sealed record Paid(string Key, Stamp? At);",
            "new Paid(key, null)", "Paid", "paid.Key"),
        "value-tuple-of-closed" => new("sealed record Paid(string Key, (int Count, long Revision) Marks);", "new Paid(key, (1, 2))", "Paid", "paid.Key"),
        "sealed-class-of-scalars" => new("sealed class Paid(string key) { public string Key { get; } = key; public int Count { get; } = 1; }",
            "new Paid(key)", "Paid", "paid.Key"),
        "authored-generic-outcome" => new("sealed record Info(string Key); sealed record Outcome<T>(T Value, string Error);",
            "new Outcome<Info>(new Info(key), \"\")", "Outcome<Info>", "paid.Value.Key"),
        "record-with-object-field" => new("sealed record Paid(string Key, object? Tag);", "new Paid(key, null)", "Paid", "paid.Key"),
        "record-with-disposable-field" => new("sealed record Paid(string Key, System.IDisposable? Resource);", "new Paid(key, null)", "Paid", "paid.Key"),
        "record-with-lease-field" => new("sealed record Paid(string Key, RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? Lease);",
            "new Paid(key, null)", "Paid", "paid.Key"),
        "record-with-delegate-field" => new("sealed record Paid(string Key, System.Func<System.Threading.Tasks.Task>? Later);", "new Paid(key, null)", "Paid", "paid.Key"),
        "unsealed-class" => new("class Paid { public string Key { get; set; } = \"\"; }", "new Paid { Key = key }", "Paid", "paid.Key"),
        "interface-typed" => new("interface IPaid { string Key { get; } } sealed record PaidImpl(string Key) : IPaid;", "new PaidImpl(key)", "IPaid", "paid.Key"),
        "sealed-subclass-of-authored-base" => new("class PaidBase { public string Key { get; set; } = \"\"; } sealed class Paid : PaidBase { public int Count { get; set; } }",
            "new Paid { Key = key }", "Paid", "paid.Key"),
        "primary-constructor-capture-of-a-disposable" => new("sealed class Paid(string key, System.IDisposable? resource) { public string Key => key; public System.IDisposable? Resource => resource; }",
            "new Paid(key, null)", "Paid", "paid.Key"),
        "explicit-field-of-object" => new("sealed class Paid { private readonly object? _tag = null; public string Key { get; init; } = \"\"; public object? Tag => _tag; }",
            "new Paid { Key = key }", "Paid", "paid.Key"),
        "authored-lookalike-guid" => new("namespace System { public readonly struct Guid { public readonly object? Tag; } } sealed record Paid(string Key, System.Guid Id);",
            "new Paid(key, default)", "Paid", "paid.Key"),
        "authored-lookalike-immutable-array" => new("namespace System.Collections.Immutable { public readonly struct ImmutableArray<T> { public readonly object? Tag; } } sealed record Paid(string Key, System.Collections.Immutable.ImmutableArray<int> Rows);",
            "new Paid(key, default)", "Paid", "paid.Key"),
        "generic-outcome-over-object" => new("sealed record Outcome<T>(T Value, string Error);", "new Outcome<object>(key, key)", "Outcome<object>", "paid.Error"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private (HostedProducerDiscovery<HostedProducerSite> Result, string CommandCoordinate, string SendCoordinate) RunPureDataOrigins(string shape)
    {
        PureDataShape data = PureDataShapeOf(shape);

        // `Door` is the paid door: it is handed the lease and returns data. `Store` builds the command the data feeds and
        // uses it, as the Long Rest stores do with a command built from the parameters of an accepted result.
        string body = R2Admission
            + $"{data.ReturnType} paid = await Door.RunAsync(lease, \"unit\").ConfigureAwait(false); "
            + "await Store.CommitAsync(UnprovedSqliteInput.Value, paid).ConfigureAwait(false); "
            + "System.IO.File.Delete(\"after-commit\");";

        string extra = PremiseInputs + "\n" + data.Declarations + " "
            + "static class Door { internal static Task<" + data.ReturnType + "> RunAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease, string key) "
            + "{ if (lease is null) throw new ArgumentNullException(nameof(lease)); return Task.FromResult<" + data.ReturnType + ">(" + data.Construct + "); } } "
            + "static class Store { "
            + "internal static async Task CommitAsync(Microsoft.Data.Sqlite.SqliteConnection connection, " + data.ReturnType + " paid) { "
            + "await using Microsoft.Data.Sqlite.SqliteCommand command = Make(connection, " + data.Key + "); "
            + "_ = await command.ExecuteNonQueryAsync().ConfigureAwait(false); } "
            + "private static Microsoft.Data.Sqlite.SqliteCommand Make(Microsoft.Data.Sqlite.SqliteConnection connection, string text) "
            + "{ Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand(); command.CommandText = text; return command; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, extra), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = Assert.Single(compilation.SyntaxTrees).GetRoot();

        string tree = compilation.SyntaxTrees.Single().FilePath;

        InvocationExpressionSyntax command = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString().EndsWith("command.ExecuteNonQueryAsync", StringComparison.Ordinal));

        InvocationExpressionSyntax send = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedGrimoireProducerInventory.ProductionPremises, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"PURE_DATA_ORIGINS_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return (result, tree + ":" + command.SpanStart, tree + ":" + send.SpanStart);
    }

    [Theory]
    [InlineData("sealed-record")]
    [InlineData("readonly-record-struct")]
    [InlineData("nested-closed-record")]
    [InlineData("immutable-array-of-closed-record")]
    [InlineData("nullable-closed-struct")]
    [InlineData("value-tuple-of-closed")]
    [InlineData("sealed-class-of-scalars")]
    [InlineData("authored-generic-outcome")]
    public void ADataValueOfClosedPureDataKeepsTheWorkAndEscapesNothing(string shape)
    {
        (HostedProducerDiscovery<HostedProducerSite> result, string commandCoordinate, string sendCoordinate) = RunPureDataOrigins(shape);

        Assert.Empty(result.Diagnostics);

        HostedProducerSite command = Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.OperationId.EndsWith("/site@" + commandCoordinate, StringComparison.Ordinal));

        Assert.NotNull(command.WorkFrontierCapsuleId);

        HostedProducerSite send = Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.OperationId.EndsWith("/site@" + sendCoordinate, StringComparison.Ordinal));

        Assert.NotNull(send.WorkFrontierCapsuleId);

        Assert.NotNull(send.EffectFrontierCapsuleId);
    }

    [Theory]
    [InlineData("record-with-object-field")]
    [InlineData("record-with-disposable-field")]
    [InlineData("record-with-lease-field")]
    [InlineData("record-with-delegate-field")]
    [InlineData("unsealed-class")]
    [InlineData("interface-typed")]
    [InlineData("sealed-subclass-of-authored-base")]
    [InlineData("primary-constructor-capture-of-a-disposable")]
    [InlineData("explicit-field-of-object")]
    [InlineData("authored-lookalike-guid")]
    [InlineData("authored-lookalike-immutable-array")]
    [InlineData("generic-outcome-over-object")]
    public void ADataValueThatCouldHoldAHandleKeepsItsOriginAndTheRefusalItHad(string shape)
    {
        (HostedProducerDiscovery<HostedProducerSite> result, string commandCoordinate, _) = RunPureDataOrigins(shape);

        // The refusal is the one the command call had before this rule: a possible carrier handed to a framework callee.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
            && diagnostic.Identity.EndsWith("/call@" + commandCoordinate, StringComparison.Ordinal)
            && diagnostic.Detail.StartsWith("System.Data.Common.DbCommand.ExecuteNonQueryAsync;", StringComparison.Ordinal));
    }
}
