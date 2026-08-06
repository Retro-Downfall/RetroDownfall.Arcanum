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
    public void ShouldShow_False_WhenDisabledOrVec0()
    {

        Assert.False(ManagedWeaveBanner.ShouldShow(CreateMeta(embeddingsEnabled: false, mode: "disabled")));

        Assert.False(ManagedWeaveBanner.ShouldShow(CreateMeta(embeddingsEnabled: true, mode: "vec0")));

        Assert.False(ManagedWeaveBanner.ShouldShow(null));

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
            false,
            false,
            false,
            5443,
            null,
            "http://localhost:5001",
            embeddingsEnabled,
            mode,
            "diagnostic",
            50_000,
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
