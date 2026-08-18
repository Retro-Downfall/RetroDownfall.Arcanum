using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ArcanumConnectionSettingsChangeTests
{

    [Fact]
    public void ConnectionSettingsUnchanged_IgnoresLayoutThemeCampaignAndApiKeyPatches()
    {

        TheForgeSettings previous = new()
        {
            BaseUrl = "http://127.0.0.1:5001",
            ApiKey = "secret",
            AutoConnect = true,
            Theme = "light",
            LayoutState = null,
            LastCampaignId = null,
        };

        TheForgeSettings patched = previous with
        {
            Theme = "dark",
            LayoutState = "{\"version\":1}",
            LastCampaignId = Guid.NewGuid(),
            ApiKey = null, // keychain migration strips plaintext
        };

        Assert.True(ArcanumConnectionService.ConnectionSettingsUnchanged(previous, patched));

    }

    [Fact]
    public void ConnectionSettingsUnchanged_DetectsBaseUrlAndAutoConnect()
    {

        TheForgeSettings previous = new()
        {
            BaseUrl = "http://127.0.0.1:5001",
            ApiKey = "secret",
            AutoConnect = true,
        };

        Assert.False(
            ArcanumConnectionService.ConnectionSettingsUnchanged(
                previous,
                previous with { BaseUrl = "http://127.0.0.1:5002" }));

        Assert.False(
            ArcanumConnectionService.ConnectionSettingsUnchanged(
                previous,
                previous with { AutoConnect = false }));

    }

    [Theory]
    [InlineData("Security.MissingApiKey", true)]
    [InlineData("Auth.Unauthorized", true)]
    [InlineData("Connection.Failed", false)]
    [InlineData("Health.Unhealthy", false)]
    public void IsImmediateError_OnlyAuthCodes(string code, bool expected) =>
        Assert.Equal(expected, ArcanumConnectionService.IsImmediateError(code));

    [Fact]
    public async Task ConnectAndDisconnect_AreSafeAcrossThreads()
    {

        // Connect()/Disconnect() run on both the Avalonia UI thread (Anvil, MainViewModel, setup wizard)
        // and the the-forge.json reload thread (IOptionsMonitor.OnChange), so an unsynchronised
        // cancel/dispose/reassign of _pollCts throws ObjectDisposedException or orphans a poll loop.
        using ArcanumConnectionService service = NewService();

        List<Exception> failures = [];

        Task[] workers = new Task[6];

        for (int worker = 0; worker < workers.Length; worker++)
        {

            workers[worker] = Task.Run(() =>
            {

                for (int iteration = 0; iteration < 300; iteration++)
                {

                    try
                    {

                        service.Connect();

                        service.Disconnect();

                    }
                    catch (Exception ex)
                    {

                        lock (failures)
                        {

                            failures.Add(ex);

                        }

                    }

                }

            });

        }

        await Task.WhenAll(workers);

        Assert.Empty(failures);

    }

    private static ArcanumConnectionService NewService()
    {

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings
        {
            BaseUrl = "http://localhost:5001",
            AutoConnect = false,
        });

        ArcanumApiClient apiClient = new(
            new UnreachableHttpClientFactory(),
            settings,
            new StubApiKeyProvider(),
            NullLogger<ArcanumApiClient>.Instance);

        return new ArcanumConnectionService(
            apiClient,
            settings,
            NullLogger<ArcanumConnectionService>.Instance);

    }

    private sealed class StubApiKeyProvider : ITheForgeApiKeyProvider
    {

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }

    }

    private sealed class UnreachableHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(new UnreachableHandler(), disposeHandler: false);

    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("unreachable"));

    }

}
