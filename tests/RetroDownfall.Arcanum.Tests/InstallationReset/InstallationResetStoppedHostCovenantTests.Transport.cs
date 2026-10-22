using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Cli;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed partial class InstallationResetStoppedHostCovenantTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopped_host_composition_never_contacts_a_listening_host_at_its_configured_endpoint(
        bool covenantFeature)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Logging.ClearProviders();

        await using WebApplication responder = builder.Build();

        int contacted = 0;

        responder.Run(context =>
        {
            Interlocked.Increment(ref contacted);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;

            return Task.CompletedTask;
        });

        await responder.StartAsync();

        Uri endpoint = new(responder.Urls.Single());

        // Prove the controlled endpoint really answers before testing that our stopped-host
        // composition isolates its transport. No operator daemon or fixed port is involved.
        using (HttpClient client = new())
        using (HttpResponseMessage response = await client.GetAsync(endpoint))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(1, Volatile.Read(ref contacted));

        await InstallPriorGrimoireAsync(covenantFeature);

        ServiceCollection services = Compose(covenantFeature);

        services.PostConfigure<ArcanumSettings>(settings =>
        {
            settings.Host.Port = endpoint.Port;

            settings.Host.Https.Enabled = false;
        });

        CliTestResult result = await CliTestHarness.RunAsync(services, GlobalArguments(apply: true));

        Assert.True(result.ExitCode == (int)CliExitCode.Success,
            $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}");

        await using ServiceProvider provider = services.BuildServiceProvider();

        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        foreach (string name in new[] { ArcanumApiClient.RequestHttpClientName, ArcanumApiClient.StreamingHttpClientName })
        {
            using HttpClient client = factory.CreateClient(name);

            HttpRequestException refusal = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.GetAsync("api/health"));

            Assert.Equal(SocketError.ConnectionRefused, Assert.IsType<SocketException>(refusal.InnerException).SocketErrorCode);
        }

        Assert.Equal(1, Volatile.Read(ref contacted));
    }

    private static void ConfigureStoppedHostTransport(IServiceCollection services)
    {
        // These lifecycle cases require a stopped host regardless of what listens on the developer's
        // configured port. Keep the real credential lease and API client, including refusal handling.
        services.AddHttpClient(ArcanumApiClient.RequestHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new StoppedHostConnectionRefusedHandler());

        services.AddHttpClient(ArcanumApiClient.StreamingHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new StoppedHostConnectionRefusedHandler());
    }

    private sealed class StoppedHostConnectionRefusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("The test host is stopped.",
                new SocketException((int)SocketError.ConnectionRefused));
    }
}
