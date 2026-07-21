using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

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
        PingRequest request = new(
            Prompt: "idempotent attachment ping",
            SessionId: Guid.NewGuid(),
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

        _factory.FakeIntelligence.ExecuteGate = gate;

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

        // Wait until the leader has entered the handler (behind the gate).
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (_factory.FakeIntelligence.ExecutePromptCallCount < before + 1
               && DateTimeOffset.UtcNow < deadline)
        {

            await Task.Delay(20);

        }

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        // Give the waiter time to join the single-flight while the leader is still gated.
        await Task.Delay(150);

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

        // A dedicated factory is required: default `IdempotencyMaxResponseBytes` (10 MiB) is far
        // larger than what's practical to generate in a fast unit test, so this narrows the cap to
        // the clamp floor (1 MiB) and drives a response just over it.
        await using ArcanumWebApplicationFactory oversizedFactory = new()
        {
            SettingsOverride = settings => settings with
            {
                Security = settings.Security with { IdempotencyMaxResponseBytes = 1 },
            },
        };

        string oversizedText = new('x', 2 * 1024 * 1024);

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

}
