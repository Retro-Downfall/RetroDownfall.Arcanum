using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableTheory]
    [InlineData("agents")]
    [InlineData("workspace-leases")]
    public async Task Operator_discovery_and_lease_list_return_bounded_empty_page(string resource)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/guild/{resource}?pageSize=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement data = json.RootElement.GetProperty("data");

        Assert.Empty(data.GetProperty("items").EnumerateArray());

        Assert.Equal("livePerPage", data.GetProperty("stateConsistency").GetString());

        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);

        using HttpResponseMessage invalid = await client.GetAsync($"/api/guild/{resource}?pageSize=0");

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [SkippableTheory]
    [InlineData("agents/refresh")]
    [InlineData("workspace-leases")]
    [InlineData("workspace-leases/00000000-0000-0000-0000-000000000001/renew")]
    [InlineData("workspace-leases/00000000-0000-0000-0000-000000000001/release")]
    public async Task Operator_discovery_and_lease_mutations_refuse_missing_key_before_effect(string route)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync($"/api/guild/{route}", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
