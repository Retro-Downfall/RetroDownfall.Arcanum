using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("ProcessEnvironment")]
public sealed class ArcanumLocalApiAddressTests : IDisposable
{

    private readonly string? _originalHostAny;

    public ArcanumLocalApiAddressTests()
    {

        _originalHostAny = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", null);

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", _originalHostAny);

    }

    [Fact]
    public void ResolveBaseUrl_loopback_uses_http_host_port()
    {

        HostSettings host = new()
        {
            Port = 5001,
            ListenAny = false,
            Https = new HttpsSettings { Enabled = false, Port = 5443 },
        };

        Assert.Equal("http://localhost:5001/", ArcanumLocalApiAddress.ResolveBaseUrl(host));

        Assert.Equal("http://localhost:5001/api/health", ArcanumLocalApiAddress.ResolveHealthProbeUrl(host));

        Assert.Equal("http://localhost:5001", ArcanumLocalApiAddress.ResolveHttpUrl(host, listenAny: false));

        Assert.Null(ArcanumLocalApiAddress.ResolveHttpsUrl(host, httpsEnabled: false));

    }

    [Fact]
    public void ResolveBaseUrl_listen_any_uses_https_https_port()
    {

        HostSettings host = new()
        {
            Port = 5001,
            ListenAny = true,
            Https = new HttpsSettings { Enabled = true, Port = 5443 },
        };

        Assert.Equal("https://localhost:5443/", ArcanumLocalApiAddress.ResolveBaseUrl(host));

        Assert.Equal("https://localhost:5443/api/health", ArcanumLocalApiAddress.ResolveHealthProbeUrl(host));

        Assert.Null(ArcanumLocalApiAddress.ResolveHttpUrl(host, listenAny: true));

        Assert.Equal("https://localhost:5443", ArcanumLocalApiAddress.ResolveHttpsUrl(host, httpsEnabled: true));

    }

    [Fact]
    public void ResolveBaseUrl_host_any_env_overrides_config_to_https()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_HOST_ANY", "1");

        HostSettings host = new()
        {
            Port = 5001,
            ListenAny = false,
            Https = new HttpsSettings { Enabled = true, Port = 8443 },
        };

        Assert.Equal("https://localhost:8443/", ArcanumLocalApiAddress.ResolveBaseUrl(host));

    }

}
