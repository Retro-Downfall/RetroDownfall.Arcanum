using System.Net;

using Microsoft.Extensions.Configuration;

using RetroDownfall.Arcanum.Api;

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
