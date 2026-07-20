using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CampaignPathComparerTests
{

    [Fact]
    public void Loopback_NormalizesTrailingSeparators()
    {

        string temp = Path.Combine(Path.GetTempPath(), $"forge-path-{Guid.NewGuid():N}");

        Directory.CreateDirectory(temp);

        try
        {

            Assert.True(CampaignPathComparer.TryNormalize(temp + Path.DirectorySeparatorChar, loopback: true, out string a, out _));

            Assert.True(CampaignPathComparer.TryNormalize(temp, loopback: true, out string b, out _));

            Assert.True(CampaignPathComparer.PathsEqual(a, b, loopback: true));

        }
        finally
        {

            Directory.Delete(temp, recursive: true);

        }

    }

    [Fact]
    public void Remote_DoesNotUseGetFullPath_AndUsesOrdinal()
    {

        Assert.True(CampaignPathComparer.TryNormalize(@"C:\Campaigns\Demo\", loopback: false, out string normalized, out _));

        Assert.Equal(@"C:\Campaigns\Demo", normalized);

        Assert.False(CampaignPathComparer.PathsEqual(@"C:\Campaigns\Demo", @"c:\campaigns\demo", loopback: false));

        Assert.True(CampaignPathComparer.PathsEqual(@"C:\Campaigns\Demo", @"C:\Campaigns\Demo", loopback: false));

    }

    [Fact]
    public void ProposeNameFromPath_RequiresSegment()
    {

        Assert.Equal("Demo", CampaignPathComparer.ProposeNameFromPath("/var/campaigns/Demo", loopback: false));

        Assert.Null(CampaignPathComparer.ProposeNameFromPath("/", loopback: false));

    }

    [Fact]
    public void FindUnambiguousMatch_ReturnsNullWhenAmbiguous()
    {

        CampaignDto first = NewCampaign("A", "/campaigns/same");

        CampaignDto second = NewCampaign("B", "/campaigns/same");

        CampaignDto? match = CampaignPathComparer.FindUnambiguousMatch(
            [first, second],
            "/campaigns/same",
            loopback: false);

        Assert.Null(match);

    }

    [Fact]
    public void ArcanumHostLocality_DetectsLoopback()
    {

        Assert.True(ArcanumHostLocality.IsLoopbackBaseUrl("http://localhost:5001"));

        Assert.True(ArcanumHostLocality.IsLoopbackBaseUrl("http://127.0.0.1:5001"));

        Assert.False(ArcanumHostLocality.IsLoopbackBaseUrl("https://arcanum.example.com"));

    }

    private static CampaignDto NewCampaign(string name, string path) =>
        new(
            Guid.NewGuid(),
            name,
            path,
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

}
