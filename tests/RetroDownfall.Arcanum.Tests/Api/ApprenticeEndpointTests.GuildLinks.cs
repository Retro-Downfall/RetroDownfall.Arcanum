using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class ApprenticeEndpointTests
{
    [SkippableTheory]
    [InlineData("parentId", false)]
    [InlineData("rootId", false)]
    [InlineData("parentId", true)]
    [InlineData("rootId", true)]
    public async Task Apprentice_link_filter_never_falls_back_to_unfiltered_rows(string filter, bool legacyIdentity)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        Guid legacy = await CreateApprenticeAsync(client);

        try
        {
            Guid selector = legacyIdentity ? legacy : Guid.NewGuid();

            using HttpResponseMessage response = await client.GetAsync($"/api/apprentices?{filter}={selector:D}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Empty(json.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());

            Assert.False(json.RootElement.GetProperty("data").GetProperty("hasMore").GetBoolean());
        }
        finally
        {
            using HttpResponseMessage cleanup = await client.DeleteAsync($"/api/apprentices/{legacy:D}");
        }
    }

    [SkippableFact]
    public async Task Apprentice_legacy_list_and_detail_expose_explicit_null_quest_identity()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        Guid legacy = await CreateApprenticeAsync(client);

        try
        {
            using HttpResponseMessage detail = await client.GetAsync($"/api/apprentices/{legacy:D}");

            using JsonDocument detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());

            Assert.True(detailJson.RootElement.GetProperty("data").TryGetProperty("guildQuestId", out JsonElement detailQuest));

            Assert.Equal(JsonValueKind.Null, detailQuest.ValueKind);

            using HttpResponseMessage list = await client.GetAsync("/api/apprentices");

            using JsonDocument listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

            JsonElement row = listJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == legacy);

            Assert.True(row.TryGetProperty("guildQuestId", out JsonElement listQuest));

            Assert.Equal(JsonValueKind.Null, listQuest.ValueKind);
        }
        finally
        {
            using HttpResponseMessage cleanup = await client.DeleteAsync($"/api/apprentices/{legacy:D}");
        }
    }
}
