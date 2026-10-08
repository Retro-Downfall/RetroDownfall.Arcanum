using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class PromptEndpointTests(ArcanumWebApplicationFactory factory)
{
    [SkippableFact]
    public async Task Update_renaming_to_an_existing_name_and_version_answers_400_DuplicateVersion()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        string taken = $"taken-{Guid.NewGuid():N}";

        _ = await CreatePromptAsync(client, taken, "1.0.0");

        Guid other = await CreatePromptAsync(client, $"other-{Guid.NewGuid():N}", "1.0.0");

        HttpResponseMessage response = await PutAsync(client, other, new UpdatePromptRequest(taken, null, null, null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Prompt.DuplicateVersion, await ReadErrorCodeAsync(response));

        // The rejected rename left the prompt as it was.
        PromptDetailDto unchanged = await GetAsync(client, other);

        Assert.NotEqual(taken, unchanged.Name);
    }

    [SkippableFact]
    public async Task Update_changing_only_the_version_onto_an_existing_pair_answers_400_DuplicateVersion()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        string name = $"versions-{Guid.NewGuid():N}";

        _ = await CreatePromptAsync(client, name, "2.0.0");

        Guid first = await CreatePromptAsync(client, name, "1.0.0");

        HttpResponseMessage response = await PutAsync(client, first, new UpdatePromptRequest(null, " 2.0.0 ", null, null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Prompt.DuplicateVersion, await ReadErrorCodeAsync(response));
    }

    [SkippableFact]
    public async Task Update_keeping_its_own_name_and_version_does_not_collide_with_itself()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        string name = $"self-{Guid.NewGuid():N}";

        Guid id = await CreatePromptAsync(client, name, "1.0.0");

        HttpResponseMessage response = await PutAsync(client, id, new UpdatePromptRequest($" {name} ", "1.0.0", "edited", null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData("  ")]
    [InlineData("")]
    public async Task Update_with_blank_name_answers_400_InvalidName(string name)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        string original = $"blank-name-{Guid.NewGuid():N}";

        Guid id = await CreatePromptAsync(client, original, "1.0.0");

        HttpResponseMessage response = await PutAsync(client, id, new UpdatePromptRequest(name, null, null, null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Prompt.InvalidName, await ReadErrorCodeAsync(response));

        Assert.Equal(original, (await GetAsync(client, id)).Name);
    }

    [SkippableFact]
    public async Task Update_with_blank_version_answers_400_InvalidVersion()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await CreatePromptAsync(client, $"blank-version-{Guid.NewGuid():N}", "1.0.0");

        HttpResponseMessage response = await PutAsync(client, id, new UpdatePromptRequest(null, "   ", null, null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Prompt.InvalidVersion, await ReadErrorCodeAsync(response));

        Assert.Equal("1.0.0", (await GetAsync(client, id)).Version);
    }

    /// <summary>
    /// Before names were validated on update, a blank name was stored as "". An update that does not
    /// name the prompt must still be able to change the rest of such a row; only a name the caller
    /// sends is validated.
    /// </summary>
    [SkippableFact]
    public async Task Update_that_omits_the_name_of_a_row_stored_with_a_blank_name_succeeds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        // A version of its own keeps the planted "" name clear of every other global prompt.
        string version = $"legacy-{Guid.NewGuid():N}";

        Guid id = await CreatePromptAsync(client, $"legacy-{Guid.NewGuid():N}", version);

        await SetStoredNameAsync(id, string.Empty);

        HttpResponseMessage response = await PutAsync(client, id, new UpdatePromptRequest(null, null, "described later", null, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PromptDetailDto updated = await GetAsync(client, id);

        Assert.Equal("described later", updated.Description);

        Assert.Equal(string.Empty, updated.Name);

        Assert.Equal(version, updated.Version);
    }

    [SkippableFact]
    public async Task Create_with_an_unknown_campaignId_answers_404()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        CreatePromptRequest request = new(
            $"orphan-{Guid.NewGuid():N}",
            "1.0.0",
            "Hello",
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Guid.NewGuid());

        HttpResponseMessage response = await client.PostAsync(
            "/api/prompts",
            new StringContent(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(ErrorCodes.Campaign.NotFound, await ReadErrorCodeAsync(response));
    }

    [SkippableFact]
    public async Task Clone_into_an_unknown_campaignId_answers_404()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid source = await CreatePromptAsync(client, $"clone-source-{Guid.NewGuid():N}", "1.0.0");

        ClonePromptRequest request = new($"clone-target-{Guid.NewGuid():N}", "1.0.0", Guid.NewGuid());

        HttpResponseMessage response = await client.PostAsync(
            $"/api/prompts/{source}/clone",
            new StringContent(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ClonePromptRequest), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(ErrorCodes.Campaign.NotFound, await ReadErrorCodeAsync(response));
    }

    [SkippableFact]
    public async Task Import_into_an_unknown_campaignId_answers_404()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        string body = "{\"payload\":{\"name\":\"imported-" + Guid.NewGuid().ToString("N") + "\",\"version\":\"1.0.0\",\"template\":\"Hello\",\"tags\":[]},\"campaignId\":\"" + Guid.NewGuid() + "\"}";

        HttpResponseMessage response = await client.PostAsync(
            "/api/prompts/import",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> CreatePromptAsync(HttpClient client, string name, string version)
    {
        CreatePromptRequest request = new(
            name,
            version,
            "Hello",
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        HttpResponseMessage response = await client.PostAsync(
            "/api/prompts",
            new StringContent(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        return body!.Data!.Id;
    }

    /// <summary>Writes a name straight into the row, the way a prompt stored before update validation existed carries it.</summary>
    private async Task SetStoredNameAsync(Guid id, string name)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """UPDATE "Prompts" SET "Name" = @name WHERE upper("Id") = upper(@id)""";

        DbParameter nameParameter = command.CreateParameter();

        nameParameter.ParameterName = "@name";

        nameParameter.Value = name;

        command.Parameters.Add(nameParameter);

        DbParameter idParameter = command.CreateParameter();

        idParameter.ParameterName = "@id";

        idParameter.Value = id.ToString();

        command.Parameters.Add(idParameter);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, UpdatePromptRequest request) =>
        client.PutAsync(
            $"/api/prompts/{id}",
            new StringContent(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.UpdatePromptRequest), Encoding.UTF8, "application/json"));

    private static async Task<PromptDetailDto> GetAsync(HttpClient client, Guid id)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/prompts/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        return body!.Data!;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        return body.Error?.Code;
    }
}
