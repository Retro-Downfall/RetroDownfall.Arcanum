using System.Net;

using Microsoft.Extensions.Configuration;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// A loopback-only host answers only the names that reach a loopback listener, so a page that has
/// rebound its own DNS name to 127.0.0.1 cannot talk to the API under that name.
/// </summary>
/// <remarks>
/// The raw-peer loopback gates cannot stop that: the browser really is on the same machine. The Host
/// header it sends carries the attacker's name, which is what the allow-list refuses. An all-interfaces
/// bind (<c>ListenAny</c>) is the one topology that legitimately answers other names (a network client,
/// or a reverse proxy relaying the public name), so the allow-list is not applied there.
/// </remarks>
[Collection("ApiHost")]
public sealed class HostHeaderAllowListTests(ArcanumWebApplicationFactory factory)
{
    [SkippableTheory]
    [InlineData("http://localhost/api/health")]
    [InlineData("http://localhost:5001/api/health")]
    [InlineData("http://LOCALHOST:5001/api/health")]
    [InlineData("http://127.0.0.1/api/health")]
    [InlineData("http://127.0.0.1:5001/api/health")]
    [InlineData("http://[::1]/api/health")]
    [InlineData("http://[::1]:5001/api/health")]
    public async Task A_loopback_name_is_answered(string url)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync(url);

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData("http://rebound.example.com/api/health")]
    [InlineData("http://rebound.example.com:5001/api/health")]
    [InlineData("http://localhost.example.com/api/health")]
    [InlineData("http://127.0.0.1.example.com:5001/api/health")]
    [InlineData("http://127.0.0.2:5001/api/health")]
    [InlineData("http://[::2]:5001/api/health")]
    public async Task Any_other_name_is_refused_before_the_route_runs(string url)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The refusal precedes authentication, so a rebinding page learns nothing from the route.
    /// </summary>
    [SkippableFact]
    public async Task The_refusal_precedes_authentication()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync("http://rebound.example.com/api/health");

        Assert.Equal(HttpStatusCode.BadRequest, anonymous.StatusCode);
    }

    /// <summary>
    /// The dev host binds loopback whatever the all-interfaces settings say, so it keeps the loopback
    /// allow-list under them too: a page that rebinds its name to 127.0.0.1 reaches a dev host exactly as it
    /// would reach a loopback <c>arcanum serve</c>.
    /// </summary>
    /// <remarks>
    /// An all-interfaces bind of <c>arcanum serve</c>, which does answer every name, is pinned by
    /// <see cref="HostHeaderAllowListRegistrationTests"/> through the same registration the host uses. This
    /// host (the dev host the test factory runs) never binds anything but loopback, so the allow-list is its
    /// own, whatever <c>Arcanum:Host:ListenAny</c> or <c>ARCANUM_HOST_ANY</c> say.
    /// </remarks>
    [Collection("ProcessEnvironment")]
    public sealed class DevHostUnderTheAllInterfacesOverride : IDisposable
    {
        private readonly string? _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        public DevHostUnderTheAllInterfacesOverride() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "true");

        public void Dispose() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

        [SkippableTheory]
        [InlineData("http://192.168.1.10:5001/api/health", true)]
        [InlineData("https://arcanum.example.com/api/health", true)]
        [InlineData("http://rebound.example.com:5001/api/health", true)]
        [InlineData("http://localhost:5001/api/health", false)]
        public async Task Only_the_loopback_names_are_answered(string url, bool refused)
        {
            Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

            await using ArcanumWebApplicationFactory devHost = new();

            // The startup validator refuses an all-interfaces setting that is not HTTPS-only. It checks the
            // certificate path names a file and loads nothing, and the test server binds no socket.
            string certificatePath = Path.Combine(devHost.TempHome, "all-interfaces-test.pfx");

            await File.WriteAllBytesAsync(certificatePath, []);

            devHost.SettingsOverride = settings => settings with
            {
                Host = settings.Host with
                {
                    Https = new HttpsSettings { Enabled = true, CertificatePath = certificatePath, Port = 5443 },
                },
            };

            using HttpClient client = devHost.CreateAuthenticatedClient();

            using HttpResponseMessage response = await client.GetAsync(url);

            Assert.Equal(refused, response.StatusCode == HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// A loopback-only host that tells peers where to post a Sending's callback also answers the name it
    /// told them, so a same-host proxy or tunnel relaying that public name reaches the callback route.
    /// </summary>
    /// <remarks>
    /// <c>Arcanum:Integrations:A2A:PushCallbackBaseUrl</c> is the operator's own statement of the name this
    /// instance is reached by from outside. Caddy's <c>reverse_proxy</c> and cloudflared pass the public
    /// Host through unchanged, and a callback refused at the door leaves a Sending that released its
    /// concurrency slot waiting for a notification that never arrives.
    /// </remarks>
    [Collection("ProcessEnvironment")]
    public sealed class ConfiguredCallbackAuthority : IDisposable
    {
        private const string CallbackRoute = "/api/conclave/a2a/callbacks/nonexistent";

        private readonly string? _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        public ConfiguredCallbackAuthority() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

        public void Dispose() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

        private static ArcanumWebApplicationFactory CreateFactory(bool pushNotifications, string pushCallbackBaseUrl) =>
            new()
            {
                SettingsOverride = settings => settings with
                {
                    Features = (settings.Features ?? new FeatureSettings()) with
                    {
                        Conclave = true,
                        A2AClient = true,
                    },
                    Integrations = (settings.Integrations ?? new IntegrationSettings()) with
                    {
                        A2A = new A2AIntegrationSettings
                        {
                            PushNotifications = pushNotifications,
                            PushCallbackBaseUrl = pushCallbackBaseUrl,
                        },
                    },
                },
            };

        [SkippableFact]
        public async Task The_host_of_the_callback_base_url_reaches_the_callback_route_and_no_other_name_is_added()
        {
            Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

            await using ArcanumWebApplicationFactory configured = CreateFactory(pushNotifications: true, "https://arcanum.example.com:8443");

            using HttpClient client = configured.CreateClient();

            using HttpResponseMessage callback = await client.PostAsync($"http://arcanum.example.com{CallbackRoute}", content: null);

            // 404 is the route's own answer for a callback nobody is waiting on: the request got past the door.
            Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);

            using HttpResponseMessage callbackWithPort = await client.PostAsync($"http://ARCANUM.example.com:8443{CallbackRoute}", content: null);

            Assert.Equal(HttpStatusCode.NotFound, callbackWithPort.StatusCode);

            using HttpResponseMessage loopback = await client.PostAsync($"http://localhost{CallbackRoute}", content: null);

            Assert.Equal(HttpStatusCode.NotFound, loopback.StatusCode);

            using HttpResponseMessage other = await client.PostAsync($"http://rebound.example.com{CallbackRoute}", content: null);

            Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);

            using HttpResponseMessage sibling = await client.PostAsync($"http://api.arcanum.example.com{CallbackRoute}", content: null);

            Assert.Equal(HttpStatusCode.BadRequest, sibling.StatusCode);
        }

        /// <summary>
        /// The configured name is admitted for the callback route alone: it is where the operator told peers
        /// to post, not a name the rest of the API is reached by, so a same-host proxy or tunnel relaying it
        /// reaches nothing else.
        /// </summary>
        [SkippableFact]
        public async Task The_host_of_the_callback_base_url_reaches_no_route_but_the_callback()
        {
            Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

            await using ArcanumWebApplicationFactory configured = CreateFactory(pushNotifications: true, "https://arcanum.example.com:8443");

            using HttpClient client = configured.CreateAuthenticatedClient();

            using HttpResponseMessage health = await client.GetAsync("http://arcanum.example.com/api/health");

            Assert.Equal(HttpStatusCode.BadRequest, health.StatusCode);

            using HttpResponseMessage meta = await client.GetAsync("http://arcanum.example.com:8443/api/meta");

            Assert.Equal(HttpStatusCode.BadRequest, meta.StatusCode);

            using HttpResponseMessage callback = await client.PostAsync($"http://arcanum.example.com{CallbackRoute}", content: null);

            Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);

            using HttpResponseMessage loopback = await client.GetAsync("http://localhost/api/health");

            Assert.NotEqual(HttpStatusCode.BadRequest, loopback.StatusCode);
        }

        /// <summary>
        /// Without the push-notification surface there is no callback route and no callback to receive, so
        /// the configured name is not an authority anyone has reason to answer.
        /// </summary>
        [SkippableFact]
        public async Task A_callback_base_url_with_push_notifications_off_adds_no_name()
        {
            Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

            await using ArcanumWebApplicationFactory configured = CreateFactory(pushNotifications: false, "https://arcanum.example.com");

            using HttpClient client = configured.CreateClient();

            using HttpResponseMessage response = await client.PostAsync($"http://arcanum.example.com{CallbackRoute}", content: null);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>
    /// Whether the allow-list applies follows the effective bind and nothing else.
    /// </summary>
    [Collection("ProcessEnvironment")]
    public sealed class Effectiveness : IDisposable
    {
        private readonly string? _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        public Effectiveness() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

        public void Dispose() => global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

        [Theory]
        [InlineData(null, true)]
        [InlineData("false", true)]
        [InlineData("true", false)]
        public void It_applies_to_a_loopback_bind_and_not_to_an_all_interfaces_bind(string? listenAny, bool expected)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(listenAny is null
                    ? []
                    : new Dictionary<string, string?> { ["Arcanum:Host:ListenAny"] = listenAny })
                .Build();

            Assert.Equal(expected, ApiBootstrapper.IsHostFilteringEffective(configuration));
        }

        [Fact]
        public void The_all_interfaces_environment_override_turns_it_off_whatever_the_configuration_says()
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "true");

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Arcanum:Host:ListenAny"] = "false" })
                .Build();

            Assert.False(ApiBootstrapper.IsHostFilteringEffective(configuration));
        }
    }
}
