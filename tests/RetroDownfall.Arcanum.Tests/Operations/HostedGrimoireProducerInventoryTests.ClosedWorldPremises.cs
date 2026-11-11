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
        Assert.Equal(HostedProducerPremises.SqliteSealedByUse | HostedProducerPremises.AuthoredEnumerablesAbsentByUse | HostedProducerPremises.DbProviderTypesClosedByUse, HostedGrimoireProducerInventory.ProductionPremises);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises(
            HostedGrimoireProducerInventory.ProductionCompilations, HostedProducerPremises.SqliteSealedByUse);

        Assert.Empty(ledger.Violations);

        Assert.True(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Guild_Quest_recovery_retains_its_own_ordinary_recovery_work_and_effect(bool inheritedEffect)
    {
        string root = "GuildQuestRecoveryHandler";

        string type = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler";

        string path = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestRecoveryHandler.cs";

        string member = "RecoverAsync";

        IReadOnlyList<CSharpCompilation> compilations = HostedGrimoireProducerInventory.ProductionCompilations;

        if (inheritedEffect)
        {
            const string shell = """
                using System.Threading;
                using System.Threading.Tasks;
                using RetroDownfall.Arcanum.Core.Operations;
                using RetroDownfall.Arcanum.Infrastructure.Data;

                namespace RetroDownfall.Arcanum.Infrastructure.Guild;

                internal sealed class QuestRecoveryProofShell(IGrimoireConnectionAdmissionGate gate,
                    GuildQuestRecoveryHandler handler, LongRunningOperation operation)
                {
                    internal async Task RunAsync(CancellationToken token)
                    {
                        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRunningOperationRecovery, out IGrimoireWorkLease? work))
                            return;

                        await using IGrimoireWorkLease held = work!;

                        if (!held.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect))
                            return;

                        await using IGrimoireExternalEffectGroup group = effect!;

                        await handler.RecoverAsync(operation, token);
                    }
                }
                """;

            root = "QuestRecoveryProofShell";

            type = "RetroDownfall.Arcanum.Infrastructure.Guild.QuestRecoveryProofShell";

            path = "src/QuestRecoveryProofShell.cs";

            member = "RunAsync";

            CSharpCompilation infrastructure = Assert.Single(compilations, static compilation =>
                compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

            SyntaxTree fixture = CSharpSyntaxTree.ParseText(shell,
                (CSharpParseOptions)infrastructure.SyntaxTrees.First().Options, path);

            CSharpCompilation modified = infrastructure.AddSyntaxTrees(fixture);

            Assert.Empty(modified.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            compilations = [.. compilations.Select(compilation => compilation == infrastructure ? modified : compilation)];
        }

        HostedProducerOperationEntry recovery = new(type + "." + member, path, type, member,
            HostedProducerAuthorityKind.OrdinaryHostedWork, GrimoireWorkKind.LongRunningOperationRecovery, null, []);

        HostedProducerDiscovery<HostedProducerSite> scoped = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedGrimoireProducerInventory.ProductionPremises, compilations,
            new([root], []), [new(root, [recovery])], []);

        HostedProducerAnalysisMetrics recoveryMetrics = Assert.IsType<HostedProducerAnalysisMetrics>(scoped.AnalysisMetrics);

        foreach (string line in RenderRootTraversalMetrics(recoveryMetrics.RootTraversals)) output.WriteLine(line);

        Assert.True(scoped.Diagnostics.Count == 0,
            HostedGrimoireProducerInventory.RenderDiagnosticReport(scoped.Diagnostics));

        Assert.Contains(scoped.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess);

        Assert.Contains(scoped.Items, static site => site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal)
            && site.AdmittedWorkKind == GrimoireWorkKind.LongRunningOperationRecovery);

        Assert.Contains(scoped.Items, static site => site.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PidFileService")]
    [InlineData("GuildMaintenanceHostedService")]
    [InlineData("GuildLocalQuestScheduler")]
    [InlineData("ApprenticeService")]
    [InlineData("McpServerBootstrapHostedService")]
    [InlineData("GuildInboundPeerReloadService")]
    [InlineData("GuildLocalHostReadiness")]
    [InlineData("Loremaster")]
    [InlineData("SagaExtractionService")]
    [InlineData("SessionAttachmentIndexingService")]
    [InlineData("GuildQuestRecovery")]
    [InlineData("Issue145AffectedPrimary")]
    [InlineData("Issue145SchemaAdditional")]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    [Trait("Category", "HostedProducerAdditionalAnalysis")]
    public void The_production_entry_runs_only_the_named_roots_under_the_production_premise(string serviceType)
    {
        if (serviceType == "GuildQuestRecovery")
        {
            const string type = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler";

            const string operation = type + ".RecoverAsync";

            HostedProducerDiscovery<HostedProducerSite> recovery = HostedGrimoireProducerInventory.DiscoverProductionSites(
                HostedProducerProductionPartition.Additional, [operation]);

            Assert.True(recovery.Diagnostics.Count == 0,
                HostedGrimoireProducerInventory.RenderDiagnosticReport(recovery.Diagnostics));

            Assert.NotEmpty(recovery.Items);

            Assert.All(recovery.Items, site => Assert.Equal(type, site.RootType));

            HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(recovery.AnalysisMetrics);

            HostedProducerRootTraversalMetric traversal = Assert.Single(metrics.RootTraversals);

            Assert.Equal(type, traversal.RootType);

            Assert.Equal(operation, traversal.RootOperation);

            foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

            output.WriteLine(HostedGrimoireProducerInventory.RenderCapsuleManifest(recovery.Items));

            HeadroomCeiling ceiling = Assert.Single(AdditionalRootCeilings, row => row.RootOperation == operation);

            string[] violations = HeadroomViolations(metrics.RootTraversals, [ceiling],
                HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot);

            Assert.True(violations.Length == 0, string.Join(global::System.Environment.NewLine, violations));

            HostedProducerCapsuleManifest reviewed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                global::System.IO.File.ReadAllLines(HostedGrimoireProducerInventory.CapsuleManifestPath));

            Assert.Empty(reviewed.Diagnostics);

            string expected = HostedGrimoireProducerInventory.RenderCapsuleManifest(
                reviewed.Sites.Where(site => site.AuthorityOperationId != operation).Concat(recovery.Items));

            HostedProducerCapsuleManifestUpdate update = HostedGrimoireProducerInventory.ReadOrUpdateCapsuleManifest(
                HostedGrimoireProducerInventory.CapsuleManifestPath, expected, recovery.Diagnostics,
                global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES") == "1",
                output.WriteLine);

            Assert.True(string.Equals(expected, update.Reviewed, StringComparison.Ordinal), update.Report);

            HostedProducerCapsuleManifest persisted = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                update.Reviewed.Split('\n', StringSplitOptions.RemoveEmptyEntries));

            Assert.Empty(persisted.Diagnostics);

            ProductionRootSelection selected = HostedGrimoireProducerInventory.SelectProductionRoots([operation],
                new([], []), [], HostedGrimoireProducerInventory.AdditionalProductionRoots);

            NonHostedProducerChainEntry declaration = Assert.Single(selected.NonHostedCatalog);

            HostedProducerInventoryValidation validation = HostedGrimoireProducerInventory.Validate([],
                [declaration with { Sites = [.. persisted.Sites.Where(site => site.AuthorityOperationId == operation)] }],
                selected.Registrations, recovery);

            Assert.True(validation.IsValid, HostedGrimoireProducerInventory.RenderDiagnosticReport(validation.Diagnostics));

            return;
        }

        if (serviceType is "Issue145AffectedPrimary" or "Issue145SchemaAdditional")
        {
            VerifyIssue145AffectedProductionRoots(serviceType == "Issue145SchemaAdditional");

            return;
        }

        HostedProducerDiscovery<HostedProducerSite> scoped = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, serviceType == "GuildLocalHostReadiness"
                ? [serviceType, "GuildLocalQuestScheduler"] : [serviceType]);

        Assert.NotEmpty(scoped.Items);

        Assert.True(scoped.Diagnostics.Count == 0,
            HostedGrimoireProducerInventory.RenderDiagnosticReport(scoped.Diagnostics));

        // The Scheduler's independently tracked Apprentice task is analyzed under its
        // own cataloged authority, even in a Scheduler-only traversal. No caller lease
        // is inherited across this handoff.
        Assert.All(scoped.Items, site => Assert.True(site.RootType == serviceType
            || serviceType == "GuildLocalHostReadiness" && site.RootType == "GuildLocalQuestScheduler"
            || (serviceType is "GuildLocalQuestScheduler" or "GuildLocalHostReadiness") && site.RootType == "ApprenticeService"
                && (site.AuthorityOperationId == "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync"
                    || (site.AuthorityOperationId.StartsWith("RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync/interceptor@", StringComparison.Ordinal)
                        || site.AuthorityOperationId.StartsWith("RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync/statechange@", StringComparison.Ordinal))
                    && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantConnectionDrain"
                    && site.Member == "ClearExactPoolAfterClose" && site.Callee == "Microsoft.Data.Sqlite.SqliteConnection.ClearPool")));

        if (serviceType == "GuildMaintenanceHostedService")
        {
            const string operationId = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildMaintenanceHostedService.ExecuteAsync";

            HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(scoped.AnalysisMetrics);

            foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

            HostedProducerRootTraversalMetric traversal = Assert.Single(metrics.RootTraversals);

            Assert.Equal(serviceType, traversal.RootType);

            Assert.Equal(operationId, traversal.RootOperation);

            Assert.True(traversal.AnalyzedStates > 0);

            Assert.All(scoped.Items, site => Assert.Equal(operationId, site.AuthorityOperationId));

            HostedProducerSite[] acquisitions = [.. scoped.Items.Where(
                static site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease")];

            Assert.Equal(
                new[]
                {
                    ("GuildLocalHostPolicy", "ExecuteAsync", (GrimoireWorkKind?)GrimoireWorkKind.GuildStorage),
                    ("GuildMaintenanceHostedService", "RunOnceAsync", (GrimoireWorkKind?)GrimoireWorkKind.GuildMaintenance),
                    ("GuildOwnerCleanupCoordinator", "RunBatchAsync", (GrimoireWorkKind?)GrimoireWorkKind.GuildMaintenance),
                    ("GuildStore", "ExecuteAsync", (GrimoireWorkKind?)GrimoireWorkKind.GuildStorage),
                    ("SignetAuthority", "WithInstallationAsync", (GrimoireWorkKind?)GrimoireWorkKind.GuildStorage),
                },
                acquisitions.Select(static site => (
                    site.EnclosingType[(site.EnclosingType.LastIndexOf('.') + 1)..], site.Member, site.AdmittedWorkKind))
                    .OrderBy(static site => site.Item1, StringComparer.Ordinal));

            Assert.Contains(scoped.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess);

            Assert.Contains(scoped.Items, static site => site.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal));

            // Retain every other reviewed partition and use the existing explicit manifest update
            // switch only for this scoped root. Without that switch, any missing/stale Guild row fails.
            HostedProducerCapsuleManifest reviewed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                global::System.IO.File.ReadAllLines(HostedGrimoireProducerInventory.CapsuleManifestPath));

            Assert.Empty(reviewed.Diagnostics);

            string expected = HostedGrimoireProducerInventory.RenderCapsuleManifest(
                reviewed.Sites.Where(site => site.RootType != serviceType).Concat(scoped.Items));

            HostedProducerCapsuleManifestUpdate update = HostedGrimoireProducerInventory.ReadOrUpdateCapsuleManifest(
                HostedGrimoireProducerInventory.CapsuleManifestPath, expected, scoped.Diagnostics,
                global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES") == "1",
                output.WriteLine);

            Assert.True(string.Equals(expected, update.Reviewed, StringComparison.Ordinal), update.Report);

            HostedProducerCapsuleManifest persisted = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                update.Reviewed.Split('\n', StringSplitOptions.RemoveEmptyEntries));

            Assert.Empty(persisted.Diagnostics);

            HostedProducerSite[] guildSites = [.. persisted.Sites.Where(site => site.RootType == serviceType)];

            Assert.NotEmpty(guildSites);

            ProductionRootSelection selected = HostedGrimoireProducerInventory.SelectProductionRoots([operationId],
                HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(HostedGrimoireProducerInventory.ProductionCompilations),
                HostedGrimoireProducerInventory.Catalog, HostedGrimoireProducerInventory.NonHostedCatalog);

            HostedProducerServiceEntry declaration = Assert.Single(selected.Catalog);

            HostedProducerOperationEntry operation = Assert.Single(declaration.Operations);

            Assert.Equal(HostedProducerAuthorityKind.OrdinaryHostedWork, operation.Authority);

            Assert.Equal(GrimoireWorkKind.GuildMaintenance, operation.WorkKind);

            HostedProducerInventoryValidation validation = HostedGrimoireProducerInventory.Validate(
                [declaration with { Operations = [operation with { Sites = guildSites }] }],
                selected.NonHostedCatalog, selected.Registrations, scoped);

            Assert.True(validation.IsValid, HostedGrimoireProducerInventory.RenderDiagnosticReport(validation.Diagnostics));

            return;
        }

        if (serviceType is "GuildLocalQuestScheduler" or "ApprenticeService" or "McpServerBootstrapHostedService"
            or "GuildInboundPeerReloadService" or "GuildLocalHostReadiness" or "Loremaster" or "SagaExtractionService" or "SessionAttachmentIndexingService")
        {
            HostedProducerSite[] schedulerSites = [.. scoped.Items.Where(site => site.RootType == serviceType)];

            Assert.NotEmpty(schedulerSites);

            HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(scoped.AnalysisMetrics);

            foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

            HeadroomCeiling[] ceilings = [.. ProductionRootCeilings.Where(row => row.RootType == serviceType)];

            string[] violations = HeadroomViolations(metrics.RootTraversals.Where(root => root.RootType == serviceType).ToArray(), ceilings,
                HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot);

            Assert.True(violations.Length == 0, string.Join(global::System.Environment.NewLine, violations));

            HostedProducerCapsuleManifest reviewed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                global::System.IO.File.ReadAllLines(HostedGrimoireProducerInventory.CapsuleManifestPath));

            Assert.Empty(reviewed.Diagnostics);

            if (serviceType == "GuildLocalQuestScheduler")
            {
                const string apprentice = "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync";

                HostedProducerSite[] importedSites = [.. scoped.Items.Where(static site => site.RootType == "ApprenticeService")];

                string[] imported = HostedGrimoireProducerInventory.RenderCapsuleManifest(
                    importedSites.Where(site => site.AuthorityOperationId == apprentice))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(static line => !line.StartsWith('#')).ToArray();

                HashSet<string> reviewedRows = HostedGrimoireProducerInventory.RenderCapsuleManifest(reviewed.Sites)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

                string[] missingImported = [.. imported.Where(row => !reviewedRows.Contains(row))];

                HostedProducerSite[] callbacks = [.. importedSites.Where(site => site.AuthorityOperationId != apprentice)];

                Assert.Equal(new[] { "ConnectionClosedAsync", "ConnectionDisposedAsync", "OnPhysicalStateChanged" },
                    callbacks.Select(static site => site.AuthorityOperationId[(site.AuthorityOperationId.LastIndexOf('/') + 1)..])
                        .Order(StringComparer.Ordinal));

                Assert.All(callbacks, site => Assert.Contains(reviewed.Sites, candidate =>
                    candidate.RootType == "ApprenticeService" && candidate.AuthorityOperationId == apprentice
                    && candidate.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal)
                    && candidate.AdmittedWorkKind == GrimoireWorkKind.ApprenticeExecution
                    && candidate.CapsuleId == site.WorkFrontierCapsuleId));

                output.WriteLine($"SCHEDULER_IMPORTED_APPRENTICE rows={imported.Length} missing={missingImported.Length} callbacks={callbacks.Length}");

                Assert.True(missingImported.Length == 0, string.Join(global::System.Environment.NewLine, missingImported));
            }

            string expected = HostedGrimoireProducerInventory.RenderCapsuleManifest(
                reviewed.Sites.Where(site => site.RootType != serviceType).Concat(schedulerSites));

            HostedProducerCapsuleManifestUpdate update = HostedGrimoireProducerInventory.ReadOrUpdateCapsuleManifest(
                HostedGrimoireProducerInventory.CapsuleManifestPath, expected, scoped.Diagnostics,
                global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES") == "1",
                output.WriteLine);

            Assert.True(string.Equals(expected, update.Reviewed, StringComparison.Ordinal), update.Report);

            HostedProducerCapsuleManifest persisted = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                update.Reviewed.Split('\n', StringSplitOptions.RemoveEmptyEntries));

            Assert.Empty(persisted.Diagnostics);

            ProductionRootSelection selected = HostedGrimoireProducerInventory.SelectProductionRoots([serviceType],
                HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(HostedGrimoireProducerInventory.ProductionCompilations),
                HostedGrimoireProducerInventory.Catalog, HostedGrimoireProducerInventory.NonHostedCatalog);

            HostedProducerServiceEntry declaration = Assert.Single(selected.Catalog);

            HostedProducerOperationEntry[] operations = [.. declaration.Operations.Select(operation => operation with
            {
                Sites = [.. persisted.Sites.Where(site => site.RootType == serviceType
                    && site.AuthorityOperationId == operation.OperationId)],
            })];

            HostedProducerInventoryValidation validation = HostedGrimoireProducerInventory.Validate(
                [declaration with { Operations = operations }], selected.NonHostedCatalog, selected.Registrations,
                scoped with { Items = schedulerSites });

            Assert.True(validation.IsValid, HostedGrimoireProducerInventory.RenderDiagnosticReport(validation.Diagnostics));

            return;
        }

        HostedProducerDiscovery<HostedProducerSite> additional = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Additional, ["RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add"]);

        Assert.NotEmpty(additional.Items);

        Assert.All(additional.Items, static site => Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", site.RootType));

        Assert.DoesNotContain(additional.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");
    }

    [Theory]
    [InlineData("exact", false)]
    [InlineData("missing", true)]
    [InlineData("ambiguous", true)]
    [InlineData("wrong-kind", true)]
    [InlineData("wrong-cleanup-task", true)]
    public void Scoped_catalog_handoff_requires_unique_independently_admitted_producer(string shape, bool refused)
    {
        const string type = "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService";

        HostedProducerOperationEntry startup = new(type + ".StartAsync", "src/Fixture.cs", type, "StartAsync",
            HostedProducerAuthorityKind.PreReadinessStartup, null, "host startup", []);

        HostedProducerOperationEntry execution = new(type + ".RunApprenticeAsync", "src/Fixture.cs", type,
            "RunApprenticeAsync", HostedProducerAuthorityKind.OrdinaryHostedWork,
            shape == "wrong-kind" ? GrimoireWorkKind.GuildStorage : GrimoireWorkKind.ApprenticeExecution,
            "independently admitted producer", []);

        HostedProducerServiceEntry[] proof = shape switch
        {
            "missing" => [],
            "ambiguous" => [new("ApprenticeService", [execution]), new("Duplicate", [execution])],
            _ => [new("ApprenticeService", [execution])],
        };

        string source = ApprenticeSelfHandoffSource(shape == "wrong-cleanup-task" ? shape : "exact");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedProducerPremises.None, [Compile(source)], new(["ApprenticeService"], []),
            [new("ApprenticeService", [startup])], [], handoffCatalog: proof);

        Assert.Equal(refused, result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith("System.Threading.Tasks.Task.Run;", StringComparison.Ordinal)));

        Assert.All(result.Items, static site => Assert.True(site.AuthorityOperationId == type + ".StartAsync"
            || site.AuthorityOperationId == type + ".RunApprenticeAsync"));

        if (!refused)
        {
            HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

            Assert.NotNull(effect.WorkFrontierCapsuleId);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
        }
    }

    [Fact]
    public void ParameterCollectionCountRequiresTheClosedProviderDispatchProof()
    {
        const string statement = "_ = DispatchInput.Parameters.Count;";

        const string callee = "System.Data.Common.DbParameterCollection.Count";

        Assert.True(DispatchRefused(DispatchDiscover(statement), callee));

        HostedProducerDiscovery<HostedProducerSite> closed = DispatchDiscoverUnder(
            HostedProducerPremises.DbProviderTypesClosedByUse, statement);

        Assert.Empty(closed.Diagnostics);

        const string authored = "internal abstract class EffectfulParameters : System.Data.Common.DbParameterCollection { public override int Count { get { System.IO.File.Delete(\"count-effect\"); return 0; } } }";

        HostedProducerDiscovery<HostedProducerSite> open = DispatchDiscoverUnder(
            HostedProducerPremises.DbProviderTypesClosedByUse, statement, authored);

        Assert.NotEmpty(PremiseViolations(open));

        Assert.True(DispatchRefused(open, callee));
    }

    [Fact]
    public void FrameworkParameterCountRejectsCounterfeitMetadataEvenWithProviderPremise()
    {
        CSharpCompilation foreign = CompileWithProductionReferencePack(
            "namespace System.Data.Common { public sealed class DbParameterCollection { public int Count { get { System.IO.File.Delete(\"counterfeit-count\"); return 0; } } } }",
            "RetroDownfall.Arcanum.Api").WithAssemblyName("Untrusted.ParameterCollection");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; "
            + R2Source(R2Admission + "_ = new counterfeit::System.Data.Common.DbParameterCollection().Count; await Task.CompletedTask;"),
            "RetroDownfall.Arcanum.Api").AddReferences(MetadataReference.CreateFromImage(image.ToArray(),
                new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedProducerPremises.DbProviderTypesClosedByUse, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.True(DispatchRefused(result, "System.Data.Common.DbParameterCollection.Count"));
    }

    private void VerifyIssue145AffectedProductionRoots(bool schemaAdditional)
    {
        string[] selectors = schemaAdditional
            ?
            [
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_AllObjects",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreObjects",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalObjects",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorObjects",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_TransitionStatements",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CanonicalSchemaFingerprint",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreSchemaFingerprint",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalSchemaFingerprint",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorSchemaFingerprint",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_Core",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantCanonical",
                "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantAccelerator",
            ]
            :
            [
                "DataRetentionSweepHostedService",
                "GrimoireDatabaseHostedService",
                "GrimoireSchemaTransitionHostedService",
                "LongRunningOperationStartupHostedService",
                "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync",
                "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync",
            ];

        // Whole affected hosted services retain their normal lifecycle and lease-handoff roots.
        // Pin the expanded declarations so a future catalog change cannot silently broaden this proof.
        string[] expectedOperations = schemaAdditional
            ? selectors
            :
            [
                "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionSweepHostedService.ExecuteAsync",
                "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync",
                "RetroDownfall.Arcanum.Infrastructure.Covenant.GrimoireSchemaTransitionHostedService.ExecuteAsync",
                "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.ContinueInBackgroundAsync",
                "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0~DF80A1ADE60651303A7C44345454EA715F10027DA9199F28FDB9F91C886443A3",
                "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync",
                "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync",
                "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StopAsync",
                "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StopAsync",
            ];

        HostedProducerProductionPartition partition = schemaAdditional
            ? HostedProducerProductionPartition.Additional
            : HostedProducerProductionPartition.Primary;

        ProductionRootSelection selected = HostedGrimoireProducerInventory.SelectProductionRoots(selectors,
            schemaAdditional ? new([], []) : HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(HostedGrimoireProducerInventory.ProductionCompilations),
            schemaAdditional ? [] : HostedGrimoireProducerInventory.Catalog,
            schemaAdditional ? HostedGrimoireProducerInventory.AdditionalProductionRoots : HostedGrimoireProducerInventory.NonHostedCatalog);

        HashSet<string> operations = new(selected.Catalog.SelectMany(static service => service.Operations)
            .Select(static operation => operation.OperationId)
            .Concat(selected.NonHostedCatalog.Select(static chain => chain.ChainId)), StringComparer.Ordinal);

        Assert.Equal(expectedOperations.Order(StringComparer.Ordinal), operations.Order(StringComparer.Ordinal));

        HostedProducerDiscovery<HostedProducerSite> scoped = HostedGrimoireProducerInventory.DiscoverProductionSites(partition, selectors);

        Assert.True(scoped.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(scoped.Diagnostics));

        HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(scoped.AnalysisMetrics);

        foreach (string operationId in operations)
        {
            HostedProducerRootTraversalMetric traversal = Assert.Single(metrics.RootTraversals,
                traversal => traversal.RootOperation == operationId);

            Assert.True(traversal.AnalyzedStates > 0, operationId);
        }

        Assert.All(scoped.Items, site => Assert.Contains(site.AuthorityOperationId, operations));

        HostedProducerCapsuleManifest reviewed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            global::System.IO.File.ReadAllLines(HostedGrimoireProducerInventory.CapsuleManifestPath));

        Assert.Empty(reviewed.Diagnostics);

        // Replace exactly the selected authority operations, retaining every other partition and root.
        string unaffected = HostedGrimoireProducerInventory.RenderCapsuleManifest(
            reviewed.Sites.Where(site => !operations.Contains(site.AuthorityOperationId)));

        string expected = HostedGrimoireProducerInventory.RenderCapsuleManifest(
            reviewed.Sites.Where(site => !operations.Contains(site.AuthorityOperationId)).Concat(scoped.Items));

        HostedProducerCapsuleManifestUpdate update = HostedGrimoireProducerInventory.ReadOrUpdateCapsuleManifest(
            HostedGrimoireProducerInventory.CapsuleManifestPath, expected, scoped.Diagnostics,
            global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_HOSTED_PRODUCER_CAPSULES") == "1", output.WriteLine);

        Assert.True(string.Equals(expected, update.Reviewed, StringComparison.Ordinal), update.Report);

        HostedProducerCapsuleManifest persisted = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            update.Reviewed.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Empty(persisted.Diagnostics);

        Assert.Equal(unaffected, HostedGrimoireProducerInventory.RenderCapsuleManifest(
            persisted.Sites.Where(site => !operations.Contains(site.AuthorityOperationId))));

        HostedProducerServiceEntry[] services = [.. selected.Catalog.Select(service => service with
        {
            Operations = [.. service.Operations.Select(operation => operation with
            {
                Sites = [.. persisted.Sites.Where(site => site.RootType == service.ServiceType && site.AuthorityOperationId == operation.OperationId)],
            })],
        })];

        NonHostedProducerChainEntry[] chains = [.. selected.NonHostedCatalog.Select(chain => chain with
        {
            Sites = [.. persisted.Sites.Where(site => site.RootType == chain.EnclosingType && site.AuthorityOperationId == chain.ChainId)],
        })];

        HostedProducerInventoryValidation validation = HostedGrimoireProducerInventory.Validate(
            services, chains, selected.Registrations, scoped);

        Assert.True(validation.IsValid, HostedGrimoireProducerInventory.RenderDiagnosticReport(validation.Diagnostics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocumentClockFrequencyOverrideIsTraversedOrRefusedRatherThanHidden(bool baseReceiver)
    {
        string body = baseReceiver
            ? "System.TimeProvider clock = new EffectClock(); _ = clock.TimestampFrequency;"
            : "_ = new EffectClock().TimestampFrequency;";

        const string helper =
            "internal sealed class EffectClock : System.TimeProvider { public override long TimestampFrequency { get { global::System.IO.File.Delete(\"clock-frequency-effect\"); return 1; } } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helper));

        if (baseReceiver)
        {
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.TimeProvider.TimestampFrequency");
        }
        else
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        }
    }

    [Theory]
    [InlineData("read", "held")]
    [InlineData("read", "missing")]
    [InlineData("mixed-access", "missing")]
    [InlineData("mixed-access", "held")]
    [InlineData("write", "held")]
    [InlineData("write", "missing")]
    [InlineData("write", "disposed")]
    [InlineData("authored", "held")]
    [InlineData("authored", "missing")]
    [InlineData("authored", "disposed")]
    public void ExplicitStreamCleanupFollowsWrapperInnerOwnership(string origin, string lifetime)
    {
        string inner = origin switch
        {
            "read" => "System.IO.File.OpenRead(\"attachment\")",
            "write" => "new System.IO.FileStream(\"attachment\", System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite)",
            "mixed-access" => "token.CanBeCanceled ? System.IO.File.OpenRead(\"attachment\") : new System.IO.FileStream(\"attachment\", System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite)",
            _ => "new EffectStream()",
        };

        string admission = lifetime == "missing"
            ? AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
            : R2Admission;

        string body = admission + "await using System.IO.Stream wrapped = new ReadWrapper(" + inner + "); "
            + (lifetime == "disposed" ? "held.Dispose();" : "");

        const string helpers = """
            internal sealed class ReadWrapper : System.IO.MemoryStream
            {
                private readonly System.IO.Stream _inner;
                public ReadWrapper(System.IO.Stream inner) { _inner = inner; }
                public override async System.Threading.Tasks.ValueTask DisposeAsync()
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            internal sealed class EffectStream : System.IO.MemoryStream
            {
                public override System.Threading.Tasks.ValueTask DisposeAsync()
                {
                    System.IO.File.Delete("inner-cleanup");
                    return default;
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helpers));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_DISPOSAL_TARGET_UNRESOLVED" or "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.Stream.DisposeAsync");

        string cleanup = origin == "authored" ? "System.IO.File.Delete" : "System.IO.FileStream.DisposeAsync";

        Assert.Contains(result.Items, site => site.Callee == cleanup
            && site.Kind == (origin == "read" ? HostedProducerSiteKind.FileSystemRead : HostedProducerSiteKind.FileSystemEffect));

        Assert.Equal(origin != "read" && lifetime != "held", result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == cleanup));
    }

    [Theory]
    [InlineData("Unknown()")]
    [InlineData("token.CanBeCanceled ? System.IO.File.OpenRead(\"read\") : Unknown()")]
    [InlineData("token.CanBeCanceled ? new FirstStream() : new SecondStream()")]
    public void ExplicitStreamCleanupRefusesUnknownOrMixedReceiver(string origin)
    {
        const string helpers = """
            internal sealed class FirstStream : System.IO.MemoryStream
            {
                public override System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete("first"); return default; }
            }
            internal sealed class SecondStream : System.IO.MemoryStream
            {
                public override System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete("second"); return default; }
            }
            """;

        string source = R2Source(R2Admission + "System.IO.Stream value = " + origin
            + "; await value.DisposeAsync();", helpers)
            .Replace("public Task StopAsync", "private static System.IO.Stream Unknown() => Input.Value; public Task StopAsync", StringComparison.Ordinal)
            + " internal static class Input { internal static System.IO.Stream Value { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disposed")]
    [InlineData("detached")]
    public void ExplicitStreamCleanupCannotBorrowAnExpiredWorkLifetime(string lifetime)
    {
        string admission = lifetime == "missing" ? "" : R2Admission;

        string body = admission + "System.IO.Stream stream = new WorkStream(); "
            + (lifetime == "disposed" ? "lease.Dispose(); " : "")
            + (lifetime == "detached" ? "stream.DisposeAsync();" : "await stream.DisposeAsync();");

        const string helper = """
            internal sealed class WorkStream : System.IO.MemoryStream
            {
                public override System.Threading.Tasks.ValueTask DisposeAsync() { System.IO.File.Delete("final-use"); return default; }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helper));

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");

        if (lifetime == "detached")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
        }
    }

    [Theory]
    [InlineData("await stream.DisposeAsync();", "public override System.Threading.Tasks.ValueTask DisposeAsync() => base.DisposeAsync();", "System.IO.Stream.DisposeAsync")]
    [InlineData("stream.Dispose();", "", "System.IO.Stream.Dispose")]
    public void StreamBaseAndSynchronousCleanupRemainConservative(string call, string asynchronousOverride, string cleanup)
    {
        string helper = """
            internal sealed class EffectStream : System.IO.Stream
            {
                public override bool CanRead => true;
                public override bool CanWrite => false;
                public override bool CanSeek => false;
                public override long Length => 0;
                public override long Position { get => 0; set => throw new System.NotSupportedException(); }
                public override int Read(byte[] bytes, int offset, int count) => 0;
                public override void Flush() { }
                public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new System.NotSupportedException();
                public override void SetLength(long value) => throw new System.NotSupportedException();
                public override void Write(byte[] bytes, int offset, int count) => throw new System.NotSupportedException();
                protected override void Dispose(bool disposing) { System.IO.File.Delete("protected-cleanup"); }
            }
            """;

        helper = helper.Replace("protected override void Dispose", asynchronousOverride + " protected override void Dispose", StringComparison.Ordinal);

        string body = AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
            + "System.IO.Stream stream = new EffectStream(); " + call;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helper));

        // These framework bodies can run protected virtual cleanup. Until that dispatch is
        // modeled, they remain a filesystem effect; they must never become mechanical cleanup.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == cleanup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyFileCleanupRequiresWorkButNoEffectForBothCleanupModes(bool asynchronous)
    {
        string body = AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal)
            + (asynchronous ? "IAsyncDisposable" : "IDisposable")
            + " stream = System.IO.File.OpenRead(\"legacy\"); "
            + (asynchronous ? "await stream.DisposeAsync();" : "stream.Dispose();");

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body));

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, site => site.Callee == "System.IO.FileStream." + (asynchronous ? "DisposeAsync" : "Dispose")
            && site.Kind == HostedProducerSiteKind.FileSystemRead
            && site.WorkFrontierCapsuleId is not null
            && site.EffectFrontierCapsuleId is null);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void ProductionAttachmentExtractionRetainsItsExactAdmittedLifetime()
    {
        HostedProducerDiscovery<HostedProducerSite> full = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerSite[] observed = ObserveFullProductionRoot(full, "SessionAttachmentIndexingService");

        Assert.NotEmpty(observed);

        output.WriteLine("FULL_GRAPH_ATTACHMENT_CAPSULES_BEGIN");

        output.WriteLine(HostedGrimoireProducerInventory.RenderCapsuleManifest(observed));

        output.WriteLine("FULL_GRAPH_ATTACHMENT_CAPSULES_END");

        Assert.All(observed, static site => Assert.Equal("SessionAttachmentIndexingService", site.RootType));

        HostedProducerSite openRead = Assert.Single(observed, static site =>
            site.Callee == "RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore.OpenReadAsync");

        Assert.NotNull(openRead.WorkFrontierCapsuleId);

        // Guild access retains both physical reads inside the exact ReadBytesAsync callback.
        const string readIdentity = "M:RetroDownfall.Arcanum.Infrastructure.Data.SessionAttachmentStore.ReadBytesAsync(RetroDownfall.Arcanum.Core.Storage.SessionAttachmentRecord,System.Threading.CancellationToken)/lambda#0~F15223B19FBDCF918147F9EBACCAB1538E12741F5C199DCAFCA58C641FBB15EE";

        HostedProducerSite[] reads = [.. observed.Where(static site =>
            site.SourcePath == "src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs"
            && site.MemberIdentity == readIdentity
            && site.Callee == "System.IO.Stream.ReadAsync")];

        Assert.Equal(2, reads.Length);

        Assert.Equal<string>(
            ["06F61F341416DD8A46029CA4320529598D131BD2597E18179CB4AC0C9F77C2E7", "5AC3AE8C8F51684422B9FD1CCEB2255667FE7988E26A400870C98090F8A253DE"],
            reads.Select(static site => site.SyntaxFingerprint).Order(StringComparer.Ordinal));

        Assert.All(reads, site =>
        {
            Assert.Equal(HostedProducerSiteKind.FileSystemRead, site.Kind);

            Assert.Equal(openRead.WorkFrontierCapsuleId, site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        Assert.Contains(observed, static site =>
            site.SourcePath.EndsWith("/Storage/EncryptedBlobStore.cs", StringComparison.Ordinal)
            && site.Member == "DisposeAsync"
            && site.Callee == "System.IO.FileStream.DisposeAsync"
            && site.Kind == HostedProducerSiteKind.FileSystemRead
            && site.WorkFrontierCapsuleId is not null
            && site.EffectFrontierCapsuleId is null);

        Assert.Contains(observed, static site =>
            site.SourcePath.EndsWith("/Data/SessionAttachmentStore.cs", StringComparison.Ordinal)
            && site.Member == "DisposeAsync"
            && site.Callee == "System.IO.FileStream.DisposeAsync"
            && site.Kind == HostedProducerSiteKind.FileSystemRead
            && site.WorkFrontierCapsuleId is not null
            && site.EffectFrontierCapsuleId is null);

        // The isolated PDF host reaches its worker process only through the reviewed seam, as a provider call; the runner
        // behind it is not traversed.
        Assert.Contains(observed, static site =>
            site.SourcePath.EndsWith("/Weave/SessionAttachmentIsolatedDocumentHost.cs", StringComparison.Ordinal)
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.IDocumentExtractionWorker.RunAsync"
            && site.Kind == HostedProducerSiteKind.ProviderCall);

        Assert.DoesNotContain(observed, static site =>
            site.SourcePath.Contains("/Process/CappedChildProcessRunner.cs", StringComparison.Ordinal));

        Assert.True(
            full.Diagnostics.Count == 0,
            string.Join(
                global::System.Environment.NewLine,
                full.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));
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

    // The two regimes a fixture of the EF lane is judged under: strict, which every fixture has, and the production regime
    // (the closed-world facts the shipped tree is judged under). The fixtures of the lane name the regime with a flag; this
    // is the one place that turns the flag into a request for the facts. A fixture that also counts the states the walk admits
    // hands the observer in here, so it never names the facts itself.
    private static HostedProducerDiscovery<HostedProducerSite> DiscoverUnderRegime(
        CSharpCompilation compilation,
        bool production,
        Action<HostedProducerAdmittedStateObservation>? admittedStateObserver = null) =>
        HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            production ? HostedGrimoireProducerInventory.ProductionPremises : HostedProducerPremises.None,
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot()])],
            [],
            admittedStateObserver: admittedStateObserver);

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

    // ---- DbProviderTypesClosedByUse: the closed-world premise behind the provider dispatch closure.

    private HostedProducerDiscovery<HostedProducerSite> DispatchDiscoverUnder(HostedProducerPremises premises, string body, string extra = "", params MetadataReference[] references)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + body + " await Task.CompletedTask;", DispatchInputs + "\n" + extra), "RetroDownfall.Arcanum.Api");

        if (references.Length != 0)
        {
            compilation = compilation.AddReferences(references);
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(premises, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }

    [Theory]
    [InlineData("_ = DispatchInput.Connection.CreateCommand();", "System.Data.Common.DbConnection.CreateCommand")]
    [InlineData("_ = DispatchInput.Command.CreateParameter();", "System.Data.Common.DbCommand.CreateParameter")]
    [InlineData("_ = DispatchInput.Command.CommandText;", "System.Data.Common.DbCommand.CommandText")]
    [InlineData("_ = DispatchInput.Reader.GetString(0);", "System.Data.Common.DbDataReader.GetString")]
    [InlineData("_ = DispatchInput.Reader.FieldCount;", "System.Data.Common.DbDataReader.FieldCount")]
    [InlineData("_ = DispatchInput.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter());", "System.Data.Common.DbParameterCollection.Add")]
    [InlineData("_ = DispatchInput.Parameter.Value;", "System.Data.Common.DbParameter.Value")]
    [InlineData("_ = DispatchInput.Transaction.Connection;", "System.Data.Common.DbTransaction.Connection")]
    [InlineData("_ = DispatchInput.Builder.ToString();", "System.Data.Common.DbConnectionStringBuilder.ToString")]
    [InlineData("_ = DispatchInput.SqliteParameter.Value;", "Microsoft.Data.Sqlite.SqliteParameter.Value")]
    [InlineData("Microsoft.EntityFrameworkCore.DbContext r = DispatchInput.Context; _ = r.Set<DispatchRow>();", "Microsoft.EntityFrameworkCore.DbContext.Set")]
    [InlineData("_ = DispatchInput.Rows.Add(new DispatchRow());", "Microsoft.EntityFrameworkCore.DbSet`1.Add")]
    [InlineData("_ = DispatchInput.Command.ExecuteNonQuery();", "System.Data.Common.DbCommand.ExecuteNonQuery")]
    [InlineData("DispatchInput.Connection.Open();", "System.Data.Common.DbConnection.Open")]
    [InlineData("DispatchInput.Transaction.Commit();", "System.Data.Common.DbTransaction.Commit")]
    public void DbProviderTypesClosedByUseClassifiesAReviewedMemberOnAnyReceiver(string statement, string callee)
    {
        // The strict twin: with no premise the same statement is refused (AReviewedDatabaseMemberOnAnOpaque...).
        Assert.True(DispatchRefused(DispatchDiscover(statement), callee));

        HostedProducerDiscovery<HostedProducerSite> premised = DispatchDiscoverUnder(HostedProducerPremises.DbProviderTypesClosedByUse, statement);

        Assert.Empty(PremiseViolations(premised));

        Assert.False(DispatchRefused(premised, callee));
    }

    [Fact]
    public void DbProviderTypesClosedByUseStillReportsADatabaseSiteAndGrantsNoWorkOrEffectAuthority()
    {
        HostedProducerDiscovery<HostedProducerSite> premised = DispatchDiscoverUnder(HostedProducerPremises.DbProviderTypesClosedByUse, "_ = DispatchInput.Command.ExecuteNonQuery();");

        Assert.True(DispatchSite(premised, "System.Data.Common.DbCommand.ExecuteNonQuery"));

        // Outside an admitted region the same site still lacks its frontiers.
        HostedProducerDiscovery<HostedProducerSite> unadmitted = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedProducerPremises.DbProviderTypesClosedByUse,
            [CompileWithProductionReferencePack(R2Source("_ = DispatchInput.Command.ExecuteNonQuery(); await Task.CompletedTask;", DispatchInputs), "RetroDownfall.Arcanum.Api")],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot()])],
            []);

        Assert.Contains(unadmitted.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.Data.Common.DbCommand.ExecuteNonQuery");
    }

    // A type the universe holds whose dispatch the reviewed members could reach: the premise is withheld, the
    // violation names the type and the family, and every reviewed member keeps its strict behavior.
    [Theory]
    [InlineData("connection", "internal abstract class HiddenProvider : System.Data.Common.DbConnection { }", "HiddenProvider", "System.Data.Common.DbConnection")]
    [InlineData("command", "internal abstract class HiddenProvider : System.Data.Common.DbCommand { }", "HiddenProvider", "System.Data.Common.DbCommand")]
    [InlineData("transaction", "internal abstract class HiddenProvider : System.Data.Common.DbTransaction { }", "HiddenProvider", "System.Data.Common.DbTransaction")]
    [InlineData("reader", "internal abstract class HiddenProvider : System.Data.Common.DbDataReader { }", "HiddenProvider", "System.Data.Common.DbDataReader")]
    [InlineData("parameter", "internal abstract class HiddenProvider : System.Data.Common.DbParameter { }", "HiddenProvider", "System.Data.Common.DbParameter")]
    [InlineData("parameter-collection", "internal abstract class HiddenProvider : System.Data.Common.DbParameterCollection { }", "HiddenProvider", "System.Data.Common.DbParameterCollection")]
    [InlineData("builder", "internal sealed class HiddenProvider : System.Data.Common.DbConnectionStringBuilder { }", "HiddenProvider", "System.Data.Common.DbConnectionStringBuilder")]
    [InlineData("sqlite-parameter", "internal sealed class HiddenProvider : Microsoft.Data.Sqlite.SqliteParameter { }", "HiddenProvider", "System.Data.Common.DbParameter")]
    [InlineData("sqlite-connection", "internal sealed class HiddenProvider : Microsoft.Data.Sqlite.SqliteConnection { }", "HiddenProvider", "System.Data.Common.DbConnection")]
    [InlineData("dbset", "internal abstract class HiddenProvider<T> : Microsoft.EntityFrameworkCore.DbSet<T> where T : class { }", "HiddenProvider", "Microsoft.EntityFrameworkCore.DbSet`1")]
    [InlineData("facade", "internal sealed class HiddenProvider(Microsoft.EntityFrameworkCore.DbContext context) : Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade(context) { }", "HiddenProvider", "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade")]
    [InlineData("tracker", "internal sealed class HiddenProvider : Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker { internal HiddenProvider() : base(null!, null!, null!, null!, null!) { } }", "HiddenProvider", "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker")]
    [InlineData("transaction-contract", "internal interface HiddenProvider : Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction { }", "HiddenProvider", "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction")]
    [InlineData("nested-indirect", "internal static class Outer { internal abstract class Base : System.Data.Common.DbCommand { } internal abstract class HiddenProvider : Base { } }", "HiddenProvider", "System.Data.Common.DbCommand")]
    public void ASourceTypeThatDerivesFromAProviderFamilyViolatesDbProviderTypesClosedByUse(string shape, string declaration, string typeName, string family)
    {
        CSharpCompilation compilation = PremiseFixture("DispatchInputsTouch.Touch(); await Task.CompletedTask;", DispatchInputs + "\nstatic class DispatchInputsTouch { internal static void Touch() { _ = DispatchInput.Connection.CreateCommand(); } }\n" + declaration);

        output.WriteLine("PREMISE " + shape);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));

        Assert.Contains(ledger.Violations, violation => violation.Identity == "DbProviderTypesClosedByUse"
            && violation.Detail.Contains(typeName, StringComparison.Ordinal)
            && violation.Detail.Contains(family, StringComparison.Ordinal));

        // The strict behavior stays: the reviewed member is refused, as it is with no premise at all.
        HostedProducerDiscovery<HostedProducerSite> premised = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            HostedProducerPremises.DbProviderTypesClosedByUse, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.NotEmpty(PremiseViolations(premised));

        Assert.True(DispatchRefused(premised, "System.Data.Common.DbConnection.CreateCommand"));
    }

    [Theory]
    [InlineData("connection", "public abstract class PeProvider : System.Data.Common.DbConnection { }")]
    [InlineData("reader", "public abstract class PeProvider : System.Data.Common.DbDataReader { }")]
    [InlineData("hidden-nested", "public static class PeOuter { private abstract class PeProvider : System.Data.Common.DbCommand { } }")]
    [InlineData("sqlite-command", "public sealed class PeProvider : Microsoft.Data.Sqlite.SqliteCommand { }")]
    public void AProviderSubclassInAPortableExecutableReferenceViolatesDbProviderTypesClosedByUse(string shape, string library)
    {
        PortableExecutableReference reference = PremiseLibrary("ProviderDerivedLibrary", library);

        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", DispatchInputs, reference);

        output.WriteLine("PREMISE " + shape);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));

        Assert.Contains(ledger.Violations, static violation => violation.Detail.Contains("PeProvider", StringComparison.Ordinal));
    }

    [Fact]
    public void ALookalikeOfATrustedProviderTypeInAnUntrustedAssemblyIsNotTrusted()
    {
        // A type that merely shares a trusted type's name and namespace is declared by an untrusted assembly, so a
        // subclass of the real family in it still violates the premise.
        PortableExecutableReference library = PremiseLibrary("Microsoft.Data.Sqlite", "namespace Microsoft.Data.Sqlite { public abstract class LookalikeCommand : System.Data.Common.DbCommand { } }");

        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", DispatchInputs).AddReferences(library);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));
    }

    [Theory]
    [InlineData("set", "public override Microsoft.EntityFrameworkCore.DbSet<T> Set<T>() => base.Set<T>();", true)]
    [InlineData("save-changes", "public override int SaveChanges() => base.SaveChanges();", true)]
    [InlineData("dispose", "public override void Dispose() { base.Dispose(); }", true)]
    [InlineData("database-property", "public override Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database => base.Database;", true)]
    [InlineData("hooks-only", "protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { } protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder builder) { }", false)]
    [InlineData("no-members", "", false)]
    public void AnAuthoredContextMayOverrideOnlyItsHooksUnderDbProviderTypesClosedByUse(string shape, string members, bool violated)
    {
        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", DispatchInputs + "\nsealed class HookContext(Microsoft.EntityFrameworkCore.DbContextOptions<HookContext> options) : Microsoft.EntityFrameworkCore.DbContext(options) { " + members + " }");

        output.WriteLine("PREMISE " + shape);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.Equal(!violated, ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));

        Assert.Equal(violated, ledger.Violations.Any(static violation => violation.Detail.Contains("HookContext", StringComparison.Ordinal)));
    }

    [Fact]
    public void ADatabaseProviderUniverseWithOnlyTrustedTypesIsGranted()
    {
        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", DispatchInputs);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.Empty(ledger.Violations);

        Assert.True(ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));

        // Granted without being requested alongside the others: each premise stands on its own verification.
        Assert.False(ledger.Holds(HostedProducerPremises.SqliteSealedByUse));
    }

    [Fact]
    public void AnUnresolvedBaseTypeInAReferenceRefusesDbProviderTypesClosedByUse()
    {
        PortableExecutableReference missing = PremiseLibrary("ProviderMissing", "public class MissingBase { }");

        PortableExecutableReference derived = PremiseLibrary(
            "ProviderDerivedFromMissing",
            "public sealed class DerivedFromMissing : MissingBase { }",
            [.. CompileWithProductionReferencePack("class Pack { }", "RetroDownfall.Arcanum.Api").References, missing]);

        CSharpCompilation compilation = PremiseFixture("await Task.CompletedTask;", DispatchInputs, derived);

        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises([compilation], HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.False(ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));

        Assert.Contains(ledger.Violations, static violation => violation.Detail.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_universe_satisfies_DbProviderTypesClosedByUse()
    {
        HostedProducerPremiseLedger ledger = HostedGrimoireProducerInventory.VerifyPremises(
            HostedGrimoireProducerInventory.ProductionCompilations, HostedProducerPremises.DbProviderTypesClosedByUse);

        Assert.Empty(ledger.Violations);

        Assert.True(ledger.Holds(HostedProducerPremises.DbProviderTypesClosedByUse));

        Assert.True(HostedGrimoireProducerInventory.ProductionPremises.HasFlag(HostedProducerPremises.DbProviderTypesClosedByUse));
    }

    // ---- AuthoredEnumerablesAbsentByUse never covers an EF query value.

    private const string QueryPremiseHelpers = "internal static class QueryHelpers { internal static void Walk(System.Collections.Generic.IEnumerable<BlobRow> rows) { foreach (BlobRow r in rows) { _ = r; } } "
        + "internal static int Count(System.Collections.Generic.IEnumerable<BlobRow> rows) => System.Linq.Enumerable.ToList(rows).Count; "
        + "internal static System.Collections.Generic.IEnumerable<BlobRow> Provide(BlobContext c) => c.Rows; }";

    private HostedProducerDiscovery<HostedProducerSite> QueryDiscoverUnder(HostedProducerPremises premises, string admitted)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(QuerySource(admitted, helpers: QueryPremiseHelpers), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(premises, [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }

    [Theory]
    [InlineData("QueryHelpers.Walk(db.Rows);")]
    [InlineData("_ = QueryHelpers.Count(db.Rows);")]
    [InlineData("foreach (BlobRow r in QueryHelpers.Provide(db)) { _ = r; }")]
    public void AuthoredEnumerablesAbsentNeverHidesAnEfQueryBehindASequenceInterface(string statement)
    {
        foreach (HostedProducerPremises premises in new[]
        {
            HostedProducerPremises.None,
            HostedProducerPremises.AuthoredEnumerablesAbsentByUse,
            HostedGrimoireProducerInventory.ProductionPremises,
        })
        {
            HostedProducerDiscovery<HostedProducerSite> result = QueryDiscoverUnder(premises, statement);

            // The premise lets the proof of an enumeration operand stop at a sequence interface it cannot follow, but the
            // query that flows into one is refused where it is built, so nothing it stops at can be a database query.
            Assert.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                    && diagnostic.Detail.Contains("EF query", StringComparison.Ordinal)),
                "premises " + premises + " must refuse the escaping query.");
        }
    }

    [Theory]
    [InlineData("QueryHelpers.Walk(new System.Collections.Generic.List<BlobRow>());")]
    [InlineData("_ = QueryHelpers.Count(new BlobRow[0]);")]
    public void AuthoredEnumerablesAbsentStillCoversAnInMemorySequenceInterface(string statement)
    {
        // The twin: the premise keeps covering what it always covered. An in-memory sequence behind the same interface
        // is no database query, and is not refused.
        HostedProducerDiscovery<HostedProducerSite> result = QueryDiscoverUnder(HostedGrimoireProducerInventory.ProductionPremises, statement);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Detail.Contains("EF query", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess
            && !site.Callee.StartsWith("System.Data.", StringComparison.Ordinal));
    }
}
