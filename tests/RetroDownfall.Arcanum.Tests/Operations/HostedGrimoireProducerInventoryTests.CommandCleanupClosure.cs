using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(true, "literal", false)]
    [InlineData(true, "literal", true)]
    [InlineData(false, "literal", false)]
    [InlineData(false, "literal", true)]
    [InlineData(true, "typed-input", false)]
    [InlineData(true, "typed-input", true)]
    [InlineData(false, "typed-input", false)]
    [InlineData(false, "typed-input", true)]
    [InlineData(true, "effectful-typed-input", false)]
    [InlineData(true, "effectful-typed-input", true)]
    [InlineData(false, "effectful-typed-input", false)]
    [InlineData(false, "effectful-typed-input", true)]
    public void HostedContinuationActualCommandSourceKeepsBorrowedConnectionAndOwnedCommandDistinct(bool sealedSqlite, string connectionShape, bool useCensus)
    {
        string relative = "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupSourceStorage.cs";

        CompilationUnitSyntax sourceRoot = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), relative))).GetCompilationUnitRoot();

        ClassDeclarationSyntax owner = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        ClassDeclarationSyntax selected = owner.WithMembers(SyntaxFactory.List(owner.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "IsInstalledAsync" or "ResolveMembershipAsync").Cast<MemberDeclarationSyntax>()));

        SyntaxTree actual = CSharpSyntaxTree.Create(sourceRoot.ReplaceNode(owner, selected), path: relative);

        SyntaxTree globals = CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", path: "src/Fixture.GlobalUsings.g.cs");

        string connection = connectionShape switch
        {
            "typed-input" => "NativeCommandInput.Take()",
            "effectful-typed-input" => "NativeCommandInput.EffectfulTake()",
            _ => "new Microsoft.Data.Sqlite.SqliteConnection()",
        };

        // Keep the new optional scalar lane real in this source fixture. Its full census
        // and mirror dependency compile alongside the original command ownership source.
        SyntaxTree census = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src/RetroDownfall.Arcanum.Infrastructure/Data/RetentionSchemaCensus.cs")), path: "src/RetroDownfall.Arcanum.Infrastructure/Data/RetentionSchemaCensus.cs");

        SyntaxTree mirror = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaVectorMirror.cs")), path: "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaVectorMirror.cs");

        string censusArgument = useCensus ? ", census" : "";

        string body = R2Admission
            + (useCensus ? "using var census = new RetroDownfall.Arcanum.Infrastructure.Data.RetentionSchemaCensus(); " : "")
            + "System.Data.Common.DbConnection connection = " + connection + "; "
            + "_ = await RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CampaignRollupSourceStorage.IsInstalledAsync(connection, null, token" + censusArgument + "); "
            + "await using System.Data.Common.DbCommand command = connection.CreateCommand(); command.CommandText = \"campaign_rollup_source_membership\"; "
            + "await RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CampaignRollupSourceStorage.ResolveMembershipAsync(command, token" + censusArgument + "); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, "internal static class NativeCommandInput { internal static extern Microsoft.Data.Sqlite.SqliteConnection Take(); internal static Microsoft.Data.Sqlite.SqliteConnection EffectfulTake() { System.IO.File.Delete(\"command-input\"); return Take(); } }"), "RetroDownfall.Arcanum.Api").AddSyntaxTrees(actual, census, mirror, globals);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = sealedSqlite ? PremiseRequested(compilation) : PremiseStrict(compilation);

        if (connectionShape == "effectful-typed-input")
        {
            HostedProducerSite inputEffect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

            Assert.NotNull(inputEffect.WorkFrontierCapsuleId);

            Assert.NotNull(inputEffect.EffectFrontierCapsuleId);
        }

        if (!sealedSqlite)
        {
            // Even an exact SqliteCommand may hold a derived SqliteConnection. Without
            // the verified premise, its borrowed connection is not an exact certificate.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.Data.Common.DbCommand.DisposeAsync", StringComparison.Ordinal));

            return;
        }

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.Data.Common.DbCommand.DisposeAsync", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Data.Common.DbCommand.Connection");

        Assert.Contains(result.Items, static site => site.Callee == "System.Data.Common.DbCommand.ExecuteScalarAsync" && site.WorkFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

        if (useCensus) Assert.Contains(result.Items, static site => site.SourcePath.EndsWith("/RetentionSchemaCensus.cs", StringComparison.Ordinal)
            && site.Kind == HostedProducerSiteKind.DatabaseAccess && site.WorkFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Items, static site => site.Callee is "System.Data.Common.DbConnection.Dispose" or "System.Data.Common.DbConnection.DisposeAsync"
            or "Microsoft.Data.Sqlite.SqliteConnection.Dispose" or "Microsoft.Data.Sqlite.SqliteConnection.DisposeAsync");
    }

    [Theory]
    [InlineData("closed", true)]
    [InlineData("unknown", false)]
    [InlineData("custom", false)]
    [InlineData("mixed", false)]
    public void HostedContinuationReferenceCommandFactoryCannotHideCustomCleanupOrConnection(string shape, bool closed)
    {
        string value = shape switch
        {
            "unknown" => "DispatchInput.Connection",
            "custom" => "new EvilConnection()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new Microsoft.Data.Sqlite.SqliteConnection() : DispatchInput.Connection",
            _ => "new Microsoft.Data.Sqlite.SqliteConnection()",
        };

        string authored = DispatchAuthoredProviders.Replace("public override void Prepare() { }", "public override void Prepare() { } public override ValueTask DisposeAsync() { System.IO.File.Delete(\"custom-command-dispose\"); return default; }", StringComparison.Ordinal)
            .Replace("protected override System.Data.Common.DbConnection? DbConnection { get; set; }", "protected override System.Data.Common.DbConnection? DbConnection { get { System.IO.File.Delete(\"custom-command-connection\"); return null; } set { } }", StringComparison.Ordinal)
            .Replace("System.IO.File.Delete(\"evil-create-command\"); return null!;", "System.IO.File.Delete(\"evil-create-command\"); return new EvilCommand();", StringComparison.Ordinal);

        string helper = "internal static class CommandConsumer { internal static async Task Use(System.Data.Common.DbConnection connection) { "
            + "await using System.Data.Common.DbCommand command = connection.CreateCommand(); _ = command.Connection; _ = await command.ExecuteScalarAsync(); } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + "await CommandConsumer.Use(" + value + "); await Task.CompletedTask;", DispatchInputs + authored + helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        bool refused = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.Data.Common.DbCommand.DisposeAsync", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail is "System.Data.Common.DbCommand.Connection" or "System.Data.Common.DbConnection.CreateCommand");

        if (closed) Assert.False(refused, HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));
        else Assert.True(refused || result.Items.Any(static site => site.Callee == "System.IO.File.Delete"));
    }

    [Theory]
    [InlineData("closed", true, true)]
    [InlineData("closed", false, false)]
    [InlineData("unknown", true, false)]
    [InlineData("custom", true, false)]
    [InlineData("mixed", true, false)]
    public void HostedContinuationReferenceBorrowedConnectionNeedsExactCommandAndVerifiedFamily(string shape, bool sealedSqlite, bool closed)
    {
        string value = shape switch
        {
            "unknown" => "DispatchInput.Command",
            "custom" => "new EvilCommand()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new Microsoft.Data.Sqlite.SqliteConnection().CreateCommand() : DispatchInput.Command",
            _ => "new Microsoft.Data.Sqlite.SqliteConnection().CreateCommand()",
        };

        string helper = "internal static class BorrowedCommandConsumer { internal static async Task Use(System.Data.Common.DbCommand source) { "
            + "await using System.Data.Common.DbCommand secondary = source.Connection!.CreateCommand(); _ = await secondary.ExecuteScalarAsync(); } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + "await BorrowedCommandConsumer.Use(" + value + "); await Task.CompletedTask;", DispatchInputs + DispatchAuthoredProviders + helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = sealedSqlite ? PremiseRequested(compilation) : PremiseStrict(compilation);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");

        bool unresolvedSecondaryCleanup = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.Data.Common.DbCommand.DisposeAsync", StringComparison.Ordinal));

        Assert.Equal(!closed, unresolvedSecondaryCleanup);

        if (closed) Assert.Contains(result.Items, static site => site.Callee == "System.Data.Common.DbCommand.ExecuteScalarAsync" && site.WorkFrontierCapsuleId is not null);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualCommandProductionSubsetKeepsRealSqlWork()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, ["Loremaster"]);

        Assert.NotEmpty(result.Items);

        Assert.All(result.Items, static site => Assert.Equal("Loremaster", site.RootType));

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && HostedContinuationTerminalAnchor(diagnostic).Contains("/CampaignRollupSourceStorage.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Data.Common.DbCommand.Connection")];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.SourcePath.EndsWith("/CampaignRollupSourceStorage.cs", StringComparison.Ordinal)
            && site.Kind == HostedProducerSiteKind.DatabaseAccess && site.WorkFrontierCapsuleId is not null);
    }
}
