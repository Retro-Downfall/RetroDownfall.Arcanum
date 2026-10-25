using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class CampaignEndpointTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_campaign_bundle_does_not_rescan_existing_spells_per_item(bool export)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string sentinel = string.Empty;

        int opens = 0;

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(
                new CatalogReadObserver(path =>
                {
                    if (path == sentinel)
                    {
                        Interlocked.Increment(ref opens);
                    }
                })),
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "catalog-count");

        sentinel = Path.Combine(campaign.Path, "sentinel", "SPELL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);

        await File.WriteAllTextAsync(sentinel, "---\nname: sentinel\n---\nbody");

        string[] spells = Enumerable.Range(0, 6).Select(i =>
            "{\"name\":\"batch-" + i + "\",\"fullContent\":\"---\\nname: batch-" + i + "\\n---\\nbody\",\"scripts\":[]}").ToArray();

        if (export)
        {
            for (int i = 0; i < spells.Length; i++)
            {
                string directory = Path.Combine(campaign.Path, "batch-" + i);

                Directory.CreateDirectory(directory);

                await File.WriteAllTextAsync(Path.Combine(directory, "SPELL.md"), $"---\nname: batch-{i}\n---\nbody");
            }
        }

        try
        {
            string payload = "{\"strategy\":\"merge\",\"payload\":{\"campaign\":" + CampaignJson(campaign)
                + ",\"spells\":[" + string.Join(',', spells.Concat([spells[0]])) + "]}}";

            HttpResponseMessage response = await client.PostAsync(
                $"/api/campaigns/{campaign.Id}/" + (export ? "export" : "import"),
                new StringContent(export ? "{}" : payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // One discovery read; export also verifies, hydrates and reads the actual bundle bytes.
            Assert.InRange(opens, 1, export ? 4 : 1);

            string responseBody = await response.Content.ReadAsStringAsync();

            if (export)
            {
                CampaignExportDto bundle = JsonSerializer.Deserialize(
                    responseBody, ArcanumJsonContext.Default.ApiResponseCampaignExportDto)!.Data!;

                Assert.Equal(7, bundle.Spells.Count);

                Assert.Equal("---\nname: sentinel\n---\nbody", Assert.Single(bundle.Spells, s => s.Name == "sentinel").FullContent);
            }
            else
            {
                CampaignImportResultDto imported = JsonSerializer.Deserialize(
                    responseBody, ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto)!.Data!;

                Assert.Equal(6, imported.SpellsImported);

                Assert.Contains("batch-0", Assert.Single(imported.Warnings));
            }
        }
        finally
        {
            global::RetroDownfall.Arcanum.Infrastructure.Security.SecureFileReader.AfterRegularFileOpenedForTests = null;
        }
    }

    [SkippableTheory]
    [InlineData("header")]
    [InlineData("sanctum")]
    [InlineData("import")]
    [InlineData("workspace")]
    public async Task FieldWrites_real_requests_preserve_interleaved_edits(string firstWriter)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Func<Task>? afterRead = null;

        int interleaved = 0;

        int reads = 0;

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ICampaignRepository>();

            services.AddScoped<ICampaignRepository>(provider => new InterleavingCampaignRepository(
                ActivatorUtilities.CreateInstance<CampaignRepository>(provider),
                async () =>
                {
                    if (afterRead is not null
                        && (firstWriter != "workspace" || Interlocked.Increment(ref reads) == 2)
                        && Interlocked.Exchange(ref interleaved, 1) == 0)
                    {
                        await afterRead();
                    }
                }));
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "field-writes");

        string header = $"/api/campaigns/{campaign.Id}";

        string sanctum = header + "/sanctum";

        afterRead = async () =>
        {
            if (firstWriter is "sanctum" or "import")
            {
                using HttpResponseMessage renamed = await client.PutAsync(header, JsonBody("""{"name":"Concurrent rename","type":"custom","description":"New description"}"""));

                Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            }

            if (firstWriter != "sanctum")
            {
                using HttpResponseMessage enabled = await client.PutAsync(sanctum, JsonBody("""{"enabled":true}"""));

                Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            }

            if (firstWriter == "workspace")
            {
                using HttpResponseMessage settings = await client.PutAsync(header, JsonBody("""{"settings":{"defaultModel":"imported-model"},"description":"New description"}"""));

                Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            }
        };

        using HttpResponseMessage response = firstWriter switch
        {
            "header" => await client.PutAsync(header, JsonBody("""{"name":"Concurrent rename","type":"custom","description":"New description"}""")),
            "sanctum" => await client.PutAsync(sanctum, JsonBody("""{"enabled":true}""")),
            "workspace" => await client.PutAsync($"/api/workspaces/{campaign.Id}", JsonBody("""{"name":"Concurrent rename","type":"custom"}""")),
            _ => await client.PostAsync(header + "/import", JsonBody("""{"strategy":"merge","payload":{"campaign":{"name":"Ignored bundle name","settings":{"defaultModel":"imported-model"}},"spells":[],"prompts":[]}}""")),
        };

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        Assert.Equal(1, interleaved);

        using HttpResponseMessage read = await client.GetAsync(header);

        ApiResponse<CampaignDto> body = JsonSerializer.Deserialize(await read.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCampaignDto)!;

        Assert.Equal("Concurrent rename", body.Data!.Name);

        Assert.Equal(WorkspaceType.Custom, body.Data.Type);

        Assert.Equal("New description", body.Data.Description);

        using HttpResponseMessage readSanctum = await client.GetAsync(sanctum);

        using JsonDocument sanctumJson = JsonDocument.Parse(await readSanctum.Content.ReadAsStringAsync());

        Assert.True(sanctumJson.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());

        if (firstWriter is "workspace" or "import")
        {
            Assert.Equal("imported-model", body.Data.Settings.DefaultModel);
        }
    }

    [SkippableTheory]
    [InlineData("header")]
    [InlineData("sanctum")]
    [InlineData("import")]
    [InlineData("workspace")]
    public async Task FieldWrites_deleted_during_request_returns_typed_NotFound(string writer)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Func<Task>? delete = null;

        int reads = 0;

        int deleted = 0;

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ICampaignRepository>();

            services.AddScoped<ICampaignRepository>(provider => new InterleavingCampaignRepository(
                ActivatorUtilities.CreateInstance<CampaignRepository>(provider),
                async () =>
                {
                    if (delete is not null
                        && (writer != "workspace" || Interlocked.Increment(ref reads) == 2)
                        && Interlocked.Exchange(ref deleted, 1) == 0)
                    {
                        await delete();
                    }
                }));
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "deleted-race");

        string path = $"/api/campaigns/{campaign.Id}";

        delete = async () =>
        {
            using HttpResponseMessage removed = await client.DeleteAsync(path);

            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        };

        using HttpResponseMessage response = writer switch
        {
            "header" => await client.PutAsync(path, JsonBody("""{"description":"Late"}""")),
            "sanctum" => await client.PutAsync(path + "/sanctum", JsonBody("""{"enabled":true}""")),
            "workspace" => await client.PutAsync($"/api/workspaces/{campaign.Id}", JsonBody("""{"name":"Late"}""")),
            _ => await client.PostAsync(path + "/import", JsonBody("""{"payload":{"campaign":{"settings":{"defaultModel":"late"}},"spells":[],"prompts":[]}}""")),
        };

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(writer == "workspace" ? "Workspace.NotFound" : "Campaign.NotFound", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldWrites_rename_loses_real_unique_race_returns_typed_BadRequest(bool workspace)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Func<Task>? takeName = null;

        int renamed = 0;

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ICampaignRepository>();

            services.AddScoped<ICampaignRepository>(provider => new InterleavingCampaignRepository(
                ActivatorUtilities.CreateInstance<CampaignRepository>(provider),
                () => Task.CompletedTask,
                async () =>
                {
                    if (takeName is not null && Interlocked.Exchange(ref renamed, 1) == 0)
                    {
                        await takeName();
                    }
                }));
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto loser = await CreateCampaignWithOnePromptAsync(factory, client, "rename-loser");

        CampaignDto winner = await CreateCampaignWithOnePromptAsync(factory, client, "rename-winner");

        takeName = async () =>
        {
            using HttpResponseMessage first = await client.PutAsync($"/api/campaigns/{winner.Id}", JsonBody("""{"name":"Contested"}"""));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        };

        string path = workspace ? $"/api/workspaces/{loser.Id}" : $"/api/campaigns/{loser.Id}";

        using HttpResponseMessage response = await client.PutAsync(path, JsonBody("""{"name":"CONTESTED"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(workspace ? "Workspace.NameDuplicate" : "Campaign.DuplicateName", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        using HttpResponseMessage read = await client.GetAsync($"/api/campaigns/{loser.Id}");

        ApiResponse<CampaignDto> stored = JsonSerializer.Deserialize(await read.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCampaignDto)!;

        Assert.Equal(loser.Name, stored.Data!.Name);
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [SkippableFact]
    public async Task RegisterCampaign_repository_failure_maps_error_code()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ICampaignRepository>();

            services.AddScoped<ICampaignRepository, MaxReachedCampaignRepository>();
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string path = Path.Combine(factory.TempHome, "campaign-max-result");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new(
            "Rejected Campaign",
            path,
            WorkspaceType.Campaign,
            null);

        string payload = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.RegisterCampaignRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/campaigns",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CampaignDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.MaxReached, body.Error!.Value.Code);

        Assert.Equal(
            "Repository failure selected by code, not legacy exception text.",
            body.Error.Value.Message);
    }

    [SkippableFact]
    public async Task ImportCampaign_replace_strategy_with_memberless_payload_rejects_without_deleting_prompts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-guard");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent("""{"strategy":"replace","payload":{}}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);

        // The rejected bundle must not have destroyed the Campaign's existing prompts: payload
        // validation has to precede the "replace" delete sweep, not follow it.
        Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));
    }

    [SkippableFact]
    public async Task ImportCampaign_replace_with_one_prompt_lacking_template_rejects_without_deleting_prompts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-shape");

        string payload = ReplaceBundle(
            campaign,
            """{"name":"complete","version":"1.0.0","template":"Hello","tags":[]}""",
            """{"name":"incomplete","version":"1.0.0","tags":[]}""");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await AssertImportRefusedAsync(response);

        // The first prompt of the bundle is valid, so the existing prompt must also survive the half
        // of the bundle that would have been written before the invalid entry was reached.
        Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));
    }

    [SkippableFact]
    public async Task ImportCampaign_replace_with_a_duplicate_name_and_version_in_the_bundle_rejects_without_deleting_prompts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-duplicate");

        PromptSummaryDto existing = Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));

        string payload = ReplaceBundle(
            campaign,
            """{"name":"twin","version":"1.0.0","template":"One","tags":[]}""",
            """{"name":" twin ","version":"1.0.0","template":"Two","tags":[]}""");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await AssertImportRefusedAsync(response);

        Assert.Equal(existing.Id, Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id)).Id);
    }

    [SkippableTheory]
    [InlineData("replce")]
    [InlineData("overwrite")]
    [InlineData("merge-all")]
    public async Task ImportCampaign_unknown_strategy_is_400(string strategy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "unknown-strategy");

        string payload = ReplaceBundle(
            campaign,
            """{"name":"fresh","version":"1.0.0","template":"Hello","tags":[]}""")
            .Replace("\"replace\"", $"\"{strategy}\"", StringComparison.Ordinal);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await AssertImportRefusedAsync(response);

        // An unrecognised strategy used to behave as a silent merge and import the bundle's prompts.
        Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));
    }

    [SkippableFact]
    public async Task ImportCampaign_replace_strategy_is_case_insensitive_and_swaps_the_prompt_set()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-swap");

        PromptSummaryDto existing = Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));

        // One incoming prompt reuses the existing prompt's name and version: the unique index only admits it
        // because the delete and the adds share one transaction.
        string payload = ReplaceBundle(
            campaign,
            $$"""{"name":"{{existing.Name}}","version":"{{existing.Version}}","template":"Replaced","tags":[]}""",
            """{"name":"added","version":"2.0.0","template":"Added","tags":[]}""")
            .Replace("\"replace\"", "\"REPLACE\"", StringComparison.Ordinal);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.Equal(2, body!.Data!.PromptsImported);

        Assert.Empty(body.Data.Warnings);

        IReadOnlyList<PromptSummaryDto> after = await ListCampaignPromptsAsync(client, campaign.Id);

        Assert.Equal(2, after.Count);

        Assert.DoesNotContain(after, p => p.Id == existing.Id);

        Assert.Contains(after, p => p.Name == existing.Name && p.Version == existing.Version);

        Assert.Contains(after, p => p.Name == "added");
    }

    /// <summary>
    /// The prompt swap commits on its own. A caller that disconnects right after it used to cancel the
    /// settings write that follows, leaving the Campaign's prompts replaced and its settings not; once
    /// the first write has committed, the rest of the import runs to completion.
    /// </summary>
    [SkippableFact]
    public async Task ImportCampaign_replace_finishes_the_settings_write_when_the_caller_disconnects_after_the_prompt_swap()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource<bool> settingsWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.AddHttpContextAccessor();

                services.RemoveAll<IPromptRepository>();

                services.AddScoped<IPromptRepository>(provider => new DisconnectingAfterSwapPromptRepository(
                    ActivatorUtilities.CreateInstance<PromptRepository>(provider),
                    provider.GetRequiredService<IHttpContextAccessor>()));

                services.RemoveAll<ICampaignRepository>();

                services.AddScoped<ICampaignRepository>(provider => new SettingsWriteRecordingCampaignRepository(
                    ActivatorUtilities.CreateInstance<CampaignRepository>(provider),
                    settingsWritten));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "replace-disconnect");

        string payload = ReplaceBundle(
            campaign,
            """{"name":"after-disconnect","version":"1.0.0","template":"Swapped","tags":[]}""");

        try
        {
            using HttpResponseMessage _ = await client.PostAsync(
                $"/api/campaigns/{campaign.Id}/import",
                new StringContent(payload, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // The disconnect is the point of the test; what the caller saw of it is not.
        }

        Assert.True(await settingsWritten.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        PromptSummaryDto swapped = Assert.Single(await ListCampaignPromptsAsync(client, campaign.Id));

        Assert.Equal("after-disconnect", swapped.Name);
    }

    [SkippableFact]
    public async Task ImportCampaign_payload_without_spells_or_prompts_imports_nothing_and_succeeds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "sparse-bundle");

        string payload = "{\"strategy\":\"merge\",\"payload\":{\"campaign\":" + CampaignJson(campaign) + "}}";

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.Equal(0, body.Data!.SpellsImported);

        Assert.Equal(0, body.Data.PromptsImported);
    }

    /// <summary>
    /// A campaign root is usually a repository the operator cloned, so what it holds at
    /// <c>.arcanum/campaign.json</c> is not trusted to be a small regular file.
    /// </summary>
    /// <remarks>
    /// The first three shapes are a valid bundle that the unbounded, link-following read used to import, so a
    /// refusal there is the reader's and not the parser's: a link to a file outside the campaign, a link in the
    /// place of the <c>.arcanum</c> directory, and a regular file past the size cap. The last two are the
    /// shapes that read could never finish: a FIFO with no writer, whose blocking open never returned, and a
    /// link to <c>/dev/zero</c>, which never ends. The request has to come back, refused, well inside the
    /// timeout, which is the evidence that neither was read to the end -- or opened for a blocking read at all.
    /// </remarks>
    [SkippableTheory]
    [InlineData("symlinked-file")]
    [InlineData("symlinked-directory")]
    [InlineData("oversized-file")]
    [InlineData("fifo")]
    [InlineData("device-link")]
    public async Task Import_from_disk_refuses_a_linked_special_or_oversized_campaign_json_without_reading_it_to_the_end(string shape)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(
            OperatingSystem.IsWindows() && shape is not "oversized-file",
            "Symbolic links need a privilege the Windows lane does not hold, and FIFOs and /dev/zero are POSIX.");

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, shape);

        string bundle = "{\"campaign\":" + CampaignJson(campaign) + ",\"spells\":[],\"prompts\":[]}";

        string arcanumDirectory = Path.Combine(campaign.Path, ".arcanum");

        string outside = Path.Combine(factory.TempHome, $"outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outside);

        switch (shape)
        {
            case "symlinked-file":

                Directory.CreateDirectory(arcanumDirectory);

                await File.WriteAllTextAsync(Path.Combine(outside, "campaign.json"), bundle);

                File.CreateSymbolicLink(
                    Path.Combine(arcanumDirectory, "campaign.json"),
                    Path.Combine(outside, "campaign.json"));

                break;

            case "symlinked-directory":

                await File.WriteAllTextAsync(Path.Combine(outside, "campaign.json"), bundle);

                // Registration may already have created the directory; the link takes its place.
                if (Directory.Exists(arcanumDirectory))
                {
                    Directory.Delete(arcanumDirectory, recursive: true);
                }

                Directory.CreateSymbolicLink(arcanumDirectory, outside);

                break;

            case "fifo":

                Directory.CreateDirectory(arcanumDirectory);

                CreateFifo(Path.Combine(arcanumDirectory, "campaign.json"));

                break;

            case "device-link":

                Directory.CreateDirectory(arcanumDirectory);

                File.CreateSymbolicLink(Path.Combine(arcanumDirectory, "campaign.json"), "/dev/zero");

                break;

            default:

                Directory.CreateDirectory(arcanumDirectory);

                // A valid bundle padded with trailing whitespace to one byte past the cap, so the only
                // thing wrong with it is its size.
                await using (FileStream stream = File.Create(Path.Combine(arcanumDirectory, "campaign.json")))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(bundle));

                    stream.SetLength(16L * 1024L * 1024L + 1L);

                    stream.Position = bundle.Length;

                    byte[] spaces = Enumerable.Repeat((byte)' ', 64 * 1024).ToArray();

                    while (stream.Position < stream.Length)
                    {
                        int count = (int)Math.Min(spaces.Length, stream.Length - stream.Position);

                        await stream.WriteAsync(spaces.AsMemory(0, count));
                    }
                }

                break;
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            deadline.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(deadline.Token),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);
    }

    private static void CreateFifo(string path)
    {
        const string mkfifo = "/usr/bin/mkfifo";

        Skip.IfNot(File.Exists(mkfifo), "The mkfifo utility is unavailable.");

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(
            new global::System.Diagnostics.ProcessStartInfo(mkfifo, [path]) { UseShellExecute = false })!;

        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
    }

    [SkippableFact]
    public async Task Import_from_disk_still_reads_a_regular_campaign_json_inside_the_campaign()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "disk-bundle");

        string arcanumDirectory = Path.Combine(campaign.Path, ".arcanum");

        Directory.CreateDirectory(arcanumDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(arcanumDirectory, "campaign.json"),
            "{\"campaign\":" + CampaignJson(campaign) + ",\"spells\":[],\"prompts\":[]}");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task ImportCampaign_spell_with_unparsable_embedded_json_reports_import_failure()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        CampaignDto campaign = await CreateCampaignWithOnePromptAsync(factory, client, "bad-spell-json");

        string payload = "{\"strategy\":\"merge\",\"payload\":{\"campaign\":"
            + CampaignJson(campaign)
            + ",\"spells\":[{\"name\":\"broken\",\"spellJson\":\"not-json\",\"fullContent\":\"x\",\"scripts\":[]}],\"prompts\":[]}}";

        HttpResponseMessage response = await client.PostAsync(
            $"/api/campaigns/{campaign.Id}/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);

        // The body itself parsed fine; only the spell's embedded metadata string did not, and the
        // message has to name the spell rather than blame the whole request body.
        Assert.Contains("broken", body.Error.Value.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>GET /api/campaigns</c> takes <c>limit</c> and <c>offset</c> as well as <c>type</c>, and the API
    /// reference now says so; this pins the behaviour it documents.
    /// </summary>
    [SkippableFact]
    public async Task GetCampaigns_pages_with_limit_and_offset_and_clamps_both()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        for (int i = 0; i < 3; i++)
        {
            _ = await CreateCampaignWithOnePromptAsync(factory, client, $"paged-{i}");
        }

        ListPageResult<CampaignDto> first = await ListCampaignsAsync(client, "limit=1&offset=0");

        CampaignDto only = Assert.Single(first.Items);

        Assert.True(first.HasMore);

        Assert.Equal(1, first.NextOffset);

        ListPageResult<CampaignDto> second = await ListCampaignsAsync(client, "limit=1&offset=1");

        Assert.NotEqual(only.Id, Assert.Single(second.Items).Id);

        // A negative offset is treated as 0, and a limit below 1 is clamped up to 1.
        Assert.Equal(only.Id, Assert.Single((await ListCampaignsAsync(client, "limit=1&offset=-5")).Items).Id);

        Assert.Single((await ListCampaignsAsync(client, "limit=0")).Items);

        Assert.Empty((await ListCampaignsAsync(client, "limit=10000&offset=100000")).Items);
    }

    private static async Task<ListPageResult<CampaignDto>> ListCampaignsAsync(HttpClient client, string query)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/campaigns?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<CampaignDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto);

        return body!.Data!;
    }

    [SkippableFact]
    public async Task GetCampaignPrompts_truncated_page_reports_hasMore_and_nextOffset()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = Guid.NewGuid();

        // The 10,000-row ceiling is real, so a Campaign with more prompts than that gets a truncated
        // page. A hard-coded hasMore:false tells the caller it has everything.
        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<ICampaignRepository>();

                services.AddScoped<ICampaignRepository>(_ => new SingleCampaignRepository(campaignId));

                services.RemoveAll<IPromptRepository>();

                services.AddScoped<IPromptRepository>(_ => new TruncatedPromptRepository(campaignId));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaignId}/prompts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<PromptSummaryDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto);

        Assert.NotNull(body?.Data);

        Assert.True(body!.Data!.HasMore);

        Assert.Equal(10_000, body.Data.NextOffset);
    }

    private sealed class SingleCampaignRepository(Guid campaignId) : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(id == campaignId
                ? new Campaign
                {
                    Id = campaignId,
                    Name = "Truncated",
                    Path = Path.GetTempPath(),
                    Type = WorkspaceType.Campaign,
                }
                : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        // This double answers by identifier only. Its Campaign governs its root and every directory below it, so answering "no Campaign"
        // to a containment lookup would leave that directory ungoverned without a word; a lookup by containing path is refused instead.
        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Campaign>> UpdateHeaderAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSettingsAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSanctumAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class TruncatedPromptRepository(Guid campaignId) : IPromptRepository
    {
        public Task<ListPageResult<Prompt>> ListAsync(
            Guid? scopeCampaignId,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Prompt>(
                [
                    new Prompt
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaignId,
                        Name = "truncated",
                        Version = "1.0.0",
                        Template = "body",
                        Tags = "[]",
                    },
                ],
                HasMore: true,
                NextOffset: 10_000));

        public Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt?> GetByNameAndVersionAsync(
            string name,
            string version,
            Guid? scopeCampaignId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Prompt>> ListVersionsAsync(
            string name,
            Guid? scopeCampaignId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Prompt>> AddAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Prompt>> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<int>> ReplaceCampaignPromptsAsync(
            Guid campaignId,
            IReadOnlyList<Prompt> prompts,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>The real prompt store, with the caller disconnecting the moment the prompt swap has committed.</summary>
    private sealed class DisconnectingAfterSwapPromptRepository(
        PromptRepository inner,
        IHttpContextAccessor accessor) : IPromptRepository
    {
        public Task<ListPageResult<Prompt>> ListAsync(
            Guid? scopeCampaignId,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(scopeCampaignId, limit, offset, cancellationToken);

        public Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<Prompt?> GetByNameAndVersionAsync(
            string name,
            string version,
            Guid? scopeCampaignId,
            CancellationToken cancellationToken = default) =>
            inner.GetByNameAndVersionAsync(name, version, scopeCampaignId, cancellationToken);

        public Task<IReadOnlyList<Prompt>> ListVersionsAsync(
            string name,
            Guid? scopeCampaignId,
            CancellationToken cancellationToken = default) =>
            inner.ListVersionsAsync(name, scopeCampaignId, cancellationToken);

        public Task<Result<Prompt>> AddAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            inner.AddAsync(prompt, cancellationToken);

        public Task<Result<Prompt>> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(prompt, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);

        public async Task<Result<int>> ReplaceCampaignPromptsAsync(
            Guid campaignId,
            IReadOnlyList<Prompt> prompts,
            CancellationToken cancellationToken = default)
        {
            Result<int> replaced = await inner.ReplaceCampaignPromptsAsync(campaignId, prompts, cancellationToken);

            accessor.HttpContext?.Abort();

            return replaced;
        }
    }

    /// <summary>The real Campaign store, reporting whether the import's settings write ran to completion.</summary>
    private sealed class SettingsWriteRecordingCampaignRepository(
        CampaignRepository inner,
        TaskCompletionSource<bool> settingsWritten) : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetByPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetBySpelledPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            inner.GetByNameAsync(name, cancellationToken);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(typeFilter, limit, offset, cancellationToken);

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.AddAsync(campaign, cancellationToken);

        public Task<Result<Campaign>> UpdateHeaderAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateHeaderAsync(campaign, cancellationToken);

        public async Task<Result<Campaign>> UpdateSettingsAsync(Campaign campaign, CancellationToken cancellationToken = default)
        {
            try
            {
                Result<Campaign> updated = await inner.UpdateSettingsAsync(campaign, cancellationToken);

                settingsWritten.TrySetResult(true);

                return updated;
            }
            catch (OperationCanceledException)
            {
                settingsWritten.TrySetResult(false);

                throw;
            }
        }

        public Task<Result<Campaign>> UpdateSanctumAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateSanctumAsync(campaign, cancellationToken);

        public Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateWorkspaceAsync(campaign, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            inner.CountAsync(cancellationToken);
    }

    private sealed class InterleavingCampaignRepository(
        CampaignRepository inner,
        Func<Task> afterRead,
        Func<Task>? afterNameRead = null) : ICampaignRepository
    {
        public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Campaign? snapshot = await inner.GetByIdAsync(id, cancellationToken);

            await afterRead();

            return snapshot;
        }

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetByPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetBySpelledPathAsync(path, cancellationToken);

        public async Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            Campaign? snapshot = await inner.GetByNameAsync(name, cancellationToken);

            if (afterNameRead is not null)
            {
                await afterNameRead();
            }

            return snapshot;
        }

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(typeFilter, limit, offset, cancellationToken);

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.AddAsync(campaign, cancellationToken);

        public Task<Result<Campaign>> UpdateHeaderAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateHeaderAsync(campaign, cancellationToken);

        public Task<Result<Campaign>> UpdateSettingsAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateSettingsAsync(campaign, cancellationToken);

        public Task<Result<Campaign>> UpdateSanctumAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateSanctumAsync(campaign, cancellationToken);

        public Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            inner.UpdateWorkspaceAsync(campaign, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            inner.CountAsync(cancellationToken);
    }

    private static string ReplaceBundle(CampaignDto campaign, params string[] promptJson) =>
        "{\"strategy\":\"replace\",\"payload\":{\"campaign\":"
        + CampaignJson(campaign)
        + ",\"spells\":[],\"prompts\":["
        + string.Join(",", promptJson)
        + "]}}";

    private static async Task AssertImportRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<CampaignImportResultDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.ImportFailed, body.Error!.Value.Code);
    }

    private static string CampaignJson(CampaignDto campaign) =>
        JsonSerializer.Serialize(campaign, ArcanumJsonContext.Default.CampaignDto);

    private sealed class CatalogReadObserver(Action<string> observe) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, following) =>
            {
                global::RetroDownfall.Arcanum.Infrastructure.Security.SecureFileReader.AfterRegularFileOpenedForTests = observe;

                await following(context);
            });

            next(app);
        };
    }

    private static async Task<CampaignDto> CreateCampaignWithOnePromptAsync(
        ArcanumWebApplicationFactory factory,
        HttpClient client,
        string suffix)
    {
        string path = Path.Combine(factory.TempHome, $"campaign-import-{suffix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest register = new($"Import {suffix}", path, WorkspaceType.Campaign, null);

        HttpResponseMessage created = await client.PostAsync(
            "/api/campaigns",
            new StringContent(
                JsonSerializer.Serialize(register, ArcanumJsonContext.Default.RegisterCampaignRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        ApiResponse<CampaignDto>? campaignBody = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        CampaignDto campaign = campaignBody!.Data!;

        CreatePromptRequest prompt = new(
            $"import-guard-{Guid.NewGuid():N}",
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
            campaign.Id);

        HttpResponseMessage promptCreated = await client.PostAsync(
            "/api/prompts",
            new StringContent(
                JsonSerializer.Serialize(prompt, ArcanumJsonContext.Default.CreatePromptRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.Created, promptCreated.StatusCode);

        return campaign;
    }

    private static async Task<IReadOnlyList<PromptSummaryDto>> ListCampaignPromptsAsync(HttpClient client, Guid campaignId)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/campaigns/{campaignId}/prompts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ListPageResult<PromptSummaryDto>>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto);

        return body!.Data!.Items;
    }

    private sealed class MaxReachedCampaignRepository : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        // No root is registered in these scenarios, so the exact match, which finds none, is the whole answer.
        public Task<Campaign?> GetByContainedPathAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            GetByPathAsync(path, cancellationToken);

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<Campaign>.Failure(
                    new Error(
                        ErrorCodes.Campaign.MaxReached,
                        "Repository failure selected by code, not legacy exception text.")));

        public Task<Result<Campaign>> UpdateHeaderAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSettingsAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSanctumAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateWorkspaceAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
