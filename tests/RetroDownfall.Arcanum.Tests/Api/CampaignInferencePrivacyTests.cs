using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class CampaignInferencePrivacyTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_HTTP_turns_are_private_before_stream_heartbeats_and_reenter_authoritative_replay(bool streaming)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { CampaignRollups = true },
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid campaignId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            string name = campaignId.ToString("N");

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            db.Campaigns.Add(new Campaign
            {
                Id = campaignId, Name = name, NameLower = name, Path = factory.TempHome,
                Settings = "{}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            _ = await db.SaveChangesAsync();
        }

        string route = streaming ? "/api/intelligence/ping-stream" : "/api/intelligence/ping";

        PingRequest request = new("continue", CampaignId: campaignId);

        string key = Guid.NewGuid().ToString("N");

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (streaming)
        {
            factory.FakeIntelligence.StreamGate = gate;
        }

        using HttpRequestMessage first = Message();

        using HttpResponseMessage response = await client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(45));

        gate.TrySetResult();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).ToString());

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

        Assert.Equal(ArcanumExecutionSurface.SessionBackedOperatorTurn, factory.FakeIntelligence.LastInvocation!.Surface);

        _ = await response.Content.ReadAsStringAsync();

        factory.FakeIntelligence.StreamGate = null;

        factory.FakeIntelligence.NextFailure = new Error("test.authoritative-replay-refusal", "The authoritative answer was erased.");

        using HttpRequestMessage retry = Message();

        using HttpResponseMessage refused = await client.SendAsync(retry);

        Assert.Contains("test.authoritative-replay-refusal", await refused.Content.ReadAsStringAsync());

        Assert.Equal(2, streaming ? factory.FakeIntelligence.StreamPromptCallCount : factory.FakeIntelligence.ExecutePromptCallCount);

        HttpRequestMessage Message()
        {
            HttpRequestMessage message = new(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(request, ArcanumJsonContext.Default.PingRequest),
            };

            message.Headers.Add(ArcanumApiHeaders.IdempotencyKey, key);

            return message;
        }
    }
}
