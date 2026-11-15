using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkParameterClearAcceptsOnlyTheClosedCommandFactoryChain(bool widened)
    {
        string declaration = widened ? "System.Data.Common.DbCommand" : "var";

        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(
            declaration + " command = new Microsoft.Data.Sqlite.SqliteConnection().CreateCommand(); command.Parameters.Clear();",
            ParameterOverrideSources);

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("opaque-sqlite", "ParameterReceiverInput.Value.Clear();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Clear")]
    [InlineData("opaque-base", "DispatchInput.Parameters.Clear();", "System.Data.Common.DbParameterCollection.Clear")]
    [InlineData("authored-base", "Microsoft.Data.Sqlite.SqliteParameterCollection value = new AuthoredSqliteParameters(); value.Clear();", "Microsoft.Data.Sqlite.SqliteParameterCollection.Clear")]
    [InlineData("command-override", "Microsoft.Data.Sqlite.SqliteCommand command = new AuthoredParametersCommand(); command.Parameters.Clear();", "Microsoft.Data.Sqlite.SqliteCommand.Parameters")]
    public void ParameterClearDoesNotHideAnOpaqueOrAuthoredReceiver(string shape, string body, string refusedCallee)
    {
        const string command = """
            internal sealed class AuthoredParametersCommand : Microsoft.Data.Sqlite.SqliteCommand
            {
                public override Microsoft.Data.Sqlite.SqliteParameterCollection Parameters
                {
                    get
                    {
                        System.IO.File.Delete("parameter-command-override");

                        return new AuthoredSqliteParameters();
                    }
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(body, ParameterOverrideSources + command);

        // The refusal belongs to this receiver operation; an unrelated global premise
        // or constructor diagnostic must not satisfy the test.
        Assert.True(DispatchRefused(result, refusedCallee), shape + ": "
            + string.Join(global::System.Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void ParameterClearCannotCreateDatabaseAdmission()
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(
            "var command = new Microsoft.Data.Sqlite.SqliteConnection().CreateCommand(); command.Parameters.Clear(); command.ExecuteNonQuery(); await Task.CompletedTask;"),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.False(DispatchRefused(result, "Microsoft.Data.Sqlite.SqliteParameterCollection.Clear"));

        HostedProducerSite execution = Assert.Single(result.Items, static site =>
            site.Callee == "Microsoft.Data.Sqlite.SqliteCommand.ExecuteNonQuery");

        Assert.Null(execution.WorkFrontierCapsuleId);

        Assert.Null(execution.RegisteredRootWorkFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteCommand.ExecuteNonQuery");
    }

    [Fact]
    public void AuthoredParameterCollectionSpellingDoesNotHideItsClearBody()
    {
        const string source = """
            namespace Microsoft.Data.Sqlite
            {
                internal sealed class SqliteParameterCollection
                {
                    internal void Clear() => System.IO.File.Delete("authored-parameters");
                }
            }
            """;

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(
            "new Microsoft.Data.Sqlite.SqliteParameterCollection().Clear(); await Task.CompletedTask;", source),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        HostedProducerSite effect = Assert.Single(EnumerationDeleteSites(result));

        Assert.Null(effect.WorkFrontierCapsuleId);

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void ForeignParameterCollectionClearDoesNotReceiveTheTrustedProviderProfile()
    {
        const string foreignSource = """
            namespace Microsoft.Data.Sqlite
            {
                public class SqliteParameterCollection
                {
                    public virtual void Clear() => System.IO.File.Delete("foreign-parameters");
                }
            }
            """;

        CSharpCompilation foreign = CompileWithProductionReferencePack(foreignSource, "RetroDownfall.Arcanum.Api")
            .WithAssemblyName("Foreign.ParameterCollection");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(
            R2Admission + "new counterfeit::Microsoft.Data.Sqlite.SqliteParameterCollection().Clear(); await Task.CompletedTask;"),
            "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.True(DispatchRefused(result, "Microsoft.Data.Sqlite.SqliteParameterCollection.Clear"));
    }
}
