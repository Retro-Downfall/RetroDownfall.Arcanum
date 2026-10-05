using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// What <see cref="ApiBootstrapper.AddArcanumHostFiltering"/> hands the framework's host-filtering
/// middleware, checked on the same kind of host the product builds (<c>WebApplication.CreateSlimBuilder</c>)
/// by sending a request, and what name it takes from the callback base URL.
/// </summary>
/// <remarks>
/// <see cref="HostHeaderAllowListTests"/> runs the whole product host. These cover what that host makes
/// slow to vary: the effective bind and every shape of configured callback URL, without a Grimoire or a
/// listener. They also pin the assumption the product relies on, that a slim host enforces the options
/// with no <c>UseHostFiltering</c> call of Arcanum's own.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class HostHeaderAllowListRegistrationTests : IDisposable
{
    private readonly string? _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

    public HostHeaderAllowListRegistrationTests() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

    public void Dispose() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

    private static ArcanumSettings CallbackSettings(string baseUrl, bool pushNotifications = true) =>
        new()
        {
            Features = new FeatureSettings { Conclave = true, A2AClient = true },
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings { PushNotifications = pushNotifications, PushCallbackBaseUrl = baseUrl },
            },
        };

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(bool listenAny, ArcanumSettings? settings = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        if (listenAny)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Arcanum:Host:ListenAny"] = "true" });
        }

        ArcanumSettings startupSettings = settings ?? new ArcanumSettings();

        builder.Services.Configure<ArcanumSettings>(target => ConfigurationBootstrapper.CopySettings(startupSettings, target));

        builder.Services.AddArcanumHostFiltering(builder.Configuration);

        WebApplication app = builder.Build();

        app.MapGet("/ping", static () => "pong");

        await app.StartAsync();

        return (app, app.GetTestClient());
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string host)
    {
        using HttpResponseMessage response = await client.GetAsync($"http://{host}/ping");

        return response.StatusCode;
    }

    [Theory]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("127.0.0.1:5001", HttpStatusCode.OK)]
    [InlineData("[::1]:5001", HttpStatusCode.OK)]
    [InlineData("rebound.example.com", HttpStatusCode.BadRequest)]
    [InlineData("192.168.1.10:5001", HttpStatusCode.BadRequest)]
    public async Task A_loopback_only_bind_answers_the_loopback_names_and_no_others(string host, HttpStatusCode expected)
    {
        (WebApplication app, HttpClient client) = await StartAsync(listenAny: false);

        await using WebApplication _ = app;

        Assert.Equal(expected, await GetAsync(client, host));
    }

    /// <summary>
    /// The framework's own fallback to <c>*</c> only applies while the list is empty, so an all-interfaces
    /// bind must register nothing: a list that held even the loopback names would answer every network
    /// client and every reverse proxy with a 400.
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("192.168.1.10:5001")]
    [InlineData("arcanum.example.com")]
    [InlineData("rebound.example.com:5001")]
    public async Task An_all_interfaces_bind_answers_any_name(string host)
    {
        (WebApplication app, HttpClient client) = await StartAsync(listenAny: true);

        await using WebApplication _ = app;

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, host));
    }

    /// <summary>
    /// The callback name is an addition to a loopback-only list, never a way to widen an all-interfaces bind
    /// into a filtered one.
    /// </summary>
    [Fact]
    public async Task An_all_interfaces_bind_with_a_callback_base_url_still_answers_any_name()
    {
        (WebApplication app, HttpClient client) = await StartAsync(listenAny: true, CallbackSettings("https://arcanum.example.com"));

        await using WebApplication _ = app;

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "arcanum.example.com"));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "192.168.1.10:5001"));
    }

    [Theory]
    [InlineData("https://arcanum.example.com", "arcanum.example.com", "rebound.example.com")]
    [InlineData("https://Arcanum.Example.com:8443/base/path", "arcanum.example.COM:8443", "rebound.example.com")]
    [InlineData("http://192.0.2.7:9000", "192.0.2.7:9000", "192.0.2.8")]
    [InlineData("https://[2001:db8::1]:8443", "[2001:db8::1]:8443", "[2001:db8::2]")]
    [InlineData("https://bücher.example", "xn--bcher-kva.example", "xn--other-kva.example")]
    public async Task The_host_of_the_callback_base_url_is_answered_beside_the_loopback_names(string baseUrl, string answered, string refused)
    {
        (WebApplication app, HttpClient client) = await StartAsync(listenAny: false, CallbackSettings(baseUrl));

        await using WebApplication _ = app;

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, answered));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "localhost"));

        Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(client, refused));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("arcanum.example.com")]
    [InlineData("ftp://arcanum.example.com")]
    [InlineData("https://")]
    [InlineData("https://*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://arcanum.*.example.com")]
    public void A_base_url_that_names_no_host_adds_nothing(string baseUrl)
    {
        Assert.Null(ApiBootstrapper.ResolveCallbackAuthorityHost(CallbackSettings(baseUrl)));

        Assert.Equal(ApiBootstrapper.LoopbackHostNames, ApiBootstrapper.ResolveAllowedHostNames(CallbackSettings(baseUrl)));
    }

    [Fact]
    public void The_callback_surface_being_off_adds_nothing()
    {
        Assert.Null(ApiBootstrapper.ResolveCallbackAuthorityHost(CallbackSettings("https://arcanum.example.com", pushNotifications: false)));

        ArcanumSettings noConclave = CallbackSettings("https://arcanum.example.com") with { Features = new FeatureSettings() };

        Assert.Null(ApiBootstrapper.ResolveCallbackAuthorityHost(noConclave));

        Assert.Null(ApiBootstrapper.ResolveCallbackAuthorityHost(new ArcanumSettings()));
    }

    [Fact]
    public void The_allowed_names_are_the_loopback_names_then_the_callback_host()
    {
        Assert.Equal(
            ["localhost", "127.0.0.1", "[::1]", "arcanum.example.com"],
            ApiBootstrapper.ResolveAllowedHostNames(CallbackSettings("https://arcanum.example.com:8443")));
    }
}
