using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string PremiseInputs = """
        static class UnprovedSqliteInput
        {
            internal static Microsoft.Data.Sqlite.SqliteConnection Value { get; set; } = null!;

            internal static Microsoft.Data.Sqlite.SqliteCommand Command { get; set; } = null!;

            internal static System.Data.Common.DbConnection AbstractConnection { get; set; } = null!;

            internal static System.Data.Common.DbCommand AbstractCommand { get; set; } = null!;

            internal static System.Data.Common.DbTransaction AbstractTransaction { get; set; } = null!;

            internal static System.Data.Common.DbDataReader AbstractReader { get; set; } = null!;
        }
        """;

    private const string PremiseReceiverFactories = """
        static class PremiseFactory
        {
            internal static void Invoke(Microsoft.Data.Sqlite.SqliteConnection connection)
            {
                _ = connection.CreateCommand();
            }
        }

        sealed class PremiseBorrowed
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            internal PremiseBorrowed(Microsoft.Data.Sqlite.SqliteConnection connection)
            {
                _connection = connection;
            }

            internal Task RunAsync()
            {
                _ = _connection.CreateCommand();

                return Task.CompletedTask;
            }
        }
        """;

    private const string PremiseDisposals = """
        static class PremiseDisposal
        {
            internal static async Task RunAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken token)
            {
                await using System.Data.Common.DbCommand abstractFactory = connection.CreateCommand();

                await using Microsoft.Data.Sqlite.SqliteCommand typedFactory = connection.CreateCommand();

                await using Microsoft.Data.Sqlite.SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

                await using Microsoft.Data.Sqlite.SqliteCommand fromProperty = UnprovedSqliteInput.Command;

                await Task.CompletedTask;
            }
        }
        """;

    private const string PremiseAbstractTwins = """
        static class PremiseAbstract
        {
            internal static async Task RunAsync(CancellationToken token)
            {
                await using System.Data.Common.DbCommand command = UnprovedSqliteInput.AbstractCommand;

                await using System.Data.Common.DbTransaction transaction = UnprovedSqliteInput.AbstractTransaction;

                await using System.Data.Common.DbDataReader reader = UnprovedSqliteInput.AbstractReader;

                await using System.Data.Common.DbCommand viaAbstractConnection = UnprovedSqliteInput.AbstractConnection.CreateCommand();

                await Task.CompletedTask;
            }
        }
        """;

    private static CSharpCompilation PremiseFixture(string body, string extra, params MetadataReference[] additionalReferences)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, PremiseInputs + "\n" + extra), "RetroDownfall.Arcanum.Api");

        if (additionalReferences.Length != 0)
        {
            compilation = compilation.AddReferences(additionalReferences);
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static HostedProducerDiscovery<HostedProducerSite> PremiseStrict(CSharpCompilation compilation) =>
        HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

    private static HostedProducerDiscovery<HostedProducerSite> PremiseRequested(CSharpCompilation compilation, HostedProducerPremises premises = HostedProducerPremises.SqliteSealedByUse, int rootWorkers = 1) =>
        HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            premises, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], [], rootWorkers: rootWorkers);

    private static InvocationExpressionSyntax PremiseCreateCommand(SyntaxTree tree, string receiver) =>
        Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateCommand" } access
                && access.Expression.ToString() == receiver);

    private static VariableDeclaratorSyntax PremiseResource(SyntaxTree tree, string name) =>
        Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            declarator => declarator.Identifier.ValueText == name
                && declarator.Parent?.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 });

    private static bool PremiseUnclassified(HostedProducerDiscovery<HostedProducerSite> result, SyntaxTree tree, SyntaxNode call) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Identity == tree.FilePath + ":" + call.SpanStart
            && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");

    private static bool PremiseDisposalUnresolved(HostedProducerDiscovery<HostedProducerSite> result, SyntaxTree tree, VariableDeclaratorSyntax resource) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Identity.EndsWith("/dispose@" + tree.FilePath + ":" + resource.SpanStart, StringComparison.Ordinal));

    private static HostedProducerInventoryDiagnostic[] PremiseViolations(HostedProducerDiscovery<HostedProducerSite> result) =>
        [.. result.Diagnostics.Where(static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED")];

    private static HostedProducerInventoryDiagnostic[] PremiseWithoutViolations(HostedProducerDiscovery<HostedProducerSite> result) =>
        [.. result.Diagnostics.Where(static diagnostic => diagnostic.Code != "HOSTED_PREMISE_VIOLATED")];

    private static PortableExecutableReference PremiseLibrary(string name, string source)
    {
        CSharpCompilation pack = CompileWithProductionReferencePack("class Pack { }", "RetroDownfall.Arcanum.Api");

        CSharpCompilation library = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, path: "src/" + name + ".cs")],
            pack.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using MemoryStream image = new();

        EmitResult emitted = library.Emit(image);

        Assert.True(emitted.Success, string.Join(global::System.Environment.NewLine, emitted.Diagnostics));

        return MetadataReference.CreateFromImage(image.ToArray(), filePath: name + ".dll");
    }

    [Theory]
    [InlineData("parameter")]
    [InlineData("static-property")]
    [InlineData("borrowed-field")]
    public void SqliteSealedByUseClassifiesTheFactoryOnAnyReceiver(string shape)
    {
        string body = shape switch
        {
            "parameter" => "PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;",
            "static-property" => "_ = UnprovedSqliteInput.Value.CreateCommand(); await Task.CompletedTask;",
            _ => "await new PremiseBorrowed(UnprovedSqliteInput.Value).RunAsync();",
        };

        CSharpCompilation compilation = PremiseFixture(body, PremiseReceiverFactories);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        string receiver = shape switch
        {
            "parameter" => "connection",
            "static-property" => "UnprovedSqliteInput.Value",
            _ => "_connection",
        };

        InvocationExpressionSyntax factory = PremiseCreateCommand(tree, receiver);

        Microsoft.CodeAnalysis.Operations.IInvocationOperation call = Assert.IsAssignableFrom<Microsoft.CodeAnalysis.Operations.IInvocationOperation>(
            compilation.GetSemanticModel(tree).GetOperation(factory));

        Assert.True(call.IsVirtual && call.TargetMethod.IsVirtual);

        AssertSqliteVirtualProviderMetadata(compilation, call.TargetMethod.ContainingType);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        Assert.True(PremiseUnclassified(strict, tree, factory));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse), string.Join(global::System.Environment.NewLine, ledger.Violations));

        Assert.Empty(ledger.Violations);

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.Empty(premised.Diagnostics);

        Assert.Empty(premised.Items);
    }

    [Fact]
    public void SqliteSealedByUseResolvesTheSqliteTypedDeclarationDisposals()
    {
        CSharpCompilation compilation = PremiseFixture(R2Admission + "await PremiseDisposal.RunAsync(UnprovedSqliteInput.Value, token);", PremiseDisposals);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        VariableDeclaratorSyntax[] resources =
        [
            PremiseResource(tree, "abstractFactory"),
            PremiseResource(tree, "typedFactory"),
            PremiseResource(tree, "transaction"),
            PremiseResource(tree, "fromProperty"),
        ];

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        foreach (VariableDeclaratorSyntax resource in resources)
        {
            Assert.True(PremiseDisposalUnresolved(strict, tree, resource), "strict baseline: " + resource.Identifier.ValueText);
        }

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.Empty(PremiseViolations(premised));

        foreach (VariableDeclaratorSyntax resource in resources)
        {
            Assert.False(PremiseDisposalUnresolved(premised, tree, resource), "premised: " + resource.Identifier.ValueText);
        }

        foreach (InvocationExpressionSyntax factory in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateCommand" }))
        {
            Assert.False(PremiseUnclassified(premised, tree, factory));
        }

        // The one physical site is the admitted BeginTransaction, which carries its Work frontier; the other
        // two items are the admission frontiers of the fixture itself.
        Assert.Empty(premised.Diagnostics);

        HostedProducerSite begin = Assert.Single(premised.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess);

        Assert.Equal("Microsoft.Data.Sqlite.SqliteConnection.BeginTransaction", begin.Callee);

        Assert.NotNull(begin.WorkFrontierCapsuleId);

        Assert.All(premised.Items.Where(static site => site.Kind != HostedProducerSiteKind.DatabaseAccess), static site => Assert.Equal(HostedProducerSiteKind.EffectFrontier, site.Kind));
    }

    [Fact]
    public void SqliteSealedByUseNeverMapsAbstractDbTypesToSqlite()
    {
        CSharpCompilation compilation = PremiseFixture("await PremiseAbstract.RunAsync(token); await Task.CompletedTask;", PremiseAbstractTwins);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        foreach (string name in new[] { "command", "transaction", "reader", "viaAbstractConnection" })
        {
            Assert.True(PremiseDisposalUnresolved(strict, tree, PremiseResource(tree, name)), "strict baseline: " + name);
        }

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.Empty(PremiseViolations(premised));

        Assert.Equal(strict.Diagnostics, premised.Diagnostics);

        Assert.Equal(strict.Items, premised.Items);
    }

    [Theory]
    [InlineData("parameter")]
    [InlineData("disposal")]
    public void NoPremiseKeepsEveryRetainedSqliteRowIdentical(string shape)
    {
        CSharpCompilation compilation = shape == "parameter"
            ? PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories)
            : PremiseFixture("await PremiseDisposal.RunAsync(UnprovedSqliteInput.Value, token); await Task.CompletedTask;", PremiseDisposals);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        HostedProducerDiscovery<HostedProducerSite> none = PremiseRequested(compilation, HostedProducerPremises.None);

        Assert.NotEmpty(strict.Diagnostics);

        Assert.Equal(strict.Diagnostics, none.Diagnostics);

        Assert.Equal(strict.Items, none.Items);

        Assert.Empty(PremiseViolations(none));
    }

    [Theory]
    [InlineData("Microsoft.Data.Sqlite.SqliteConnection")]
    [InlineData("Microsoft.Data.Sqlite.SqliteCommand")]
    public void ASourceSubclassViolatesTheSealedByUsePremiseAndKeepsStrictBehavior(string baseType)
    {
        string derived = "sealed class PremiseDerived : " + baseType + " { }";

        CSharpCompilation compilation = PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories + "\n" + derived);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        InvocationExpressionSyntax factory = PremiseCreateCommand(tree, "connection");

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        Assert.True(PremiseUnclassified(strict, tree, factory));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        HostedProducerInventoryDiagnostic violation = Assert.Single(PremiseViolations(premised));

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("PremiseDerived", violation.Detail, StringComparison.Ordinal);

        Assert.Contains(baseType, violation.Detail, StringComparison.Ordinal);

        Assert.True(PremiseUnclassified(premised, tree, factory));

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));

        Assert.Equal(strict.Items, premised.Items);
    }

    [Fact]
    public void ASubclassKeepsTheSqliteTypedDisposalsStrict()
    {
        CSharpCompilation compilation = PremiseFixture(
            R2Admission + "await PremiseDisposal.RunAsync(UnprovedSqliteInput.Value, token);",
            PremiseDisposals + "\nsealed class PremiseDerivedCommand : Microsoft.Data.Sqlite.SqliteCommand { }");

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.NotEmpty(PremiseViolations(premised));

        foreach (string name in new[] { "abstractFactory", "typedFactory", "transaction", "fromProperty" })
        {
            VariableDeclaratorSyntax resource = PremiseResource(tree, name);

            Assert.True(PremiseDisposalUnresolved(strict, tree, resource), "strict baseline: " + name);

            Assert.True(PremiseDisposalUnresolved(premised, tree, resource), "premised: " + name);
        }

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));

        Assert.Equal(strict.Items, premised.Items);
    }

    [Fact]
    public void AnUnrelatedDbConnectionSubclassDoesNotViolateTheSealedByUsePremiseOrWidenIt()
    {
        string other = """
            sealed class PremiseOtherConnection : System.Data.Common.DbConnection
            {
                public override string ConnectionString { get; set; } = "";

                public override string Database => "";

                public override string DataSource => "";

                public override string ServerVersion => "";

                public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;

                public override void ChangeDatabase(string databaseName) { }

                public override void Close() { }

                public override void Open() { }

                protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();

                protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotSupportedException();
            }
            """;

        CSharpCompilation compilation = PremiseFixture("await PremiseAbstract.RunAsync(token); await Task.CompletedTask;", PremiseAbstractTwins + "\n" + other);

        Assert.Single(compilation.SyntaxTrees);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse), string.Join(global::System.Environment.NewLine, ledger.Violations));

        // Granting the premise still maps no abstract Db* type to Sqlite.
        Assert.Equal(PremiseStrict(compilation).Diagnostics, PremiseRequested(compilation).Diagnostics);
    }

    [Fact]
    public void SqliteSealedByUseRefusesAnAuthoredLookalikeOfTheProviderTypes()
    {
        string lookalikes = """
            namespace PremiseLookalike
            {
                public class SqliteConnection
                {
                    public virtual object CreateCommand()
                    {
                        System.IO.File.Delete("lookalike-factory");

                        return new object();
                    }
                }

                public class SqliteCommand : System.IAsyncDisposable
                {
                    public virtual ValueTask DisposeAsync()
                    {
                        System.IO.File.Delete("lookalike-cleanup");

                        return ValueTask.CompletedTask;
                    }
                }

                public static class Input
                {
                    public static SqliteConnection Connection { get; set; } = null!;

                    public static SqliteCommand Command { get; set; } = null!;
                }
            }

            static class PremiseLookalikeRun
            {
                internal static async Task RunAsync()
                {
                    _ = PremiseLookalike.Input.Connection.CreateCommand();

                    await using PremiseLookalike.SqliteCommand command = PremiseLookalike.Input.Command;

                    await Task.CompletedTask;
                }
            }
            """;

        CSharpCompilation compilation = PremiseFixture("await PremiseLookalikeRun.RunAsync();", lookalikes);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        // The lookalike bodies are authored and reachable, so their effects are reported either way.
        Assert.Contains(strict.Items, static site => site.Callee == "System.IO.File.Delete");

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.Equal(strict.Diagnostics, premised.Diagnostics);

        Assert.Equal(strict.Items, premised.Items);

        Assert.True(PremiseDisposalUnresolved(premised, tree, PremiseResource(tree, "command")) == PremiseDisposalUnresolved(strict, tree, PremiseResource(tree, "command")));
    }

    [Fact]
    public void SqliteSealedByUseRefusesASourceTypeThatImpersonatesTheProviderNamespace()
    {
        string impersonator = """
            namespace Microsoft.Data.Sqlite
            {
                public class SqliteCommand : System.IAsyncDisposable
                {
                    public virtual ValueTask DisposeAsync()
                    {
                        System.IO.File.Delete("impersonated-cleanup");

                        return ValueTask.CompletedTask;
                    }
                }
            }

            static class PremiseImpersonated
            {
                internal static async Task RunAsync()
                {
                    await using Microsoft.Data.Sqlite.SqliteCommand command = ImpersonatedInput.Command;

                    await Task.CompletedTask;
                }
            }

            static class ImpersonatedInput
            {
                internal static Microsoft.Data.Sqlite.SqliteCommand Command { get; set; } = null!;
            }
            """;

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source("await PremiseImpersonated.RunAsync();", impersonator), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        VariableDeclaratorSyntax command = PremiseResource(tree, "command");

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        // The impersonator is authored and unsealed with an unknown value, so strict leaves its cleanup unresolved.
        Assert.True(PremiseDisposalUnresolved(strict, tree, command));

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.True(PremiseDisposalUnresolved(premised, tree, command));

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));

        Assert.Equal(strict.Items, premised.Items);
    }

    [Fact]
    public void ANestedGenericIndirectSubclassViolatesTheSealedByUsePremise()
    {
        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", """
            static class PremiseOuter
            {
                internal class Middle<T> : Microsoft.Data.Sqlite.SqliteConnection { }

                internal sealed class Leaf : Middle<int> { }
            }
            """);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        Assert.Contains(ledger.Violations, static violation => violation.Code == "HOSTED_PREMISE_VIOLATED"
            && violation.Identity == "SqliteSealedByUse"
            && violation.Detail.Contains("PremiseOuter.Middle", StringComparison.Ordinal));

        Assert.Contains(ledger.Violations, static violation => violation.Detail.Contains("PremiseOuter.Leaf", StringComparison.Ordinal));
    }

    [Fact]
    public void ASubclassInAPortableExecutableReferenceViolatesTheSealedByUsePremise()
    {
        PortableExecutableReference library = PremiseLibrary("PremiseDerivedLibrary", "public sealed class PeDerivedConnection : Microsoft.Data.Sqlite.SqliteConnection { }");

        CSharpCompilation compilation = PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories, library);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        InvocationExpressionSyntax factory = PremiseCreateCommand(tree, "connection");

        Assert.NotNull(compilation.GetTypeByMetadataName("PeDerivedConnection"));

        Assert.Empty(compilation.GetTypeByMetadataName("PeDerivedConnection")!.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        HostedProducerInventoryDiagnostic violation = Assert.Single(PremiseViolations(premised));

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("PeDerivedConnection", violation.Detail, StringComparison.Ordinal);

        Assert.True(PremiseUnclassified(premised, tree, factory));

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));
    }

    [Fact]
    public void ALibraryThatReferencesAnUnresolvedSqliteAssemblyIsNotGranted()
    {
        PortableExecutableReference library = PremiseLibrary(
            "PremiseUnresolvedLibrary",
            "public sealed class PremiseHolder { public Microsoft.Data.Sqlite.SqliteConnection? Connection; }");

        // Only the framework assemblies: nothing here can resolve Microsoft.Data.Sqlite.
        MetadataReference[] framework =
        [
            .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(static path => Path.GetFileName(path) is { } file && (file.StartsWith("System.", StringComparison.Ordinal) || file is "netstandard.dll" or "mscorlib.dll"))
                .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)),
        ];

        CSharpCompilation unresolved = CSharpCompilation.Create(
            "PremiseUnresolvedFixture",
            [CSharpSyntaxTree.ParseText("class Empty { }", path: "src/Fixture.cs")],
            [.. framework, library],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([unresolved], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerInventoryDiagnostic violation = Assert.Single(ledger.Violations);

        Assert.Equal("HOSTED_PREMISE_VIOLATED", violation.Code);

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("PremiseUnresolvedLibrary", violation.Detail, StringComparison.Ordinal);

        Assert.Contains("unresolved", violation.Detail, StringComparison.OrdinalIgnoreCase);

        // The positive control resolves the same reference: nothing else changed, so resolution is what grants it.
        MetadataReference sqlite = MetadataReference.CreateFromFile(typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.Location);

        MetadataReference pclRaw = MetadataReference.CreateFromFile(typeof(SQLitePCL.raw).Assembly.Location);

        CSharpCompilation resolved = unresolved.AddReferences(sqlite, pclRaw);

        HostedProducerPremiseLedger control = HostedGrimoireProducerInventory.VerifyPremises([resolved], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(control.Holds(HostedProducerPremises.SqliteSealedByUse), string.Join(global::System.Environment.NewLine, control.Violations));
    }

    [Fact]
    public void AModuleReferenceIsUnresolvedAndKeepsStrictBehavior()
    {
        CSharpCompilation pack = CompileWithProductionReferencePack("class Pack { }", "RetroDownfall.Arcanum.Api");

        CSharpCompilation module = CSharpCompilation.Create(
            "PremiseModule",
            [CSharpSyntaxTree.ParseText("public sealed class ModuleDerivedConnection : Microsoft.Data.Sqlite.SqliteConnection { }", path: "src/PremiseModule.cs")],
            pack.References,
            new CSharpCompilationOptions(OutputKind.NetModule));

        using MemoryStream image = new();

        EmitResult emitted = module.Emit(image);

        Assert.True(emitted.Success, string.Join(global::System.Environment.NewLine, emitted.Diagnostics));

        MetadataReference moduleReference = ModuleMetadata.CreateFromImage(image.ToArray()).GetReference(display: "PremiseModule.netmodule");

        CSharpCompilation compilation = PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories, moduleReference);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        InvocationExpressionSyntax factory = PremiseCreateCommand(tree, "connection");

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        HostedProducerInventoryDiagnostic[] violations = PremiseViolations(premised);

        Assert.All(violations, static violation => Assert.Equal("SqliteSealedByUse", violation.Identity));

        // The module is itself unresolved, and the type it contributes to the assembly is also seen.
        Assert.Contains(violations, static violation => violation.Detail.Contains("PremiseModule.netmodule", StringComparison.Ordinal)
            && violation.Detail.Contains("unresolved", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(violations, static violation => violation.Detail.Contains("ModuleDerivedConnection", StringComparison.Ordinal));

        Assert.True(PremiseUnclassified(premised, tree, factory));

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));

        Assert.Equal(strict.Items, premised.Items);
    }

    [Fact]
    public void ThePremiseIsVerifiedOncePerDiscoveryAndSharedByEveryRootWorker()
    {
        CSharpCompilation compilation = PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories + "\nsealed class PremiseDerived : Microsoft.Data.Sqlite.SqliteConnection { }");

        int before = HostedGrimoireProducerInventory.PremiseVerifications;

        HostedProducerDiscovery<HostedProducerSite> single = PremiseRequested(compilation, rootWorkers: 1);

        Assert.Equal(before + 1, HostedGrimoireProducerInventory.PremiseVerifications);

        HostedProducerDiscovery<HostedProducerSite> parallel = PremiseRequested(compilation, rootWorkers: 3);

        Assert.Equal(before + 2, HostedGrimoireProducerInventory.PremiseVerifications);

        Assert.Single(PremiseViolations(single));

        Assert.Single(PremiseViolations(parallel));

        _ = PremiseRequested(compilation, HostedProducerPremises.None);

        Assert.Equal(before + 2, HostedGrimoireProducerInventory.PremiseVerifications);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_universe_satisfies_SqliteSealedByUse()
    {
        Assert.Equal(HostedProducerPremises.SqliteSealedByUse, HostedGrimoireProducerInventory.ProductionPremises);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises(
            HostedGrimoireProducerInventory.ProductionCompilations, HostedProducerPremises.SqliteSealedByUse);

        Assert.Empty(ledger.Violations);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void The_production_entry_runs_only_the_named_roots_under_the_production_premise()
    {
        HostedProducerDiscovery<HostedProducerSite> scoped = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, ["PidFileService"]);

        Assert.NotEmpty(scoped.Items);

        Assert.All(scoped.Items, static site => Assert.Equal("PidFileService", site.RootType));

        Assert.DoesNotContain(scoped.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");

        Assert.Empty(scoped.Diagnostics);

        HostedProducerDiscovery<HostedProducerSite> additional = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Additional, ["RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add"]);

        Assert.NotEmpty(additional.Items);

        Assert.All(additional.Items, static site => Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", site.RootType));

        Assert.DoesNotContain(additional.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");
    }

    [Fact]
    public void OnlyTheProductionEntryAndTheClosedWorldFixturesRequestPremises()
    {
        System.Reflection.MethodInfo[] discoveries = [.. typeof(HostedGrimoireProducerInventory)
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Where(static method => method.Name is "DiscoverProducerSites" or "DiscoverProductionSites")];

        Assert.NotEmpty(discoveries);

        Assert.All(discoveries, static method => Assert.DoesNotContain(method.GetParameters(), static parameter => parameter.ParameterType == typeof(HostedProducerPremises)));

        string root = global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();

        string tests = Path.Combine(root, "tests", "RetroDownfall.Arcanum.Tests");

        string[] allowed =
        [
            "HostedGrimoireProducerInventory.cs",
            "HostedGrimoireProducerInventory.Premises.cs",
            "HostedGrimoireProducerInventory.ProductionEntry.cs",
            "HostedGrimoireProducerInventoryTests.ClosedWorldPremises.cs",
        ];

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(tests, file);

            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            if (text.Contains("DiscoverProducerSitesUnderPremises(", StringComparison.Ordinal)
                || text.Contains("HostedProducerPremises.", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.Empty(offenders);
    }

    private static HostedProducerOperationEntry PremiseRoot(string type, string member) =>
        new(type + "." + member, "src/" + type + ".cs", type, member, HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.WorkspaceIndexing, null, []);

    [Fact]
    public void TheRootSubsetSelectsExactlyTheNamedRoots()
    {
        HostedProducerOperationEntry execute = PremiseRoot("A.First", "ExecuteAsync");

        HostedProducerOperationEntry stop = PremiseRoot("A.First", "StopAsync");

        HostedProducerOperationEntry other = PremiseRoot("A.Second", "ExecuteAsync");

        HostedProducerServiceEntry[] catalog = [new("First", [execute, stop]), new("Second", [other])];

        NonHostedProducerChainEntry[] chains =
        [
            new("A.Chain.Run", "src/A.Chain.cs", "A.Chain", "Run", HostedProducerAuthorityKind.FiniteRequest, "proof", []),
            new("A.Chain.Other", "src/A.Chain.cs", "A.Chain", "Other", HostedProducerAuthorityKind.FiniteRequest, "proof", []),
        ];

        HostedProducerDiscovery<string> registrations = new(["First", "Second"], []);

        ProductionRootSelection all = HostedGrimoireProducerInventory.SelectProductionRoots(null, registrations, catalog, chains);

        Assert.Same(registrations, all.Registrations);

        Assert.Same(catalog, all.Catalog);

        Assert.Same(chains, all.NonHostedCatalog);

        ProductionRootSelection service = HostedGrimoireProducerInventory.SelectProductionRoots(["First"], registrations, catalog, chains);

        Assert.Equal(["First"], service.Registrations.Items);

        HostedProducerServiceEntry kept = Assert.Single(service.Catalog);

        Assert.Equal(["A.First.ExecuteAsync", "A.First.StopAsync"], kept.Operations.Select(static operation => operation.OperationId));

        Assert.Empty(service.NonHostedCatalog);

        ProductionRootSelection operation = HostedGrimoireProducerInventory.SelectProductionRoots(["A.Second.ExecuteAsync", "A.Chain.Other"], registrations, catalog, chains);

        Assert.Equal(["Second"], operation.Registrations.Items);

        Assert.Equal("A.Second.ExecuteAsync", Assert.Single(Assert.Single(operation.Catalog).Operations).OperationId);

        Assert.Equal("A.Chain.Other", Assert.Single(operation.NonHostedCatalog).ChainId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Missing")]
    [InlineData("First,Typo")]
    public void ARootSubsetThatNamesNothingIsRefused(string subset)
    {
        HostedProducerServiceEntry[] catalog = [new("First", [PremiseRoot("A.First", "ExecuteAsync")])];

        string[] selectors = subset.Length == 0 ? [] : subset.Split(',');

        ArgumentException refused = Assert.Throws<ArgumentException>(() => HostedGrimoireProducerInventory.SelectProductionRoots(selectors, new(["First"], []), catalog, []));

        if (selectors.Length != 0)
        {
            Assert.Contains(selectors[^1], refused.Message, StringComparison.Ordinal);
        }
    }
}
