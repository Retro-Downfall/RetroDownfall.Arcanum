using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliContextPrecedenceTests
{

    [Fact]
    public void Resolve_prefers_explicit_then_active_then_directory_then_server_default()
    {

        Guid explicitCampaign = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid activeSession = Guid.Parse("22222222-2222-2222-2222-222222222222");

        CliContextDocument active = CliContextDocument.Empty with
        {
            CampaignId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            WorkspacePath = "/active/workspace",
            Model = "active-model",
            SessionId = activeSession,
        };

        CliEffectiveContext result = CliContextPrecedence.Resolve(
            new CliContextResolutionRequest(
                ExplicitCampaignId: explicitCampaign,
                ExplicitWorkspacePath: null,
                ExplicitModel: "explicit-model",
                ExplicitSessionId: null,
                Active: active,
                DetectedCampaignId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
                DetectedCampaignPath: "/detected/campaign",
                DetectedWorkspacePath: "/detected/workspace",
                ServerDefaultModel: "server-model",
                NoContext: false));

        Assert.Equal(explicitCampaign, result.Campaign.Value);

        Assert.Equal(CliContextSource.ExplicitOption, result.Campaign.Source);

        Assert.Equal("/active/workspace", result.Workspace.Value);

        Assert.Equal(CliContextSource.ActiveContext, result.Workspace.Source);

        Assert.Equal("explicit-model", result.Model.Value);

        Assert.Equal(CliContextSource.ExplicitOption, result.Model.Source);

        Assert.Equal(activeSession, result.Session.Value);

        Assert.Equal(CliContextSource.ActiveContext, result.Session.Source);

    }

    [Fact]
    public void Resolve_no_context_bypasses_saved_values_but_keeps_directory_detection()
    {

        Guid detectedCampaign = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        CliContextDocument active = CliContextDocument.Empty with
        {
            CampaignId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            WorkspacePath = "/active/workspace",
            Model = "active-model",
            SessionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        };

        CliEffectiveContext result = CliContextPrecedence.Resolve(
            new CliContextResolutionRequest(
                ExplicitCampaignId: null,
                ExplicitWorkspacePath: null,
                ExplicitModel: null,
                ExplicitSessionId: null,
                Active: active,
                DetectedCampaignId: detectedCampaign,
                DetectedCampaignPath: "/detected/campaign",
                DetectedWorkspacePath: "/detected/workspace",
                ServerDefaultModel: "server-model",
                NoContext: true));

        Assert.Equal(detectedCampaign, result.Campaign.Value);

        Assert.Equal(CliContextSource.CurrentDirectory, result.Campaign.Source);

        Assert.Equal("/detected/workspace", result.Workspace.Value);

        Assert.Equal(CliContextSource.CurrentDirectory, result.Workspace.Source);

        Assert.Equal("server-model", result.Model.Value);

        Assert.Equal(CliContextSource.ServerDefault, result.Model.Source);

        Assert.Null(result.Session.Value);

        Assert.Equal(CliContextSource.ServerDefault, result.Session.Source);

    }

}
