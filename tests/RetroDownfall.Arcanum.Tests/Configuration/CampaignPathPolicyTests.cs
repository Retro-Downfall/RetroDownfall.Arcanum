using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class CampaignPathPolicyTests : IClassFixture<TempWorkspace>
{

    private readonly TempWorkspace _workspace;

    public CampaignPathPolicyTests(TempWorkspace workspace)
    {

        _workspace = workspace;

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAndNormalizePath_EmptyPath_ReturnsInvalidPath(string path)
    {

        ArcanumSettings settings = new();

        Result<string> result = CampaignPathPolicy.ValidateAndNormalizePath(path, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Campaign.InvalidPath", result.Error.Code);

    }

    [Fact]
    public void ValidateAndNormalizePath_NonexistentDirectory_ReturnsInvalidPath()
    {

        string missing = Path.Combine(_workspace.Root, "does-not-exist");

        ArcanumSettings settings = new();

        Result<string> result = CampaignPathPolicy.ValidateAndNormalizePath(missing, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Campaign.InvalidPath", result.Error.Code);

        Assert.Contains("does not exist", result.Error.Message);

    }

    [Fact]
    public void ValidateAndNormalizePath_EmptyAllowedRoots_DeniesEvenExistingDirectory()
    {

        string campaignDir = _workspace.CreateSubdir("campaign");

        ArcanumSettings settings = new()
        {
            Campaigns = new CampaignsSettings { AllowedRoots = [] },
        };

        Result<string> result = CampaignPathPolicy.ValidateAndNormalizePath(campaignDir, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Campaign.PathNotAllowed", result.Error.Code);

    }

    [Fact]
    public void ValidateAndNormalizePath_UnderAllowedRoot_ReturnsNormalizedPath()
    {

        string campaignDir = _workspace.CreateSubdir("allowed-campaign");

        ArcanumSettings settings = new()
        {
            Campaigns = new CampaignsSettings { AllowedRoots = [_workspace.Root] },
        };

        Result<string> result = CampaignPathPolicy.ValidateAndNormalizePath(campaignDir, settings);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(campaignDir), result.Value);

    }

    [Fact]
    public void ValidateAndNormalizePath_OutsideAllowedRoot_ReturnsPathNotAllowed()
    {

        string outsideRoot = _workspace.CreateSubdir("outside");

        string otherRoot = _workspace.CreateSubdir("other-root");

        ArcanumSettings settings = new()
        {
            Campaigns = new CampaignsSettings { AllowedRoots = [otherRoot] },
        };

        Result<string> result = CampaignPathPolicy.ValidateAndNormalizePath(outsideRoot, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Campaign.PathNotAllowed", result.Error.Code);

    }

}
