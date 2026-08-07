using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Daemons;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /api/unseen-servant/jobs/{name}/initiative</c>. The pacer is deliberately a no-op for a name
/// that is not in <c>Arcanum:Daemon:Jobs</c>, so the endpoint must not report success for one — a typo'd
/// job name previously returned 200 with a fabricated status and the operator believed the change landed.
/// </summary>
public sealed class DaemonInitiativeEndpointTests
{

    private const string ConfiguredJob = "saga-extraction";

    [Fact]
    public async Task Initiative_for_an_unknown_job_returns_404_and_never_touches_the_pacer()
    {

        (WebApplication app, RecordingPacer pacer) = await CreateHostAsync();

        await using (app)
        {

            using HttpClient client = app.GetTestClient();

            HttpResponseMessage response = await PostInitiativeAsync(client, "saga-extraciton", 15);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.Contains(ErrorCodes.Daemon.NotFound, body, StringComparison.Ordinal);

            Assert.Contains("arcanum daemon jobs", body, StringComparison.Ordinal);

            Assert.Empty(pacer.Applied);

            await app.StopAsync();

        }

    }

    [Fact]
    public async Task Initiative_for_a_configured_job_applies_the_override()
    {

        (WebApplication app, RecordingPacer pacer) = await CreateHostAsync();

        await using (app)
        {

            using HttpClient client = app.GetTestClient();

            HttpResponseMessage response = await PostInitiativeAsync(client, ConfiguredJob, 15);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Equal((ConfiguredJob, 15), Assert.Single(pacer.Applied));

            await app.StopAsync();

        }

    }

    [Fact]
    public async Task Initiative_rejects_an_out_of_range_interval()
    {

        (WebApplication app, RecordingPacer pacer) = await CreateHostAsync();

        await using (app)
        {

            using HttpClient client = app.GetTestClient();

            HttpResponseMessage zero = await PostInitiativeAsync(client, ConfiguredJob, 0);

            Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

            HttpResponseMessage negative = await PostInitiativeAsync(client, ConfiguredJob, -5);

            Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

            Assert.Empty(pacer.Applied);

            await app.StopAsync();

        }

    }

    private static Task<HttpResponseMessage> PostInitiativeAsync(HttpClient client, string jobName, int minutes)
    {

        string payload = JsonSerializer.Serialize(
            new AdjustInitiativeRequestDto(minutes),
            ArcanumJsonContext.Default.AdjustInitiativeRequestDto);

        return client.PostAsync(
            $"/api/unseen-servant/jobs/{jobName}/initiative",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private static async Task<(WebApplication App, RecordingPacer Pacer)> CreateHostAsync()
    {

        RecordingPacer pacer = new();

        ArcanumSettings settings = new()
        {
            Daemon = new DaemonSettings
            {
                Jobs =
                [
                    new UnseenServantJob
                    {
                        Name = ConfiguredJob,
                        TargetSpell = "extract-saga",
                        IntervalMinutes = 60,
                        Enabled = true,
                    },
                ],
            },
        };

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<IUnseenServantPacer>(pacer);

        builder.Services.AddSingleton<IUnseenServantJobTracker>(new InertTracker());

        builder.Services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(settings));

        builder.Services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        WebApplication app = builder.Build();

        _ = app.MapGroup("/api").MapDaemonEndpoints();

        await app.StartAsync();

        return (app, pacer);

    }

    private sealed class RecordingPacer : IUnseenServantPacer
    {

        public List<(string JobName, int IntervalMinutes)> Applied { get; } = [];

        public void SetDynamicInterval(string jobName, int intervalMinutes) =>
            Applied.Add((jobName, intervalMinutes));

        public int GetEffectiveInterval(UnseenServantJob job) => job.IntervalMinutes;

        public Task HydrateAsync(
            IReadOnlyList<UnseenServantWatermark> watermarks,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

    }

    private sealed class InertTracker : IUnseenServantJobTracker
    {

        public void RecordCompletion(UnseenServantJob job, bool success, string? resultSummary)
        {

            // Nothing to record for an endpoint-shape test.

        }

        public DateTimeOffset? GetLastRunAt(UnseenServantJob job) => null;

        public DateTimeOffset? GetNextDueAt(UnseenServantJob job, int effectiveIntervalMinutes) => null;

        public string? GetLastResult(UnseenServantJob job) => null;

        public Task HydrateAsync(
            IReadOnlyList<UnseenServantWatermark> watermarks,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

    }

}
