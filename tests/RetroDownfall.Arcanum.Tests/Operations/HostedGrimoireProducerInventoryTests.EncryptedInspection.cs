using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("RetroDownfall.Arcanum.Core.Storage", "IEncryptedBlobStore", "InspectAuthenticatedAsync", true)]
    [InlineData("Lookalike.Storage", "IEncryptedBlobStore", "InspectAuthenticatedAsync", false)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage", "OtherBlobStore", "InspectAuthenticatedAsync", false)]
    [InlineData("RetroDownfall.Arcanum.Core.Storage", "IEncryptedBlobStore", "InspectAuthenticatedOtherAsync", false)]
    public void Authenticated_plaintext_inspection_is_an_exact_read_boundary(
        string space, string type, string method, bool expectedRead)
    {
        string helper = $$"""
            namespace {{space}}
            {
                public interface {{type}}
                {
                    System.Threading.Tasks.Task<object> {{method}}(string path);
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(
            $"{space}.{type} store = null!; _ = await store.{method}(\"blob\");", helper));

        Assert.Equal(expectedRead, result.Items.Any(site =>
            site.Kind == HostedProducerSiteKind.FileSystemRead
            && site.Callee == $"{space}.{type}.{method}"));

        if (!expectedRead)
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED");
        }
        else
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
        }
    }

    [Fact]
    public void Authenticated_inspection_boundary_does_not_excuse_an_unknown_stream_cleanup()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(
            "Unknown factory = null!; await using System.IO.Stream stream = factory.Open();",
            "interface Unknown { System.IO.Stream Open(); }"));

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.Contains("System.IO.Stream.DisposeAsync", StringComparison.Ordinal));
    }
}
