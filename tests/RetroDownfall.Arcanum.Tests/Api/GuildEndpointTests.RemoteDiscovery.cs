using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Remote_resolution_is_operator_authenticated_and_rejects_unconfigured_destination()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.A2AClient = true;

                return settings;
            },
        };

        using HttpClient anonymous = factory.CreateClient();

        using HttpResponseMessage denied = await anonymous.PostAsync("/api/guild/agents/remote/resolve", Json("{\"agent\":\"https://unconfigured.example/\"}"));

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage refused = await client.PostAsync("/api/guild/agents/remote/resolve", Json("{\"agent\":\"https://unconfigured.example/\"}"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());

        Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Mounted_peer_work_rejects_master_before_binding_invalid_body()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.A2AServer = true;

                settings.Integrations.A2A.ServerPath = "/custom-peers";

                return settings;
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage refused = await client.PostAsync("/api/custom-peers", Json("{invalid"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());

        Assert.Equal(ErrorCodes.Guild.PeerGrantRequired, body.RootElement.GetProperty("error").GetProperty("code").GetString());

        using HttpResponseMessage discovered = await client.GetAsync("/api/custom-peers/agent-card");

        Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
    }
}
