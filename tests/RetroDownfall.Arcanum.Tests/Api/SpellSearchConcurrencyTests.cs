using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
[Trait("Category", "Integration")]
public sealed class SpellSearchConcurrencyTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellSearchConcurrencyTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task Concurrent_spell_search_requests_complete_without_dbcontext_conflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync("/api/spells/search?q=test"))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        foreach (HttpResponseMessage response in responses)
        {

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string json = await response.Content.ReadAsStringAsync();

            ApiResponse<SpellSummary[]>? body =
                JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellSummaryArray);

            Assert.NotNull(body);

            Assert.True(body!.IsSuccess);

            Assert.NotNull(body.Data);

        }

    }

}
