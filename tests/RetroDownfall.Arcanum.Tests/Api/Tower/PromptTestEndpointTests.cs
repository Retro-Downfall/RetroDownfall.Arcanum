using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

public sealed class PromptTestEndpointTests : IDisposable
{
    private const string Sentinel = "SENTINEL-prompt-test-codex-5d41c0a7";

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        $"arcanum-prompt-test-{Guid.NewGuid():N}");

    public PromptTestEndpointTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [SkippableFact]
    public async Task Test_with_codexPath_under_a_workingDirectory_outside_the_configured_roots_is_refused()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "outside");

        Directory.CreateDirectory(directory);

        string secret = Path.Combine(directory, "secret.txt");

        File.WriteAllText(secret, Sentinel);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, secret, null));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Contains(ErrorCodes.Spell.PathNotAllowed, json);

        Assert.DoesNotContain(Sentinel, json);
    }

    [SkippableFact]
    public async Task Test_with_a_workingDirectory_outside_the_configured_roots_is_refused_even_without_a_codexPath()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "outside-no-codex");

        Directory.CreateDirectory(directory);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Test_without_a_workingDirectory_still_succeeds_when_no_workspace_roots_are_configured()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Test_with_codexPath_under_a_configured_root_still_reads_the_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "allowed");

        Directory.CreateDirectory(directory);

        string codex = Path.Combine(directory, "codex.md");

        File.WriteAllText(codex, Sentinel);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([_scratch]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, codex, null));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(Sentinel, json);
    }

    private static ArcanumWebApplicationFactory CreateFactoryWithSpellRoots(string[] spellRoots) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Security = settings.Security with
                {
                    SpellWorkspaceRoots = spellRoots,
                },
            },
        };

    private static async Task<Guid> CreateGlobalPromptAsync(HttpClient client)
    {
        CreatePromptRequest request = new(
            $"prompt-test-{Guid.NewGuid():N}",
            "1.0.0",
            "Hello",
            "Test prompt",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/prompts",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        return body!.Data!.Id;
    }

    private static async Task<HttpResponseMessage> PostTestAsync(HttpClient client, Guid promptId, TestPromptRequest request)
    {
        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.TestPromptRequest);

        return await client.PostAsync(
            $"/api/prompts/{promptId}/test",
            new StringContent(payload, Encoding.UTF8, "application/json"));
    }
}
