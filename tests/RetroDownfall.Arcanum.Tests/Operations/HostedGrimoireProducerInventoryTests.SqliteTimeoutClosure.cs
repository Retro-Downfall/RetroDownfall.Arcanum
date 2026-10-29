using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("connection")]
    [InlineData("command")]
    [InlineData("base-command")]
    public void HostedTimeoutClosedSqliteScalarPropertiesPreserveCleanup(string shape)
    {
        string body = shape switch
        {
            "connection" => "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); int original = connection.DefaultTimeout; connection.DefaultTimeout = original;",
            "command" => "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using var command = connection.CreateCommand(); command.CommandTimeout = 1;",
            _ => "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using System.Data.Common.DbCommand command = connection.CreateCommand(); command.CommandTimeout = 1;",
        };

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission + body + " await Task.CompletedTask;");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("authored")]
    public void HostedTimeoutAbstractProviderDoesNotHideOverrides(string shape)
    {
        string authored = DispatchAuthoredProviders.Replace("public override int CommandTimeout { get; set; }", "public override int CommandTimeout { get => 1; set { System.IO.File.Delete(\"timeout-override\"); } }", StringComparison.Ordinal);

        string value = shape == "unknown" ? "DispatchInput.Command" : "new EvilCommand()";

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission + "System.Data.Common.DbCommand command = " + value + "; command.CommandTimeout = 1; await Task.CompletedTask;", DispatchInputs + authored);

        Assert.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail.StartsWith("System.Data.Common.DbCommand.CommandTimeout", StringComparison.Ordinal))
            || result.Items.Any(static site => site.Callee == "System.IO.File.Delete"), HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("work", true)]
    [InlineData("released", false)]
    public void HostedTimeoutNativeStateMutationRequiresLiveWork(string admission, bool admitted)
    {
        string prefix = admission == "none" ? "" : AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal);

        if (admission == "released") prefix += " lease.Dispose(); ";

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(prefix + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); _ = SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, 0); await Task.CompletedTask;");

        HostedProducerSite site = Assert.Single(result.Items, static site => site.Callee == "SQLitePCL.raw.sqlite3_busy_timeout");

        Assert.Equal(HostedProducerSiteKind.DatabaseAccess, site.Kind);

        Assert.Equal(admitted, site.WorkFrontierCapsuleId is not null);
    }

    [Fact]
    public void HostedTimeoutSetterInputEffectsRemainVisible()
    {
        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); connection.DefaultTimeout = TimeoutInput.Take(); await Task.CompletedTask;", "internal static class TimeoutInput { internal static int Take() { System.IO.File.Delete(\"timeout-input\"); return 1; } }");

        HostedProducerSite site = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(site.WorkFrontierCapsuleId);

        Assert.NotNull(site.EffectFrontierCapsuleId);
    }

    [Fact]
    public void HostedTimeoutActualShortAttemptSourceHasExactCleanupAndNativeEffects()
    {
        const string relative = "src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteBusyRetry.cs";

        CompilationUnitSyntax source = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), relative))).GetCompilationUnitRoot();

        ClassDeclarationSyntax owner = source.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(static type => type.Identifier.ValueText == "SqliteBusyRetry");

        ClassDeclarationSyntax selected = owner.WithMembers(SyntaxFactory.List(owner.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "BeginShortAttempt" or "TryRestoreShortAttempt").Cast<MemberDeclarationSyntax>()))
            .AddMembers(SyntaxFactory.ParseMemberDeclaration("internal static void ReportShortAttemptCleanupFailure(Exception failure, string stage) { }")!);

        SyntaxTree actual = CSharpSyntaxTree.Create(source.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(((FileScopedNamespaceDeclarationSyntax)source.Members.Single()).WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(selected)))), path: relative);

        SyntaxTree globals = CSharpSyntaxTree.ParseText("global using System; global using System.Threading; global using System.Threading.Tasks;", path: "src/Timeout.GlobalUsings.g.cs");

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission
            + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); var settings = RetroDownfall.Arcanum.Infrastructure.Data.SqliteBusyRetry.BeginShortAttempt(connection, token); _ = RetroDownfall.Arcanum.Infrastructure.Data.SqliteBusyRetry.TryRestoreShortAttempt(connection, settings); await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api").AddSyntaxTrees(actual, globals);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = PremiseRequested(compilation);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

        Assert.Contains(result.Items, static site => site.Callee == "Microsoft.Data.Sqlite.SqliteCommand.Dispose");

        HostedProducerSite[] mutations = [.. result.Items.Where(static site => site.Callee == "SQLitePCL.raw.sqlite3_busy_timeout")];

        Assert.NotEmpty(mutations);

        Assert.All(mutations, static site => Assert.NotNull(site.WorkFrontierCapsuleId));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("escape")]
    [InlineData("capture")]
    [InlineData("alias")]
    public void HostedTimeoutCommandCleanupCannotHideCallbacksOrEscapes(string shape)
    {
        string use = shape switch
        {
            "alias" => "var alias = command; alias.Disposed += (_, _) => System.IO.File.Delete(\"disposed-callback\");",
            "event" => "command.Disposed += (_, _) => System.IO.File.Delete(\"disposed-callback\");",
            "capture" => "Action captured = () => command.Disposed += (_, _) => System.IO.File.Delete(\"disposed-callback\"); captured();",
            _ => "TimeoutEscape.Accept(command);",
        };

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using var command = connection.CreateCommand(); " + use + " await Task.CompletedTask;", "internal static class TimeoutEscape { internal static extern void Accept(System.Data.Common.DbCommand command); }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

        if (shape != "escape") Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedTimeoutExactCommandCleanupRequiresLiveWork(bool admitted)
    {
        string admission = admitted ? AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) : "";

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(admission + "var connection = new Microsoft.Data.Sqlite.SqliteConnection(); using var command = connection.CreateCommand(); command.CommandText = \"SELECT 1;\"; _ = command.ExecuteScalar(); await Task.CompletedTask;");

        HostedProducerSite cleanup = Assert.Single(result.Items, static site => site.Callee == "Microsoft.Data.Sqlite.SqliteCommand.Dispose");

        Assert.Equal(HostedProducerSiteKind.DatabaseAccess, cleanup.Kind);

        Assert.Equal(admitted, cleanup.WorkFrontierCapsuleId is not null);
    }

    [Fact]
    public void HostedTimeoutCustomConnectionFactoryCannotProveCommandCleanup()
    {
        string extra = "internal sealed class TimeoutConnection : Microsoft.Data.Sqlite.SqliteConnection { public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand() { System.IO.File.Delete(\"custom-command\"); return new Microsoft.Data.Sqlite.SqliteCommand(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission + "Microsoft.Data.Sqlite.SqliteConnection connection = new TimeoutConnection(); using var command = connection.CreateCommand(); command.CommandText = \"SELECT 1;\"; await Task.CompletedTask;", extra);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");
    }

    [Theory]
    [InlineData("_ = command.CommandTimeout;")]
    [InlineData("command.CommandTimeout += 1;")]
    [InlineData("command.CommandTimeout++;")]
    public void HostedTimeoutCommandGetterRemainsConservative(string read)
    {
        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission + "var command = new Microsoft.Data.Sqlite.SqliteCommand(); " + read + " await Task.CompletedTask;");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteCommand.CommandTimeout");
    }

    private static HostedProducerDiscovery<HostedProducerSite> TimeoutDiscovery(string body, string extra = "")
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body, extra), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return PremiseRequested(compilation);
    }
}
