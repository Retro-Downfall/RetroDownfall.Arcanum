using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkParameterCountAcceptsExactSqliteFactoryReceivers(bool widened)
    {
        string declaration = widened ? "System.Data.Common.DbCommand" : "var";

        HostedProducerDiscovery<HostedProducerSite> result = DispatchDiscover(
            declaration + " command = new Microsoft.Data.Sqlite.SqliteConnection().CreateCommand(); _ = command.Parameters.Count;");

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void FrameworkStreamBaseProfileKeepsFileStreamConstructionPhysical()
    {
        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(CensusCompileSource(
            "_ = new System.IO.FileStream(\"physical-constructor\", System.IO.FileMode.Create);", helpers: "", admitted: false));

        HostedProducerSite site = Assert.Single(result.Items, static site => site.Callee == "System.IO.FileStream..ctor");

        Assert.Equal(HostedProducerSiteKind.FileSystemEffect, site.Kind);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.FileStream..ctor");
    }
}
