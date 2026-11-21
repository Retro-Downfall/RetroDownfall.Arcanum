using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class RepairActivationDrainTests
{
    [SkippableFact]
    public async Task Authenticated_activation_waits_for_previously_admitted_work_and_fences_later_requests()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var host = new ArcanumWebApplicationFactory();

        using HttpClient client = host.CreateAuthenticatedClient();

        using JsonDocument meta = JsonDocument.Parse(await client.GetStringAsync("/api/meta"));

        JsonElement boot = meta.RootElement.GetProperty("data");

        var intent = new
        {
            activationId = Guid.NewGuid(),
            processId = boot.GetProperty("processId").GetInt32(),
            startTime = boot.GetProperty("startTime").GetDateTimeOffset(),
            qualificationSha256 = new string('1', 64),
        };

        StringContent Body(Guid id) => new($$"""
            {"activationId":"{{id:D}}","processId":{{intent.processId}},"startTime":"{{intent.startTime:O}}","qualificationSha256":"{{intent.qualificationSha256}}"}
            """, Encoding.UTF8, "application/json");

        using HttpClient anonymous = host.CreateClient();

        using HttpResponseMessage untrusted = await anonymous.PostAsync("/api/server/repair-drain", Body(intent.activationId));

        Assert.Equal(HttpStatusCode.Unauthorized, untrusted.StatusCode);

        var gate = host.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out var admitted));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        Task<HttpResponseMessage> drain = client.PostAsync("/api/server/repair-drain", Body(intent.activationId), deadline.Token);

        try
        {
            while (!drain.IsCompleted)
            {
                using HttpResponseMessage current = await client.GetAsync("/api/grimoire/stats", deadline.Token);

                if (current.StatusCode == HttpStatusCode.ServiceUnavailable) break;

                await Task.Delay(20, deadline.Token);
            }

            Assert.False(drain.IsCompleted);
        }
        finally
        {
            await admitted!.DisposeAsync();
        }

        using HttpResponseMessage result = await drain;

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        using JsonDocument drained = JsonDocument.Parse(await result.Content.ReadAsStringAsync());

        Assert.Equal(intent.activationId, drained.RootElement.GetProperty("data").GetProperty("activationId").GetGuid());

        using HttpResponseMessage repeat = await client.PostAsync("/api/server/repair-drain", Body(intent.activationId), deadline.Token);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);

        using HttpResponseMessage competitor = await client.PostAsync("/api/server/repair-drain",
            Body(Guid.NewGuid()), deadline.Token);

        Assert.Equal(HttpStatusCode.Conflict, competitor.StatusCode);

        using HttpResponseMessage refused = await client.GetAsync("/api/grimoire/stats", deadline.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
    }
}
