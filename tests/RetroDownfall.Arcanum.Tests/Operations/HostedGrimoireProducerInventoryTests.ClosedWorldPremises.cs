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

    private static PortableExecutableReference PremiseLibrary(string name, string source) =>
        PremiseLibrary(name, source, CompileWithProductionReferencePack("class Pack { }", "RetroDownfall.Arcanum.Api").References);

    // Every managed assembly of the shared runtime directory and nothing else: nothing here can resolve
    // Microsoft.Data.Sqlite, and the set is closed, so no framework type has an unresolved base. A subset of
    // the framework would leave unrelated unresolved bases, which the premise correctly refuses on their own.
    private static MetadataReference[] PremiseFrameworkReferences() =>
    [
        .. Directory.EnumerateFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
            .Where(static path =>
            {
                try
                {
                    _ = System.Reflection.AssemblyName.GetAssemblyName(path);

                    return true;
                }
                catch (BadImageFormatException)
                {
                    return false;
                }
            })
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];

    private static PortableExecutableReference PremiseLibrary(string name, string source, IEnumerable<MetadataReference> references)
    {
        CSharpCompilation library = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, path: "src/" + name + ".cs")],
            references,
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

        MetadataReference[] framework = PremiseFrameworkReferences();

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
    public void AnUnresolvedBaseTypeInAReferenceThatNeverNamesTheProviderStillRefusesThePremise()
    {
        MetadataReference[] framework = PremiseFrameworkReferences();

        PortableExecutableReference middle = PremiseLibrary("PremiseMiddle", "public class Middle { }", framework);

        PortableExecutableReference leaf = PremiseLibrary("PremiseLeaf", "public class Leaf : Middle { }", [.. framework, middle]);

        // The analysed compilation references the leaf but not the assembly that holds its base class, and
        // nothing about the leaf mentions the provider: only a scan of every reference can see that its
        // ancestry cannot be excluded from deriving from a provider class.
        CSharpCompilation unresolved = CSharpCompilation.Create(
            "PremiseUnresolvedBase",
            [CSharpSyntaxTree.ParseText("class Empty { }", path: "src/Fixture.cs")],
            [.. framework, leaf],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([unresolved], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerInventoryDiagnostic violation = Assert.Single(ledger.Violations);

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("Leaf", violation.Detail, StringComparison.Ordinal);

        Assert.Contains("unresolved base type", violation.Detail, StringComparison.Ordinal);

        // The positive control resolves the base's assembly: nothing else changed.
        CSharpCompilation resolved = unresolved.AddReferences(middle);

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
    public void ASubclassReachedOnlyThroughATypeForwardingFacadeViolatesTheSealedByUsePremise()
    {
        MetadataReference[] framework = PremiseFrameworkReferences();

        // The subclass is compiled against a facade that declares its own SqliteConnection. The analysed
        // compilation references a facade of the same identity that only forwards that name to the real
        // provider type, so the subclass's base resolves to the real SqliteConnection although the
        // subclass's own assembly names nothing but the facade.
        PortableExecutableReference compileTimeFacade = PremiseLibrary("PremiseFacade", "namespace Microsoft.Data.Sqlite { public class SqliteConnection { } }", framework);

        PortableExecutableReference evil = PremiseLibrary("PremiseEvil", "public sealed class EvilConnection : Microsoft.Data.Sqlite.SqliteConnection { }", [.. framework, compileTimeFacade]);

        PortableExecutableReference forwarder = PremiseLibrary("PremiseFacade", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Microsoft.Data.Sqlite.SqliteConnection))]");

        CSharpCompilation compilation = PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories, forwarder, evil);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        InvocationExpressionSyntax factory = PremiseCreateCommand(tree, "connection");

        // The shape under test: the base resolves to the real provider type, yet the subclass's assembly
        // never references Microsoft.Data.Sqlite, so a scan keyed on that reference could not see it.
        INamedTypeSymbol evilType = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("EvilConnection"));

        Assert.Empty(evilType.DeclaringSyntaxReferences);

        Assert.Equal("Microsoft.Data.Sqlite.SqliteConnection", evilType.BaseType!.ToDisplayString());

        Assert.Equal("Microsoft.Data.Sqlite", evilType.BaseType.ContainingAssembly.Name);

        Assert.DoesNotContain(
            evilType.ContainingAssembly.Modules.SelectMany(static module => module.ReferencedAssemblies),
            static identity => identity.Name == "Microsoft.Data.Sqlite");

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerInventoryDiagnostic violation = Assert.Single(ledger.Violations);

        Assert.Equal("HOSTED_PREMISE_VIOLATED", violation.Code);

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("EvilConnection", violation.Detail, StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.Single(PremiseViolations(premised));

        Assert.True(PremiseUnclassified(premised, tree, factory));

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));

        Assert.Equal(strict.Items, premised.Items);

        // The positive control keeps the facade and drops only the subclass: the subclass alone refuses the premise.
        CSharpCompilation control = PremiseFixture("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseReceiverFactories, forwarder);

        HostedProducerPremiseLedger granted = HostedGrimoireProducerInventory.VerifyPremises([control], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(granted.Holds(HostedProducerPremises.SqliteSealedByUse), string.Join(global::System.Environment.NewLine, granted.Violations));
    }

    [Theory]
    [InlineData("internal sealed class HiddenTopLevel : Microsoft.Data.Sqlite.SqliteConnection { }", "HiddenTopLevel")]
    [InlineData("public static class HiddenOuter { private sealed class PrivateNested : Microsoft.Data.Sqlite.SqliteCommand { } }", "PrivateNested")]
    [InlineData("public static class HiddenOuter { internal sealed class InternalNested : Microsoft.Data.Sqlite.SqliteConnection { } }", "InternalNested")]
    public void AnInaccessibleSubclassInAPortableExecutableReferenceViolatesTheSealedByUsePremise(string library, string subclass)
    {
        // An internal or private subclass is still a dispatch target: the library can hand an instance back
        // through a public member typed as the provider class.
        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", PremiseReceiverFactories, PremiseLibrary("PremiseHiddenLibrary", library));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        Assert.Contains(ledger.Violations, violation => violation.Code == "HOSTED_PREMISE_VIOLATED"
            && violation.Identity == "SqliteSealedByUse"
            && violation.Detail.Contains(subclass, StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceThatFailsToLoadIsUnresolvedAndKeepsStrictBehavior()
    {
        MetadataReference garbage = MetadataReference.CreateFromImage(new byte[8], filePath: "Garbage.dll");

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("PremiseFactory.Invoke(UnprovedSqliteInput.Value); await Task.CompletedTask;", PremiseInputs + "\n" + PremiseReceiverFactories),
            "RetroDownfall.Arcanum.Api").AddReferences(garbage);

        // The shape under test: the compiler reports the image and binds no symbol for it.
        Assert.Contains(compilation.GetDiagnostics(), static diagnostic => diagnostic.Id == "CS0009");

        Assert.Null(compilation.GetAssemblyOrModuleSymbol(garbage));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        InvocationExpressionSyntax factory = PremiseCreateCommand(tree, "connection");

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerInventoryDiagnostic violation = Assert.Single(ledger.Violations);

        Assert.Equal("HOSTED_PREMISE_VIOLATED", violation.Code);

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("Garbage.dll", violation.Detail, StringComparison.Ordinal);

        Assert.Contains("unresolved", violation.Detail, StringComparison.OrdinalIgnoreCase);

        HostedProducerDiscovery<HostedProducerSite> strict = PremiseStrict(compilation);

        HostedProducerDiscovery<HostedProducerSite> premised = PremiseRequested(compilation);

        Assert.Single(PremiseViolations(premised));

        Assert.True(PremiseUnclassified(premised, tree, factory));

        Assert.Equal(strict.Diagnostics, PremiseWithoutViolations(premised));
    }

    [Fact]
    public void ADuplicateOfABoundReferenceIsNotAnUnresolvedReference()
    {
        // Two different images of one assembly identity. The compiler binds one and silently ignores the
        // other, so analysed code can only ever see the bound one. The ignored image is readable and its
        // identity is exactly that of a loaded assembly: it is a duplicate, not an unresolved reference.
        PortableExecutableReference ignored = PremiseLibrary("PremiseDuplicate", "public class DuplicateIgnored { }");

        PortableExecutableReference bound = PremiseLibrary("PremiseDuplicate", "public class DuplicateBound { }");

        CSharpCompilation compilation = CompileWithProductionReferencePack("class Pack { }", "RetroDownfall.Arcanum.Api").AddReferences(ignored, bound);

        Assert.Empty(compilation.GetDiagnostics());

        Assert.Null(compilation.GetAssemblyOrModuleSymbol(ignored));

        Assert.NotNull(compilation.GetAssemblyOrModuleSymbol(bound));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse), string.Join(global::System.Environment.NewLine, ledger.Violations));
    }

    [Fact]
    public void AnUnboundImageThatIsNotAnExactDuplicateOfALoadedAssemblyIsUnresolved()
    {
        // The twin of the duplicate: the same simple name at another version is equivalent to the loaded
        // weak-named assembly for the compiler (CS1704), which binds nothing for the earlier image. It is
        // not an exact duplicate, and the premise accepts only an exact identity match, so it stays unresolved.
        PortableExecutableReference earlier = PremiseLibrary("PremiseDuplicate", "public class DuplicateEarlier { }");

        PortableExecutableReference otherVersion = PremiseLibrary(
            "PremiseDuplicate",
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")] public class DuplicateOther { }");

        CSharpCompilation compilation = CompileWithProductionReferencePack("class Pack { }", "RetroDownfall.Arcanum.Api").AddReferences(earlier, otherVersion);

        Assert.Contains(compilation.GetDiagnostics(), static diagnostic => diagnostic.Id == "CS1704");

        Assert.Null(compilation.GetAssemblyOrModuleSymbol(earlier));

        Assert.NotNull(compilation.GetAssemblyOrModuleSymbol(otherVersion));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));

        HostedProducerInventoryDiagnostic violation = Assert.Single(ledger.Violations);

        Assert.Equal("SqliteSealedByUse", violation.Identity);

        Assert.Contains("PremiseDuplicate", violation.Detail, StringComparison.Ordinal);

        Assert.Contains("unresolved", violation.Detail, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(HostedProducerPremises.SqliteSealedByUse | HostedProducerPremises.AuthoredEnumerablesAbsentByUse, HostedGrimoireProducerInventory.ProductionPremises);

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

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_universe_satisfies_AuthoredEnumerablesAbsentByUse()
    {
        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises(
            HostedGrimoireProducerInventory.ProductionCompilations, HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.Empty(ledger.Violations);

        Assert.True(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse));

        Assert.True(HostedGrimoireProducerInventory.ProductionPremises.HasFlag(HostedProducerPremises.AuthoredEnumerablesAbsentByUse));
    }

    private const string EnumerationPremiseInputs = """
        static class UnprovedEnumerableInput
        {
            internal static System.Collections.Generic.IEnumerable<int> Items { get; set; } = null!;

            internal static System.Collections.Generic.IReadOnlyList<int> List { get; set; } = null!;

            internal static System.Collections.Generic.IAsyncEnumerable<int> Async { get; set; } = null!;

            internal static System.Collections.ObjectModel.ReadOnlyCollection<int> Collection { get; set; } = null!;

            internal static System.Collections.Generic.IReadOnlyList<int> Never()
            {
                throw new System.NotSupportedException();
            }

            internal static async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> LoadAsync()
            {
                await System.Threading.Tasks.Task.CompletedTask;

                return List;
            }
        }
        """;

    private static CSharpCompilation EnumerationPremiseFixture(string body, string extra = "", params MetadataReference[] additionalReferences)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + body + " await Task.CompletedTask;", EnumerationPremiseInputs + "\n" + extra), "RetroDownfall.Arcanum.Api");

        if (additionalReferences.Length != 0)
        {
            compilation = compilation.AddReferences(additionalReferences);
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static bool EnumerationPremiseUnclassified(HostedProducerDiscovery<HostedProducerSite> result, CSharpCompilation compilation)
    {
        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode site = tree.GetRoot().DescendantNodes().First(static node => node is CommonForEachStatementSyntax or SpreadElementSyntax);

        return result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == tree.FilePath + ":" + site.SpanStart);
    }

    private static HostedProducerDiscovery<HostedProducerSite> EnumerationPremiseRequested(CSharpCompilation compilation) =>
        PremiseRequested(compilation, HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

    // Strict discovery refuses an interface-typed receiver nothing proves. Once the universe declares no
    // authored enumerable, the same receiver cannot dispatch to one and the production entry classifies it.
    [Theory]
    [InlineData("foreach (int item in UnprovedEnumerableInput.Items) { _ = item; }")]
    [InlineData("foreach (int item in UnprovedEnumerableInput.List) { _ = item; }")]
    [InlineData("int[] values = [.. UnprovedEnumerableInput.Items]; _ = values;")]
    [InlineData("await foreach (int item in UnprovedEnumerableInput.Async) { _ = item; }")]
    [InlineData("await foreach (int item in UnprovedEnumerableInput.Async.ConfigureAwait(false)) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.Select(UnprovedEnumerableInput.Items, value => value)) { _ = item; }")]
    [InlineData("int[] values = [.. await UnprovedEnumerableInput.LoadAsync()]; _ = values;")]
    [InlineData("foreach (int item in await UnprovedEnumerableInput.LoadAsync()) { _ = item; }")]
    [InlineData("foreach (int item in System.Linq.Enumerable.OrderBy(UnprovedEnumerableInput.Items, value => value)) { _ = item; }")]
    [InlineData("foreach (int item in UnprovedEnumerableInput.Never()) { _ = item; }")]
    [InlineData("int[] values = [.. UnprovedEnumerableInput.Never()]; _ = values;")]
    public void AuthoredEnumerablesAbsentClassifiesAnUnprovenFrameworkCollectionInterface(string statement)
    {
        CSharpCompilation compilation = EnumerationPremiseFixture(statement);

        Assert.True(EnumerationPremiseUnclassified(PremiseStrict(compilation), compilation));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse), string.Join(global::System.Environment.NewLine, ledger.Violations));

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.False(EnumerationPremiseUnclassified(premised, compilation));

        Assert.Empty(premised.Diagnostics);
    }

    // The negative twins: any authored enumeration member anywhere in the analysed source withholds the
    // premise, and the unproven receiver then stays refused, exactly as under strict discovery.
    [Theory]
    [InlineData("interface-enumerable", "sealed class Elsewhere : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new NotSupportedException(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }")]
    [InlineData("async-enumerable", "sealed class Elsewhere : System.Collections.Generic.IAsyncEnumerable<int> { public System.Collections.Generic.IAsyncEnumerator<int> GetAsyncEnumerator(System.Threading.CancellationToken token = default) => throw new NotSupportedException(); }")]
    [InlineData("pattern-only", "sealed class Elsewhere { public System.Collections.Generic.List<int>.Enumerator GetEnumerator() => default; }")]
    [InlineData("authored-enumerator", "struct Elsewhere : System.Collections.Generic.IEnumerator<int> { public bool MoveNext() => false; public int Current => 0; object System.Collections.IEnumerator.Current => Current; public void Reset() { } public void Dispose() { } }")]
    [InlineData("authored-interface", "interface IElsewhere : System.Collections.Generic.IEnumerable<int> { }")]
    [InlineData("default-interface-member", "interface IElsewhere { System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new NotSupportedException(); }")]
    [InlineData("extension-enumerator", "static class Extensions { public static System.Collections.Generic.IEnumerator<int> GetEnumerator(this Elsewhere value) => throw new NotSupportedException(); } sealed class Elsewhere { }")]
    [InlineData("framework-derived", "sealed class Elsewhere : System.Collections.Generic.List<int> { }")]
    [InlineData("nested", "static class Outer { internal sealed class Elsewhere : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException(); } }")]
    public void AuthoredEnumerablesAbsentIsWithheldWhenTheUniverseDeclaresAnEnumerationMember(string shape, string declaration)
    {
        CSharpCompilation compilation = EnumerationPremiseFixture("foreach (int item in UnprovedEnumerableInput.Items) { _ = item; }", declaration);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse), shape);

        Assert.NotEmpty(ledger.Violations);

        Assert.All(ledger.Violations, static violation =>
        {
            Assert.Equal("HOSTED_PREMISE_VIOLATED", violation.Code);

            Assert.Equal("AuthoredEnumerablesAbsentByUse", violation.Identity);
        });

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.NotEmpty(PremiseViolations(premised));

        Assert.True(EnumerationPremiseUnclassified(premised, compilation));

        Assert.Equal(PremiseStrict(compilation).Diagnostics, PremiseWithoutViolations(premised));
    }

    [Fact]
    public void AuthoredEnumerablesAbsentIsWithheldForAnUnresolvedSourceBaseType()
    {
        CSharpCompilation unresolved = CSharpCompilation.Create(
            "PremiseUnresolvedEnumerable",
            [CSharpSyntaxTree.ParseText("sealed class Elsewhere : MissingBase { }", path: "src/Fixture.cs")],
            PremiseFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([unresolved], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse));

        Assert.Contains("unresolved base type", Assert.Single(ledger.Violations).Detail, StringComparison.Ordinal);

        CSharpCompilation resolved = CSharpCompilation.Create(
            "PremiseResolvedEnumerable",
            [CSharpSyntaxTree.ParseText("class MissingBase { } sealed class Elsewhere : MissingBase { }", path: "src/Fixture.cs")],
            PremiseFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        HostedProducerPremiseLedger control = HostedGrimoireProducerInventory.VerifyPremises([resolved], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.True(control.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse), string.Join(global::System.Environment.NewLine, control.Violations));
    }

    // The indexer of a framework read-only list is the framework's own once the universe declares no authored
    // enumerable. Strict discovery refuses the unproven receiver, the premised run classifies it, and a universe that
    // declares an authored list withholds the premise so that the indexer stays refused.
    [Fact]
    public void AuthoredEnumerablesAbsentClassifiesTheIndexerOfAnUnprovenReadOnlyList()
    {
        static bool IndexerUnclassified(HostedProducerDiscovery<HostedProducerSite> result) =>
            result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");

        CSharpCompilation compilation = EnumerationPremiseFixture("_ = UnprovedEnumerableInput.List[0];");

        Assert.True(IndexerUnclassified(PremiseStrict(compilation)));

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.Empty(PremiseViolations(premised));

        Assert.False(IndexerUnclassified(premised));

        Assert.Empty(premised.Diagnostics);

        const string authoredList = "sealed class Elsewhere : System.Collections.Generic.IReadOnlyList<int> { public int this[int index] { get { System.IO.File.Delete(\"indexer-effect\"); return 0; } } public int Count => 0; public System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new NotSupportedException(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException(); }";

        CSharpCompilation withheld = EnumerationPremiseFixture("_ = UnprovedEnumerableInput.List[0];", authoredList);

        HostedProducerDiscovery<HostedProducerSite> refused = EnumerationPremiseRequested(withheld);

        Assert.NotEmpty(PremiseViolations(refused));

        Assert.True(IndexerUnclassified(refused));
    }

    // The premise is about interface-typed receivers only. A concrete framework type outside the closed set keeps
    // its own members, and an interface that merely shares a framework interface's name is not that interface.
    [Fact]
    public void AuthoredEnumerablesAbsentDoesNotCoverAConcreteFrameworkTypeOutsideTheClosedSet()
    {
        CSharpCompilation compilation = EnumerationPremiseFixture("foreach (int item in UnprovedEnumerableInput.Collection) { _ = item; }");

        Assert.True(EnumerationPremiseUnclassified(PremiseStrict(compilation), compilation));

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.Empty(PremiseViolations(premised));

        Assert.True(EnumerationPremiseUnclassified(premised, compilation));
    }

    [Fact]
    public void AuthoredEnumerablesAbsentDoesNotCoverAnInterfaceFromAnotherAssemblyThatSharesAFrameworkName()
    {
        CSharpCompilation foreign = CompileWithProductionReferencePack(
            "namespace System.Linq { public interface IGrouping<TKey, TElement> : System.Collections.Generic.IEnumerable<TElement> { TKey Key { get; } } } namespace Unreviewed { public static class ForeignFactory { public static System.Linq.IGrouping<int, int> Make() => null!; } }",
            "RetroDownfall.Arcanum.Api").WithAssemblyName("Unreviewed.Linq");

        using MemoryStream image = new();

        Microsoft.CodeAnalysis.Emit.EmitResult emitted = foreign.Emit(image);

        Assert.True(emitted.Success, string.Join(global::System.Environment.NewLine, emitted.Diagnostics));

        CSharpCompilation compilation = EnumerationPremiseFixture("foreach (int item in Unreviewed.ForeignFactory.Make()) { _ = item; }", string.Empty, MetadataReference.CreateFromImage(image.ToArray()));

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.Empty(PremiseViolations(premised));

        Assert.True(EnumerationPremiseUnclassified(premised, compilation));
    }

    [Fact]
    public void AuthoredEnumerablesAbsentAndSqliteSealedByUseAreGrantedIndependently()
    {
        CSharpCompilation compilation = EnumerationPremiseFixture("foreach (int item in UnprovedEnumerableInput.Items) { _ = item; }", "sealed class Elsewhere : System.Collections.Generic.IEnumerable<int> { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new NotSupportedException(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }");

        HostedProducerPremiseLedger both = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse | HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.True(both.Holds(HostedProducerPremises.SqliteSealedByUse));

        Assert.False(both.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse));

        Assert.Equal(HostedProducerPremises.SqliteSealedByUse, both.Granted);

        Assert.NotEmpty(both.Violations);

        HostedProducerPremiseLedger onlySqlite = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.SqliteSealedByUse);

        Assert.False(onlySqlite.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse));

        Assert.Empty(onlySqlite.Violations);
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

    // ---------------------------------------------------------------------------------------------------
    // C# 14 extension blocks declare enumeration members that Roslyn exposes only as a nested extension type and
    // an implicitly declared implementation method, so the premise scan reads both.

    [Theory]
    [InlineData("extension-block-enumerator", "static class Extensions { extension(Elsewhere value) { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new NotSupportedException(); } } sealed class Elsewhere { }")]
    [InlineData("extension-block-async-enumerator", "static class Extensions { extension(Elsewhere value) { public System.Collections.Generic.IAsyncEnumerator<int> GetAsyncEnumerator(System.Threading.CancellationToken token = default) => throw new NotSupportedException(); } } sealed class Elsewhere { }")]
    [InlineData("extension-block-over-interface", "static class Extensions { extension(System.Collections.Generic.IAsyncEnumerable<int> value) { public System.Collections.Generic.List<int>.Enumerator GetEnumerator() => default; } }")]
    public void AuthoredEnumerablesAbsentIsWithheldForAnExtensionBlockEnumerationMember(string shape, string declaration)
    {
        CSharpCompilation compilation = EnumerationPremiseFixture("foreach (int item in UnprovedEnumerableInput.Items) { _ = item; }", declaration);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse), shape);

        Assert.NotEmpty(ledger.Violations);

        HostedProducerDiscovery<HostedProducerSite> premised = EnumerationPremiseRequested(compilation);

        Assert.NotEmpty(PremiseViolations(premised));

        Assert.True(EnumerationPremiseUnclassified(premised, compilation));
    }

    [Fact]
    public void AuthoredEnumerablesAbsentIsGrantedForAnExtensionBlockWithoutAnEnumerationMember()
    {
        CSharpCompilation compilation = EnumerationPremiseFixture(
            "foreach (int item in UnprovedEnumerableInput.Items) { _ = item; }",
            "static class Extensions { extension(string value) { public int Twice => value.Length * 2; } }");

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

        Assert.True(ledger.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse), string.Join(global::System.Environment.NewLine, ledger.Violations));

        Assert.False(EnumerationPremiseUnclassified(EnumerationPremiseRequested(compilation), compilation));
    }
}
