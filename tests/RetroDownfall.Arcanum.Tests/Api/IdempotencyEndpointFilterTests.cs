using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>Idempotency-Key</c> request replay — DESIGN.md §11.17. Covers buffered
/// (<c>/api/intelligence/ping</c>) and streaming (<c>/api/intelligence/ping-stream</c>) call sites;
/// <c>/v1/chat/completions</c> and <c>/v1/embeddings</c> share the exact same
/// <c>IdempotencyEndpointFilters</c> implementation so are not independently re-tested here.
/// </summary>
[Collection("ApiHost")]
public sealed class IdempotencyEndpointFilterTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public IdempotencyEndpointFilterTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostPing_WithIdempotencyKey_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "first-response";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "idempotent ping");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage first = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        // Change the fake's canned response so a re-execution would be observably different.
        _factory.FakeIntelligence.NextText = "second-response-should-never-be-seen";

        HttpRequestMessage second = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.DoesNotContain("second-response-should-never-be-seen", secondBody);

        // The provider must not have been invoked a second time — this is a replay, not a re-execution.
        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_WithoutIdempotencyKey_AlwaysExecutes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "no-key-response";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "no idempotency key here");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        for (int i = 0; i < 2; i++)
        {

            HttpResponseMessage response = await client.PostAsync(
                "/api/intelligence/ping",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        }

        Assert.Equal(before + 2, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_DifferentBodySameKey_Returns409Conflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        async Task<HttpResponseMessage> SendAsync(string prompt)
        {

            string payload = JsonSerializer.Serialize(new PingRequest(Prompt: prompt), ArcanumJsonContext.Default.PingRequest);

            HttpRequestMessage req = new(HttpMethod.Post, "/api/intelligence/ping")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

            return await client.SendAsync(req);

        }

        _factory.FakeIntelligence.NextText = "response-a";

        HttpResponseMessage responseA = await SendAsync("prompt-a");

        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        _factory.FakeIntelligence.NextText = "response-b";

        HttpResponseMessage responseB = await SendAsync("prompt-b");

        Assert.Equal(HttpStatusCode.Conflict, responseB.StatusCode);

        // Same Idempotency-Key with a different fingerprint must not execute again.
        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_SameKeyAndBodyButDifferentQueryString_Returns409Conflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Query parameters steer execution (?workspace= / ?version= on the spell and prompt execute
        // routes), so a fingerprint blind to them replays the first request's answer against the wrong
        // target instead of raising a conflict.
        _factory.FakeIntelligence.NextFailure = null;

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = JsonSerializer.Serialize(
            new PingRequest(Prompt: "identical body"),
            ArcanumJsonContext.Default.PingRequest);

        async Task<HttpResponseMessage> SendAsync(string query)
        {

            HttpRequestMessage req = new(HttpMethod.Post, "/api/intelligence/ping" + query)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

            return await client.SendAsync(req);

        }

        _factory.FakeIntelligence.NextText = "workspace-a-response";

        HttpResponseMessage first = await SendAsync("?workspace=/repo/A");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        _factory.FakeIntelligence.NextText = "workspace-b-response";

        HttpResponseMessage second = await SendAsync("?workspace=/repo/B");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Contains(
            ErrorCodes.Security.IdempotencyConflict,
            await second.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_SameKeyAndSameQueryInAnyOrder_StillReplays()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "canonical-query-response";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = JsonSerializer.Serialize(
            new PingRequest(Prompt: "identical body"),
            ArcanumJsonContext.Default.PingRequest);

        async Task<HttpResponseMessage> SendAsync(string query)
        {

            HttpRequestMessage req = new(HttpMethod.Post, "/api/intelligence/ping" + query)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

            return await client.SendAsync(req);

        }

        HttpResponseMessage first = await SendAsync("?a=1&b=2");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Parameter order alone must not manufacture a conflict.
        HttpResponseMessage second = await SendAsync("?b=2&a=1");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_WithDuplicateIdempotencyKeyHeaders_Returns400AndDoesNotExecute()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // An ambiguous header used to fall through to the handler with no claim at all — the caller
        // believed it was protected and was billed twice.
        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "must-not-execute";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = JsonSerializer.Serialize(
            new PingRequest(Prompt: "ambiguous key"),
            ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage request = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(ArcanumApiHeaders.IdempotencyKey, "duplicate-key");

        request.Headers.Add(ArcanumApiHeaders.IdempotencyKey, "duplicate-key");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(
            ErrorCodes.Security.IdempotencyKeyAmbiguous,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(before, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [Fact]
    public void ResolvePrincipal_IsIndependentOfTheClientSuppliedHostHeader()
    {

        // A caller that varies Host could otherwise partition its own claims and defeat replay protection.
        DefaultHttpContext localhost = new();

        localhost.Request.Host = new HostString("localhost", 5001);

        DefaultHttpContext loopbackIp = new();

        loopbackIp.Request.Host = new HostString("127.0.0.1", 5001);

        Assert.Equal(
            IdempotencyIdentity.ResolvePrincipal(localhost),
            IdempotencyIdentity.ResolvePrincipal(loopbackIp));

    }

    [SkippableFact]
    public async Task PostSpellExecute_WithIdempotencyKey_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspaceRoot = _factory.TempHome;

        string spellDir = Path.Combine(workspaceRoot, "test-spell");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: test-spell
            description: test
            ---
            spell body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "skill.json"),
            """{"name":"test-spell","version":"1.0.0","description":"test","tags":[],"declaredTools":[],"dependencies":[]}""");

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "spell-first";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        SpellExecuteRequest request = new(
            Prompt: "idempotent spell",
            Model: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            Stop: null,
            Seed: null,
            ResponseFormat: null,
            PresencePenalty: null,
            FrequencyPenalty: null,
            Workspace: workspaceRoot,
            CampaignId: null,
            SessionId: null,
            ToolPolicy: null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SpellExecuteRequest);

        HttpRequestMessage first = new(HttpMethod.Post, "/api/spells/test-spell/execute")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        _factory.FakeIntelligence.NextText = "spell-second-should-never-be-seen";

        HttpRequestMessage second = new(HttpMethod.Post, "/api/spells/test-spell/execute")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.DoesNotContain("spell-second-should-never-be-seen", secondBody);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPromptExecute_WithIdempotencyKey_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "prompt-first";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PromptExecuteRequest request = new(
            UserMessage: "idempotent prompt",
            Parameters: new Dictionary<string, string>(),
            Workspace: null,
            CampaignId: null,
            SessionId: null,
            Model: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            Stop: null,
            Seed: null,
            ResponseFormat: null,
            PresencePenalty: null,
            FrequencyPenalty: null,
            ToolPolicy: null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PromptExecuteRequest);

        Guid promptId = await CreatePromptInFactoryGrimoireAsync();

        HttpRequestMessage first = new(HttpMethod.Post, $"/api/prompts/{promptId}/execute")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        _factory.FakeIntelligence.NextText = "prompt-second-should-never-be-seen";

        HttpRequestMessage second = new(HttpMethod.Post, $"/api/prompts/{promptId}/execute")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.DoesNotContain("prompt-second-should-never-be-seen", secondBody);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPingStream_WithIdempotencyKey_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "streamed-first";

        int before = _factory.FakeIntelligence.StreamPromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "idempotent stream ping");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage first = new(HttpMethod.Post, "/api/intelligence/ping-stream")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _factory.FakeIntelligence.StreamPromptCallCount);

        _factory.FakeIntelligence.NextText = "streamed-second-should-never-be-seen";

        HttpRequestMessage second = new(HttpMethod.Post, "/api/intelligence/ping-stream")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.DoesNotContain("streamed-second-should-never-be-seen", secondBody);

        Assert.Equal(before + 1, _factory.FakeIntelligence.StreamPromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_IdempotencyKeyOver256Chars_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "too long key");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage req = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        req.Headers.Add(ArcanumApiHeaders.IdempotencyKey, new string('k', 257));

        HttpResponseMessage response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The oversized key must be rejected before the handler ever runs.
        Assert.Equal(before, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    private async Task<Guid> CreatePromptInFactoryGrimoireAsync()
    {

        using IServiceScope scope = _factory.Services.CreateScope();

        IServiceProvider sp = scope.ServiceProvider;

        ArcanumDbContext db = sp.GetRequiredService<ArcanumDbContext>();

        Prompt prompt = new()
        {

            Id = Guid.NewGuid(),
            Name = "idempotent-test-prompt",
            Version = "1.0.0",
            Description = "test",
            Tags = "[]",
            Template = "be helpful\n\n{{userMessage}}",
            Model = "mistral:latest",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,

        };

        db.Prompts.Add(prompt);

        await db.SaveChangesAsync();

        return prompt.Id;

    }

    [SkippableFact]
    public async Task PostPing_WithIdempotencyKey_AttachmentBearingTurn_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "attachment-first";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        // Simulated attachment-bearing turn: AttachmentReferences present so the body hash
        // includes attachment identity; FakeIntelligence still counts executions.
        //
        // No Session ID: a supplied one must now identify an existing Session, because canonical
        // Campaign resolution never silently substitutes a new one (§10.12). This turn's subject is
        // idempotent replay, not Session identity.
        PingRequest request = new(
            Prompt: "idempotent attachment ping",
            AttachmentReferences: [Guid.NewGuid()]);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage first = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        _factory.FakeIntelligence.NextText = "attachment-second-should-never-be-seen";

        HttpRequestMessage second = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.DoesNotContain("attachment-second-should-never-be-seen", secondBody);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_ConcurrentIdenticalIdempotencyKey_SharesSingleExecution()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "concurrent-shared";

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _factory.FakeIntelligence.ExecuteGate = gate;
        _factory.FakeIntelligence.ExecuteEntered = entered;

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        PingRequest request = new(Prompt: "concurrent idempotent ping");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpClient client1 = _factory.CreateAuthenticatedClient();

        HttpClient client2 = _factory.CreateAuthenticatedClient();

        async Task<HttpResponseMessage> SendAsync(HttpClient client)
        {

            HttpRequestMessage req = new(HttpMethod.Post, "/api/intelligence/ping")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

            return await client.SendAsync(req);

        }

        Task<HttpResponseMessage> firstTask = SendAsync(client1);

        Task<HttpResponseMessage> secondTask = SendAsync(client2);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        gate.SetResult();

        HttpResponseMessage[] responses = await Task.WhenAll(firstTask, secondTask);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        string body0 = await responses[0].Content.ReadAsStringAsync();

        string body1 = await responses[1].Content.ReadAsStringAsync();

        Assert.Equal(body0, body1);

        Assert.Contains("concurrent-shared", body0);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        _factory.FakeIntelligence.ExecuteGate = null;
        _factory.FakeIntelligence.ExecuteEntered = null;

    }

    [SkippableFact]
    public async Task PostPing_IdempotencyKeyFingerprintMismatch_Returns409()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "first-response";

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest firstRequest = new(Prompt: "fingerprint-a");

        string firstPayload = JsonSerializer.Serialize(firstRequest, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage first = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(firstPayload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        PingRequest secondRequest = new(Prompt: "fingerprint-b");

        string secondPayload = JsonSerializer.Serialize(secondRequest, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage second = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(secondPayload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        string body = await secondResponse.Content.ReadAsStringAsync();

        Assert.Contains("IdempotencyConflict", body, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task PostPing_OversizedResponse_IsNotCached()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory oversizedFactory = new();
        int maxCacheBytes = ArcanumSettingClamps.SecurityIdempotencyMaxResponseBytes(
            ArcanumRuntimeDefaults.SecurityIdempotencyMaxResponseBytes);
        string oversizedText = new('x', maxCacheBytes + 1);

        oversizedFactory.FakeIntelligence.NextFailure = null;

        oversizedFactory.FakeIntelligence.NextText = oversizedText;

        int before = oversizedFactory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = oversizedFactory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "oversized response probe");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        async Task<HttpResponseMessage> SendAsync()
        {

            HttpRequestMessage req = new(HttpMethod.Post, "/api/intelligence/ping")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

            return await client.SendAsync(req);

        }

        HttpResponseMessage firstResponse = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        _ = await firstResponse.Content.ReadAsStringAsync();

        HttpResponseMessage secondResponse = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        _ = await secondResponse.Content.ReadAsStringAsync();

        // The response exceeded the cap, so it was never cached — the second call re-executed.
        Assert.Equal(before + 2, oversizedFactory.FakeIntelligence.ExecutePromptCallCount);

    }

    [SkippableFact]
    public async Task PostPing_ProviderUnreachableFirstCall_SecondRequestReExecutesFreshInsteadOfReplaying503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = new Error(ErrorCodes.Connection.Unreachable, "provider unreachable");

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "retry after transient failure");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpRequestMessage first = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        first.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage firstResponse = await client.SendAsync(first);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        // The provider recovers before the retry; a re-execution must be observably different from
        // the frozen 503.
        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "recovered-response";

        HttpRequestMessage second = new(HttpMethod.Post, "/api/intelligence/ping")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        second.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

        HttpResponseMessage secondResponse = await client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Contains("recovered-response", secondBody, StringComparison.Ordinal);

        // The retry must have re-executed the handler fresh, not replayed the cached 503.
        Assert.Equal(before + 2, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

}

public sealed class IdempotencyEndpointFilterOwnershipTests
{

    [Fact]
    public void OwnerIds_UseStableProcessComponentAndUniqueRequestComponent()
    {

        string first = IdempotencyEndpointFilters.CreateOwnerId();

        string second = IdempotencyEndpointFilters.CreateOwnerId();

        Assert.NotEqual(first, second);

        Assert.True(IdempotencyEndpointFilters.IsSameProcessOwner(first));

        Assert.True(IdempotencyEndpointFilters.IsSameProcessOwner(second));

        Assert.False(IdempotencyEndpointFilters.IsSameProcessOwner(
            $"different-process:{Guid.NewGuid():N}"));

        Assert.Equal(
            first[..first.IndexOf(':')],
            second[..second.IndexOf(':')]);

    }

    [Fact]
    public async Task ConcurrentRequest_WhenFirstAcquireHasNotRegisteredLocally_NeverExecutesAsNonOwner()
    {

        FakeClaimStore store = new()
        {
            BlockFirstAcquire = true,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext firstContext, CompletionTrackingResponseFeature firstResponse) =
            CreateContext(services, "acquire-registration-race");

        (TestEndpointFilterInvocationContext secondContext, CompletionTrackingResponseFeature secondResponse) =
            CreateContext(services, "acquire-registration-race");

        int handlerCalls = 0;

        EndpointFilterDelegate next = async invocationContext =>
        {

            _ = Interlocked.Increment(ref handlerCalls);

            invocationContext.HttpContext.Response.ContentType = "application/json";

            await invocationContext.HttpContext.Response.WriteAsync(
                """{"ok":true}""",
                invocationContext.HttpContext.RequestAborted);

            return null;

        };

        Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> filter =
            IdempotencyEndpointFilters.ForBoundArgument(0, ArcanumJsonContext.Default.PingRequest);

        Task first = InvokeAndCompleteAsync(filter, firstContext, firstResponse, next);

        await store.FirstAcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task second = InvokeAndCompleteAsync(filter, secondContext, secondResponse, next);

        Assert.Equal(1, store.TryAcquireCallCount);

        store.ReleaseFirstAcquire.SetResult();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handlerCalls);

    }

    [Fact]
    public async Task LiveCrossProcessLease_ReturnsNativeInProgressWithoutInvokingHandler()
    {

        string key = $"cross-process-native-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            Claim = CreateClaim(
                key,
                ownerId: $"other-process:{Guid.NewGuid():N}",
                IdempotencyClaimState.Running,
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        int handlerCalls = 0;

        EndpointFilterDelegate next = _ =>
        {

            Interlocked.Increment(ref handlerCalls);

            return ValueTask.FromResult<object?>(null);

        };

        await InvokeAndCompleteAsync(CreateFilter(), context, response, next);

        Assert.Equal(StatusCodes.Status409Conflict, context.HttpContext.Response.StatusCode);

        Assert.Equal(0, handlerCalls);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadResponseBody(context.HttpContext),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal(ErrorCodes.Security.IdempotencyInProgress, body?.Error?.Code);

    }

    [Fact]
    public async Task StaleReclaimLostToLiveCrossProcessWinner_Returns409WithoutInvokingHandler()
    {

        string key = $"stale-reclaim-loser-{Guid.NewGuid():N}";
        IdempotencyClaim stale = CreateClaim(
            key,
            ownerId: $"expired-process:{Guid.NewGuid():N}",
            IdempotencyClaimState.Running,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        IdempotencyClaim winner = stale with
        {
            OwnerId = $"winning-process:{Guid.NewGuid():N}",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            HeartbeatAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        FakeClaimStore store = new()
        {
            Claim = stale,
            AcquireResultOverride = new IdempotencyClaimAcquireResult(
                Conflict: false,
                Acquired: false,
                Claim: winner),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);
        int handlerCalls = 0;

        await InvokeAndCompleteAsync(
            CreateFilter(),
            context,
            response,
            _ =>
            {

                Interlocked.Increment(ref handlerCalls);

                return ValueTask.FromResult<object?>(null);

            });

        Assert.Equal(StatusCodes.Status409Conflict, context.HttpContext.Response.StatusCode);
        Assert.Equal(0, handlerCalls);
        Assert.Equal(1, store.TryAcquireCallCount);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadResponseBody(context.HttpContext),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal(ErrorCodes.Security.IdempotencyInProgress, body?.Error?.Code);

    }

    [Fact]
    public async Task LiveCrossProcessLease_OnV1_ReturnsStableOpenAiInProgressCode()
    {

        string key = $"cross-process-openai-{Guid.NewGuid():N}";

        const string path = "/v1/chat/completions";

        FakeClaimStore store = new()
        {
            Claim = CreateClaim(
                key,
                ownerId: $"other-process:{Guid.NewGuid():N}",
                IdempotencyClaimState.Running,
                DateTimeOffset.UtcNow.AddMinutes(5),
                path),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key, path);

        int handlerCalls = 0;

        EndpointFilterDelegate next = _ =>
        {

            Interlocked.Increment(ref handlerCalls);

            return ValueTask.FromResult<object?>(null);

        };

        await InvokeAndCompleteAsync(CreateFilter(), context, response, next);

        Assert.Equal(StatusCodes.Status409Conflict, context.HttpContext.Response.StatusCode);

        Assert.Equal(0, handlerCalls);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            ReadResponseBody(context.HttpContext),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.Equal("idempotency_in_progress", body?.Error.Code);

    }

    [Fact]
    public async Task LiveSameProcessLease_WithoutCoordinator_IsRetiredAndReacquired()
    {

        string key = $"same-process-orphan-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            Claim = CreateClaim(
                key,
                IdempotencyEndpointFilters.CreateOwnerId(),
                IdempotencyClaimState.Running,
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        int handlerCalls = 0;

        await InvokeAndCompleteAsync(
            CreateFilter(),
            context,
            response,
            async invocationContext =>
            {

                _ = Interlocked.Increment(ref handlerCalls);

                await invocationContext.HttpContext.Response.WriteAsync("""{"recovered":true}""");

                return null;

            });

        Assert.Equal(1, handlerCalls);

        Assert.Equal(1, store.MarkFailedCallCount);

        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

    }

    [Fact]
    public async Task LiveSameProcessLease_WhenRetirementDoesNotTransition_FailsSafeWithoutExecution()
    {

        string key = $"same-process-fail-safe-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            IgnoreMarkFailed = true,
            Claim = CreateClaim(
                key,
                IdempotencyEndpointFilters.CreateOwnerId(),
                IdempotencyClaimState.Running,
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, _) = CreateContext(services, key);

        int handlerCalls = 0;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CreateFilter()(
                context,
                _ =>
                {

                    Interlocked.Increment(ref handlerCalls);

                    return ValueTask.FromResult<object?>(null);

                }));

        Assert.Contains("ownership", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, handlerCalls);

    }

    [Fact]
    public async Task AcquireResult_WithDifferentOwner_FailsSafeWithoutExecution()
    {

        string key = $"wrong-acquired-owner-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            ReturnDifferentOwnerOnAcquire = true,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, _) = CreateContext(services, key);

        int handlerCalls = 0;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CreateFilter()(
                context,
                _ =>
                {

                    Interlocked.Increment(ref handlerCalls);

                    return ValueTask.FromResult<object?>(null);

                }));

        Assert.Contains("owner", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, handlerCalls);

    }

    [Fact]
    public async Task AcquireResult_InClaimedState_FailsSafeWithoutExecution()
    {

        string key = $"claimed-acquire-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            ReturnClaimedOnAcquire = true,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, _) = CreateContext(services, key);

        int handlerCalls = 0;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CreateFilter()(
                context,
                _ =>
                {

                    Interlocked.Increment(ref handlerCalls);
                    return ValueTask.FromResult<object?>(null);

                }));

        Assert.Contains("ownership", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handlerCalls);

    }

    [Theory]
    [InlineData("/api/no-content")]
    [InlineData("/v1/no-content")]
    public async Task ExplicitNoContentResponse_IsReplayedWithEmptyBody(string path)
    {

        string key = $"empty-terminal-{Guid.NewGuid():N}";
        FakeClaimStore store = new();

        using ServiceProvider services = CreateServices(store);

        int handlerCalls = 0;
        EndpointFilterDelegate next = invocationContext =>
        {

            Interlocked.Increment(ref handlerCalls);
            invocationContext.HttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return ValueTask.FromResult<object?>(null);

        };

        (TestEndpointFilterInvocationContext firstContext, CompletionTrackingResponseFeature firstResponse) =
            CreateContext(services, key, path);

        await InvokeAndCompleteAsync(CreateFilter(), firstContext, firstResponse, next);

        (TestEndpointFilterInvocationContext secondContext, CompletionTrackingResponseFeature secondResponse) =
            CreateContext(services, key, path);

        await InvokeAndCompleteAsync(CreateFilter(), secondContext, secondResponse, next);

        Assert.Equal(1, handlerCalls);
        Assert.Equal(StatusCodes.Status204NoContent, secondContext.HttpContext.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(secondContext.HttpContext));
        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);
        Assert.Equal(string.Empty, store.Claim?.ResponseBody);

    }

    [Fact]
    public async Task RequestAbort_DoesNotStopOwnedHeartbeatBeforeExecutionCompletes()
    {

        string key = $"heartbeat-disconnect-{Guid.NewGuid():N}";
        ManualTimeProvider time = new();
        FakeClaimStore store = new();
        IdempotencyLeaseTiming timing = new(
            time,
            LeaseDuration: TimeSpan.FromMinutes(5),
            HeartbeatInterval: TimeSpan.FromMinutes(1),
            MaximumLifetime: TimeSpan.FromHours(1));

        using ServiceProvider services = CreateServices(store, timing);
        using CancellationTokenSource caller = new();

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key, requestAborted: caller.Token);

        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<object?> invocation = CreateFilter()(
                context,
                async invocationContext =>
                {

                    handlerEntered.SetResult();
                    await releaseHandler.Task;
                    await invocationContext.HttpContext.Response.WriteAsync(
                        """{"continued":true}""",
                        CancellationToken.None);
                    return null;

                })
            .AsTask();

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await time.WaitForScheduledTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(timing.HeartbeatInterval);
        await store.WaitForHeartbeatCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

        releaseHandler.SetResult();
        _ = await invocation;
        await response.CompleteAsync();

        Assert.True(store.HeartbeatCallCount >= 1);

    }

    [Fact]
    public async Task AcquiredLeaseExpiry_DrivesFirstHeartbeat()
    {

        string key = $"actual-lease-{Guid.NewGuid():N}";
        ManualTimeProvider time = new();
        FakeClaimStore store = new()
        {
            AcquiredLeaseDuration = TimeSpan.FromSeconds(20),
        };
        IdempotencyLeaseTiming timing = new(
            time,
            LeaseDuration: TimeSpan.FromMinutes(5),
            HeartbeatInterval: TimeSpan.FromMinutes(1),
            MaximumLifetime: TimeSpan.FromHours(1));

        using ServiceProvider services = CreateServices(store, timing);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<object?> invocation = CreateFilter()(
                context,
                async invocationContext =>
                {

                    await releaseHandler.Task.WaitAsync(invocationContext.HttpContext.RequestAborted);
                    await invocationContext.HttpContext.Response.WriteAsync("""{"ok":true}""");
                    return null;

                })
            .AsTask();

        DateTimeOffset originalLease = Assert.IsType<IdempotencyClaim>(store.Claim).LeaseExpiresAt;

        await time.WaitForScheduledTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(10));
        await store.WaitForHeartbeatCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(store.Claim?.LeaseExpiresAt > originalLease);

        releaseHandler.SetResult();
        _ = await invocation;
        await response.CompleteAsync();

    }

    [Fact]
    public async Task HeartbeatOwnershipLoss_CancelsEndpointAndStopsOldOwner()
    {

        string key = $"ownership-loss-{Guid.NewGuid():N}";
        ManualTimeProvider time = new();
        FakeClaimStore store = new()
        {
            HeartbeatResult = false,
            ReclaimOnHeartbeatFailure = true,
        };
        IdempotencyLeaseTiming timing = new(
            time,
            LeaseDuration: TimeSpan.FromMinutes(5),
            HeartbeatInterval: TimeSpan.FromMinutes(1),
            MaximumLifetime: TimeSpan.FromHours(1));

        using ServiceProvider services = CreateServices(store, timing);

        (TestEndpointFilterInvocationContext context, _) = CreateContext(services, key);
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ownershipCancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<object?> invocation = CreateFilter()(
                context,
                async invocationContext =>
                {

                    CancellationToken executionToken = invocationContext.HttpContext.RequestAborted;
                    handlerEntered.SetResult();

                    try
                    {

                        await Task.Delay(Timeout.InfiniteTimeSpan, executionToken);

                    }
                    catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
                    {

                        ownershipCancellationObserved.SetResult();
                        throw;

                    }

                    return null;

                })
            .AsTask();

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await time.WaitForScheduledTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(timing.HeartbeatInterval);
        await store.WaitForHeartbeatCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await ownershipCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await invocation);

        int heartbeatCountAfterLoss = store.HeartbeatCallCount;
        time.Advance(timing.HeartbeatInterval + timing.HeartbeatInterval);

        Assert.Equal(heartbeatCountAfterLoss, store.HeartbeatCallCount);
        Assert.False(context.HttpContext.RequestAborted.IsCancellationRequested);
        Assert.Equal(IdempotencyClaimState.Running, store.Claim?.State);
        Assert.StartsWith("reclaimed-process:", store.Claim?.OwnerId, StringComparison.Ordinal);

        (TestEndpointFilterInvocationContext retryContext, CompletionTrackingResponseFeature retryResponse) =
            CreateContext(services, key);
        int retryHandlerCalls = 0;

        await InvokeAndCompleteAsync(
            CreateFilter(),
            retryContext,
            retryResponse,
            _ =>
            {

                Interlocked.Increment(ref retryHandlerCalls);
                return ValueTask.FromResult<object?>(null);

            });

        Assert.Equal(0, retryHandlerCalls);
        Assert.Equal(StatusCodes.Status409Conflict, retryContext.HttpContext.Response.StatusCode);

    }

    [Fact]
    public async Task LongRunningOwner_RenewsBeyondOriginalLease_AndStopsAfterCompletion()
    {

        string key = $"heartbeat-{Guid.NewGuid():N}";

        ManualTimeProvider time = new();

        FakeClaimStore store = new();

        IdempotencyLeaseTiming timing = new(
            time,
            LeaseDuration: TimeSpan.FromMinutes(5),
            HeartbeatInterval: TimeSpan.FromMinutes(1),
            MaximumLifetime: TimeSpan.FromHours(1));

        using ServiceProvider services = CreateServices(store, timing);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<object?> invocation = CreateFilter()(
                context,
                async invocationContext =>
                {

                    handlerEntered.SetResult();

                    await releaseHandler.Task.WaitAsync(invocationContext.HttpContext.RequestAborted);

                    await invocationContext.HttpContext.Response.WriteAsync("""{"renewed":true}""");

                    return null;

                })
            .AsTask();

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        DateTimeOffset originalLease = Assert.IsType<IdempotencyClaim>(store.Claim).LeaseExpiresAt;

        for (int heartbeat = 1; heartbeat <= 6; heartbeat++)
        {

            await time.WaitForScheduledTimerCountAsync(heartbeat).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(timing.HeartbeatInterval);
            await store.WaitForHeartbeatCountAsync(heartbeat).WaitAsync(TimeSpan.FromSeconds(5));

        }

        IdempotencyClaim renewed = Assert.IsType<IdempotencyClaim>(store.Claim);

        Assert.True(time.GetUtcNow() > originalLease);

        Assert.True(renewed.LeaseExpiresAt > time.GetUtcNow());

        releaseHandler.SetResult();

        _ = await invocation;

        await response.CompleteAsync();

        int heartbeatCountAtCompletion = store.HeartbeatCallCount;

        time.Advance(timing.HeartbeatInterval + timing.HeartbeatInterval);

        Assert.Equal(heartbeatCountAtCompletion, store.HeartbeatCallCount);

        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

    }

    [Fact]
    public async Task Completion_DoesNotWaitForHeartbeatStoreCallThatIgnoresCancellation()
    {

        string key = $"heartbeat-shutdown-{Guid.NewGuid():N}";

        TaskCompletionSource blockedHeartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ManualTimeProvider time = new();

        FakeClaimStore store = new()
        {
            HeartbeatBlocker = blockedHeartbeat.Task,
        };

        IdempotencyLeaseTiming timing = new(
            time,
            LeaseDuration: TimeSpan.FromMinutes(5),
            HeartbeatInterval: TimeSpan.FromMinutes(1),
            MaximumLifetime: TimeSpan.FromHours(1));

        using ServiceProvider services = CreateServices(store, timing);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<object?> invocation = CreateFilter()(
                context,
                async invocationContext =>
                {

                    await releaseHandler.Task.WaitAsync(invocationContext.HttpContext.RequestAborted);

                    await invocationContext.HttpContext.Response.WriteAsync("""{"ok":true}""");

                    return null;

                })
            .AsTask();

        try
        {

            await time.WaitForScheduledTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(timing.HeartbeatInterval);
            await store.WaitForHeartbeatCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

            releaseHandler.SetResult();

            _ = await invocation;

            await response.CompleteAsync().WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

        }
        finally
        {

            blockedHeartbeat.TrySetResult();

        }

    }

    [Fact]
    public async Task RepeatedHeartbeatFaults_AbortOwnerBeforeOriginalLeaseExpires()
    {

        string key = $"heartbeat-fault-{Guid.NewGuid():N}";

        ManualTimeProvider time = new();

        FakeClaimStore store = new()
        {
            HeartbeatException = new InvalidOperationException("heartbeat failed"),
        };

        IdempotencyLeaseTiming timing = new(
            time,
            LeaseDuration: TimeSpan.FromMinutes(5),
            HeartbeatInterval: TimeSpan.FromMinutes(1),
            MaximumLifetime: TimeSpan.FromHours(1));

        using ServiceProvider services = CreateServices(store, timing);

        (TestEndpointFilterInvocationContext context, _) = CreateContext(services, key);

        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<object?> invocation = CreateFilter()(
                context,
                async invocationContext =>
                {

                    handlerEntered.SetResult();

                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        invocationContext.HttpContext.RequestAborted);

                    return null;

                })
            .AsTask();

        try
        {

            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (int heartbeat = 1; heartbeat <= 4; heartbeat++)
            {

                await time.WaitForScheduledTimerCountAsync(heartbeat).WaitAsync(TimeSpan.FromSeconds(5));
                time.Advance(timing.HeartbeatInterval);
                await store.WaitForHeartbeatCountAsync(heartbeat).WaitAsync(TimeSpan.FromSeconds(5));

            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await invocation.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.True(store.HeartbeatCallCount >= 1);

            Assert.Equal(IdempotencyClaimState.Failed, store.Claim?.State);

        }
        finally
        {

            context.HttpContext.Abort();

        }

    }

    [Fact]
    public async Task LookupFault_FailsOpenExactlyOnce_WithoutAttemptingAcquire()
    {

        string key = $"lookup-fault-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            TryGetFailure = call => call == 1 ? new InvalidOperationException("lookup failed") : null,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        int handlerCalls = 0;

        await InvokeAndCompleteAsync(
            CreateFilter(),
            context,
            response,
            async invocationContext =>
            {

                _ = Interlocked.Increment(ref handlerCalls);

                await invocationContext.HttpContext.Response.WriteAsync("""{"fresh":true}""");

                return null;

            });

        Assert.Equal(1, handlerCalls);

        Assert.Equal(1, store.TryGetCallCount);

        Assert.Equal(0, store.TryAcquireCallCount);

        Assert.Null(store.Claim);

    }

    [Fact]
    public async Task AcquireFault_FailsOpenExactlyOnce_WithoutReturningInProgress()
    {

        string key = $"acquire-fault-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            TryAcquireFailure = call => call == 1 ? new InvalidOperationException("acquire failed") : null,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        int handlerCalls = 0;

        await InvokeAndCompleteAsync(
            CreateFilter(),
            context,
            response,
            async invocationContext =>
            {

                _ = Interlocked.Increment(ref handlerCalls);

                await invocationContext.HttpContext.Response.WriteAsync("""{"fresh":true}""");

                return null;

            });

        Assert.Equal(1, handlerCalls);

        Assert.Equal(StatusCodes.Status200OK, context.HttpContext.Response.StatusCode);

        Assert.Equal(1, store.TryAcquireCallCount);

        Assert.Null(store.Claim);

    }

    [Fact]
    public async Task WaiterReReadFault_FailsOpenOnce_AfterLeaderReleases()
    {

        string key = $"reread-fault-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            TryGetFailure = call => call == 2 ? new InvalidOperationException("re-read failed") : null,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext leaderContext, CompletionTrackingResponseFeature leaderResponse) =
            CreateContext(services, key);

        (TestEndpointFilterInvocationContext waiterContext, CompletionTrackingResponseFeature waiterResponse) =
            CreateContext(services, key);

        TaskCompletionSource leaderEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseLeader = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int handlerCalls = 0;

        EndpointFilterDelegate next = async invocationContext =>
        {

            int call = Interlocked.Increment(ref handlerCalls);

            if (call == 1)
            {

                leaderEntered.SetResult();

                await releaseLeader.Task.WaitAsync(invocationContext.HttpContext.RequestAborted);

            }

            await invocationContext.HttpContext.Response.WriteAsync("""{"ok":true}""");

            return null;

        };

        Task<object?> leader = InvokeAndCompleteAsync(
            CreateFilter(),
            leaderContext,
            leaderResponse,
            next);

        await leaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<object?> waiter = InvokeAndCompleteAsync(
            CreateFilter(),
            waiterContext,
            waiterResponse,
            next);

        releaseLeader.SetResult();

        await Task.WhenAll(leader, waiter).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handlerCalls);

        Assert.Equal(2, store.TryGetCallCount);

    }

    [Fact]
    public async Task CompletionSaveFault_DoesNotReEnterHandler_AndLaterRequestExecutesFresh()
    {

        string key = $"save-fault-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            CompleteException = new InvalidOperationException("save failed"),
        };

        using ServiceProvider services = CreateServices(store);

        int handlerCalls = 0;

        EndpointFilterDelegate next = async invocationContext =>
        {

            _ = Interlocked.Increment(ref handlerCalls);

            await invocationContext.HttpContext.Response.WriteAsync("""{"ok":true}""");

            return null;

        };

        (TestEndpointFilterInvocationContext firstContext, CompletionTrackingResponseFeature firstResponse) =
            CreateContext(services, key);

        await InvokeAndCompleteAsync(CreateFilter(), firstContext, firstResponse, next);

        Assert.Equal(1, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Failed, store.Claim?.State);

        store.CompleteException = null;

        (TestEndpointFilterInvocationContext secondContext, CompletionTrackingResponseFeature secondResponse) =
            CreateContext(services, key);

        await InvokeAndCompleteAsync(CreateFilter(), secondContext, secondResponse, next);

        Assert.Equal(2, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

    }

    [Fact]
    public async Task AbandonSaveFault_DoesNotReEnterHandler_AndLaterRequestExecutesFresh()
    {

        string key = $"abandon-fault-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            MarkAbandonedException = new InvalidOperationException("abandon failed"),
        };

        using ServiceProvider services = CreateServices(store);

        int handlerCalls = 0;

        (TestEndpointFilterInvocationContext firstContext, CompletionTrackingResponseFeature firstResponse) =
            CreateContext(services, key);

        await InvokeAndCompleteAsync(
            CreateFilter(),
            firstContext,
            firstResponse,
            _ =>
            {

                Interlocked.Increment(ref handlerCalls);

                return ValueTask.FromResult<object?>(null);

            });

        Assert.Equal(1, handlerCalls);

        Assert.Equal(1, store.MarkAbandonedCallCount);

        Assert.Equal(IdempotencyClaimState.Failed, store.Claim?.State);

        store.MarkAbandonedException = null;

        (TestEndpointFilterInvocationContext secondContext, CompletionTrackingResponseFeature secondResponse) =
            CreateContext(services, key);

        await InvokeAndCompleteAsync(
            CreateFilter(),
            secondContext,
            secondResponse,
            async invocationContext =>
            {

                _ = Interlocked.Increment(ref handlerCalls);

                await invocationContext.HttpContext.Response.WriteAsync("""{"fresh":true}""");

                return null;

            });

        Assert.Equal(2, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

    }

    [Theory]
    [InlineData(IdempotencyClaimState.Failed)]
    [InlineData(IdempotencyClaimState.Abandoned)]
    public async Task NonReplayableTerminalClaim_IsReacquiredBeforeHandlerExecution(
        IdempotencyClaimState state)
    {

        string key = $"terminal-reacquire-{state}-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            Claim = CreateClaim(
                key,
                ownerId: $"previous-owner:{Guid.NewGuid():N}",
                state,
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, CompletionTrackingResponseFeature response) =
            CreateContext(services, key);

        int handlerCalls = 0;

        await InvokeAndCompleteAsync(
            CreateFilter(),
            context,
            response,
            async invocationContext =>
            {

                _ = Interlocked.Increment(ref handlerCalls);

                await invocationContext.HttpContext.Response.WriteAsync("""{"fresh":true}""");

                return null;

            });

        Assert.Equal(1, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

    }

    [Fact]
    public async Task HandlerFailure_WhenFailureSaveAlsoFaults_ReleasesCoordinatorWithoutReEntry()
    {

        string key = $"handler-failure-{Guid.NewGuid():N}";

        FakeClaimStore store = new()
        {
            MarkFailedException = new InvalidOperationException("mark failed"),
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext firstContext, _) = CreateContext(services, key);

        int handlerCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CreateFilter()(
                firstContext,
                _ =>
                {

                    Interlocked.Increment(ref handlerCalls);

                    throw new InvalidOperationException("handler failed");

                }));

        Assert.Equal(1, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Running, store.Claim?.State);

        store.MarkFailedException = null;

        (TestEndpointFilterInvocationContext secondContext, CompletionTrackingResponseFeature secondResponse) =
            CreateContext(services, key);

        await InvokeAndCompleteAsync(
            CreateFilter(),
            secondContext,
            secondResponse,
            async invocationContext =>
            {

                _ = Interlocked.Increment(ref handlerCalls);

                await invocationContext.HttpContext.Response.WriteAsync("""{"fresh":true}""");

                return null;

            });

        Assert.Equal(2, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Completed, store.Claim?.State);

    }

    [Fact]
    public async Task LocalWait_IsCallerCancellable_WithoutInvokingWaiterHandler()
    {

        string key = $"wait-cancel-{Guid.NewGuid():N}";

        FakeClaimStore store = new();

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext leaderContext, CompletionTrackingResponseFeature leaderResponse) =
            CreateContext(services, key);

        TaskCompletionSource leaderEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseLeader = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int handlerCalls = 0;

        EndpointFilterDelegate next = async invocationContext =>
        {

            _ = Interlocked.Increment(ref handlerCalls);

            leaderEntered.SetResult();

            await releaseLeader.Task.WaitAsync(invocationContext.HttpContext.RequestAborted);

            await invocationContext.HttpContext.Response.WriteAsync("""{"ok":true}""");

            return null;

        };

        Task<object?> leader = CreateFilter()(leaderContext, next).AsTask();

        await leaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using CancellationTokenSource waiterCancellation = new();

        (TestEndpointFilterInvocationContext waiterContext, _) =
            CreateContext(services, key, requestAborted: waiterCancellation.Token);

        Task<object?> waiter = CreateFilter()(waiterContext, next).AsTask();

        waiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waiter.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(1, handlerCalls);

        releaseLeader.SetResult();

        _ = await leader;

        await leaderResponse.CompleteAsync();

        Assert.Equal(1, handlerCalls);

    }

    [Fact]
    public async Task CancellationAfterAcquire_DoesNotInvokeHandlerOrReturnSuccess()
    {

        string key = $"acquired-cancel-{Guid.NewGuid():N}";

        using CancellationTokenSource cancellation = new();

        FakeClaimStore store = new()
        {
            AfterSuccessfulAcquire = cancellation.Cancel,
        };

        using ServiceProvider services = CreateServices(store);

        (TestEndpointFilterInvocationContext context, _) =
            CreateContext(services, key, requestAborted: cancellation.Token);

        int handlerCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CreateFilter()(
                context,
                _ =>
                {

                    Interlocked.Increment(ref handlerCalls);

                    return ValueTask.FromResult<object?>(null);

                }));

        Assert.Equal(0, handlerCalls);

        Assert.Equal(IdempotencyClaimState.Failed, store.Claim?.State);

    }

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> CreateFilter() =>
        IdempotencyEndpointFilters.ForBoundArgument(0, ArcanumJsonContext.Default.PingRequest);

    private static ServiceProvider CreateServices(
        FakeClaimStore store,
        IdempotencyLeaseTiming? timing = null)
    {

        ServiceCollection services = new();

        services.AddLogging();

        services.AddSingleton(store);

        services.AddSingleton<IIdempotencyClaimStore>(store);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        if (timing is not null)
        {

            services.AddSingleton(timing);

        }

        return services.BuildServiceProvider();

    }

    private static (
        TestEndpointFilterInvocationContext Context,
        CompletionTrackingResponseFeature ResponseFeature)
        CreateContext(
            IServiceProvider services,
            string key,
            string path = "/api/intelligence/ping",
            string prompt = "ownership race",
            CancellationToken requestAborted = default)
    {

        DefaultHttpContext httpContext = new();

        IHttpResponseFeature responseFeature = httpContext.Features.Get<IHttpResponseFeature>()!;

        CompletionTrackingResponseFeature completionFeature = new(responseFeature);

        httpContext.Features.Set<IHttpResponseFeature>(completionFeature);

        httpContext.Features.Set<IHttpRequestLifetimeFeature>(
            new TestRequestLifetimeFeature(requestAborted));

        httpContext.RequestServices = services;

        httpContext.Request.Method = HttpMethods.Post;

        httpContext.Request.Path = path;

        httpContext.Request.ContentType = "application/json";

        httpContext.Request.Headers[ArcanumApiHeaders.IdempotencyKey] = key;

        httpContext.Response.Body = new MemoryStream();

        return (
            new TestEndpointFilterInvocationContext(
                httpContext,
                [new PingRequest(Prompt: prompt)]),
            completionFeature);

    }

    private static async Task<object?> InvokeAndCompleteAsync(
        Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> filter,
        EndpointFilterInvocationContext context,
        CompletionTrackingResponseFeature responseFeature,
        EndpointFilterDelegate next)
    {

        object? result = await filter(context, next);

        if (result is IResult responseResult)
        {

            await responseResult.ExecuteAsync(context.HttpContext);

        }

        await responseFeature.CompleteAsync();

        return result;

    }

    private static IdempotencyClaim CreateClaim(
        string key,
        string ownerId,
        IdempotencyClaimState state,
        DateTimeOffset leaseExpiresAt,
        string path = "/api/intelligence/ping",
        string prompt = "ownership race")
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
            new PingRequest(Prompt: prompt),
            ArcanumJsonContext.Default.PingRequest);

        string claimKeyHash = IdempotencyIdentity.ComputeClaimKeyHash(
            "local",
            HttpMethods.Post,
            path,
            key);

        string fingerprintHash = IdempotencyIdentity.ComputeFingerprintHash(
            bodyBytes,
            path,
            string.Empty,
            "application/json");

        return new IdempotencyClaim(
            Guid.NewGuid(),
            claimKeyHash,
            fingerprintHash,
            state,
            ownerId,
            leaseExpiresAt,
            now,
            RunId: null,
            StatusCode: null,
            ContentType: null,
            ResponseBody: null,
            TerminalStreamComplete: false,
            now,
            now);

    }

    private static string ReadResponseBody(HttpContext httpContext)
    {

        httpContext.Response.Body.Position = 0;

        using StreamReader reader = new(
            httpContext.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        return reader.ReadToEnd();

    }

    private sealed class TestEndpointFilterInvocationContext : EndpointFilterInvocationContext
    {

        private readonly IList<object?> _arguments;

        public TestEndpointFilterInvocationContext(
            HttpContext httpContext,
            IList<object?> arguments)
        {

            HttpContext = httpContext;

            _arguments = arguments;

        }

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments => _arguments;

        public override T GetArgument<T>(int index) => (T)_arguments[index]!;

    }

    private sealed class CompletionTrackingResponseFeature(IHttpResponseFeature inner) : IHttpResponseFeature
    {

        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public int StatusCode
        {
            get => inner.StatusCode;
            set => inner.StatusCode = value;
        }

        public string? ReasonPhrase
        {
            get => inner.ReasonPhrase;
            set => inner.ReasonPhrase = value;
        }

        public IHeaderDictionary Headers
        {
            get => inner.Headers;
            set => inner.Headers = value;
        }

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => inner.HasStarted;

        public void OnStarting(Func<object, Task> callback, object state) =>
            inner.OnStarting(callback, state);

        public void OnCompleted(Func<object, Task> callback, object state)
        {

            _completed.Add((callback, state));

        }

        public async Task CompleteAsync()
        {

            for (int index = _completed.Count - 1; index >= 0; index--)
            {

                (Func<object, Task> callback, object state) = _completed[index];

                await callback(state);

            }

            _completed.Clear();

        }

    }

    private sealed class TestRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {

        private readonly CancellationTokenSource _abort;

        public TestRequestLifetimeFeature(CancellationToken callerCancellation)
        {

            _abort = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);

            RequestAborted = _abort.Token;

        }

        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {

            _abort.Cancel();

        }

    }

    private sealed class ManualTimeProvider : TimeProvider
    {

        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int ExpectedCount, TaskCompletionSource Completion)> _timerWaiters = [];
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _scheduledTimerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {

            lock (_gate)
            {

                return _utcNow;

            }

        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {

            ArgumentNullException.ThrowIfNull(callback);

            ManualTimer timer = new(this, callback, state);
            _ = timer.Change(dueTime, period);

            return timer;

        }

        public void Advance(TimeSpan amount)
        {

            if (amount < TimeSpan.Zero)
            {

                throw new ArgumentOutOfRangeException(nameof(amount));

            }

            List<(TimerCallback Callback, object? State)> callbacks = [];

            lock (_gate)
            {

                _utcNow = _utcNow.Add(amount);

                foreach (ManualTimer timer in _timers.ToArray())
                {

                    timer.CollectDueCallbacks(_utcNow, callbacks);

                }

            }

            foreach ((TimerCallback callback, object? state) in callbacks)
            {

                callback(state);

            }

        }

        public Task WaitForScheduledTimerCountAsync(int expectedCount)
        {
            lock (_gate)
            {
                if (_scheduledTimerCount >= expectedCount)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource waiter =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerWaiters.Add((expectedCount, waiter));
                return waiter.Task;
            }
        }

        private void ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {

            if (dueTime < Timeout.InfiniteTimeSpan)
            {

                throw new ArgumentOutOfRangeException(nameof(dueTime));

            }

            if (period < Timeout.InfiniteTimeSpan || period == TimeSpan.Zero)
            {

                throw new ArgumentOutOfRangeException(nameof(period));

            }

            List<TaskCompletionSource> completedWaiters = [];

            lock (_gate)
            {

                if (timer.Disposed)
                {

                    throw new ObjectDisposedException(nameof(ManualTimer));

                }

                if (!_timers.Contains(timer))
                {

                    _timers.Add(timer);

                }

                timer.DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _utcNow.Add(dueTime);
                timer.Period = period;

                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _scheduledTimerCount++;

                    for (int index = _timerWaiters.Count - 1; index >= 0; index--)
                    {
                        if (_timerWaiters[index].ExpectedCount > _scheduledTimerCount)
                        {
                            continue;
                        }

                        completedWaiters.Add(_timerWaiters[index].Completion);
                        _timerWaiters.RemoveAt(index);
                    }
                }

            }

            foreach (TaskCompletionSource waiter in completedWaiters)
            {
                waiter.TrySetResult();
            }

        }

        private void RemoveTimer(ManualTimer timer)
        {

            lock (_gate)
            {

                _ = _timers.Remove(timer);

            }

        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state)
            : ITimer
        {

            public bool Disposed { get; private set; }
            public DateTimeOffset? DueAt { get; set; }
            public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {

                owner.ChangeTimer(this, dueTime, period);
                return true;

            }

            public void Dispose()
            {

                if (Disposed)
                {

                    return;

                }

                Disposed = true;
                owner.RemoveTimer(this);

            }

            public ValueTask DisposeAsync()
            {

                Dispose();
                return ValueTask.CompletedTask;

            }

            public void CollectDueCallbacks(
                DateTimeOffset now,
                List<(TimerCallback Callback, object? State)> callbacks)
            {

                if (Disposed || DueAt is not DateTimeOffset dueAt || dueAt > now)
                {

                    return;

                }

                callbacks.Add((callback, state));

                if (Period == Timeout.InfiniteTimeSpan)
                {

                    DueAt = null;

                }
                else
                {

                    do
                    {

                        dueAt = dueAt.Add(Period);

                    }

                    while (dueAt <= now);

                    DueAt = dueAt;

                }

            }

        }

    }

    private sealed class FakeClaimStore : IIdempotencyClaimStore
    {

        private readonly object _gate = new();

        private IdempotencyClaim? _claim;

        private int _acquireCalls;

        private int _completeCalls;

        private int _heartbeatCalls;

        private int _markAbandonedCalls;

        private int _markFailedCalls;

        private int _tryGetCalls;

        public bool BlockFirstAcquire { get; init; }

        public Action? AfterSuccessfulAcquire { get; init; }

        public Task? HeartbeatBlocker { get; init; }

        public Exception? HeartbeatException { get; init; }

        public bool HeartbeatResult { get; set; } = true;

        public bool ReclaimOnHeartbeatFailure { get; set; }

        public Exception? CompleteException { get; set; }

        public Exception? MarkAbandonedException { get; set; }

        public Exception? MarkFailedException { get; set; }

        public Func<int, Exception?>? TryAcquireFailure { get; set; }

        public Func<int, Exception?>? TryGetFailure { get; set; }

        public IdempotencyClaimAcquireResult? AcquireResultOverride { get; init; }

        public bool IgnoreMarkFailed { get; set; }

        public bool ReturnDifferentOwnerOnAcquire { get; set; }

        public bool ReturnClaimedOnAcquire { get; set; }

        public TimeSpan? AcquiredLeaseDuration { get; set; }

        public int TryAcquireCallCount => Volatile.Read(ref _acquireCalls);

        public int CompleteCallCount => Volatile.Read(ref _completeCalls);

        public int HeartbeatCallCount => Volatile.Read(ref _heartbeatCalls);

        public int MarkAbandonedCallCount => Volatile.Read(ref _markAbandonedCalls);

        public int MarkFailedCallCount => Volatile.Read(ref _markFailedCalls);

        public int TryGetCallCount => Volatile.Read(ref _tryGetCalls);

        public IdempotencyClaim? Claim
        {
            get
            {

                lock (_gate)
                {

                    return _claim;

                }

            }
            set
            {

                lock (_gate)
                {

                    _claim = value;

                }

            }
        }

        public IReadOnlyList<DateTimeOffset> HeartbeatLeases
        {
            get
            {

                lock (_gate)
                {

                    return _heartbeatLeases.ToArray();

                }

            }
        }

        private readonly List<DateTimeOffset> _heartbeatLeases = [];

        public TaskCompletionSource FirstAcquireEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstAcquire { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HeartbeatObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForHeartbeatCountAsync(int expectedCount)
        {

            lock (_gate)
            {

                if (_heartbeatCalls >= expectedCount)
                {

                    return Task.CompletedTask;

                }

                TaskCompletionSource waiter =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                _heartbeatWaiters.Add((expectedCount, waiter));
                return waiter.Task;

            }

        }

        private readonly List<(int ExpectedCount, TaskCompletionSource Completion)> _heartbeatWaiters = [];

        public Task<IdempotencyClaim?> GetByIdAsync(
            Guid claimId,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {

                return Task.FromResult(_claim?.Id == claimId ? _claim : null);

            }

        }

        public Task<IdempotencyClaim?> TryGetAsync(
            string claimKeyHash,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            int call = Interlocked.Increment(ref _tryGetCalls);

            Exception? failure = TryGetFailure?.Invoke(call);

            if (failure is not null)
            {

                return Task.FromException<IdempotencyClaim?>(failure);

            }

            lock (_gate)
            {

                return Task.FromResult(_claim);

            }

        }

        public async Task<IdempotencyClaimAcquireResult> TryAcquireAsync(
            IdempotencyClaimAcquireRequest request,
            CancellationToken cancellationToken = default)
        {

            int call = Interlocked.Increment(ref _acquireCalls);

            Exception? failure = TryAcquireFailure?.Invoke(call);

            if (failure is not null)
            {

                throw failure;

            }

            if (AcquireResultOverride is { } overrideResult)
            {

                Claim = overrideResult.Claim;

                return overrideResult;

            }

            IdempotencyClaimAcquireResult result;

            lock (_gate)
            {

                if (_claim is null)
                {

                    DateTimeOffset leaseExpiresAt = AcquiredLeaseDuration is TimeSpan leaseDuration
                        ? request.CreatedAt.Add(leaseDuration)
                        : request.LeaseExpiresAt;

                    IdempotencyClaim created = new(
                        Guid.NewGuid(),
                        request.ClaimKeyHash,
                        request.FingerprintHash,
                        ReturnClaimedOnAcquire
                            ? IdempotencyClaimState.Claimed
                            : IdempotencyClaimState.Running,
                        request.OwnerId,
                        leaseExpiresAt,
                        request.CreatedAt,
                        RunId: null,
                        StatusCode: null,
                        ContentType: null,
                        ResponseBody: null,
                        TerminalStreamComplete: false,
                        request.CreatedAt,
                        request.CreatedAt);

                    _claim = created;

                    result = new IdempotencyClaimAcquireResult(
                        Conflict: false,
                        Acquired: true,
                        Claim: created);

                }
                else if (!string.Equals(
                             _claim.FingerprintHash,
                             request.FingerprintHash,
                             StringComparison.Ordinal))
                {

                    result = new IdempotencyClaimAcquireResult(
                        Conflict: true,
                        Acquired: false,
                        Claim: _claim);

                }
                else if (_claim.State == IdempotencyClaimState.Completed
                         && _claim.TerminalStreamComplete)
                {

                    result = new IdempotencyClaimAcquireResult(
                        Conflict: false,
                        Acquired: false,
                        Claim: _claim);

                }
                else if (_claim.State is IdempotencyClaimState.Failed or IdempotencyClaimState.Abandoned
                         || (_claim.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed
                             && _claim.LeaseExpiresAt <= request.CreatedAt))
                {

                    _claim = _claim with
                    {
                        State = IdempotencyClaimState.Running,
                        OwnerId = request.OwnerId,
                        LeaseExpiresAt = request.LeaseExpiresAt,
                        HeartbeatAt = request.CreatedAt,
                        StatusCode = null,
                        ContentType = null,
                        ResponseBody = null,
                        TerminalStreamComplete = false,
                        UpdatedAt = request.CreatedAt,
                    };

                    result = new IdempotencyClaimAcquireResult(
                        Conflict: false,
                        Acquired: true,
                        Claim: _claim);

                }
                else
                {

                    result = new IdempotencyClaimAcquireResult(
                        Conflict: false,
                        Acquired: false,
                        Claim: _claim);

                }

            }

            if (call == 1
                && BlockFirstAcquire
                && result.Acquired)
            {

                FirstAcquireEntered.SetResult();

                await ReleaseFirstAcquire.Task.WaitAsync(cancellationToken);

            }

            if (result.Acquired && ReturnDifferentOwnerOnAcquire)
            {

                result = result with
                {
                    Claim = result.Claim with
                    {
                        OwnerId = $"different-process:{Guid.NewGuid():N}",
                    },
                };

            }

            if (result.Acquired)
            {

                AfterSuccessfulAcquire?.Invoke();

            }

            return result;

        }

        public async Task<bool> HeartbeatAsync(
            Guid claimId,
            string ownerId,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            int heartbeatCount = Interlocked.Increment(ref _heartbeatCalls);
            List<TaskCompletionSource> completedWaiters = [];
            bool renewed = false;

            lock (_gate)
            {

                _heartbeatLeases.Add(leaseExpiresAt);

                if (HeartbeatException is null
                    && HeartbeatResult
                    && _claim is { } claim
                    && claim.Id == claimId
                    && claim.State == IdempotencyClaimState.Running
                    && string.Equals(claim.OwnerId, ownerId, StringComparison.Ordinal))
                {

                    DateTimeOffset now = DateTimeOffset.UtcNow;

                    _claim = claim with
                    {
                        LeaseExpiresAt = leaseExpiresAt,
                        HeartbeatAt = now,
                        UpdatedAt = now,
                    };
                    renewed = true;

                }
                else if (HeartbeatException is null
                         && !HeartbeatResult
                         && ReclaimOnHeartbeatFailure
                         && _claim is { } reclaimedClaim
                         && reclaimedClaim.Id == claimId)
                {

                    _claim = reclaimedClaim with
                    {
                        OwnerId = $"reclaimed-process:{Guid.NewGuid():N}",
                        LeaseExpiresAt = leaseExpiresAt,
                    };

                }

                for (int index = _heartbeatWaiters.Count - 1; index >= 0; index--)
                {

                    if (_heartbeatWaiters[index].ExpectedCount > heartbeatCount)
                    {

                        continue;

                    }

                    completedWaiters.Add(_heartbeatWaiters[index].Completion);
                    _heartbeatWaiters.RemoveAt(index);

                }

            }

            foreach (TaskCompletionSource waiter in completedWaiters)
            {

                waiter.TrySetResult();

            }

            HeartbeatObserved.TrySetResult();

            if (HeartbeatException is not null)
            {

                throw HeartbeatException;

            }

            if (HeartbeatBlocker is not null)
            {

                await HeartbeatBlocker.WaitAsync(cancellationToken);

            }

            return renewed;

        }

        public Task CompleteAsync(
            Guid claimId,
            string ownerId,
            int statusCode,
            string? contentType,
            string responseBody,
            bool terminalStreamValid,
            Guid? runId,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            _ = Interlocked.Increment(ref _completeCalls);

            if (CompleteException is not null)
            {

                return Task.FromException(CompleteException);

            }

            if (!terminalStreamValid)
            {

                return MarkAbandonedAsync(claimId, ownerId, cancellationToken);

            }

            lock (_gate)
            {

                if (_claim is { } claim
                    && claim.Id == claimId
                    && claim.State == IdempotencyClaimState.Running
                    && string.Equals(claim.OwnerId, ownerId, StringComparison.Ordinal))
                {

                    _claim = claim with
                    {
                        State = IdempotencyClaimState.Completed,
                        StatusCode = statusCode,
                        ContentType = contentType,
                        ResponseBody = responseBody,
                        TerminalStreamComplete = terminalStreamValid,
                    };

                }

            }

            return Task.CompletedTask;

        }

        public Task MarkFailedAsync(
            Guid claimId,
            string ownerId,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            _ = Interlocked.Increment(ref _markFailedCalls);

            if (MarkFailedException is not null)
            {

                return Task.FromException(MarkFailedException);

            }

            lock (_gate)
            {

                if (!IgnoreMarkFailed
                    && _claim is { } claim
                    && claim.Id == claimId
                    && claim.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed
                    && string.Equals(claim.OwnerId, ownerId, StringComparison.Ordinal))
                {

                    _claim = claim with
                    {
                        State = IdempotencyClaimState.Failed,
                        TerminalStreamComplete = false,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };

                }

            }

            return Task.CompletedTask;

        }

        public Task MarkAbandonedAsync(
            Guid claimId,
            string ownerId,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            _ = Interlocked.Increment(ref _markAbandonedCalls);

            if (MarkAbandonedException is not null)
            {

                return Task.FromException(MarkAbandonedException);

            }

            lock (_gate)
            {

                if (_claim is { } claim
                    && claim.Id == claimId
                    && claim.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed
                    && string.Equals(claim.OwnerId, ownerId, StringComparison.Ordinal))
                {

                    _claim = claim with
                    {
                        State = IdempotencyClaimState.Abandoned,
                        TerminalStreamComplete = false,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };

                }

            }

            return Task.CompletedTask;

        }

        public Task<bool> TryReclaimAsync(
            Guid claimId,
            string newOwnerId,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {

                if (_claim is not { } claim
                    || claim.Id != claimId
                    || (claim.State is not (IdempotencyClaimState.Failed or IdempotencyClaimState.Abandoned)
                        && !(claim.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed
                             && claim.LeaseExpiresAt < DateTimeOffset.UtcNow)))
                {

                    return Task.FromResult(false);

                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                _claim = claim with
                {
                    State = IdempotencyClaimState.Running,
                    OwnerId = newOwnerId,
                    LeaseExpiresAt = leaseExpiresAt,
                    HeartbeatAt = now,
                    StatusCode = null,
                    ContentType = null,
                    ResponseBody = null,
                    TerminalStreamComplete = false,
                    UpdatedAt = now,
                };

                return Task.FromResult(true);

            }

        }

        public Task LinkRunAsync(
            Guid claimId,
            Guid runId,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {

                if (_claim is { } claim && claim.Id == claimId)
                {

                    _claim = claim with { RunId = runId };

                }

            }

            return Task.CompletedTask;

        }

        public Task<int> DeleteExpiredAsync(
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

}
