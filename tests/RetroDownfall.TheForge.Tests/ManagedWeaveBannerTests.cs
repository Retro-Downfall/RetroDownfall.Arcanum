using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ManagedWeaveBannerTests
{
    [Fact]
    public void ShouldShow_WhenEmbeddingsEnabledAndManaged()
    {
        InstanceMetadataDto meta = CreateMeta(embeddingsEnabled: true, mode: "managed");

        Assert.True(ManagedWeaveBanner.ShouldShow(meta));
    }

    [Fact]
    public void ShouldShow_False_WhenDisabledOrUnavailable()
    {
        Assert.False(ManagedWeaveBanner.ShouldShow(CreateMeta(embeddingsEnabled: false, mode: "disabled")));

        Assert.False(ManagedWeaveBanner.ShouldShow(CreateMeta(embeddingsEnabled: true, mode: "unavailable")));

        Assert.False(ManagedWeaveBanner.ShouldShow(null));
    }

    [Fact]
    public void Managed_banner_describes_complete_search_without_a_hypothetical_accelerator()
    {
        Assert.Contains("complete", ManagedWeaveBanner.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("50,000", ManagedWeaveBanner.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("fallback", ManagedWeaveBanner.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("sqlite-vec", ManagedWeaveBanner.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static InstanceMetadataDto CreateMeta(bool embeddingsEnabled, string mode) =>
        new(
            "1.0",
            "os",
            "rid",
            1,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            false,
            "/tmp",
            "/tmp/arcanum.json",
            5001,
            false,
            false,
            false,
            5443,
            null,
            "http://localhost:5001",
            embeddingsEnabled,
            mode,
            "diagnostic",
            0,
            "local",
            false,
            false,
            false,
            false,
            "disabled",
            null,
            null,
            0);
}
