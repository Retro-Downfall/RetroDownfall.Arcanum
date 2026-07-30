using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Each test owns its own host because a successful quit stops it.
/// </summary>
[Collection("ApiHost")]
public sealed class ServerLifecycleEndpointTests
{

    [SkippableFact]
    public async Task QuitServer_Authenticated_AcceptsThenStopsTheHost()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        IHostApplicationLifetime lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration registration =
            lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        HttpResponseMessage response = await client.PostAsync("/api/server/quit", content: null);

        // Accepted must be observable by the caller: the host commits the response before stopping
        // Kestrel, otherwise `arcanum serve quit` would only ever see a dropped connection.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [SkippableFact]
    public async Task QuitServer_WithoutApiKey_IsRejectedAndLeavesTheHostRunning()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateClient();

        IHostApplicationLifetime lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        HttpResponseMessage response = await client.PostAsync("/api/server/quit", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

}
