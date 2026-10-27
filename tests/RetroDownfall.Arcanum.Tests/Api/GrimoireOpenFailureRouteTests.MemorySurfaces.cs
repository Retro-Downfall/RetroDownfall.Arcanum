using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GrimoireOpenFailureRouteTests
{
    [SkippableTheory]
    [InlineData("/api/memory/status", false)]
    [InlineData("/api/memory/status/{session}", false)]
    [InlineData("/api/memory/search", true)]
    public async Task Memory_routes_that_open_storage_distinguish_native_failure_from_maintenance(string route, bool post)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FailingOpen seam = new();

        MaintenanceHostLogCapture logs = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.AddSingleton<IGrimoireOrdinaryConnectionFactoryTestSeam>(seam);

                services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        if (route.Contains("{session}", StringComparison.Ordinal))
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            ISessionRepository sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

            var session = await sessions.CreateAsync(null, "Open failure scope", CancellationToken.None);

            route = route.Replace("{session}", session.Id.ToString("D"), StringComparison.Ordinal);
        }

        seam.Enabled = true;

        using HttpRequestMessage request = new(post ? HttpMethod.Post : HttpMethod.Get, route);

        if (post)
        {
            request.Content = new StringContent("{\"query\":\"needle\",\"scope\":\"session\"}", Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Contains(ErrorCodes.Covenant.Unavailable, body, StringComparison.Ordinal);

        Assert.Contains("526", body, StringComparison.Ordinal);

        Assert.DoesNotContain("maintenance", body, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(FailingOpen.Sentinel, body, StringComparison.Ordinal);

        Assert.Contains(logs.Entries, static entry => entry.Level == LogLevel.Warning && entry.Message.Contains("526", StringComparison.Ordinal));

        Assert.All(logs.Entries, static entry =>
        {
            Assert.DoesNotContain(FailingOpen.Sentinel, entry.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(FailingOpen.Sentinel, entry.Exception?.ToString() ?? "", StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task Session_divination_preserves_the_native_open_failure_category()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FailingOpen seam = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<IGrimoireOrdinaryConnectionFactoryTestSeam>(seam),
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        seam.Enabled = true;

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        await Assert.ThrowsAsync<GrimoireConnectionUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Tower.ISessionDivinationService>()
                .SearchAsync(new Microsoft.Extensions.AI.Embedding<float>(new float[] { 1, 0, 0 }), null, null, 1, 0, CancellationToken.None));
    }
}
