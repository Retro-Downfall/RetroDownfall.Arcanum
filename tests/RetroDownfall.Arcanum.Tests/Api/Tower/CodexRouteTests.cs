using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The codex routes through the real host, for the answers the helper tests cannot see.
/// </summary>
[Collection("ApiHost")]
public sealed class CodexRouteTests(ArcanumWebApplicationFactory factory)
{
    [SkippableTheory]
    [InlineData("{}")]
    [InlineData("""{"content":null}""")]
    public async Task PutCodex_with_null_content_answers_400(string body)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PutAsync(
            "/api/codex",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CodexContentDto>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCodexContentDto);

        Assert.NotNull(envelope);

        Assert.False(envelope.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, envelope.Error?.Code);
    }
}
